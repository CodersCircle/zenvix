using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Zenvix.Core.Models;
using Zenvix.Core.Services;

namespace Zenvix.Modules.Containers
{
    public enum ContainerKind { MySQL, MariaDB, PostgreSQL, Redis, MongoDB, Mailpit }
    public enum ContainerState { Stopped, Starting, Running, Stopping, Error }

    /// <summary>A kind of container Zenvix can create, backed by a portable runtime it downloads itself.</summary>
    public sealed record ContainerTemplate(
        ContainerKind Kind, string Title, string Description, RuntimeServiceType Runtime, string Version,
        string ServerExe, int DefaultPort, string? DefaultUser, bool UsesPassword,
        int? DefaultExtraPort = null, string? ExtraPortLabel = null);

    /// <summary>Persisted definition of one container.</summary>
    public sealed class ContainerSpec
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        public ContainerKind Kind { get; set; }
        public string Version { get; set; } = "";
        public int Port { get; set; }
        public int? ExtraPort { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public int MemoryLimitMb { get; set; }
        public bool AutoStart { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public int? Pid { get; set; }
    }

    /// <summary>Live view of a container for the UI.</summary>
    public sealed class ContainerInstance : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            if (name == nameof(State)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
        }

        public ContainerSpec Spec { get; }
        public ContainerTemplate Template { get; }
        public string Folder { get; }
        public ContainerInstance(ContainerSpec spec, ContainerTemplate template, string folder)
        { Spec = spec; Template = template; Folder = folder; }

        public Guid Id => Spec.Id;
        public string Name => Spec.Name;
        public string Title => Template.Title;
        public string Version => Spec.Version;
        public int Port => Spec.Port;
        public string DataPath => Path.Combine(Folder, "data");
        public string LogPath => Path.Combine(Folder, "logs", "container.log");
        public string MemoryLabel => Spec.MemoryLimitMb > 0 ? $"{Spec.MemoryLimitMb} MB limit" : "No memory limit";
        public bool IsRunning => State == ContainerState.Running;

        private ContainerState _state;
        public ContainerState State { get => _state; set => Set(ref _state, value); }
        private string _message = "";
        public string Message { get => _message; set => Set(ref _message, value); }
        private string _usage = "";
        public string Usage { get => _usage; set => Set(ref _usage, value); }

        public string ConnectionString => Spec.Kind switch
        {
            ContainerKind.MySQL or ContainerKind.MariaDB => $"mysql://{Spec.Username}:{Spec.Password}@127.0.0.1:{Spec.Port}",
            ContainerKind.PostgreSQL => $"postgresql://{Spec.Username}:{Spec.Password}@127.0.0.1:{Spec.Port}/postgres",
            ContainerKind.Redis => string.IsNullOrEmpty(Spec.Password) ? $"redis://127.0.0.1:{Spec.Port}" : $"redis://:{Spec.Password}@127.0.0.1:{Spec.Port}",
            ContainerKind.MongoDB => $"mongodb://127.0.0.1:{Spec.Port}",
            ContainerKind.Mailpit => $"smtp://127.0.0.1:{Spec.Port}  ·  Inbox http://127.0.0.1:{Spec.ExtraPort}",
            _ => "",
        };

        /// <summary>Ready-to-paste Laravel .env lines.</summary>
        public string EnvSnippet => Spec.Kind switch
        {
            ContainerKind.MySQL or ContainerKind.MariaDB =>
                $"DB_CONNECTION={(Spec.Kind == ContainerKind.MariaDB ? "mariadb" : "mysql")}\nDB_HOST=127.0.0.1\nDB_PORT={Spec.Port}\nDB_DATABASE=laravel\nDB_USERNAME={Spec.Username}\nDB_PASSWORD={Spec.Password}",
            ContainerKind.PostgreSQL => $"DB_CONNECTION=pgsql\nDB_HOST=127.0.0.1\nDB_PORT={Spec.Port}\nDB_DATABASE=postgres\nDB_USERNAME={Spec.Username}\nDB_PASSWORD={Spec.Password}",
            ContainerKind.Redis => $"REDIS_HOST=127.0.0.1\nREDIS_PORT={Spec.Port}\nREDIS_PASSWORD={(string.IsNullOrEmpty(Spec.Password) ? "null" : Spec.Password)}",
            ContainerKind.MongoDB => $"MONGODB_URI=mongodb://127.0.0.1:{Spec.Port}",
            ContainerKind.Mailpit => $"MAIL_MAILER=smtp\nMAIL_HOST=127.0.0.1\nMAIL_PORT={Spec.Port}\nMAIL_USERNAME=null\nMAIL_PASSWORD=null",
            _ => "",
        };

        public void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public interface IContainerEngine
    {
        IReadOnlyList<ContainerTemplate> Templates { get; }
        IReadOnlyList<ContainerInstance> Containers { get; }
        event Action? ContainersChanged;
        Task InitializeAsync();
        Task<ContainerInstance> CreateAsync(ContainerKind kind, string name, int? port, string? password, int memoryLimitMb, IProgress<string> log);
        Task<bool> StartAsync(ContainerInstance c, IProgress<string> log);
        Task StopAsync(ContainerInstance c, IProgress<string>? log = null);
        Task RemoveAsync(ContainerInstance c, bool deleteData);
        string ReadLog(ContainerInstance c, int maxLines = 400);
        void RefreshUsage();
        Task StopAllAsync();
    }

    /// <summary>
    /// Zenvix's built-in container engine. Each container is an isolated service instance with its own
    /// runtime version, data folder, port, credentials, log file and optional memory limit (Windows Job Object).
    /// No Docker, WSL or Hyper-V required.
    /// </summary>
    public sealed class ContainerEngine : IContainerEngine
    {
        private readonly IRuntimeInstaller _installer;
        private readonly string _root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "containers");
        private readonly string _store = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "containers.json");
        private readonly List<ContainerInstance> _containers = new();
        private readonly Dictionary<Guid, IntPtr> _jobs = new();
        private readonly SemaphoreSlim _installLock = new(1, 1);
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        public event Action? ContainersChanged;
        public IReadOnlyList<ContainerInstance> Containers { get { lock (_containers) return _containers.ToList(); } }

        public IReadOnlyList<ContainerTemplate> Templates { get; } = new List<ContainerTemplate>
        {
            new(ContainerKind.MySQL,      "MySQL",      "Popular relational database",   RuntimeServiceType.MySQL,      "9.7.0",  @"bin\mysqld.exe",     3310,  "root",     true),
            new(ContainerKind.MariaDB,    "MariaDB",    "MySQL-compatible database",     RuntimeServiceType.MariaDB,    "11.4.4", @"bin\mariadbd.exe",   3320,  "root",     true),
            new(ContainerKind.PostgreSQL, "PostgreSQL", "Advanced SQL database",         RuntimeServiceType.PostgreSQL, "16.6",   @"bin\postgres.exe",   5433,  "postgres", true),
            new(ContainerKind.Redis,      "Redis",      "Cache, queues & sessions",      RuntimeServiceType.Redis,      "5.0.14", @"redis-server.exe",   6380,  null,       false),
            new(ContainerKind.MongoDB,    "MongoDB",    "Document database",             RuntimeServiceType.MongoDB,    "7.0.15", @"bin\mongod.exe",     27018, null,       false),
            new(ContainerKind.Mailpit,    "Mailpit",    "Catch & preview test emails",   RuntimeServiceType.Mailpit,    "1.15.0", @"mailpit.exe",        1030,  null,       false, 8030, "Inbox"),
        };

        public ContainerEngine(IRuntimeInstaller installer) => _installer = installer;

        // ───────────────────────── lifecycle ─────────────────────────

        public Task InitializeAsync() => Task.Run(() =>
        {
            Directory.CreateDirectory(_root);
            List<ContainerSpec> specs = new();
            try { if (File.Exists(_store)) specs = JsonSerializer.Deserialize<List<ContainerSpec>>(File.ReadAllText(_store), Json) ?? new(); }
            catch (Exception ex) { Log.Error(ex, "[Containers] could not read {Path}", _store); }

            lock (_containers)
            {
                _containers.Clear();
                foreach (var spec in specs)
                {
                    var t = Templates.First(x => x.Kind == spec.Kind);
                    var c = new ContainerInstance(spec, t, Path.Combine(_root, spec.Name));
                    // Re-adopt containers that kept running while Zenvix was closed.
                    c.State = IsOurProcessAlive(c) ? ContainerState.Running : ContainerState.Stopped;
                    if (c.State == ContainerState.Stopped) spec.Pid = null;
                    _containers.Add(c);
                }
            }
            Save();
            ContainersChanged?.Invoke();
        });

        public async Task<ContainerInstance> CreateAsync(ContainerKind kind, string name, int? port, string? password, int memoryLimitMb, IProgress<string> log)
        {
            var t = Templates.First(x => x.Kind == kind);
            name = SanitizeName(string.IsNullOrWhiteSpace(name) ? kind.ToString().ToLowerInvariant() : name);
            if (Containers.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A container named \"{name}\" already exists.");

            var used = UsedPorts();
            var chosen = port ?? NextFree(t.DefaultPort, used);
            if (used.Contains(chosen)) throw new InvalidOperationException($"Port {chosen} is already in use.");
            int? extra = t.DefaultExtraPort is int ep ? NextFree(ep, used.Append(chosen).ToHashSet()) : null;

            var spec = new ContainerSpec
            {
                Name = name, Kind = kind, Version = t.Version, Port = chosen, ExtraPort = extra,
                Username = t.DefaultUser,
                Password = t.UsesPassword ? (string.IsNullOrEmpty(password) ? "secret" : password) : (string.IsNullOrEmpty(password) ? null : password),
                // Below ~256 MB most engines cannot even start; treat tiny values as "at least 256".
                MemoryLimitMb = memoryLimitMb <= 0 ? 0 : Math.Max(256, memoryLimitMb),
            };
            var c = new ContainerInstance(spec, t, Path.Combine(_root, name));
            Directory.CreateDirectory(c.DataPath);
            Directory.CreateDirectory(Path.GetDirectoryName(c.LogPath)!);

            lock (_containers) _containers.Add(c);
            Save();
            ContainersChanged?.Invoke();
            log.Report($"Created container \"{name}\" ({t.Title} {t.Version}) on port {chosen}.");

            await StartAsync(c, log);
            return c;
        }

        public async Task<bool> StartAsync(ContainerInstance c, IProgress<string> log)
        {
            if (c.State is ContainerState.Running or ContainerState.Starting) return true;
            c.State = ContainerState.Starting;
            c.Message = "Preparing...";
            try
            {
                var binRoot = await EnsureRuntimeAsync(c, log);
                var serverExe = Path.Combine(binRoot, c.Template.ServerExe);
                Directory.CreateDirectory(c.DataPath);
                Directory.CreateDirectory(Path.GetDirectoryName(c.LogPath)!);

                foreach (var p in new[] { c.Spec.Port, c.Spec.ExtraPort ?? 0 }.Where(p => p > 0))
                    if (IsPortListening(p)) throw new InvalidOperationException($"Port {p} is already in use by another program.");

                c.Message = "Initializing data...";
                await InitializeDataAsync(c, binRoot, log);

                c.Message = "Starting...";
                var pid = await LaunchAsync(c, binRoot, serverExe);
                c.Spec.Pid = pid;

                var ok = await WaitForPortAsync(c.Spec.Port, TimeSpan.FromSeconds(60), pid);
                if (!ok)
                {
                    var tail = ReadLog(c, 6);
                    throw new InvalidOperationException("Did not start listening in time." + (tail.Length > 0 ? "\n" + tail : ""));
                }

                // Apply the memory cap once start-up is done: engines briefly spike while creating
                // their files (e.g. InnoDB redo logs) and would otherwise fail to boot.
                ApplyMemoryLimit(c, pid);

                c.State = ContainerState.Running;
                c.Message = $"Listening on 127.0.0.1:{c.Spec.Port}";
                log.Report($"✔ {c.Name} is running on 127.0.0.1:{c.Spec.Port}");
                Save();
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Containers] {Name} failed to start", c.Name);
                c.State = ContainerState.Error;
                c.Message = ex.Message.Split('\n')[0];
                log.Report($"✖ {c.Name}: {ex.Message}");
                KillTree(c.Spec.Pid);
                c.Spec.Pid = null;
                Save();
                return false;
            }
        }

        public async Task StopAsync(ContainerInstance c, IProgress<string>? log = null)
        {
            if (c.State == ContainerState.Stopped) return;
            c.State = ContainerState.Stopping;
            c.Message = "Stopping...";
            var binRoot = _installer.GetRuntimeDirectory(c.Template.Runtime, c.Spec.Version);
            try { await GracefulStopAsync(c, binRoot); } catch (Exception ex) { Log.Warning(ex, "[Containers] graceful stop failed"); }

            // Give it a moment, then make sure.
            for (int i = 0; i < 20 && IsOurProcessAlive(c); i++) await Task.Delay(500);
            KillTree(c.Spec.Pid);
            if (_jobs.Remove(c.Id, out var job)) CloseHandle(job);

            c.Spec.Pid = null;
            c.State = ContainerState.Stopped;
            c.Message = "Stopped";
            c.Usage = "";
            log?.Report($"Stopped {c.Name}.");
            Save();
        }

        public async Task RemoveAsync(ContainerInstance c, bool deleteData)
        {
            await StopAsync(c);
            lock (_containers) _containers.Remove(c);
            Save();
            if (deleteData)
            {
                for (int i = 0; i < 10 && Directory.Exists(c.Folder); i++)
                {
                    try { Directory.Delete(c.Folder, true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        if (i == 9) Log.Warning(ex, "[Containers] could not delete {Dir}", c.Folder);
                        await Task.Delay(500);
                    }
                }
            }
            ContainersChanged?.Invoke();
        }

        public async Task StopAllAsync()
        {
            foreach (var c in Containers.Where(x => x.State != ContainerState.Stopped))
                await StopAsync(c);
        }

        public string ReadLog(ContainerInstance c, int maxLines = 400)
        {
            try
            {
                if (!File.Exists(c.LogPath)) return "";
                using var fs = new FileStream(c.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length > 256 * 1024) fs.Seek(-256 * 1024, SeekOrigin.End);
                var lines = new StreamReader(fs).ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
                return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Count - maxLines)));
            }
            catch (IOException) { return ""; }
        }

        public void RefreshUsage()
        {
            foreach (var c in Containers)
            {
                if (c.State != ContainerState.Running) continue;
                if (!IsOurProcessAlive(c))
                {
                    c.State = ContainerState.Error;
                    c.Message = "Process exited unexpectedly – see logs.";
                    c.Spec.Pid = null;
                    c.Usage = "";
                    continue;
                }
                try
                {
                    using var p = Process.GetProcessById(c.Spec.Pid!.Value);
                    c.Usage = $"{p.WorkingSet64 / 1024 / 1024} MB RAM";
                }
                catch { }
            }
        }

        // ───────────────────────── per-engine details ─────────────────────────

        private async Task<string> EnsureRuntimeAsync(ContainerInstance c, IProgress<string> log)
        {
            var t = c.Template;
            await _installLock.WaitAsync();
            try
            {
                if (!_installer.IsInstalled(t.Runtime, c.Spec.Version))
                {
                    c.Message = $"Downloading {t.Title} {c.Spec.Version}...";
                    log.Report($"Downloading {t.Title} {c.Spec.Version} (one time)...");
                    var progress = new Progress<double>(p => c.Message = $"Downloading {t.Title}... {p:P0}");
                    if (!await _installer.InstallAsync(t.Runtime, c.Spec.Version, progress))
                        throw new InvalidOperationException($"Could not download {t.Title}. {_installer.GetLastReport().Split('\n').LastOrDefault(l => l.Contains("ERROR"))}");
                    log.Report($"✔ {t.Title} {c.Spec.Version} installed.");
                }
            }
            finally { _installLock.Release(); }
            return _installer.GetRuntimeDirectory(t.Runtime, c.Spec.Version);
        }

        private static string Q(string path) => "\"" + path.Replace("\\", "/") + "\"";

        private async Task InitializeDataAsync(ContainerInstance c, string bin, IProgress<string> log)
        {
            var data = c.DataPath;
            bool empty = !Directory.EnumerateFileSystemEntries(data).Any();
            var s = c.Spec;

            switch (s.Kind)
            {
                case ContainerKind.MySQL when empty:
                    log.Report("Initializing MySQL data directory...");
                    await RunAsync(Path.Combine(bin, @"bin\mysqld.exe"), $"--initialize-insecure --datadir={Q(data)} --console", bin);
                    break;

                case ContainerKind.MariaDB when empty:
                    log.Report("Initializing MariaDB data directory...");
                    var pw = string.IsNullOrEmpty(s.Password) ? "" : $" --password={s.Password}";
                    await RunAsync(Path.Combine(bin, @"bin\mariadb-install-db.exe"), $"--datadir={Q(data)}{pw}", bin);
                    break;

                case ContainerKind.PostgreSQL when empty:
                    log.Report("Initializing PostgreSQL cluster...");
                    var pwFile = Path.Combine(c.Folder, "pwfile.tmp");
                    await File.WriteAllTextAsync(pwFile, s.Password ?? "postgres");
                    try
                    {
                        await RunAsync(Path.Combine(bin, @"bin\initdb.exe"),
                            $"-D {Q(data)} -U {s.Username} -E UTF8 --auth=scram-sha-256 --pwfile={Q(pwFile)}", bin);
                    }
                    finally { File.Delete(pwFile); }
                    break;
            }

            if (s.Kind == ContainerKind.MySQL)
            {
                // Applied on every start (idempotent) so the configured password always works.
                var init = Path.Combine(c.Folder, "init.sql");
                await File.WriteAllTextAsync(init,
                    $"ALTER USER 'root'@'localhost' IDENTIFIED BY '{(s.Password ?? "").Replace("'", "''")}';\n");
            }
        }

        /// <summary>
        /// With a memory limit, MySQL/MariaDB must be sized to fit or the Job Object kills them on start-up
        /// (MySQL 9 commits ~600 MB by default, mostly performance_schema + buffer pool).
        /// </summary>
        private static string MemoryTuning(ContainerSpec s)
        {
            if (s.MemoryLimitMb <= 0) return "";
            var pool = Math.Max(32, s.MemoryLimitMb * 35 / 100);
            return $" --performance-schema=OFF --innodb-buffer-pool-size={pool}M --innodb-log-buffer-size=8M --max-connections=60 --table-open-cache=400";
        }

        private Task<int> LaunchAsync(ContainerInstance c, string bin, string serverExe)
        {
            var s = c.Spec;
            var data = Q(c.DataPath);
            var logFile = Q(c.LogPath);
            string args = s.Kind switch
            {
                ContainerKind.MySQL =>
                    $"--datadir={data} --port={s.Port} --bind-address=127.0.0.1 --mysqlx=OFF --log-error={logFile} " +
                    $"--init-file={Q(Path.Combine(c.Folder, "init.sql"))}" + MemoryTuning(s),
                ContainerKind.MariaDB =>
                    $"--datadir={data} --port={s.Port} --bind-address=127.0.0.1 --log-error={logFile}" + MemoryTuning(s),
                ContainerKind.PostgreSQL => "", // started through pg_ctl below
                ContainerKind.Redis =>
                    $"--port {s.Port} --bind 127.0.0.1 --dir {data} --logfile {logFile}" +
                    (string.IsNullOrEmpty(s.Password) ? "" : $" --requirepass {s.Password}"),
                ContainerKind.MongoDB =>
                    $"--dbpath {data} --port {s.Port} --bind_ip 127.0.0.1 --logpath {logFile} --logappend",
                ContainerKind.Mailpit =>
                    $"--smtp 127.0.0.1:{s.Port} --listen 127.0.0.1:{s.ExtraPort} --db-file {Q(Path.Combine(c.DataPath, "mailpit.db"))} --log-file {logFile}",
                _ => throw new NotSupportedException(),
            };

            if (s.Kind == ContainerKind.PostgreSQL)
                return StartPostgresAsync(c, bin);

            var psi = new ProcessStartInfo(serverExe, args)
            {
                WorkingDirectory = Path.GetDirectoryName(serverExe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var p = Process.Start(psi) ?? throw new InvalidOperationException("Process failed to start.");
            Log.Information("[Containers] {Name} started PID {Pid}: {Exe} {Args}", c.Name, p.Id, serverExe, args);
            return Task.FromResult(p.Id);
        }

        private async Task<int> StartPostgresAsync(ContainerInstance c, string bin)
        {
            var s = c.Spec;
            var opts = $"-p {s.Port} -c listen_addresses=127.0.0.1";
            await RunAsync(Path.Combine(bin, @"bin\pg_ctl.exe"),
                $"start -D {Q(c.DataPath)} -l {Q(c.LogPath)} -o \"{opts}\"", bin, waitForExit: false);

            // postmaster.pid's first line is the server PID.
            var pidFile = Path.Combine(c.DataPath, "postmaster.pid");
            for (int i = 0; i < 60; i++)
            {
                if (File.Exists(pidFile))
                {
                    try
                    {
                        using var fs = new FileStream(pidFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var first = new StreamReader(fs).ReadLine();
                        if (int.TryParse(first, out var pid)) return pid;
                    }
                    catch (IOException) { }
                }
                await Task.Delay(500);
            }
            throw new InvalidOperationException("PostgreSQL did not start – see logs.");
        }

        private async Task GracefulStopAsync(ContainerInstance c, string bin)
        {
            var s = c.Spec;
            switch (s.Kind)
            {
                case ContainerKind.PostgreSQL:
                    await RunAsync(Path.Combine(bin, @"bin\pg_ctl.exe"), $"stop -D {Q(c.DataPath)} -m fast", bin);
                    break;
                case ContainerKind.MySQL:
                    await RunAsync(Path.Combine(bin, @"bin\mysqladmin.exe"), $"-h 127.0.0.1 -P {s.Port} -u root --password={s.Password} shutdown", bin);
                    break;
                case ContainerKind.MariaDB:
                    var pw = string.IsNullOrEmpty(s.Password) ? "" : $" --password={s.Password}";
                    await RunAsync(Path.Combine(bin, @"bin\mariadb-admin.exe"), $"-h 127.0.0.1 -P {s.Port} -u root{pw} shutdown", bin);
                    break;
                case ContainerKind.Redis:
                    var auth = string.IsNullOrEmpty(s.Password) ? "" : $" -a {s.Password}";
                    await RunAsync(Path.Combine(bin, "redis-cli.exe"), $"-p {s.Port}{auth} shutdown", bin);
                    break;
                default:
                    KillTree(s.Pid); // MongoDB/Mailpit handle termination safely (journaled / sqlite)
                    break;
            }
        }

        // ───────────────────────── helpers ─────────────────────────

        private static async Task<string> RunAsync(string exe, string args, string cwd, bool waitForExit = true)
        {
            if (!File.Exists(exe)) throw new FileNotFoundException($"Missing runtime file: {Path.GetFileName(exe)}", exe);
            var psi = new ProcessStartInfo(exe, args)
            {
                WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = waitForExit, RedirectStandardError = waitForExit,
            };
            using var p = Process.Start(psi)!;
            if (!waitForExit) return "";
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await p.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { try { p.Kill(true); } catch { } }
            var text = (await output) + (await error);
            if (p.ExitCode != 0) Log.Warning("[Containers] {Exe} exited {Code}: {Out}", Path.GetFileName(exe), p.ExitCode, text);
            return text;
        }

        private static string SanitizeName(string name)
        {
            var clean = new string(name.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '-').ToArray()).Trim('-');
            return clean.Length == 0 ? "container" : clean;
        }

        private HashSet<int> UsedPorts()
        {
            var used = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
            foreach (var c in Containers) { used.Add(c.Spec.Port); if (c.Spec.ExtraPort is int e) used.Add(e); }
            return used;
        }

        private static int NextFree(int start, HashSet<int> used)
        {
            var p = start;
            while (used.Contains(p)) p++;
            return p;
        }

        private static bool IsPortListening(int port) =>
            IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);

        private static async Task<bool> WaitForPortAsync(int port, TimeSpan timeout, int pid)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                try
                {
                    using var client = new TcpClient();
                    var connect = client.ConnectAsync("127.0.0.1", port);
                    if (await Task.WhenAny(connect, Task.Delay(500)) == connect && client.Connected) return true;
                }
                catch (SocketException) { }
                try { using var p = Process.GetProcessById(pid); if (p.HasExited) return false; }
                catch (ArgumentException) { return false; }
                await Task.Delay(500);
            }
            return false;
        }

        private bool IsOurProcessAlive(ContainerInstance c)
        {
            if (c.Spec.Pid is not int pid) return false;
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) return false;
                var runtimeDir = _installer.GetRuntimeDirectory(c.Template.Runtime, c.Spec.Version);
                var path = p.MainModule?.FileName;
                return path != null && path.StartsWith(runtimeDir, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void KillTree(int? pid)
        {
            if (pid is not int id) return;
            try { using var p = Process.GetProcessById(id); p.Kill(entireProcessTree: true); p.WaitForExit(5000); } catch { }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_store)!);
                File.WriteAllText(_store, JsonSerializer.Serialize(Containers.Select(c => c.Spec).ToList(), Json));
            }
            catch (Exception ex) { Log.Error(ex, "[Containers] save failed"); }
        }

        // ───────────── memory limit via Windows Job Object (what Docker uses on Windows) ─────────────

        private void ApplyMemoryLimit(ContainerInstance c, int pid)
        {
            if (c.Spec.MemoryLimitMb <= 0 || !OperatingSystem.IsWindows()) return;
            var job = CreateJobObject(IntPtr.Zero, $"Zenvix.Container.{c.Id:N}");
            if (job == IntPtr.Zero) return;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_JOB_MEMORY },
                JobMemoryLimit = (UIntPtr)((ulong)c.Spec.MemoryLimitMb * 1024 * 1024),
            };
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (SetInformationJobObject(job, 9 /* ExtendedLimitInformation */, ptr, (uint)size))
                {
                    using var p = Process.GetProcessById(pid);
                    if (AssignProcessToJobObject(job, p.Handle)) { _jobs[c.Id] = job; return; }
                }
                Log.Warning("[Containers] could not apply memory limit to {Name}", c.Name);
                CloseHandle(job);
            }
            catch (Exception ex) { Log.Warning(ex, "[Containers] memory limit failed"); CloseHandle(job); }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        private const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
