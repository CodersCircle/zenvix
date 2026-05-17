using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Hostix.Core.Models;
using Hostix.Core.Services;
using Serilog;

namespace Hostix.Runtime.Services
{
    public interface IServiceOrchestrator
    {
        Task<bool> StartAsync(ServiceType type);
        Task<bool> StopAsync(ServiceType type);
        Task<bool> RestartAsync(ServiceType type);
        bool IsRunning(ServiceType type);
        string? GetBinaryPath(ServiceType type);

        /// <summary>
        /// Checks if any managed service is already running on the machine (before Hostix started it).
        /// Returns list of types that were found running and then forcibly stopped.
        /// </summary>
        Task<List<ServiceType>> DetectAndStopExternalInstancesAsync();
    }

    public class ServiceOrchestrator : IServiceOrchestrator
    {
        private readonly IProcessManager _processManager;
        private readonly IRuntimeLocator _runtimeLocator;
        private readonly Dictionary<ServiceType, Process> _processes = new();
        private readonly string _binRoot;

        // Maps ServiceType → (binary filename, startup args, process names, expected port)
        private static readonly Dictionary<ServiceType, (string Binary, string Args, string[] ProcessNames, int Port)> _binaryMap = new()
        {
            [ServiceType.Nginx]    = ("nginx.exe",        "",                                                              new[] { "nginx" },               80),
            [ServiceType.Apache]   = ("httpd.exe",         "-k start",                                                     new[] { "httpd", "apache" },     8080),
            [ServiceType.MariaDB]  = ("mysqld.exe",        "--defaults-file=conf\\my.ini",                                 new[] { "mysqld", "mariadbd" },  3306),
            [ServiceType.MySQL]    = ("mysqld.exe",        "--defaults-file=conf\\mysql.ini --port=3307",                  new[] { "mysqld" },              3307),
            [ServiceType.Postgres] = ("pg_ctl.exe",        "start -D data",                                                new[] { "postgres", "pg_ctl" }, 5432),
            [ServiceType.Redis]    = ("redis-server.exe",  "conf\\redis.conf",                                             new[] { "redis-server" },        6379),
            [ServiceType.Mailpit]  = ("mailpit.exe",       "--smtp 0.0.0.0:1025 --ui-bind-addr 0.0.0.0:8025",             new[] { "mailpit" },             1025),
        };

        public ServiceOrchestrator(IProcessManager processManager, IRuntimeLocator runtimeLocator)
        {
            _processManager = processManager;
            _runtimeLocator = runtimeLocator;
            _binRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtimes");
        }

        public string? GetBinaryPath(ServiceType type)
        {
            // Map ServiceType to RuntimeServiceType
            var runtimeType = type switch
            {
                ServiceType.Nginx    => RuntimeServiceType.Nginx,
                ServiceType.Apache   => RuntimeServiceType.Apache,
                ServiceType.PhpFpm   => RuntimeServiceType.PhpFpm,
                ServiceType.MariaDB  => RuntimeServiceType.MariaDB,
                ServiceType.MySQL    => RuntimeServiceType.MySQL,
                ServiceType.Postgres => RuntimeServiceType.PostgreSQL,
                ServiceType.Redis    => RuntimeServiceType.Redis,
                ServiceType.Mailpit  => RuntimeServiceType.Mailpit,
                _                    => (RuntimeServiceType?)null
            };

            if (runtimeType.HasValue)
            {
                var best = Task.Run(() => _runtimeLocator.FindBestAsync(runtimeType.Value)).Result;
                if (best != null && File.Exists(best.ExecutablePath))
                {
                    return best.ExecutablePath;
                }
            }

            // Fallback to legacy path logic
            if (!_binaryMap.TryGetValue(type, out var info)) return null;
            var fullPath = Path.Combine(_binRoot, type.ToString().ToLower(), info.Binary);
            if (File.Exists(fullPath)) return fullPath;
            
            // Deeper fallback for bin subfolder
            var binSubPath = Path.Combine(_binRoot, type.ToString().ToLower(), "bin", info.Binary);
            return File.Exists(binSubPath) ? binSubPath : null;
        }

        // ── IsRunning: validate BOTH process name AND port ─────────────────────
        public bool IsRunning(ServiceType type)
        {
            if (!_binaryMap.TryGetValue(type, out var info)) return false;

            // 1. Check our tracked process is alive
            var processAlive = _processes.TryGetValue(type, out var proc)
                            && proc != null && !proc.HasExited;

            // 2. Also check OS-level process name (external instances)
            if (!processAlive)
                processAlive = info.ProcessNames.Any(n => Process.GetProcessesByName(n).Length > 0);

            if (!processAlive) return false;

            // 3. Validate the port is actually listening
            return IsPortListening("127.0.0.1", info.Port);
        }

        // ── StartAsync: real launch with port-based health validation ───────────
        public async Task<bool> StartAsync(ServiceType type)
        {
            var binPath = GetBinaryPath(type);

            if (binPath == null)
            {
                // ── CRITICAL: Do NOT simulate. Binary is not installed. ──────────
                // Return false so the UI shows Error/NotInstalled, NOT Running.
                Log.Warning("[REAL] {Service} binary not found at {BinRoot}. Cannot start — binary not installed.",
                    type, Path.Combine(_binRoot, type.ToString().ToLower()));
                return false;
            }

            if (!_binaryMap.TryGetValue(type, out var info)) return false;

            try
            {
                var workDir = Path.GetDirectoryName(binPath)!;
                Log.Information("[REAL] Launching {Service}: {BinPath} {Args}", type, binPath, info.Args);

                var proc = await _processManager.StartProcessAsync(binPath, info.Args, workDir);
                _processes[type] = proc;
                Log.Information("[REAL] {Service} launched (PID: {Pid}). Waiting for port {Port}...",
                    type, proc.Id, info.Port);

                // ── Wait for port to actually start listening (up to 30s) ────────
                var portReady = await WaitForPortAsync("127.0.0.1", info.Port, timeoutMs: 30_000);

                if (!portReady)
                {
                    Log.Error("[REAL] {Service} launched but port {Port} never became ready. Stopping.", type, info.Port);
                    // Kill the hung process
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    _processes.Remove(type);
                    return false;
                }

                Log.Information("[REAL] {Service} CONFIRMED running on port {Port} (PID: {Pid}).", type, info.Port, proc.Id);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[REAL] Failed to launch {Service}", type);
                return false;
            }
        }

        // ── StopAsync: kill process and wait for port to close ──────────────────
        public async Task<bool> StopAsync(ServiceType type)
        {
            // Stop our tracked process
            if (_processes.TryGetValue(type, out var proc) && !proc.HasExited)
            {
                _processManager.StopProcess(proc.Id);
                _processes.Remove(type);
            }

            // Also kill any externally-detected instances
            if (_binaryMap.TryGetValue(type, out var info))
            {
                foreach (var procName in info.ProcessNames)
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    try { p.Kill(true); } catch { /* already dead */ }
                }

                // Wait for port to actually close (up to 8 seconds)
                await WaitForPortCloseAsync("127.0.0.1", info.Port, timeoutMs: 8_000);
                Log.Information("[REAL] {Service} stopped. Port {Port} closed.", type, info.Port);
            }

            return true;
        }

        public async Task<bool> RestartAsync(ServiceType type)
        {
            await StopAsync(type);
            await Task.Delay(500);
            return await StartAsync(type);
        }

        // ── External instance detection ─────────────────────────────────────────
        public async Task<List<ServiceType>> DetectAndStopExternalInstancesAsync()
        {
            var stopped = new List<ServiceType>();

            foreach (var kvp in _binaryMap)
            {
                var type = kvp.Key;
                var info = kvp.Value;

                var runningProcs = info.ProcessNames
                    .SelectMany(n => Process.GetProcessesByName(n))
                    .ToList();

                if (runningProcs.Any())
                {
                    Log.Warning("Found external {ServiceType} instance(s) — stopping before Hostix takes control.", type);
                    foreach (var p in runningProcs)
                    {
                        try { p.Kill(true); Log.Information("Stopped external {ServiceType} (PID: {Pid})", type, p.Id); }
                        catch (Exception ex) { Log.Error(ex, "Could not stop external {ServiceType} (PID: {Pid})", type, p.Id); }
                    }
                    stopped.Add(type);
                    await Task.Delay(200);
                }
            }

            return stopped;
        }

        // ── Port utilities ──────────────────────────────────────────────────────

        /// <summary>
        /// Checks if a TCP port is actively listening right now.
        /// </summary>
        private static bool IsPortListening(string host, int port)
        {
            try
            {
                using var client = new TcpClient();
                var result = client.BeginConnect(host, port, null, null);
                var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(300));
                if (!success) return false;
                client.EndConnect(result);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Polls until the port starts listening or timeout expires.
        /// Returns true if port became ready within timeout.
        /// </summary>
        private static async Task<bool> WaitForPortAsync(string host, int port, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            Log.Information("[PORT] Waiting for {Host}:{Port} to become available...", host, port);

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (IsPortListening(host, port))
                {
                    Log.Information("[PORT] {Host}:{Port} is now listening (after {Ms}ms).", host, port, sw.ElapsedMilliseconds);
                    return true;
                }
                await Task.Delay(500);
            }

            Log.Warning("[PORT] Timeout: {Host}:{Port} never became available after {TimeoutMs}ms.", host, port, timeoutMs);
            return false;
        }

        /// <summary>
        /// Polls until the port stops listening or timeout expires.
        /// </summary>
        private static async Task WaitForPortCloseAsync(string host, int port, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!IsPortListening(host, port)) return;
                await Task.Delay(300);
            }
        }
    }
}
