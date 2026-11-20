using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Zenvix.Modules.Toolchain
{
    public enum ToolKind { Php, Composer, Node, Git, Flutter }

    public sealed record ToolStatus(ToolKind Kind, string Name, string? Installed, string? Latest, string Description)
    {
        public bool IsInstalled => Installed != null;
        public bool UpdateAvailable => Installed != null && Latest != null && ToolchainManager.IsNewer(Latest, Installed);
    }

    public sealed record ToolRelease(string Version, string Url);

    /// <summary>A copy of a tool installed on Windows outside Zenvix (found on the user's PATH).</summary>
    public sealed record SystemTool(ToolKind Kind, string Path, string? Version);

    public sealed record CommandResult(int ExitCode, string Output)
    {
        public bool Ok => ExitCode == 0;
    }

    public interface IToolchainManager
    {
        string? GetBinDir(ToolKind kind);
        string? GetInstalledVersion(ToolKind kind);
        Task<string?> GetLatestVersionAsync(ToolKind kind, CancellationToken ct = default);
        Task<IReadOnlyList<ToolStatus>> GetStatusAsync(bool checkLatest, CancellationToken ct = default);
        /// <summary>Installs the tool if missing (and its dependencies, e.g. Composer needs PHP, Flutter needs Git).</summary>
        Task<bool> EnsureAsync(ToolKind kind, IProgress<string> log, CancellationToken ct = default);
        Task<bool> InstallOrUpdateAsync(ToolKind kind, IProgress<string> log, CancellationToken ct = default);
        Task<bool> InstallVersionAsync(ToolKind kind, string? version, IProgress<string> log, CancellationToken ct = default);
        /// <summary>Latest stable releases, one per release line (newest first).</summary>
        Task<IReadOnlyList<ToolRelease>> GetReleasesAsync(ToolKind kind, int count = 2, CancellationToken ct = default);
        /// <summary>The user's own installation of the tool (outside Zenvix), if any.</summary>
        Task<SystemTool?> DetectSystemAsync(ToolKind kind, CancellationToken ct = default);
        /// <summary>Updates the user's own installation using the tool's official method.</summary>
        Task<bool> UpdateSystemAsync(SystemTool tool, IProgress<string> log, CancellationToken ct = default);
        /// <summary>Environment with every Zenvix-managed tool on PATH (used for scaffolding and dev servers).</summary>
        IDictionary<string, string> BuildEnvironment();
        Task<CommandResult> RunAsync(string commandLine, string workingDir, IProgress<string> log, TimeSpan? timeout = null, CancellationToken ct = default);
    }

    /// <summary>
    /// Downloads and manages the developer tools Zenvix needs (PHP CLI, Composer, Node.js LTS, Git, Flutter)
    /// inside the Zenvix folder, so nothing has to be installed on the PC beforehand.
    /// </summary>
    public sealed class ToolchainManager : IToolchainManager
    {
        private readonly string _base;

        public ToolchainManager() : this(AppDomain.CurrentDomain.BaseDirectory) { }

        /// <param name="baseDir">Zenvix install folder (runtimes\, sdk\ live under it). Overridable for tests.</param>
        public ToolchainManager(string baseDir) => _base = baseDir;
        private string SdkRoot => Path.Combine(_base, "sdk");
        private static readonly HttpClient Http = CreateHttp();
        private readonly SemaphoreSlim _installLock = new(1, 1);

        private static HttpClient CreateHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Zenvix/1.0 (+https://github.com/CodersCircle/zenvix)");
            return h;
        }

        // ───────────────────────── locations ─────────────────────────

        private string ToolDir(ToolKind k) => Path.Combine(SdkRoot, k.ToString().ToLowerInvariant());

        public string? GetBinDir(ToolKind kind)
        {
            string? dir = kind switch
            {
                ToolKind.Php => FindPhpDir(),
                ToolKind.Composer => ToolDir(ToolKind.Composer),
                ToolKind.Node => ToolDir(ToolKind.Node),
                ToolKind.Git => Path.Combine(ToolDir(ToolKind.Git), "cmd"),
                ToolKind.Flutter => Path.Combine(ToolDir(ToolKind.Flutter), "flutter", "bin"),
                _ => null,
            };
            var exe = kind switch
            {
                ToolKind.Php => "php.exe", ToolKind.Composer => "composer.phar", ToolKind.Node => "node.exe",
                ToolKind.Git => "git.exe", ToolKind.Flutter => "flutter.bat", _ => "",
            };
            return dir != null && File.Exists(Path.Combine(dir, exe)) ? dir : null;
        }

        /// <summary>Newest PHP that Zenvix has (runtimes\phpfpm\x.y.z) – the same PHP the web server uses.</summary>
        private string? FindPhpDir()
        {
            var root = Path.Combine(_base, "runtimes", "phpfpm");
            if (!Directory.Exists(root)) return null;
            if (Zenvix.Runtime.Services.ActivePhpVersion.Get(_base) is string pinned
                && File.Exists(Path.Combine(root, pinned, "php.exe")))
                return Path.Combine(root, pinned);
            return Directory.GetDirectories(root)
                .Where(d => File.Exists(Path.Combine(d, "php.exe")))
                .OrderByDescending(d => ParseVersion(Path.GetFileName(d)))
                .FirstOrDefault();
        }

        public string? GetInstalledVersion(ToolKind kind)
        {
            var bin = GetBinDir(kind);
            if (bin == null) return null;
            if (kind == ToolKind.Php) return Path.GetFileName(bin);
            var marker = Path.Combine(ToolDir(kind), ".zenvix-version");
            return File.Exists(marker) ? File.ReadAllText(marker).Trim() : "unknown";
        }

        // ───────────────────────── latest versions ─────────────────────────

        public async Task<string?> GetLatestVersionAsync(ToolKind kind, CancellationToken ct = default)
        {
            try
            {
                return (await ResolveLatestAsync(kind, ct)).Version;
            }
            catch (Exception ex)
            {
                Log.Warning("[Toolchain] could not check latest {Tool}: {Msg}", kind, ex.Message);
                return null;
            }
        }

        private async Task<(string Version, string Url)> ResolveLatestAsync(ToolKind kind, CancellationToken ct)
        {
            var list = await ResolveReleasesAsync(kind, 1, ct);
            return (list[0].Version, list[0].Url);
        }

        public async Task<IReadOnlyList<ToolRelease>> GetReleasesAsync(ToolKind kind, int count = 2, CancellationToken ct = default)
        {
            try { return await ResolveReleasesAsync(kind, count, ct); }
            catch (Exception ex)
            {
                Log.Warning("[Toolchain] could not list {Tool} releases: {Msg}", kind, ex.Message);
                return Array.Empty<ToolRelease>();
            }
        }

        /// <summary>Newest stable releases, one per release line (e.g. PHP 8.6.x and 8.5.x, Node 26 LTS and 24 LTS).</summary>
        private async Task<List<ToolRelease>> ResolveReleasesAsync(ToolKind kind, int count, CancellationToken ct)
        {
            var result = new List<ToolRelease>();
            switch (kind)
            {
                case ToolKind.Php:
                {
                    // {"8.6": {"version":"8.6.0", "nts-vs17-x64": {"zip": {"path": "php-8.6.0-nts-Win32-vs17-x64.zip"}}}, "8.5": {...}}
                    using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://windows.php.net/downloads/releases/releases.json", ct));
                    foreach (var v in doc.RootElement.EnumerateObject().Select(p => p.Value)
                                 .Where(v => v.TryGetProperty("version", out _))
                                 .OrderByDescending(v => ParseVersion(v.GetProperty("version").GetString()!)).Take(count))
                    {
                        var build = v.EnumerateObject().First(p => p.Name.StartsWith("nts-") && p.Name.EndsWith("-x64")).Value;
                        result.Add(new ToolRelease(v.GetProperty("version").GetString()!,
                            "https://windows.php.net/downloads/releases/" + build.GetProperty("zip").GetProperty("path").GetString()));
                    }
                    break;
                }
                case ToolKind.Composer:
                {
                    using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://api.github.com/repos/composer/composer/releases?per_page=20", ct));
                    foreach (var r in doc.RootElement.EnumerateArray().Where(r => !r.GetProperty("prerelease").GetBoolean()).Take(count))
                    {
                        var ver = r.GetProperty("tag_name").GetString()!.TrimStart('v');
                        result.Add(new ToolRelease(ver, $"https://getcomposer.org/download/{ver}/composer.phar"));
                    }
                    break;
                }
                case ToolKind.Node:
                {
                    using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://nodejs.org/dist/index.json", ct));
                    // index.json is newest-first: the first entry of each LTS major is that line's latest.
                    var seen = new HashSet<int>();
                    foreach (var e in doc.RootElement.EnumerateArray().Where(e => e.GetProperty("lts").ValueKind == JsonValueKind.String))
                    {
                        var v = e.GetProperty("version").GetString()!;
                        if (!seen.Add(ParseVersion(v).Major)) continue;
                        result.Add(new ToolRelease(v.TrimStart('v'), $"https://nodejs.org/dist/{v}/node-{v}-win-x64.zip"));
                        if (result.Count == count) break;
                    }
                    break;
                }
                case ToolKind.Git:
                {
                    using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://api.github.com/repos/git-for-windows/git/releases?per_page=15", ct));
                    foreach (var r in doc.RootElement.EnumerateArray().Where(r => !r.GetProperty("prerelease").GetBoolean()))
                    {
                        var asset = r.GetProperty("assets").EnumerateArray()
                            .FirstOrDefault(a => Regex.IsMatch(a.GetProperty("name").GetString()!, @"^MinGit-[\d.]+-64-bit\.zip$"));
                        if (asset.ValueKind != JsonValueKind.Object) continue;
                        var name = asset.GetProperty("name").GetString()!;
                        result.Add(new ToolRelease(Regex.Match(name, @"MinGit-([\d.]+)-64").Groups[1].Value, asset.GetProperty("browser_download_url").GetString()!));
                        if (result.Count == count) break;
                    }
                    break;
                }
                case ToolKind.Flutter:
                {
                    using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://storage.googleapis.com/flutter_infra_release/releases/releases_windows.json", ct));
                    var root = doc.RootElement;
                    var baseUrl = root.GetProperty("base_url").GetString();
                    foreach (var r in root.GetProperty("releases").EnumerateArray()
                                 .Where(r => r.GetProperty("channel").GetString() == "stable")
                                 .OrderByDescending(r => r.GetProperty("release_date").GetString()))
                    {
                        var ver = r.GetProperty("version").GetString()!;
                        if (result.Any(x => x.Version == ver)) continue;
                        result.Add(new ToolRelease(ver, baseUrl + "/" + r.GetProperty("archive").GetString()));
                        if (result.Count == count) break;
                    }
                    break;
                }
                default: throw new NotSupportedException(kind.ToString());
            }
            if (result.Count == 0) throw new InvalidOperationException($"No releases found for {kind}.");
            return result;
        }

        // ───────────────────────── the user's own installs ─────────────────────────

        public async Task<SystemTool?> DetectSystemAsync(ToolKind kind, CancellationToken ct = default)
        {
            var exe = kind switch
            {
                ToolKind.Php => "php", ToolKind.Composer => "composer", ToolKind.Node => "node", ToolKind.Git => "git", ToolKind.Flutter => "flutter", _ => "",
            };
            try
            {
                // Search the user's normal PATH only (not Zenvix's tool folders).
                var r = await RunPlainAsync("where.exe", exe, ct);
                // Prefer runnable Windows files: "where composer" also lists the extension-less Unix shell script.
                var candidates = r.Output.Split('\n').Select(l => l.Trim())
                    .Where(l => l.Length > 0 && File.Exists(l) && !l.StartsWith(_base, StringComparison.OrdinalIgnoreCase)).ToList();
                var path = candidates.FirstOrDefault(l => new[] { ".exe", ".bat", ".cmd" }.Contains(Path.GetExtension(l).ToLowerInvariant()))
                           ?? candidates.FirstOrDefault();
                if (path == null) return null;

                var verArgs = kind == ToolKind.Flutter ? "--version" : (kind == ToolKind.Php ? "-v" : "--version");
                var v = await RunPlainAsync("cmd.exe", $"/d /c \"\"{path}\" {verArgs}\"", ct, TimeSpan.FromSeconds(60));
                var plain = Regex.Replace(v.Output, @"\[[0-9;?]*[A-Za-z]", ""); // tools print ANSI colours between words
                var m = Regex.Match(plain, kind == ToolKind.Composer ? @"Composer (?:version )?(\d+\.\d+\.\d+)" : @"(\d+\.\d+\.\d+)");
                return new SystemTool(kind, path, m.Success ? m.Groups[1].Value : null);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[Toolchain] system {Tool} detection failed", kind);
                return null;
            }
        }

        public async Task<bool> UpdateSystemAsync(SystemTool tool, IProgress<string> log, CancellationToken ct = default)
        {
            switch (tool.Kind)
            {
                case ToolKind.Git:
                    // Git for Windows ships its own updater.
                    return (await RunPlainLoggedAsync("cmd.exe", "/d /c git update-git-for-windows -y", log, ct)).ExitCode == 0;

                case ToolKind.Composer:
                    // Composer installed in Program Files / ProgramData needs admin rights: run elevated.
                    var needsAdmin = tool.Path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)
                                     || tool.Path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), StringComparison.OrdinalIgnoreCase);
                    if (needsAdmin) return RunElevated("cmd.exe", $"/c \"\"{tool.Path}\" self-update\"", log);
                    return (await RunPlainLoggedAsync("cmd.exe", $"/d /c \"\"{tool.Path}\" self-update\"", log, ct)).ExitCode == 0;

                case ToolKind.Flutter:
                    return (await RunPlainLoggedAsync("cmd.exe", $"/d /c \"\"{tool.Path}\" upgrade\"", log, ct, TimeSpan.FromMinutes(30))).ExitCode == 0;

                case ToolKind.Node:
                    if ((await RunPlainAsync("cmd.exe", "/d /c winget --version", ct)).ExitCode != 0)
                    {
                        log.Report("winget is not available – opening nodejs.org so you can download the LTS installer.");
                        OpenUrl("https://nodejs.org/en/download");
                        return false;
                    }
                    log.Report("Updating Node.js LTS with winget (a Windows prompt may appear) ...");
                    return (await RunPlainLoggedAsync("cmd.exe",
                        "/d /c winget upgrade --id OpenJS.NodeJS.LTS -e --accept-source-agreements --accept-package-agreements --disable-interactivity",
                        log, ct, TimeSpan.FromMinutes(15))).ExitCode == 0;

                case ToolKind.Php:
                    // PHP from XAMPP/WAMP/Herd/manual zips cannot be upgraded in place safely (extensions, php.ini, stack bundles).
                    log.Report($"Your system PHP ({tool.Path}) belongs to another stack, so Zenvix won't overwrite it. " +
                               "Opening windows.php.net – download the new zip there, or simply use Zenvix's PHP, which updates with one click.");
                    OpenUrl("https://windows.php.net/download/");
                    return false;
            }
            return false;
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        private static bool RunElevated(string file, string args, IProgress<string> log)
        {
            try
            {
                log.Report("Needs administrator rights – please allow the Windows prompt.");
                using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
                p?.WaitForExit(10 * 60 * 1000);
                var ok = p?.ExitCode == 0;
                log.Report(ok ? "✔ Updated." : "✖ Update failed or was cancelled.");
                return ok;
            }
            catch (System.ComponentModel.Win32Exception) { log.Report("Cancelled at the Windows prompt."); return false; }
        }

        /// <summary>Runs with the user's normal environment (no Zenvix tools on PATH).</summary>
        private static async Task<CommandResult> RunPlainAsync(string file, string args, CancellationToken ct, TimeSpan? timeout = null)
            => await RunPlainLoggedAsync(file, args, new Progress<string>(_ => { }), ct, timeout);

        private static async Task<CommandResult> RunPlainLoggedAsync(string file, string args, IProgress<string> log, CancellationToken ct, TimeSpan? timeout = null)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            };
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var sb = new StringBuilder();
            void OnLine(string? l) { if (string.IsNullOrWhiteSpace(l)) return; lock (sb) sb.AppendLine(l); log.Report(l.TrimEnd()); }
            p.OutputDataReceived += (_, e) => OnLine(e.Data);
            p.ErrorDataReceived += (_, e) => OnLine(e.Data);
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return new CommandResult(-1, sb.ToString()); }
            p.WaitForExit();
            return new CommandResult(p.ExitCode, sb.ToString());
        }

        public async Task<IReadOnlyList<ToolStatus>> GetStatusAsync(bool checkLatest, CancellationToken ct = default)
        {
            var list = new List<ToolStatus>();
            foreach (var k in Enum.GetValues<ToolKind>())
            {
                var latest = checkLatest ? await GetLatestVersionAsync(k, ct) : null;
                list.Add(new ToolStatus(k, DisplayName(k), GetInstalledVersion(k), latest, Describe(k)));
            }
            return list;
        }

        private static string DisplayName(ToolKind k) => k switch
        {
            ToolKind.Php => "PHP", ToolKind.Composer => "Composer", ToolKind.Node => "Node.js (LTS)", ToolKind.Git => "Git", ToolKind.Flutter => "Flutter SDK", _ => k.ToString(),
        };

        private static string Describe(ToolKind k) => k switch
        {
            ToolKind.Php => "Runs your PHP sites, Laravel and WordPress",
            ToolKind.Composer => "PHP packages – Laravel, Filament",
            ToolKind.Node => "npm / npx – React, Vue, Next.js, Expo, Ionic",
            ToolKind.Git => "Needed by Flutter and Git deploys",
            ToolKind.Flutter => "Flutter & Dart for mobile apps (~1.5 GB)",
            _ => "",
        };

        // ───────────────────────── install / update ─────────────────────────

        public async Task<bool> EnsureAsync(ToolKind kind, IProgress<string> log, CancellationToken ct = default)
        {
            // Dependencies first.
            if (kind == ToolKind.Composer && !await EnsureAsync(ToolKind.Php, log, ct)) return false;
            if (kind == ToolKind.Flutter && !await EnsureAsync(ToolKind.Git, log, ct)) return false;
            if (GetBinDir(kind) != null) return true;
            return await InstallOrUpdateAsync(kind, log, ct);
        }

        public Task<bool> InstallOrUpdateAsync(ToolKind kind, IProgress<string> log, CancellationToken ct = default)
            => InstallVersionAsync(kind, null, log, ct);

        /// <param name="version">One of the versions from GetReleasesAsync; null = latest.</param>
        public async Task<bool> InstallVersionAsync(ToolKind kind, string? version, IProgress<string> log, CancellationToken ct = default)
        {
            // Switching to a PHP that is already downloaded needs no download: just make it the active one.
            if (kind == ToolKind.Php && version != null && File.Exists(Path.Combine(_base, "runtimes", "phpfpm", version, "php-cgi.exe")))
            {
                Zenvix.Runtime.Services.ActivePhpVersion.Set(version, _base);
                log.Report($"✔ Switched to PHP {version}.");
                return true;
            }
            await _installLock.WaitAsync(ct);
            try
            {
                var releases = await ResolveReleasesAsync(kind, 2, ct);
                var pick = version == null ? releases[0] : releases.FirstOrDefault(r => r.Version == version)
                           ?? throw new InvalidOperationException($"{DisplayName(kind)} {version} is not one of the offered versions.");
                var url = pick.Url;
                version = pick.Version;
                log.Report($"Installing {DisplayName(kind)} {version} ...");

                if (kind == ToolKind.Composer)
                {
                    Directory.CreateDirectory(ToolDir(kind));
                    await DownloadAsync(url, Path.Combine(ToolDir(kind), "composer.phar"), log, ct);
                    WriteComposerShim();
                    await File.WriteAllTextAsync(Path.Combine(ToolDir(kind), ".zenvix-version"), version, ct);
                    log.Report($"✔ Composer {version} ready.");
                    return true;
                }

                var tmpZip = Path.Combine(Path.GetTempPath(), $"zenvix-{kind}-{version}.zip");
                await DownloadAsync(url, tmpZip, log, ct);

                string target = kind == ToolKind.Php
                    ? Path.Combine(_base, "runtimes", "phpfpm", version)   // the web server picks the newest PHP automatically
                    : ToolDir(kind);
                var staging = target + ".new";
                if (Directory.Exists(staging)) Directory.Delete(staging, true);

                log.Report("Extracting (this can take a while for large SDKs) ...");
                await Task.Run(() => ZipFile.ExtractToDirectory(tmpZip, staging), ct);
                File.Delete(tmpZip);

                // Node zips contain one top-level folder; flatten it. Flutter keeps its "flutter" folder.
                if (kind == ToolKind.Node)
                {
                    var inner = Directory.GetDirectories(staging).Single();
                    var flat = staging + ".flat";
                    Directory.Move(inner, flat);
                    Directory.Delete(staging, true);
                    Directory.Move(flat, staging);
                }

                if (Directory.Exists(target))
                {
                    var old = target + ".old";
                    if (Directory.Exists(old)) Directory.Delete(old, true);
                    Directory.Move(target, old);
                    Directory.Move(staging, target);
                    try { Directory.Delete(old, true); } catch { /* files in use; harmless */ }
                }
                else Directory.Move(staging, target);

                if (kind != ToolKind.Php)
                    await File.WriteAllTextAsync(Path.Combine(target, ".zenvix-version"), version, ct);
                else
                    Zenvix.Runtime.Services.ActivePhpVersion.Set(version, _base); // the version you picked becomes active
                if (kind == ToolKind.Composer || kind == ToolKind.Php) WriteComposerShim();

                if (kind == ToolKind.Flutter)
                {
                    log.Report("Preparing Flutter (first run downloads the Dart tools) ...");
                    await RunAsync("flutter config --no-analytics", _base, log, TimeSpan.FromMinutes(10), ct);
                    await RunAsync("flutter --version", _base, log, TimeSpan.FromMinutes(10), ct);
                }

                log.Report($"✔ {DisplayName(kind)} {version} installed.");
                return true;
            }
            catch (OperationCanceledException) { log.Report("Cancelled."); return false; }
            catch (Exception ex)
            {
                Log.Error(ex, "[Toolchain] install of {Tool} failed", kind);
                log.Report($"✖ Could not install {DisplayName(kind)}: {ex.Message}");
                return false;
            }
            finally { _installLock.Release(); }
        }

        private void WriteComposerShim()
        {
            var dir = ToolDir(ToolKind.Composer);
            if (!Directory.Exists(dir)) return;
            // composer.bat lets every tool (and you) call plain "composer" with Zenvix's PHP.
            File.WriteAllText(Path.Combine(dir, "composer.bat"), "@php \"%~dp0composer.phar\" %*\r\n");
        }

        private static async Task DownloadAsync(string url, string dest, IProgress<string> log, CancellationToken ct)
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(dest);
            var buf = new byte[1 << 16];
            long done = 0; int read; var lastPct = -10;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                done += read;
                if (total > 0)
                {
                    var pct = (int)(done * 100 / total);
                    if (pct >= lastPct + 10) { lastPct = pct; log.Report($"Downloading... {pct}% of {total / 1024 / 1024} MB"); }
                }
            }
        }

        // ───────────────────────── running commands ─────────────────────────

        /// <summary>PHP CLI ini with the extensions Composer/Laravel need (written next to Zenvix's PHP).</summary>
        private string? EnsurePhpCliIni()
        {
            var php = GetBinDir(ToolKind.Php);
            if (php == null) return null;
            var ext = Path.Combine(php, "ext").Replace("\\", "/");
            var sb = new StringBuilder();
            sb.AppendLine("[PHP]");
            sb.AppendLine("memory_limit = -1");
            sb.AppendLine("max_execution_time = 0");
            sb.AppendLine($"extension_dir = \"{ext}\"");
            // What Composer/Laravel need, plus whatever the user turned on in Settings → PHP extensions.
            sb.Append(Zenvix.Runtime.Services.PhpExtensions.IniLines(Path.Combine(php, "ext"),
                new[] { "openssl", "curl", "mbstring", "fileinfo", "zip", "pdo_mysql", "mysqli", "pdo_sqlite", "sqlite3", "intl", "gd", "exif", "sodium", "bcmath" },
                includeZend: false, baseDir: _base));
            sb.AppendLine("[curl]");
            var ca = Path.Combine(_base, "ssl", "cacert.pem");
            if (File.Exists(ca)) sb.AppendLine($"curl.cainfo = \"{ca.Replace("\\", "/")}\"");
            var ini = Path.Combine(SdkRoot, "php-cli.ini");
            Directory.CreateDirectory(SdkRoot);
            File.WriteAllText(ini, sb.ToString());
            return ini;
        }

        public IDictionary<string, string> BuildEnvironment()
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                env[(string)e.Key] = (string?)e.Value ?? "";

            var dirs = new List<string>();
            foreach (var k in new[] { ToolKind.Php, ToolKind.Composer, ToolKind.Node, ToolKind.Git, ToolKind.Flutter })
            {
                var d = GetBinDir(k);
                if (d != null) dirs.Add(d);
            }
            var nodeGlobal = Path.Combine(SdkRoot, "npm-global");
            Directory.CreateDirectory(nodeGlobal);
            dirs.Add(nodeGlobal);

            env.TryGetValue("PATH", out var path);
            env["PATH"] = string.Join(";", dirs) + ";" + path;

            var ini = EnsurePhpCliIni();
            if (ini != null) env["PHPRC"] = ini;
            env["COMPOSER_HOME"] = Path.Combine(SdkRoot, "composer-home");
            env["COMPOSER_NO_INTERACTION"] = "1";
            env["NPM_CONFIG_PREFIX"] = nodeGlobal;
            env["NPM_CONFIG_UPDATE_NOTIFIER"] = "false";
            env["CI"] = "1";                      // non-interactive scaffolders (create-vite, create-next-app, expo, ionic)
            env["FLUTTER_SUPPRESS_ANALYTICS"] = "true";
            return env;
        }

        public async Task<CommandResult> RunAsync(string commandLine, string workingDir, IProgress<string> log, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            log.Report($"$ {commandLine}");
            var psi = new ProcessStartInfo("cmd.exe", $"/d /s /c \"{commandLine}\"")
            {
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var kv in BuildEnvironment()) psi.Environment[kv.Key] = kv.Value;

            var output = new StringBuilder();
            using var proc = new Process { StartInfo = psi };
            void OnLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                var clean = Regex.Replace(line, @"\x1B\[[0-9;?]*[A-Za-z]", "").TrimEnd();  // strip ANSI colors
                if (clean.Length == 0) return;
                lock (output) output.AppendLine(clean);
                log.Report(clean);
            }
            proc.OutputDataReceived += (_, e) => OnLine(e.Data);
            proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
            proc.Start();
            proc.StandardInput.Close(); // anything that tries to prompt gets EOF instead of hanging
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(20));
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                log.Report("✖ Command timed out or was cancelled.");
                return new CommandResult(-1, output.ToString());
            }
            proc.WaitForExit(); // flush async output
            return new CommandResult(proc.ExitCode, output.ToString());
        }

        // ───────────────────────── versions ─────────────────────────

        public static Version ParseVersion(string s)
        {
            var m = Regex.Match(s ?? "", @"\d+(\.\d+){0,3}");
            return m.Success && Version.TryParse(m.Value.Contains('.') ? m.Value : m.Value + ".0", out var v) ? v : new Version(0, 0);
        }

        public static bool IsNewer(string candidate, string current) =>
            current != "unknown" && ParseVersion(candidate) > ParseVersion(current);
    }
}
