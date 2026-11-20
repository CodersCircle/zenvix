using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Zenvix.Core.Models;
using Zenvix.Runtime.Services;

namespace Zenvix.Modules.Services
{
    public interface ILegacyDatabaseImporter
    {
        IReadOnlyList<LegacyMySqlSource> DetectAll();

        /// <summary>
        /// Quietly copies databases from every other MySQL on this PC into Zenvix MySQL (only ones not copied
        /// before) and points projects at Zenvix MySQL. Runs in the background; results go to the log only.
        /// </summary>
        Task AutoImportAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Copies the databases of other local MySQL/MariaDB servers into Zenvix MySQL so projects keep their data
    /// even after XAMPP/WAMP/Laragon/MySQL is uninstalled. Each server is read with its own tools: a running
    /// server is read directly (root without password, or the credentials in the project's .env); a stopped one
    /// is started privately on a named pipe just for the export. Existing Zenvix databases are never overwritten
    /// and a .sql backup of every copied database is kept in backups/.
    /// </summary>
    public sealed class LegacyDatabaseImporter : ILegacyDatabaseImporter
    {
        private static readonly HashSet<string> SystemDbs = new(StringComparer.OrdinalIgnoreCase)
            { "information_schema", "performance_schema", "mysql", "sys", "phpmyadmin", "test" };

        private readonly IServicesOrchestrator _services;
        private readonly IDatabaseCredentialsManager _credentials;
        private readonly IWebsiteOrchestrator _websites;
        private readonly string _base = AppDomain.CurrentDomain.BaseDirectory;
        private readonly SemaphoreSlim _gate = new(1, 1);

        private string StateFile => Path.Combine(_base, "config", "imported-databases.json");

        public LegacyDatabaseImporter(IServicesOrchestrator services, IDatabaseCredentialsManager credentials, IWebsiteOrchestrator websites)
        {
            _services = services;
            _credentials = credentials;
            _websites = websites;
        }

        // ───────────────────────── detection ─────────────────────────

        public IReadOnlyList<LegacyMySqlSource> DetectAll() => LocalMySqlServers.DetectAll(_base);

        /// <summary>Database names from the data folder (one sub-folder per database) – cheap, no server needed.</summary>
        private static List<string> DatabasesOnDisk(string dataDir)
        {
            try
            {
                return Directory.GetDirectories(dataDir)
                    .Select(Path.GetFileName)
                    .Where(n => n != null && !n.StartsWith("#"))
                    .Select(n => Regex.Replace(n!, "@([0-9a-fA-F]{4})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString()))
                    .Where(n => !SystemDbs.Contains(n))
                    .ToList();
            }
            catch { return new List<string>(); }
        }

        // ───────────────────────── automatic import ─────────────────────────

        public async Task AutoImportAsync(CancellationToken ct = default)
        {
            if (!await _gate.WaitAsync(0, ct)) return;
            try
            {
                var state = LoadState();
                foreach (var src in DetectAll())
                {
                    if (!state.TryGetValue(src.Key, out var done)) state[src.Key] = done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var pending = DatabasesOnDisk(src.DataDir).Where(d => !done.Contains(d)).ToList();
                    if (pending.Count > 0)
                    {
                        Log.Information("[Import] {Source} has {Count} database(s) not yet in Zenvix: {Dbs}", src.Name, pending.Count, string.Join(", ", pending));
                        try
                        {
                            var copied = await ImportSourceAsync(src, pending, ct);
                            done.UnionWith(copied);
                            SaveState(state);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warning(ex, "[Import] {Source} failed", src.Name); }
                    }
                    if (done.Count > 0) RepointProjects(src, done);
                }
            }
            finally { _gate.Release(); }
        }

        /// <summary>Copies <paramref name="pending"/> from one source; returns the databases now in Zenvix.</summary>
        private async Task<List<string>> ImportSourceAsync(LegacyMySqlSource src, List<string> pending, CancellationToken ct)
        {
            var result = new List<string>();

            // ── Zenvix MySQL (target) ──
            var target = _services.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.MySQL);
            if (target == null) return result;
            if (target.Status != ServiceStatus.Running && !await _services.StartAsync(target.Id))
            {
                Log.Warning("[Import] Zenvix MySQL could not be started; will retry next time.");
                return result;
            }
            var targetBin = (await _services.GetRuntimeMetadataAsync(RuntimeServiceType.MySQL))?.BinDir;
            var targetCli = targetBin == null ? null : Path.Combine(targetBin, "mysql.exe");
            if (targetCli == null || !File.Exists(targetCli)) return result;
            var (tUser, tPass) = _credentials.GetCredentials(RuntimeServiceType.MySQL);
            var tConn = new[] { "--protocol=TCP", "-h", "127.0.0.1", "-P", target.Port.ToString(), $"-u{tUser}" };

            var (_, existingOut, _) = await RunAsync(targetCli, tConn.Append("-N").Append("-e").Append("SHOW DATABASES").ToArray(), tPass, ct);
            var existing = new HashSet<string>(Lines(existingOut), StringComparer.OrdinalIgnoreCase);
            foreach (var db in pending.Where(existing.Contains)) result.Add(db); // already there: never overwritten
            var todo = pending.Where(d => !existing.Contains(d)).ToList();
            if (todo.Count == 0) return result;

            var client = src.Exe("mysql.exe", "mariadb.exe");
            var dump = src.Exe("mysqldump.exe", "mariadb-dump.exe")!;
            if (client == null) return result;

            // ── how to read the source ──
            Process? temp = null;
            string[] conn;
            var creds = new List<(string User, string Pass, string? Db)> { ("root", "", null) };
            creds.AddRange(ProjectCredentials(src.Port));

            if (IsServerRunning(src))
            {
                conn = new[] { "--protocol=TCP", "-h", "127.0.0.1", "-P", src.Port.ToString() };
            }
            else
            {
                var pipe = "zenvix-import-" + Guid.NewGuid().ToString("N")[..8];
                temp = StartTempServer(src, pipe);
                if (temp == null) return result;
                conn = new[] { "--protocol=PIPE", $"--socket={pipe}" };
                creds = new List<(string, string, string?)> { ("root", "", null) }; // grant tables are skipped
                if (!await WaitReadyAsync(client, conn, ct))
                {
                    Log.Warning("[Import] Could not start {Source} privately for the export.", src.Name);
                    Kill(temp);
                    return result;
                }
            }

            try
            {
                var backupDir = Path.Combine(_base, "backups", $"import-{Slug(src.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}");
                foreach (var db in todo)
                {
                    ct.ThrowIfCancellationRequested();
                    // root first, then the user/password the project itself uses for this database
                    var tried = false;
                    foreach (var (user, pass, onlyDb) in creds.Where(c => c.Db == null || c.Db.Equals(db, StringComparison.OrdinalIgnoreCase)))
                    {
                        Directory.CreateDirectory(backupDir);
                        var file = Path.Combine(backupDir, db + ".sql");
                        var (dc, _, de) = await RunAsync(dump, conn.Concat(new[] { $"-u{user}", "--databases", db, "--routines", "--triggers", "--events",
                            "--single-transaction", "--skip-lock-tables", "--hex-blob", "--default-character-set=utf8mb4", $"--result-file={file}" }).ToArray(), pass, ct);
                        if (dc != 0)
                        {
                            Log.Debug("[Import] {Db} as {User}: {Err}", db, user, First(de));
                            continue;
                        }
                        tried = true;
                        MakeMySqlCompatible(file);
                        var (ic, _, ie) = await RunAsync(targetCli, tConn.Append("--default-character-set=utf8mb4").ToArray(), tPass, ct, stdinFile: file);
                        if (ic == 0) { result.Add(db); Log.Information("[Import] {Db} copied from {Source} into Zenvix MySQL", db, src.Name); }
                        else Log.Warning("[Import] {Db} from {Source}: import failed – {Err}", db, src.Name, First(ie));
                        break;
                    }
                    if (!tried) Log.Information("[Import] {Db} in {Source}: no working login yet (root has a password and no project uses it).", db, src.Name);
                }
            }
            finally { Kill(temp); }
            return result;
        }

        private bool IsServerRunning(LegacyMySqlSource src)
        {
            var bin = Path.GetFullPath(src.BinDir).TrimEnd('\\') + "\\";
            foreach (var name in new[] { "mysqld", "mariadbd" })
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { if (p.MainModule?.FileName.StartsWith(bin, StringComparison.OrdinalIgnoreCase) == true) return true; }
                    catch { if (IsListening(src.Port)) return true; } // a service we may not inspect
                }
            return false;
        }

        /// <summary>DB user/password/database from projects whose .env uses this port on localhost.</summary>
        private IEnumerable<(string User, string Pass, string? Db)> ProjectCredentials(int port)
        {
            foreach (var site in _websites.Websites.ToList())
            {
                var env = ReadEnv(site);
                if (env == null || !IsLocal(env) || EnvPort(env) != port) continue;
                if (env.TryGetValue("DB_USERNAME", out var u) && env.TryGetValue("DB_DATABASE", out var d))
                    yield return (u, env.GetValueOrDefault("DB_PASSWORD") ?? "", d);
            }
        }

        /// <summary>Points projects that used this source's port at Zenvix MySQL for databases now in Zenvix.</summary>
        private void RepointProjects(LegacyMySqlSource src, HashSet<string> databases)
        {
            var zenvix = _services.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.MySQL);
            if (zenvix == null || zenvix.Port == src.Port) return;
            var (zUser, zPass) = _credentials.GetCredentials(RuntimeServiceType.MySQL);
            foreach (var site in _websites.Websites.ToList())
            {
                try
                {
                    var env = ReadEnv(site);
                    if (env == null || !IsLocal(env) || EnvPort(env) != src.Port) continue;
                    if (!env.TryGetValue("DB_DATABASE", out var db) || !databases.Contains(db)) continue;

                    var envPath = Path.Combine(site.LocalPath, ".env");
                    var backup = envPath + ".before-zenvix";
                    if (!File.Exists(backup)) File.Copy(envPath, backup);
                    var lines = File.ReadAllLines(envPath).ToList();
                    Set(lines, "DB_PORT", zenvix.Port.ToString(), after: "DB_HOST");
                    // The old server's user may not exist in Zenvix MySQL: use Zenvix's login.
                    Set(lines, "DB_USERNAME", zUser);
                    Set(lines, "DB_PASSWORD", zPass);
                    File.WriteAllLines(envPath, lines);
                    Log.Information("[Import] {Site}: .env now uses Zenvix MySQL (port {Port}); previous file kept as .env.before-zenvix", site.Domain, zenvix.Port);
                }
                catch (Exception ex) { Log.Warning(ex, "[Import] could not update .env of {Site}", site.Domain); }
            }

            static void Set(List<string> lines, string key, string value, string? after = null)
            {
                var i = lines.FindLastIndex(l => l.TrimStart().StartsWith(key + "="));
                if (i >= 0) { lines[i] = $"{key}={value}"; return; }
                var a = after == null ? -1 : lines.FindLastIndex(l => l.TrimStart().StartsWith(after + "="));
                lines.Insert(a >= 0 ? a + 1 : lines.Count, $"{key}={value}");
            }
        }

        private static Dictionary<string, string>? ReadEnv(Website site)
        {
            try
            {
                var path = Path.Combine(site.LocalPath, ".env");
                if (!File.Exists(path)) return null;
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    var eq = line.IndexOf('=');
                    if (line.StartsWith("#") || eq <= 0) continue;
                    env[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"', '\'');
                }
                return env;
            }
            catch { return null; }
        }

        private static bool IsLocal(Dictionary<string, string> env)
        {
            var conn = env.GetValueOrDefault("DB_CONNECTION") ?? "mysql";
            var host = env.GetValueOrDefault("DB_HOST") ?? "127.0.0.1";
            return conn is "mysql" or "mariadb" && host is "127.0.0.1" or "localhost";
        }

        private static int EnvPort(Dictionary<string, string> env) =>
            int.TryParse(env.GetValueOrDefault("DB_PORT"), out var p) ? p : 3306;

        // ───────────────────────── helpers ─────────────────────────

        /// <summary>MariaDB-only bits that MySQL 9 rejects.</summary>
        private static void MakeMySqlCompatible(string file)
        {
            var text = File.ReadAllText(file);
            var fixedText = Regex.Replace(text, @"^/\*M!999999\\- enable the sandbox mode \*/\s*$", "", RegexOptions.Multiline);
            fixedText = Regex.Replace(fixedText, @"ENGINE=Aria\b", "ENGINE=InnoDB");
            fixedText = Regex.Replace(fixedText, @" (PAGE_CHECKSUM|TRANSACTIONAL)=\d", "");
            if (fixedText != text) File.WriteAllText(file, fixedText);
        }

        /// <summary>Starts a stopped server privately: no TCP port, named pipe only, logins not checked, own pid/log files.</summary>
        private Process? StartTempServer(LegacyMySqlSource src, string pipe)
        {
            try
            {
                var exe = src.Exe("mysqld.exe", "mariadbd.exe");
                if (exe == null) return null;
                var tmp = Path.Combine(Path.GetTempPath(), pipe);
                Directory.CreateDirectory(tmp);
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(src.BinDir)! };
                if (src.DefaultsFile != null) psi.ArgumentList.Add($"--defaults-file={src.DefaultsFile}");
                foreach (var a in new[] { $"--datadir={src.DataDir}", "--skip-grant-tables", "--skip-networking", "--named-pipe", $"--socket={pipe}",
                                          $"--pid-file={Path.Combine(tmp, "temp.pid")}", $"--log-error={Path.Combine(tmp, "error.log")}", "--skip-slave-start" })
                    psi.ArgumentList.Add(a);
                return Process.Start(psi);
            }
            catch (Exception ex) { Log.Warning(ex, "[Import] temp server failed"); return null; }
        }

        private static async Task<bool> WaitReadyAsync(string client, string[] conn, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(60))
            {
                var (code, _, _) = await RunAsync(client, conn.Concat(new[] { "-uroot", "-e", "SELECT 1" }).ToArray(), "", ct);
                if (code == 0) return true;
                await Task.Delay(1000, ct);
            }
            return false;
        }

        private static async Task<(int Code, string Out, string Err)> RunAsync(string exe, string[] args, string? password, CancellationToken ct, string? stdinFile = null)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = stdinFile != null,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["MYSQL_PWD"] = password ?? ""; // never on the command line
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

        private Dictionary<string, HashSet<string>> LoadState()
        {
            try
            {
                if (File.Exists(StateFile))
                {
                    var raw = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(StateFile));
                    if (raw != null)
                        return raw.ToDictionary(k => k.Key, v => new HashSet<string>(v.Value, StringComparer.OrdinalIgnoreCase));
                }
            }
            catch { }
            return new Dictionary<string, HashSet<string>>();
        }

        private void SaveState(Dictionary<string, HashSet<string>> state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state.ToDictionary(k => k.Key, v => v.Value.OrderBy(x => x).ToArray()),
                new JsonSerializerOptions { WriteIndented = true }));
        }

        private static IEnumerable<string> Lines(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        private static string First(string s) => Lines(s).FirstOrDefault() ?? "unknown error";
        private static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        private static void Kill(Process? p) { try { if (p != null && !p.HasExited) { p.Kill(true); p.WaitForExit(5000); } } catch { } }

        private static bool IsListening(int port)
        {
            try { using var c = new TcpClient(); return c.ConnectAsync("127.0.0.1", port).Wait(700) && c.Connected; }
            catch { return false; }
        }
    }
}
