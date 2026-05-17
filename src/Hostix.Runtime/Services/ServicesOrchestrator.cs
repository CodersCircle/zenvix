using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hostix.Core.Models;
using Hostix.Core.Services;
using Serilog;

namespace Hostix.Runtime.Services
{
    public interface IServicesOrchestrator
    {
        IEnumerable<RuntimeServiceInstance> Instances { get; }
        object InstancesLock { get; }
        event Action<RuntimeServiceInstance>? InstanceStateChanged;
        event Action? RegistryUpdated;
        Task InitializeAsync();
        Task<bool> StartAsync(Guid instanceId);
        Task<bool> StopAsync(Guid instanceId);
        Task<bool> RestartAsync(Guid instanceId);
        void OpenConfig(Guid instanceId);
        void OpenLogs(Guid instanceId);
        void OpenDataFolder(Guid instanceId);
        string? GetBinaryPath(RuntimeServiceType type);
        Task<RuntimeMetadata?> GetRuntimeMetadataAsync(RuntimeServiceType type);
        RuntimeServiceInstance AddService(RuntimeServiceType type, string? customName = null, int? customPort = null);
        void RemoveService(Guid instanceId);
    }

    public class ServicesOrchestrator : IServicesOrchestrator
    {
        private readonly IProcessManager _processManager;
        private readonly IRuntimeLocator _runtimeLocator;
        private readonly IRuntimeInstaller _runtimeInstaller;
        private readonly IRuntimeConfigGenerator _configGenerator;
        private readonly IDatabaseCredentialsManager _credentialsManager;
        private readonly SemaphoreSlim _startupLock = new(1, 1);
        
        private readonly string _binRoot;
        private readonly string _logsRoot;
        private readonly string _configRoot;
        private readonly Dictionary<RuntimeServiceType, RuntimeMetadata> _resolvedRuntimes = new();
        private readonly Dictionary<Guid, Process> _runningProcesses = new();
        
        private Timer? _metricsTimer;

        private readonly List<RuntimeServiceInstance> _instances = new();
        public object InstancesLock { get; } = new();
        public IEnumerable<RuntimeServiceInstance> Instances 
        { 
            get 
            { 
                lock (InstancesLock) { return _instances.ToList(); } 
            } 
        }
        public event Action<RuntimeServiceInstance>? InstanceStateChanged;
        public event Action? RegistryUpdated;

        public ServicesOrchestrator(
            IProcessManager processManager, 
            IRuntimeLocator runtimeLocator,
            IRuntimeInstaller runtimeInstaller,
            IRuntimeConfigGenerator configGenerator,
            IDatabaseCredentialsManager credentialsManager)
        {
            _processManager = processManager;
            _runtimeLocator = runtimeLocator;
            _runtimeInstaller = runtimeInstaller;
            _configGenerator = configGenerator;
            _credentialsManager = credentialsManager;
            _binRoot    = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin");
            _logsRoot   = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            _configRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config");

            _runtimeInstaller.LogMessage += (msg) => {
                Log.Information("[Installer] {Msg}", msg);
            };
        }

        public async Task InitializeAsync()
        {
            Log.Information("ServicesOrchestrator: initializing service registry and runtimes...");

            await InitializeBinaryPaths();

            // Always re-seed to ensure correct display order
            lock (InstancesLock)
            {
                _instances.Clear();
                SeedDefaultServices();
            }
            RegistryUpdated?.Invoke();

            // Detect and STOP external processes for managed services to ensure clean ownership
            foreach (var instance in Instances)
            {
                if (RuntimeServiceMeta.Data.TryGetValue(instance.Type, out var meta))
                {
                    var existingProcesses = meta.ProcessNames
                        .SelectMany(n => Process.GetProcessesByName(n))
                        .ToList();

                    if (existingProcesses.Any())
                    {
                        Log.Warning("[Orchestrator] Found external {Name} instance(s) — stopping to ensure portable ownership.", instance.Name);
                        foreach (var p in existingProcesses)
                        {
                            try { p.Kill(true); } catch { }
                        }
                    }
                    
                    instance.Status = ServiceStatus.Stopped;
                    if (instance.Type == RuntimeServiceType.Mailpit)
                        instance.Notes = "Mailpit service is not running.";
                    instance.ProcessId = null;
                    InstanceStateChanged?.Invoke(instance);
                }
            }

            _metricsTimer = new Timer(PollMetrics, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        }

        private async Task InitializeBinaryPaths()
        {
            _resolvedRuntimes.Clear();
            var allTypes = Enum.GetValues<RuntimeServiceType>();
            
            foreach (var type in allTypes)
            {
                var best = await _runtimeLocator.FindBestAsync(type);
                
                // If missing, and it's a critical runtime, try to auto-install default version
                if (best == null && IsCriticalRuntime(type))
                {
                    Log.Warning("{Type} not found. Attempting auto-installation...", type);
                    var defaultVersion = GetDefaultVersion(type);
                    if (await _runtimeInstaller.InstallAsync(type, defaultVersion))
                    {
                        best = await _runtimeLocator.FindBestAsync(type);
                    }
                }

                if (best != null)
                {
                    _resolvedRuntimes[type] = best;
                    Log.Information("Found best binary for {Type}: {Path} ({Source})", type, best.ExecutablePath, best.InstallSource);
                }
            }
        }

        private bool IsCriticalRuntime(RuntimeServiceType type)
        {
            return type == RuntimeServiceType.Mailpit || 
                   type == RuntimeServiceType.Nginx || 
                   type == RuntimeServiceType.Apache ||
                   type == RuntimeServiceType.PhpFpm;
        }

        private string GetDefaultVersion(RuntimeServiceType type)
        {
            return type switch
            {
                RuntimeServiceType.PhpFpm => "8.5.0",
                RuntimeServiceType.Nginx => "1.26.0",
                RuntimeServiceType.Apache => "2.4.67",
                RuntimeServiceType.MySQL => "8.4.0",
                _ => "latest"
            };
        }

        public async Task<RuntimeMetadata?> GetRuntimeMetadataAsync(RuntimeServiceType type)
        {
            if (_resolvedRuntimes.TryGetValue(type, out var meta)) return meta;
            return await _runtimeLocator.FindBestAsync(type);
        }

        public string? GetBinaryPath(RuntimeServiceType type)
        {
            if (!_resolvedRuntimes.TryGetValue(type, out var meta))
            {
                // Try one last check on demand
                var best = _runtimeLocator.FindBestAsync(type).GetAwaiter().GetResult();
                if (best != null) _resolvedRuntimes[type] = best;
            }
            return _resolvedRuntimes.TryGetValue(type, out var result) ? result.ExecutablePath : null;
        }

        private void SeedDefaultServices()
        {
            // Enforce stable startup/display order
            AddService(RuntimeServiceType.Apache);
            AddService(RuntimeServiceType.MySQL);
            AddService(RuntimeServiceType.Nginx);
            AddService(RuntimeServiceType.Mailpit);
            AddService(RuntimeServiceType.Redis);
            AddService(RuntimeServiceType.PhpFpm);
        }

        public RuntimeServiceInstance AddService(RuntimeServiceType type, string? customName = null, int? customPort = null)
        {
            var meta = RuntimeServiceMeta.Data[type];
            
            // Use detected version if available, otherwise use default from metadata
            string version = _resolvedRuntimes.TryGetValue(type, out var runtimeMeta) 
                ? runtimeMeta.Version 
                : meta.Version;

            var instance = new RuntimeServiceInstance
            {
                Id = Guid.NewGuid(),
                Type = type,
                Name = customName ?? meta.Label,
                Version = version,
                Port = customPort ?? meta.DefaultPort,
                DataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", type.ToString().ToLower()),
                Status = ServiceStatus.Stopped
            };

            lock (InstancesLock)
            {
                _instances.Add(instance);
            }
            RegistryUpdated?.Invoke();
            return instance;
        }

        public void RemoveService(Guid instanceId)
        {
            lock (InstancesLock)
            {
                var inst = _instances.FirstOrDefault(i => i.Id == instanceId);
                if (inst != null) _instances.Remove(inst);
            }
            RegistryUpdated?.Invoke();
        }

        public async Task<bool> StartAsync(Guid instanceId)
        {
            var inst = Instances.FirstOrDefault(i => i.Id == instanceId);
            if (inst == null || inst.Status == ServiceStatus.Running) return false;

            await _startupLock.WaitAsync();
            try
            {
                inst.Status = ServiceStatus.Starting;
                InstanceStateChanged?.Invoke(inst);

                var binPath = GetBinaryPath(inst.Type);
                
                // ── 0. DYNAMIC INSTALLATION IF MISSING ──────────────────────────
                if (binPath == null && IsCriticalRuntime(inst.Type))
                {
                    inst.Notes = "Downloading runtime...";
                    InstanceStateChanged?.Invoke(inst);
                    
                    var progress = new Progress<double>(p => {
                        inst.Notes = $"Downloading... {p:P0}";
                        InstanceStateChanged?.Invoke(inst);
                    });

                    if (await _runtimeInstaller.InstallAsync(inst.Type, GetDefaultVersion(inst.Type), progress))
                    {
                        await InitializeBinaryPaths();
                        binPath = GetBinaryPath(inst.Type);
                    }
                    else
                    {
                        inst.Status = ServiceStatus.Error;
                        inst.Notes = "Download/Installation failed.";
                        inst.DiagnosticLogs = _runtimeInstaller.GetLastReport();
                        InstanceStateChanged?.Invoke(inst);
                        return false;
                    }
                }

                if (binPath == null)
                {
                    inst.Status = ServiceStatus.Error;
                    inst.Notes = "Binary missing and download failed.";
                    inst.DiagnosticLogs = _runtimeInstaller.GetLastReport();
                    InstanceStateChanged?.Invoke(inst);
                    return false;
                }

                // Resolve runtime metadata and config
                _resolvedRuntimes.TryGetValue(inst.Type, out var meta);
                if (meta == null) meta = new RuntimeMetadata { ExecutablePath = binPath, Type = inst.Type };
                
                inst.Version = meta.Version ?? GetDefaultVersion(inst.Type);
                InstanceStateChanged?.Invoke(inst);
                
                // ── 1. PORT CONFLICT AUTO-RESOLUTION ──────────────────────────
                if (inst.Port > 0)
                {
                    var ownerPid = _processManager.GetPidUsingPort(inst.Port);
                    if (ownerPid.HasValue)
                    {
                        // For primary web servers, don't auto-shift. Require the user to fix the conflict.
                        if (inst.Type == RuntimeServiceType.Apache || inst.Type == RuntimeServiceType.Nginx)
                        {
                            Log.Error("[Orchestrator] Port {Port} is occupied by PID {Pid}. Cannot start {Name}.", inst.Port, ownerPid, inst.Name);
                            inst.Status = ServiceStatus.Error;
                            inst.Notes = $"Port {inst.Port} is already in use by another process.";
                            InstanceStateChanged?.Invoke(inst);
                            return false;
                        }

                        Log.Warning("[Orchestrator] Port {Port} is occupied. Searching for free port...", inst.Port);
                        var oldPort = inst.Port;
                        inst.Port = GetFreePort(inst.Port);
                        inst.Notes = $"Port conflict ({oldPort} -> {inst.Port})";
                        InstanceStateChanged?.Invoke(inst);
                        Log.Information("[Orchestrator] Resolved port conflict for {Name}: {Old} -> {New}", inst.Name, oldPort, inst.Port);
                    }
                }

                var configPath = _configGenerator.GenerateAndSave(inst, meta);
                var args       = BuildArgs(inst, meta, configPath);
                var workDir    = Path.GetDirectoryName(binPath)!;

                // ── 2.5 DATABASE AUTO-INITIALIZATION ──────────────────────────────
                if (inst.Type == RuntimeServiceType.MySQL || inst.Type == RuntimeServiceType.MariaDB)
                {
                    await EnsureDatabaseInitialized(inst, binPath);
                }

                // ── 2.7 APACHE DEPENDENCY VALIDATION ──────────────────────────────
                if (inst.Type == RuntimeServiceType.Apache)
                {
                    VerifyApacheDependencies(meta);

                    // Auto-start PHP-FPM if not running
                    var php = Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.PhpFpm);
                    if (php != null && php.Status != ServiceStatus.Running)
                    {
                        Log.Information("[Orchestrator] Auto-starting PHP-FPM dependency for Apache...");
                        _ = StartAsync(php.Id); // Fire and forget or await? Let's not block Apache startup too much
                    }
                }

                // ── 3. REAL PROCESS LAUNCH ────────────────────────────────────────
                
                // Pre-launch validation for Apache
                if (inst.Type == RuntimeServiceType.Apache)
                {
                    inst.Notes = "Validating config...";
                    InstanceStateChanged?.Invoke(inst);

                    // httpd -t is a transient command, use RunCommandAsync to avoid "immediate crash" detection
                    var result = await _processManager.RunCommandAsync(binPath, $"-t -d \"{meta.RootDir}\" -f \"{configPath}\"", workDir);
                    
                    if (result.ExitCode != 0)
                    {
                        throw new Exception($"Apache config error: {result.StdErr}");
                    }
                }

                // Pre-launch validation for Nginx
                if (inst.Type == RuntimeServiceType.Nginx)
                {
                    inst.Notes = "Validating Nginx config...";
                    InstanceStateChanged?.Invoke(inst);

                    var result = await _processManager.RunCommandAsync(binPath, $"-t -c \"{configPath}\"", workDir);
                    if (result.ExitCode != 0)
                    {
                        throw new Exception($"Nginx config error: {result.StdErr}");
                    }
                    
                    // Cleanup any stale Nginx workers if the port is taken by 'nginx'
                    var currentOwner = _processManager.GetPidUsingPort(inst.Port);
                    if (currentOwner.HasValue)
                    {
                        try 
                        {
                            var p = Process.GetProcessById(currentOwner.Value);
                            if (p.ProcessName.Contains("nginx", StringComparison.OrdinalIgnoreCase))
                            {
                                Log.Warning("[Orchestrator] Found stale Nginx process {Pid} on port {Port}. Cleaning up...", currentOwner.Value, inst.Port);
                                p.Kill(true);
                                await Task.Delay(500); // Wait for socket release
                            }
                        }
                        catch { }
                    }
                }

                // ── 2.8 NGINX RUNTIME BOOTSTRAP ───────────────────────────────────
                if (inst.Type == RuntimeServiceType.Nginx)
                {
                    var runtimeRoot = meta.RootDir ?? Path.GetDirectoryName(binPath) ?? "";
                    EnsureNginxDirectoriesExist(runtimeRoot);
                }

                Log.Warning("[Orchestrator] LAUNCHING NGINX WITH CUSTOM CONFIG:");
                Log.Warning("[Orchestrator] Executable: {Path}", binPath);
                Log.Warning("[Orchestrator] Arguments: {Args}", args);
                Log.Warning("[Orchestrator] WorkingDir: {Dir}", workDir);

                var proc = await _processManager.StartProcessAsync(binPath, args, workDir);
                _runningProcesses[instanceId] = proc;
                inst.ProcessId = proc.Id;

                // ── 4. HEALTH VALIDATION ──────────────────────────
                // Databases can take longer to initialize, give them up to 45s
                var timeout = (inst.Type == RuntimeServiceType.MySQL || inst.Type == RuntimeServiceType.MariaDB) ? 45_000 : 30_000;
                
                inst.Notes = "Waiting for port...";
                InstanceStateChanged?.Invoke(inst);
                
                var portReady = await WaitForPortAsync("127.0.0.1", inst.Port, timeoutMs: timeout);

                // Special Case: Mailpit dual-port validation (SMTP + Web UI)
                if (portReady && inst.Type == RuntimeServiceType.Mailpit)
                {
                    inst.Notes = "Waiting for Web UI (8025)...";
                    InstanceStateChanged?.Invoke(inst);
                    portReady = await WaitForPortAsync("127.0.0.1", 8025, timeoutMs: 10_000);
                }

                if (portReady)
                {
                    inst.Status = ServiceStatus.Running;
                    inst.LastStarted = DateTime.Now;
                    inst.Notes = "Healthy";
                    InstanceStateChanged?.Invoke(inst);
                    return true;
                }
                else
                {
                    // Identify the offender if the port is taken
                    var currentOwner = _processManager.GetPidUsingPort(inst.Port);
                    var ownerInfo = "Unknown Process";
                    if (currentOwner.HasValue)
                    {
                        ownerInfo = GetProcessNameSafely(currentOwner.Value);
                        ownerInfo = $"{ownerInfo} (PID {currentOwner.Value})";
                    }

                    // Check if process still alive but port not bound
                    if (_processManager.IsProcessRunning(proc.Id))
                    {
                        throw new Exception($"Service {inst.Type} is alive (PID {proc.Id}) but port {inst.Port} failed to bind. " +
                                          $"Port is currently occupied by: {ownerInfo}. Check {inst.Type} error logs for details.");
                    }
                    throw new Exception($"Service {inst.Type} failed to start or port {inst.Port} failed to bind. Occupied by: {ownerInfo}.");
                }
            }
            catch (Exception ex)
            {
                inst.Status = ServiceStatus.Error;
                inst.Notes = ex.Message;
                inst.DiagnosticLogs = $"[STARTUP FAILURE] {DateTime.Now}\n{ex.Message}\n\n" +
                                     $"Stack Trace:\n{ex.StackTrace}";
                
                InstanceStateChanged?.Invoke(inst);
                Log.Error(ex, "Failed to start {Type}", inst.Type);
                return false;
            }
            finally
            {
                _startupLock.Release();
            }
        }

        private string GetProcessNameSafely(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return p.ProcessName;
            }
            catch (Exception ex)
            {
                // Fallback for system processes or access denied
                Log.Debug(ex, "Could not get process name for PID {Pid} via standard API", pid);
                return "System/Protected Process";
            }
        }

        public async Task<bool> RestartAsync(Guid instanceId)
        {
            await StopAsync(instanceId);
            return await StartAsync(instanceId);
        }

        public void OpenConfig(Guid instanceId)
        {
            var inst = Instances.FirstOrDefault(i => i.Id == instanceId);
            if (inst != null && File.Exists(inst.ConfigPath))
                Process.Start("explorer.exe", $"/select,\"{inst.ConfigPath}\"");
            else if (inst != null)
                Process.Start("explorer.exe", $"\"{_configRoot}\"");
        }

        public void OpenLogs(Guid instanceId)
        {
            if (!Directory.Exists(_logsRoot)) Directory.CreateDirectory(_logsRoot);
            Process.Start("explorer.exe", $"\"{_logsRoot}\"");
        }

        public void OpenDataFolder(Guid instanceId)
        {
            var inst = Instances.FirstOrDefault(i => i.Id == instanceId);
            if (inst != null)
            {
                if (!Directory.Exists(inst.DataPath)) Directory.CreateDirectory(inst.DataPath);
                Process.Start("explorer.exe", $"\"{inst.DataPath}\"");
            }
        }

        public async Task<bool> StopAsync(Guid instanceId)
        {
            var inst = Instances.FirstOrDefault(i => i.Id == instanceId);
            if (inst == null) return false;

            inst.Status = ServiceStatus.Stopping;
            InstanceStateChanged?.Invoke(inst);

            if (_runningProcesses.TryGetValue(instanceId, out var proc))
            {
                try { proc.Kill(true); } catch { }
                _runningProcesses.Remove(instanceId);
            }
            else
            {
                // Fallback: kill by PID if managed process object is lost
                if (inst.ProcessId.HasValue)
                {
                    try { Process.GetProcessById(inst.ProcessId.Value).Kill(true); } catch { }
                }
            }

            inst.Status = ServiceStatus.Stopped;
            inst.ProcessId = null;
            InstanceStateChanged?.Invoke(inst);
            return true;
        }

        private async Task<bool> WaitForPortAsync(string host, int port, int timeoutMs)
        {
            if (port <= 0) return true; // Services without ports are assumed healthy immediately

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (IsPortListening(host, port)) return true;
                await Task.Delay(500);
            }
            return false;
        }

        private bool IsPortListening(string host, int port)
        {
            // 1. Check if the port is in LISTENING state via netstat-like check
            var pid = _processManager.GetPidUsingPort(port);
            if (pid == null) return false;

            // 2. Try to connect to verify it's actually accepting connections
            try
            {
                using var client = new TcpClient();
                var result = client.BeginConnect(host, port, null, null);
                var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(500));
                if (success) client.EndConnect(result);
                return success;
            }
            catch { return false; }
        }

        private int GetRealPid(RuntimeServiceType type)
        {
            if (RuntimeServiceMeta.Data.TryGetValue(type, out var meta))
            {
                foreach (var name in meta.ProcessNames)
                {
                    var procs = Process.GetProcessesByName(name);
                    if (procs.Length > 0) return procs[0].Id;
                }
            }
            return 0;
        }

        private int GetFreePort(int suggestedPort)
        {
            var port = suggestedPort;
            while (port < 65535)
            {
                if (_processManager.GetPidUsingPort(port) == null)
                {
                    // Double check with TCP client
                    if (!IsPortListening("127.0.0.1", port))
                        return port;
                }
                port++;
            }
            return suggestedPort; // Fallback
        }

        private void VerifyApacheDependencies(RuntimeMetadata meta)
        {
            var root = meta.RootDir ?? "";
            Log.Information("[Apache-Diagnostics] Starting deep validation for ServerRoot: {Root}", root);

            // 1. Check for VC++ Redistributable
            if (!IsVCRedistInstalled())
            {
                Log.Error("[Apache-Diagnostics] CRITICAL: Microsoft VC++ Redistributable 2015-2022 x64 is NOT detected.");
                throw new Exception("Apache requires Microsoft VC++ Redistributable 2015-2022 x64. Please install it to continue.");
            }
            Log.Information("[Apache-Diagnostics] VC++ Redistributable check passed.");

            // 2. Physical Structure Check
            var criticalFolders = new[] { "modules", "conf", "bin" };
            foreach (var folder in criticalFolders)
            {
                var folderPath = Path.Combine(root, folder);
                var exists = Directory.Exists(folderPath);
                Log.Information("[Apache-Diagnostics] Folder check: {Folder} -> {Status}", folder, exists ? "EXISTS" : "MISSING");
                if (!exists) throw new Exception($"Apache structural error: Folder '{folder}' is missing in {root}.");
            }

            // 3. Core DLL Verification (Dependencies of modules)
            var criticalDlls = new[] { "libhttpd.dll", "libapr-1.dll", "libapriconv-1.dll", "libaprutil-1.dll" };
            foreach (var dll in criticalDlls)
            {
                var dllPath = Path.Combine(root, "bin", dll);
                var exists = File.Exists(dllPath);
                Log.Information("[Apache-Diagnostics] Library check: bin/{Dll} -> {Status} ({Size} bytes)", 
                    dll, exists ? "EXISTS" : "MISSING", exists ? new FileInfo(dllPath).Length : 0);
                if (!exists) throw new Exception($"Apache dependency error: '{dll}' is missing in bin folder.");
            }
            
            // 4. Module existence check for standard modules
            var standardModules = new[] { "mod_authz_core.so", "mod_dir.so", "mod_mime.so" };
            foreach (var mod in standardModules)
            {
                var modPath = Path.Combine(root, "modules", mod);
                var exists = File.Exists(modPath);
                Log.Information("[Apache-Diagnostics] Module check: modules/{Mod} -> {Status}", mod, exists ? "EXISTS" : "MISSING");
            }

            Log.Information("[Apache-Diagnostics] All structural and dependency checks passed for {Root}", root);
        }

        private bool IsVCRedistInstalled()
        {
            // Check for the most recent VC++ runtime DLL required by VS17/VS18
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var runtimeDll = Path.Combine(system32, "vcruntime140_1.dll");
            
            // vcruntime140_1.dll was introduced in later 14.x updates and is essential for VS2019/VS2022 builds
            return File.Exists(runtimeDll);
        }

        private string BuildArgs(RuntimeServiceInstance inst, RuntimeMetadata runtime, string confPath)
        {
            var binPath = runtime.ExecutablePath;
            var binDir  = Path.GetDirectoryName(binPath);
            
            switch (inst.Type)
            {
                case RuntimeServiceType.Nginx:
                    var prefix = (runtime.RootDir ?? Path.GetDirectoryName(runtime.ExecutablePath) ?? "").Replace("\\", "/");
                    var absConf = Path.GetFullPath(confPath).Replace("\\", "/");
                    return $"-p \"{prefix}/\" -c \"{absConf}\"";

                case RuntimeServiceType.Apache:
                    var serverRoot = runtime.RootDir ?? (binDir != null ? Path.GetFullPath(Path.Combine(binDir, "..")) : "");
                    return $"-d \"{serverRoot}\" -f \"{confPath}\"";

                case RuntimeServiceType.PhpFpm:
                    return $"-b 127.0.0.1:{inst.Port} -d cgi.fix_pathinfo=1 -c \"{confPath}\"";

                case RuntimeServiceType.MariaDB:
                case RuntimeServiceType.MySQL:
                    var dataDir = inst.DataPath;
                    if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);
                    
                    var extraArgs = "";
                    var bootstrapPath = Path.Combine(dataDir, "hostix_init.sql");
                    if (File.Exists(bootstrapPath))
                    {
                        extraArgs = $" --init-file=\"{bootstrapPath.Replace("\\", "/")}\"";
                    }

                    return $"--defaults-file=\"{confPath}\" --port={inst.Port} --datadir=\"{dataDir}\" --console{extraArgs}";

                case RuntimeServiceType.Redis:
                    return $"--port {inst.Port}";

                case RuntimeServiceType.Mailpit:
                    return $"--smtp 127.0.0.1:{inst.Port} --listen 127.0.0.1:8025";

                default:
                    return "";
            }
        }

        private async Task EnsureDatabaseInitialized(RuntimeServiceInstance inst, string binPath)
        {
            var dataDir = inst.DataPath;
            if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);

            if (inst.Type != RuntimeServiceType.MySQL && inst.Type != RuntimeServiceType.MariaDB) return;

            var systemDbPath = Path.Combine(dataDir, "mysql");
            var ibdataPath   = Path.Combine(dataDir, "ibdata1");

            // MySQL requires an ENTIRELY EMPTY directory for --initialize
            if (!Directory.Exists(systemDbPath) && !File.Exists(ibdataPath))
            {
                var files = Directory.GetFiles(dataDir);
                var dirs  = Directory.GetDirectories(dataDir);
                
                if (files.Length > 0 || dirs.Length > 0)
                {
                    Log.Warning("[Infrastructure] Data directory {Dir} is not empty but missing system DB. Clearing for initialization...", dataDir);
                    foreach (var f in files) File.Delete(f);
                    foreach (var d in dirs) Directory.Delete(d, true);
                }

                Log.Information("[Infrastructure] Initializing database data directory: {Dir}", dataDir);
                inst.Notes = "Initializing database...";
                InstanceStateChanged?.Invoke(inst);

                var initArgs = $"--initialize-insecure --datadir=\"{dataDir.Replace("\\", "/")}\" --console";
                var result = await _processManager.RunCommandAsync(binPath, initArgs, Path.GetDirectoryName(binPath)!);
                
                if (result.ExitCode == 0)
                {
                    // Create a bootstrap SQL file to set the password dynamically from the credentials manager
                    var (user, pass) = _credentialsManager.GetCredentials(inst.Type);
                    var bootstrapPath = Path.Combine(dataDir, "hostix_init.sql");
                    var sql = $"ALTER USER '{user}'@'localhost' IDENTIFIED BY '{pass}';\n" +
                              "FLUSH PRIVILEGES;";
                    await File.WriteAllTextAsync(bootstrapPath, sql);
                    Log.Information("[Infrastructure] Created bootstrap SQL to set {User} password.", user);
                }
                else
                {
                    Log.Error("[Infrastructure] Database initialization failed with exit code {Code}: {Err}", result.ExitCode, result.StdErr);
                }
            }
        }

        private void PollMetrics(object? state)
        {
            // Snapshot the collection to avoid "Collection was modified" exceptions
            var snapshot = Instances.ToList();
            foreach (var instance in snapshot)
            {
                if (instance.Status == ServiceStatus.Running && instance.ProcessId.HasValue)
                {
                    try
                    {
                        var proc = Process.GetProcessById(instance.ProcessId.Value);
                        instance.CpuUsage = "0%"; // Placeholder for real metrics
                        instance.RamUsage = $"{proc.WorkingSet64 / 1024 / 1024} MB";
                        
                        if (instance.LastStarted.HasValue)
                        {
                            var uptime = DateTime.Now - instance.LastStarted.Value;
                            instance.UptimeDisplay = uptime.TotalHours >= 1 
                                ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m" 
                                : $"{uptime.Minutes}m {uptime.Seconds}s";
                        }
                    }
                    catch { }
                }
            }
        }

        private void EnsureNginxDirectoriesExist(string runtimeRoot)
        {
            try
            {
                var dirs = new[]
                {
                    "logs",
                    "temp",
                    "temp/client_body_temp",
                    "temp/proxy_temp",
                    "temp/fastcgi_temp",
                    "temp/uwsgi_temp",
                    "temp/scgi_temp"
                };

                foreach (var dir in dirs)
                {
                    var path = Path.Combine(runtimeRoot, dir);
                    if (!Directory.Exists(path))
                    {
                        Log.Information("[Orchestrator] Bootstrapping Nginx directory: {Path}", path);
                        Directory.CreateDirectory(path);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Orchestrator] Failed to bootstrap Nginx directories in {Root}", runtimeRoot);
            }
        }
    }
}
