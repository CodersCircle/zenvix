using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Zenvix.Core.Models;
using Zenvix.Runtime.Services;

namespace Zenvix.Modules.Services
{
    /// <summary>A MySQL/MariaDB installed by another local stack (XAMPP...).</summary>
    public sealed record LegacyMySqlSource(string Name, string BinDir, string DataDir, string DefaultsFile);

    public sealed record LegacyImportResult(bool Success, IReadOnlyList<string> Imported, IReadOnlyList<string> Skipped,
                                            IReadOnlyList<string> Failed, IReadOnlyList<string> UpdatedProjects, string Message);

    public interface ILegacyDatabaseImporter
    {
        /// <summary>The other stack whose databases can be copied into Zenvix, or null.</summary>
        LegacyMySqlSource? Detect();
        Task<LegacyImportResult> ImportAsync(IProgress<string> log, CancellationToken ct = default);
    }

    /// <summary>
    /// Copies the databases of another stack's MySQL (XAMPP) into Zenvix MySQL, so projects keep their data
    /// even after that stack is uninstalled. Works whether the other MySQL is running or not: when it is
    /// stopped, a private copy of its server is started on a spare port just for the export.
    /// Existing Zenvix databases are never overwritten. A .sql backup of every database is kept.
    /// </summary>
    public sealed class LegacyDatabaseImporter : ILegacyDatabaseImporter
    {
        private static readonly HashSet<string> SystemDbs = new(StringComparer.OrdinalIgnoreCase)
            { "information_schema", "performance_schema", "mysql", "sys", "phpmyadmin", "test" };

        private const int TempPort = 3399;

        private readonly IServicesOrchestrator _services;
        private readonly IDatabaseCredentialsManager _credentials;
        private readonly IWebsiteOrchestrator _websites;
        private readonly string _base = AppDomain.CurrentDomain.BaseDirectory;

        public LegacyDatabaseImporter(IServicesOrchestrator services, IDatabaseCredentialsManager credentials, IWebsiteOrchestrator websites)
        {
            _services = services;
            _credentials = credentials;
            _websites = websites;
        }

        public LegacyMySqlSource? Detect()
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                var root = Path.Combine(drive.RootDirectory.FullName, "xampp", "mysql");
                var bin = Path.Combine(root, "bin");
                var data = Path.Combine(root, "data");
                var ini = Path.Combine(bin, "my.ini");
                if (File.Exists(Path.Combine(bin, "mysqld.exe")) && File.Exists(Path.Combine(bin, "mysqldump.exe")) && Directory.Exists(data))
                    return new LegacyMySqlSource("XAMPP", bin, data, ini);
            }
            return null;
        }

        public async Task<LegacyImportResult> ImportAsync(IProgress<string> log, CancellationToken ct = default)
        {
            var src = Detect();
            if (src == null) return Fail("No XAMPP MySQL found on this PC.");

            // ── Zenvix MySQL (target) ──
            var target = _services.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.MySQL);
            if (target == null) return Fail("Zenvix MySQL is not set up.");
            if (target.Status != ServiceStatus.Running)
            {
                log.Report("Starting Zenvix MySQL...");
                if (!await _services.StartAsync(target.Id)) return Fail("Could not start Zenvix MySQL.");
            }
            var targetBin = (await _services.GetRuntimeMetadataAsync(RuntimeServiceType.MySQL))?.BinDir;
            var targetCli = targetBin == null ? null : Path.Combine(targetBin, "mysql.exe");
            if (targetCli == null || !File.Exists(targetCli)) return Fail("Zenvix's MySQL client (mysql.exe) is missing.");
            var (tUser, tPass) = _credentials.GetCredentials(RuntimeServiceType.MySQL);

            // ── Source: the running XAMPP server, or a private temporary copy of it ──
            Process? temp = null;
            int srcPort;
            string[] srcAuth;
            if (IsListening(3306) && Process.GetProcessesByName("mysqld").Any(p => SafePath(p)?.StartsWith(src.BinDir, StringComparison.OrdinalIgnoreCase) == true))
            {
                srcPort = 3306;
                srcAuth = new[] { "-uroot" };
                log.Report($"Reading from the running {src.Name} MySQL (port 3306)...");
            }
            else
            {
                log.Report($"{src.Name} MySQL is not running – starting a private copy just for the export...");
                srcPort = TempPort;
                srcAuth = new[] { "-uroot" };
                temp = StartTempServer(src);
                if (temp == null || !await WaitListeningAsync(srcPort, TimeSpan.FromSeconds(40), ct))
                {
                    Kill(temp);
                    return Fail($"Could not start {src.Name}'s MySQL for the export. See logs.");
                }
            }

            var imported = new List<string>();
            var skipped = new List<string>();
            var failed = new List<string>();
            try
            {
                var srcCli = Path.Combine(src.BinDir, "mysql.exe");
                var (code, list, err) = await RunAsync(srcCli, Args(srcPort, srcAuth, "-N", "-e", "SHOW DATABASES"), null, ct);
                if (code != 0)
                    return Fail(err.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
                        ? $"{src.Name}'s root user has a password. Stop {src.Name} MySQL and try again – Zenvix can then read it without one."
                        : $"Could not list {src.Name} databases: {err.Trim()}");

                var sourceDbs = list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                    .Where(d => !SystemDbs.Contains(d)).ToList();
                if (sourceDbs.Count == 0) return Fail($"{src.Name} has no databases of its own to import.");

                var (_, existingOut, _) = await RunAsync(targetCli, Args(target.Port, new[] { $"-u{tUser}" }, "-N", "-e", "SHOW DATABASES"), tPass, ct);
                var existing = new HashSet<string>(existingOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

                var backupDir = Path.Combine(_base, "backups", $"{src.Name.ToLowerInvariant()}-import-{DateTime.Now:yyyyMMdd-HHmmss}");
                Directory.CreateDirectory(backupDir);
                log.Report($"Found {sourceDbs.Count} database(s). Backups go to {backupDir}");

                var dump = Path.Combine(src.BinDir, "mysqldump.exe");
                foreach (var db in sourceDbs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (existing.Contains(db)) { skipped.Add(db); log.Report($"• {db}: already in Zenvix – kept as is."); continue; }

                    log.Report($"• {db}: exporting...");
                    var file = Path.Combine(backupDir, db + ".sql");
                    var (dc, _, de) = await RunAsync(dump, Args(srcPort, srcAuth, "--databases", db, "--routines", "--triggers", "--events",
                        "--single-transaction", "--skip-lock-tables", "--hex-blob", "--default-character-set=utf8mb4", $"--result-file={file}"), null, ct);
                    if (dc != 0) { failed.Add(db); log.Report($"  ✖ export failed: {First(de)}"); continue; }

                    MakeMySqlCompatible(file);
                    log.Report($"• {db}: importing ({new FileInfo(file).Length / 1024:N0} KB)...");
                    var (ic, _, ie) = await RunAsync(targetCli, Args(target.Port, new[] { $"-u{tUser}" }, "--default-character-set=utf8mb4"), tPass, ct, stdinFile: file);
                    if (ic != 0) { failed.Add(db); log.Report($"  ✖ import failed: {First(ie)}"); continue; }
                    imported.Add(db);
                    log.Report($"  ✔ {db} imported");
                }
            }
            finally
            {
                if (temp != null) { log.Report($"Stopping the temporary {src.Name} MySQL..."); Kill(temp); }
            }

            // Projects whose .env points at the old MySQL now use Zenvix MySQL.
            var nowInZenvix = new HashSet<string>(imported.Concat(skipped), StringComparer.OrdinalIgnoreCase);
            var updated = RepointProjects(nowInZenvix, target.Port, log);

            var msg = $"Imported {imported.Count} database(s)" +
                      (skipped.Count > 0 ? $", {skipped.Count} already existed" : "") +
                      (failed.Count > 0 ? $", {failed.Count} failed ({string.Join(", ", failed)})" : "") +
                      (updated.Count > 0 ? $". Updated .env of {string.Join(", ", updated)} to use Zenvix MySQL (port {target.Port})." : ".");
            Log.Information("[Import] {Message}", msg);
            return new LegacyImportResult(failed.Count == 0, imported, skipped, failed, updated, msg);

            static LegacyImportResult Fail(string m) =>
                new(false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), m);
        }

        /// <summary>Sets DB_PORT to Zenvix MySQL in projects that used port 3306 for a database now in Zenvix.</summary>
        private List<string> RepointProjects(HashSet<string> databases, int zenvixPort, IProgress<string> log)
        {
            var updated = new List<string>();
            foreach (var site in _websites.Websites.ToList())
            {
                try
                {
                    var envPath = Path.Combine(site.LocalPath, ".env");
                    if (!File.Exists(envPath)) continue;
                    var lines = File.ReadAllLines(envPath).ToList();
                    string? Get(string key) => lines.Select(l => l.Trim()).Where(l => l.StartsWith(key + "=")).Select(l => l[(key.Length + 1)..].Trim().Trim('"', '\'')).LastOrDefault();

                    var conn = Get("DB_CONNECTION") ?? "mysql";
                    var host = Get("DB_HOST") ?? "127.0.0.1";
                    var port = Get("DB_PORT") ?? "3306";
                    var db = Get("DB_DATABASE");
                    if ((conn != "mysql" && conn != "mariadb") || port != "3306" || db == null || !databases.Contains(db)) continue;
                    if (host != "127.0.0.1" && host != "localhost") continue;

                    var backup = envPath + ".before-zenvix";
                    if (!File.Exists(backup)) File.Copy(envPath, backup);
                    var idx = lines.FindLastIndex(l => l.TrimStart().StartsWith("DB_PORT="));
                    if (idx >= 0) lines[idx] = $"DB_PORT={zenvixPort}";
                    else
                    {
                        var after = lines.FindLastIndex(l => l.TrimStart().StartsWith("DB_HOST="));
                        lines.Insert(after >= 0 ? after + 1 : lines.Count, $"DB_PORT={zenvixPort}");
                    }
                    File.WriteAllLines(envPath, lines);
                    updated.Add(site.Domain);
                    log.Report($"• {site.Domain}: .env now uses Zenvix MySQL (DB_PORT={zenvixPort}); old file saved as .env.before-zenvix");
                }
                catch (Exception ex) { log.Report($"• {site.Domain}: could not update .env – {ex.Message}"); }
            }
            return updated;
        }

        /// <summary>MariaDB-only bits that MySQL 9 rejects.</summary>
        private static void MakeMySqlCompatible(string file)
        {
            var text = File.ReadAllText(file);
            var fixedText = Regex.Replace(text, @"^/\*M!999999\\- enable the sandbox mode \*/\s*$", "", RegexOptions.Multiline);
            fixedText = Regex.Replace(fixedText, @"ENGINE=Aria\b", "ENGINE=InnoDB");
            fixedText = Regex.Replace(fixedText, @" (PAGE_CHECKSUM|TRANSACTIONAL)=\d", "");
            if (fixedText != text) File.WriteAllText(file, fixedText);
        }

        private Process? StartTempServer(LegacyMySqlSource src)
        {
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(src.BinDir, "mysqld.exe"))
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(src.BinDir)!,
                };
                if (File.Exists(src.DefaultsFile)) psi.ArgumentList.Add($"--defaults-file={src.DefaultsFile}");
                foreach (var a in new[] { $"--datadir={src.DataDir}", $"--port={TempPort}", "--bind-address=127.0.0.1",
                                          "--skip-grant-tables", "--skip-slave-start", "--console" })
                    psi.ArgumentList.Add(a);
                return Process.Start(psi);
            }
            catch (Exception ex) { Log.Warning(ex, "[Import] temp server failed"); return null; }
        }

        private static string[] Args(int port, string[] auth, params string[] rest) =>
            new[] { "--protocol=TCP", "-h", "127.0.0.1", "-P", port.ToString() }.Concat(auth).Concat(rest).ToArray();

        private static async Task<(int Code, string Out, string Err)> RunAsync(string exe, string[] args, string? password, CancellationToken ct, string? stdinFile = null)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = stdinFile != null,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (password != null) psi.Environment["MYSQL_PWD"] = password;
            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (stdinFile != null)
            {
                using (var fs = File.OpenRead(stdinFile)) await fs.CopyToAsync(p.StandardInput.BaseStream, ct);
                p.StandardInput.Close();
            }
            await p.WaitForExitAsync(ct);
            return (p.ExitCode, await outTask, await errTask);
        }

        private static string First(string s) => s.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "unknown error";

        private static string? SafePath(Process p) { try { return p.MainModule?.FileName; } catch { return null; } }

        private static void Kill(Process? p) { try { if (p != null && !p.HasExited) { p.Kill(true); p.WaitForExit(5000); } } catch { } }

        private static bool IsListening(int port)
        {
            try { using var c = new TcpClient(); return c.ConnectAsync("127.0.0.1", port).Wait(700) && c.Connected; }
            catch { return false; }
        }

        private static async Task<bool> WaitListeningAsync(int port, TimeSpan timeout, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (IsListening(port)) return true;
                await Task.Delay(500, ct);
            }
            return false;
        }
    }
}
