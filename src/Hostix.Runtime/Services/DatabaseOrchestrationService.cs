using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Hostix.Core.Models;
using Hostix.Core.Services;
using Serilog;

namespace Hostix.Runtime.Services
{
    public interface IDatabaseOrchestrationService
    {
        ObservableCollection<DatabaseInstance> Instances { get; }
        event Action<DatabaseInstance>? InstanceStateChanged;

        Task InitializeAsync();
        Task<bool> StartInstanceAsync(Guid instanceId);
        Task<bool> StopInstanceAsync(Guid instanceId);
        Task<bool> RestartInstanceAsync(Guid instanceId);
        DatabaseInstance CreateInstance(string name, DbEngineType engine, int port, string? version = null);
        void RemoveInstance(Guid instanceId);
        void OpenPanel(Guid instanceId);
        void OpenDataFolder(Guid instanceId);
        void OpenConfig(Guid instanceId);
        void OpenLogs(Guid instanceId);
        /// <summary>Called by RuntimeStateBridge to sync state from ServicesOrchestrator.</summary>
        void SyncInstanceStatus(DbEngineType engine, DbInstanceStatus status, int? processId);
    }

    public class DatabaseOrchestrationService : IDatabaseOrchestrationService
    {
        public ObservableCollection<DatabaseInstance> Instances { get; } = new();
        public event Action<DatabaseInstance>? InstanceStateChanged;

        // Maps DbEngineType → OS process names for detection
        private static readonly Dictionary<DbEngineType, string[]> _processNames = new()
        {
            [DbEngineType.MariaDB]     = new[] { "mariadbd", "mysqld" },
            [DbEngineType.MySQL]       = new[] { "mysqld" },
            [DbEngineType.PostgreSQL]  = new[] { "postgres" },
            [DbEngineType.MongoDB]     = new[] { "mongod" },
            [DbEngineType.Redis]       = new[] { "redis-server" },
            [DbEngineType.Meilisearch] = new[] { "meilisearch" },
            [DbEngineType.SQLite]      = Array.Empty<string>(),
            // Cloud engines never have local processes:
            [DbEngineType.Supabase]    = Array.Empty<string>(),
            [DbEngineType.Firebase]    = Array.Empty<string>(),
            [DbEngineType.PlanetScale] = Array.Empty<string>(),
            [DbEngineType.Neon]        = Array.Empty<string>(),
        };

        // Binary names for launching
        private static readonly Dictionary<DbEngineType, string> _binaryNames = new()
        {
            [DbEngineType.MariaDB]    = "mysqld.exe",
            [DbEngineType.MySQL]      = "mysqld.exe",
            [DbEngineType.PostgreSQL] = "pg_ctl.exe",
            [DbEngineType.MongoDB]    = "mongod.exe",
            [DbEngineType.Redis]      = "redis-server.exe",
        };

        private readonly string _binRoot;
        private readonly string _dataRoot;
        private readonly IEmbeddedToolsOrchestrator _tools;
        private readonly IRuntimeLocator _runtimeLocator;
        private readonly Dictionary<Guid, Process> _runningProcesses = new();

        public DatabaseOrchestrationService(IEmbeddedToolsOrchestrator tools, IRuntimeLocator runtimeLocator)
        {
            _tools    = tools;
            _runtimeLocator = runtimeLocator;
            _binRoot  = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtimes");
            _dataRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "databases");
        }

        public async Task InitializeAsync()
        {
            Log.Information("DatabaseOrchestrationService: scanning for running DB instances...");

            // Seed default instances if none exist
            if (!Instances.Any())
                SeedDefaultInstances();

            // Detect and STOP external database processes to ensure clean ownership
            foreach (var instance in Instances)
            {
                if (_processNames.TryGetValue(instance.Engine, out var processNames))
                {
                    foreach (var name in processNames)
                    {
                        var procs = Process.GetProcessesByName(name);
                        if (procs.Any())
                        {
                            Log.Warning("[Database] Found external {Engine} instance — stopping for clean portable ownership.", instance.Engine);
                            foreach (var p in procs)
                            {
                                try { p.Kill(true); } catch { }
                            }
                        }
                    }
                }
                
                instance.Status = DbInstanceStatus.Stopped;
                InstanceStateChanged?.Invoke(instance);
            }

            await Task.CompletedTask;
        }

        private void SeedDefaultInstances()
        {
            // ── Local Relational ──────────────────────────────────────
            CreateInstance("MySQL Dev",         DbEngineType.MySQL,      3307);
        }

        public DatabaseInstance CreateInstance(string name, DbEngineType engine, int port, string? version = null)
        {
            if (!DbEngineDefaults.Metadata.TryGetValue(engine, out var meta))
                meta = ("latest", port, "", "DB", false);

            var dataPath = Path.Combine(_dataRoot, engine.ToString().ToLower(), name.Replace(" ", "_").ToLower());
            var instance = new DatabaseInstance
            {
                Name       = name,
                Engine     = engine,
                Version    = version ?? meta.LatestVersion,
                Port       = port,
                DataPath   = dataPath,
                ConfigPath = Path.Combine(dataPath, "my.ini"),
                LogsPath   = Path.Combine(dataPath, "error.log"),
                Status     = DbInstanceStatus.Stopped,
            };

            Instances.Add(instance);
            Log.Information("Created DB instance: {Name} ({Engine} v{Version}) on port {Port}", name, engine, instance.Version, port);
            return instance;
        }

        public void RemoveInstance(Guid instanceId)
        {
            var instance = Instances.FirstOrDefault(i => i.Id == instanceId);
            if (instance == null) return;

            if (instance.Status == DbInstanceStatus.Running)
                _ = StopInstanceAsync(instanceId);

            Instances.Remove(instance);
            Log.Information("Removed DB instance: {Name}", instance.Name);
        }

        public async Task<bool> StartInstanceAsync(Guid instanceId)
        {
            var instance = Instances.FirstOrDefault(i => i.Id == instanceId);
            if (instance == null || instance.Status == DbInstanceStatus.Running) return false;

            instance.Status = DbInstanceStatus.Starting;
            InstanceStateChanged?.Invoke(instance);

            try
            {
                Log.Information("Starting {Name} ({Engine})...", instance.Name, instance.Engine);

                // Check if binary exists
                var binPath = GetBinaryPath(instance.Engine);
                bool launched = false;

                if (binPath != null)
                {
                    var args = BuildStartArgs(instance);
                    var psi = new ProcessStartInfo(binPath, args)
                    {
                        WorkingDirectory = Path.GetDirectoryName(binPath),
                        UseShellExecute  = false,
                        CreateNoWindow   = true,
                    };
                    var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        _runningProcesses[instanceId] = proc;
                        instance.ProcessId = proc.Id;
                        launched = true;
                    }
                }
                else
                {
                    // Binary not installed — simulate with delay for UI responsiveness
                    Log.Warning("{Engine} binary not found. Simulating startup.", instance.Engine);
                    await Task.Delay(1500);
                    launched = true;
                }

                instance.Status    = launched ? DbInstanceStatus.Running : DbInstanceStatus.Error;
                instance.LastStarted = launched ? DateTime.Now : instance.LastStarted;
                InstanceStateChanged?.Invoke(instance);
                return launched;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start {Name}", instance.Name);
                instance.Status = DbInstanceStatus.Error;
                InstanceStateChanged?.Invoke(instance);
                return false;
            }
        }

        public async Task<bool> StopInstanceAsync(Guid instanceId)
        {
            var instance = Instances.FirstOrDefault(i => i.Id == instanceId);
            if (instance == null || instance.Status == DbInstanceStatus.Stopped) return false;

            instance.Status = DbInstanceStatus.Stopping;
            InstanceStateChanged?.Invoke(instance);

            try
            {
                if (_runningProcesses.TryGetValue(instanceId, out var proc) && !proc.HasExited)
                {
                    proc.Kill(true);
                    _runningProcesses.Remove(instanceId);
                }

                await Task.Delay(400);
                instance.Status    = DbInstanceStatus.Stopped;
                instance.ProcessId = null;
                InstanceStateChanged?.Invoke(instance);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to stop {Name}", instance.Name);
                instance.Status = DbInstanceStatus.Error;
                InstanceStateChanged?.Invoke(instance);
                return false;
            }
        }

        public async Task<bool> RestartInstanceAsync(Guid instanceId)
        {
            await StopInstanceAsync(instanceId);
            await Task.Delay(500);
            return await StartInstanceAsync(instanceId);
        }

        public void OpenPanel(Guid instanceId)
        {
            var instance = Instances.FirstOrDefault(i => i.Id == instanceId);
            if (instance == null) return;

            // Cloud DBs → open their web console directly
            if (instance.IsCloud && !string.IsNullOrEmpty(instance.PanelUrl))
            {
                try { Process.Start(new ProcessStartInfo(instance.PanelUrl) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Warning(ex, "Could not open cloud panel for {Name}", instance.Name); }
                return;
            }

            // Local DBs → route through EmbeddedToolsOrchestrator (Adminer via PHP)
            if (instance.Status != DbInstanceStatus.Running)
            {
                Log.Warning("Cannot open panel for {Name} — not running.", instance.Name);
                return;
            }

            var dbType = instance.Engine switch
            {
                DbEngineType.MySQL      => "mysql",
                DbEngineType.MariaDB    => "mariadb",
                DbEngineType.PostgreSQL => "postgresql",
                DbEngineType.SQLite     => "sqlite",
                DbEngineType.MongoDB    => "mongodb",
                DbEngineType.Redis      => "redis",
                DbEngineType.Meilisearch => "meilisearch",
                _                       => "mysql"
            };

            _ = _tools.OpenDatabasePanelAsync(dbType, "127.0.0.1", instance.Port);
            Log.Information("Panel requested for {Name} via EmbeddedToolsOrchestrator.", instance.Name);
        }

        public void OpenDataFolder(Guid instanceId)
        {
            var instance = Instances.FirstOrDefault(i => i.Id == instanceId);
            if (instance == null) return;
            if (!Directory.Exists(instance.DataPath)) Directory.CreateDirectory(instance.DataPath);
            try { Process.Start("explorer.exe", instance.DataPath); }
            catch (Exception ex) { Log.Warning(ex, "Could not open data folder for {Name}", instance.Name); }
        }

        public void OpenConfig(Guid instanceId)
        {
            var instance = Instances.FirstOrDefault(i => i.Id == instanceId);
            if (instance == null || string.IsNullOrEmpty(instance.ConfigPath)) return;
            if (!File.Exists(instance.ConfigPath)) 
            {
                Directory.CreateDirectory(Path.GetDirectoryName(instance.ConfigPath)!);
                File.WriteAllText(instance.ConfigPath, "# Database Configuration\n");
            }
            try { Process.Start(new ProcessStartInfo(instance.ConfigPath) { UseShellExecute = true }); }
            catch { }
        }

        public void OpenLogs(Guid instanceId)
        {
            var instance = Instances.FirstOrDefault(i => i.Id == instanceId);
            if (instance == null || string.IsNullOrEmpty(instance.LogsPath)) return;
            if (!File.Exists(instance.LogsPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(instance.LogsPath)!);
                File.WriteAllText(instance.LogsPath, "# Database Logs\n");
            }
            try { Process.Start(new ProcessStartInfo(instance.LogsPath) { UseShellExecute = true }); }
            catch { }
        }

        private bool IsProcessRunning(DbEngineType engine)
        {
            if (!_processNames.TryGetValue(engine, out var names)) return false;
            return names.Any(n => Process.GetProcessesByName(n).Length > 0);
        }

        private string? GetBinaryPath(DbEngineType engine)
        {
            // 1. Try to find the best internal/portable runtime via RuntimeLocator
            var runtimeType = engine switch
            {
                DbEngineType.MySQL      => RuntimeServiceType.MySQL,
                DbEngineType.MariaDB    => RuntimeServiceType.MariaDB,
                DbEngineType.PostgreSQL => RuntimeServiceType.PostgreSQL,
                DbEngineType.MongoDB    => RuntimeServiceType.MongoDB,
                DbEngineType.Redis      => RuntimeServiceType.Redis,
                _                       => (RuntimeServiceType?)null
            };

            if (runtimeType.HasValue)
            {
                // We use Task.Run().Result because this is a sync method and 
                // RuntimeLocator is already optimized for fast path checks.
                var best = Task.Run(() => _runtimeLocator.FindBestAsync(runtimeType.Value)).Result;
                if (best != null && File.Exists(best.ExecutablePath))
                {
                    return best.ExecutablePath;
                }
            }

            // 2. Legacy fallback check in runtimes folder structure
            if (!_binaryNames.TryGetValue(engine, out var binary)) return null;
            var legacyPath = Path.Combine(_binRoot, engine.ToString().ToLower(), binary);
            if (File.Exists(legacyPath)) return legacyPath;

            // 3. Deeper fallback for bin subfolder
            var binSubPath = Path.Combine(_binRoot, engine.ToString().ToLower(), "bin", binary);
            return File.Exists(binSubPath) ? binSubPath : null;
        }

        private string BuildStartArgs(DatabaseInstance instance)
        {
            // Ensure config directory exists
            var configDir = Path.GetDirectoryName(instance.ConfigPath);
            if (!string.IsNullOrEmpty(configDir) && !Directory.Exists(configDir))
                Directory.CreateDirectory(configDir);

            // Generate or ensure config file exists
            if (!File.Exists(instance.ConfigPath))
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Hostix Managed MySQL/MariaDB Configuration");
                sb.AppendLine("[mysqld]");
                sb.AppendLine($"port={instance.Port}");
                sb.AppendLine($"datadir=\"{instance.DataPath.Replace("\\", "/")}\"");
                sb.AppendLine("bind-address=127.0.0.1");
                sb.AppendLine("max_connections=100");
                sb.AppendLine("innodb_buffer_pool_size=128M");
                // Allow user to add more settings below
                File.WriteAllText(instance.ConfigPath, sb.ToString());
            }

            return instance.Engine switch
            {
                DbEngineType.MariaDB or DbEngineType.MySQL =>
                    $"--defaults-file=\"{instance.ConfigPath}\"",
                DbEngineType.PostgreSQL =>
                    $"start -D \"{instance.DataPath}\" -o \"-p {instance.Port}\"",
                DbEngineType.MongoDB =>
                    $"--port {instance.Port} --dbpath \"{instance.DataPath}\"",
                DbEngineType.Redis =>
                    $"--port {instance.Port}",
                _ => ""
            };
        }

        public void SyncInstanceStatus(DbEngineType engine, DbInstanceStatus status, int? processId)
        {
            // Find ALL instances of this engine type and update their status.
            // Because INotifyPropertyChanged is on DatabaseInstance, WPF updates instantly.
            foreach (var inst in Instances)
            {
                if (inst.Engine == engine && !inst.IsCloud)
                {
                    inst.Status    = status;
                    inst.ProcessId = processId;
                    InstanceStateChanged?.Invoke(inst);
                    Log.Information("DB section synced: {Name} → {Status}", inst.Name, status);
                }
            }
        }
    }
}
