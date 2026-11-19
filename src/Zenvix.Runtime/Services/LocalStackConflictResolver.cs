using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Zenvix.Runtime.Services
{
    /// <summary>A port that is held by a process Zenvix does not own.</summary>
    public sealed record PortConflict(
        int Port,
        int Pid,
        string ProcessName,
        string? ExecutablePath,
        string StackName,
        IReadOnlyList<string> WindowsServices,
        bool IsProtected);

    public sealed record ConflictResolution(bool Freed, string Message);

    /// <summary>
    /// Detects other local dev stacks (XAMPP, WAMP, Laragon, Laravel Herd, MAMP, Local, AMPPS,
    /// standalone MySQL / MariaDB / Apache services, IIS ...) that hold a port Zenvix needs,
    /// and shuts them down so Zenvix services can take over.
    /// </summary>
    public interface ILocalStackConflictResolver
    {
        PortConflict? Detect(int port);
        Task<ConflictResolution> ResolveAsync(int port, CancellationToken ct = default);

        /// <summary>Kills leftover processes started by a previous Zenvix run (exe under the Zenvix folder).</summary>
        int KillStaleZenvixProcesses(IEnumerable<string> processNames);
    }

    public class LocalStackConflictResolver : ILocalStackConflictResolver
    {
        private readonly IProcessManager _processManager;
        private readonly string _zenvixRoot;

        // path fragment (lower-case) -> (stack name, controller processes that respawn services)
        private static readonly (string PathHint, string Stack, string[] Controllers)[] KnownStacks =
        {
            (@"\xampp",              "XAMPP",            new[] { "xampp-control" }),
            (@"\wamp",               "WampServer",       new[] { "wampmanager" }),
            (@"\laragon",            "Laragon",          new[] { "laragon" }),
            (@"\.config\herd",       "Laravel Herd",     new[] { "Herd" }),
            (@"\herd\",              "Laravel Herd",     new[] { "Herd" }),
            (@"\mamp",               "MAMP",             new[] { "MAMP", "MAMP PRO" }),
            (@"\local\lightning",    "Local (Flywheel)", new[] { "Local" }),
            (@"\ampps",              "AMPPS",            new[] { "Ampps" }),
            (@"\uwamp",              "UwAmp",            new[] { "UwAmp" }),
            (@"\easyphp",            "EasyPHP",          new[] { "EasyPHP-Devserver", "run-devserver" }),
            (@"\usbwebserver",       "USBWebserver",     new[] { "usbwebserver" }),
        };

        // Never kill these blindly – they are the OS or tools the user certainly wants running.
        private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "svchost", "lsass", "services", "wininit", "csrss", "smss", "winlogon",
            "com.docker.backend", "com.docker.proxy", "vpnkit", "wslrelay", "wslhost", "vmmem", "vmmemWSL",
            "explorer", "Zenvix",
        };

        // Services that hold port 80/443 through http.sys (owner PID shows up as 4 = System).
        private static readonly string[] HttpSysServices = { "W3SVC", "WAS" };

        public LocalStackConflictResolver(IProcessManager processManager)
        {
            _processManager = processManager;
            _zenvixRoot = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\') + "\\";
        }

        public PortConflict? Detect(int port)
        {
            if (port <= 0) return null;
            var pid = _processManager.GetPidUsingPort(port);
            if (pid == null) return null;
            return Describe(port, pid.Value);
        }

        private PortConflict Describe(int port, int pid)
        {
            string name = "Unknown";
            try { using var p = Process.GetProcessById(pid); name = p.ProcessName; } catch { }

            var path = GetProcessPath(pid);
            var services = GetServicesForPid(pid);

            // http.sys (IIS & friends) – PID 4
            if (pid == 4)
            {
                var running = HttpSysServices.Where(IsServiceRunning).ToList();
                return new PortConflict(port, pid, "System (http.sys)", null,
                    running.Count > 0 ? "IIS / Windows HTTP Server" : "Windows HTTP Server (http.sys)",
                    running, IsProtected: running.Count == 0);
            }

            var stack = IdentifyStack(name, path, services);
            // svchost hosts many unrelated services – stopping all of them would hurt the system.
            var isProtected = name.Equals("svchost", StringComparison.OrdinalIgnoreCase)
                              || (ProtectedProcesses.Contains(name) && services.Count == 0);
            return new PortConflict(port, pid, name, path, stack, services, isProtected);
        }

        private string IdentifyStack(string processName, string? path, IReadOnlyList<string> services)
        {
            if (path != null)
            {
                if (path.StartsWith(_zenvixRoot, StringComparison.OrdinalIgnoreCase)) return "Zenvix (stale)";
                var lower = path.ToLowerInvariant();
                foreach (var s in KnownStacks)
                    if (lower.Contains(s.PathHint)) return s.Stack;
            }

            var svc = services.FirstOrDefault();
            var friendly = processName.ToLowerInvariant() switch
            {
                "mysqld" => "MySQL",
                "mariadbd" => "MariaDB",
                "httpd" => "Apache",
                "nginx" => "Nginx",
                "php-cgi" or "php" => "PHP",
                "postgres" => "PostgreSQL",
                "redis-server" => "Redis",
                "mongod" => "MongoDB",
                "mailpit" or "mailhog" => "Mail catcher",
                "com.docker.backend" or "com.docker.proxy" or "vpnkit" or "wslrelay" => "Docker / WSL",
                _ => processName,
            };
            return svc != null ? $"{friendly} (Windows service '{svc}')" : friendly;
        }

        public async Task<ConflictResolution> ResolveAsync(int port, CancellationToken ct = default)
        {
            var conflict = Detect(port);
            if (conflict == null) return new ConflictResolution(true, "");

            if (conflict.IsProtected)
            {
                var msg = $"Port {port} is held by {conflict.StackName} (PID {conflict.Pid}), which Zenvix will not stop automatically.";
                Log.Warning("[ConflictResolver] {Msg}", msg);
                return new ConflictResolution(false, msg);
            }

            Log.Warning("[ConflictResolver] Port {Port} held by {Stack} – {Proc} PID {Pid} ({Path}) services=[{Svc}]",
                port, conflict.StackName, conflict.ProcessName, conflict.Pid, conflict.ExecutablePath ?? "?",
                string.Join(",", conflict.WindowsServices));

            var elevatedCommands = new List<string>();

            // 1. Close the stack's controller apps first so they don't respawn what we kill.
            var stack = KnownStacks.Where(s => s.Stack == conflict.StackName).SelectMany(s => s.Controllers).Distinct();
            foreach (var controller in stack)
            {
                foreach (var p in Process.GetProcessesByName(controller))
                {
                    if (!TryKill(p)) elevatedCommands.Add($"taskkill /F /T /PID {p.Id}");
                }
            }

            // 2. Windows services hosted in that process (or IIS for http.sys).
            foreach (var svc in conflict.WindowsServices)
                elevatedCommands.Add($"net stop \"{svc}\" /y");

            // 3. The process itself.
            if (conflict.Pid != 4 && conflict.WindowsServices.Count == 0)
            {
                try
                {
                    using var p = Process.GetProcessById(conflict.Pid);
                    if (!TryKill(p)) elevatedCommands.Add($"taskkill /F /T /PID {conflict.Pid}");
                }
                catch (ArgumentException) { /* already gone */ }
            }

            if (elevatedCommands.Count > 0)
            {
                if (!await RunElevatedAsync(elevatedCommands, ct))
                {
                    return new ConflictResolution(false,
                        $"Port {port} is used by {conflict.StackName}. Administrator permission is needed to stop it – please allow the prompt or stop it manually.");
                }
            }

            // 4. Wait for the socket to be released.
            for (int i = 0; i < 30 && !ct.IsCancellationRequested; i++)
            {
                if (_processManager.GetPidUsingPort(port) == null)
                {
                    var ok = $"Stopped {conflict.StackName} that was using port {port}.";
                    Log.Information("[ConflictResolver] {Msg}", ok);
                    return new ConflictResolution(true, ok);
                }
                await Task.Delay(300, ct);
            }

            // Something else (or a respawned child) still has it.
            var still = Detect(port);
            return new ConflictResolution(false,
                $"Port {port} is still in use by {still?.StackName ?? conflict.StackName} (PID {still?.Pid ?? conflict.Pid}).");
        }

        public int KillStaleZenvixProcesses(IEnumerable<string> processNames)
        {
            int killed = 0;
            foreach (var name in processNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    var path = GetProcessPath(p.Id);
                    if (path != null && path.StartsWith(_zenvixRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryKill(p)) killed++;
                    }
                    p.Dispose();
                }
            }
            if (killed > 0) Log.Information("[ConflictResolver] Cleaned up {Count} stale Zenvix process(es).", killed);
            return killed;
        }

        // ── helpers ────────────────────────────────────────────────────────────

        private static bool TryKill(Process p)
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
                return true;
            }
            catch (Win32Exception) { return false; }          // access denied → needs elevation
            catch (InvalidOperationException) { return true; } // already exited
        }

        private static async Task<bool> RunElevatedAsync(IReadOnlyList<string> commands, CancellationToken ct)
        {
            var script = string.Join(" & ", commands);
            Log.Information("[ConflictResolver] Running {Mode}: {Script}", IsElevated() ? "elevated" : "with UAC", script);
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", $"/c {script}")
                {
                    UseShellExecute = true,
                    Verb = IsElevated() ? "" : "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                await proc.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Log.Warning("[ConflictResolver] User declined the UAC prompt.");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ConflictResolver] Elevated stop failed.");
                return false;
            }
        }

        private static bool IsElevated()
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static IReadOnlyList<string> GetServicesForPid(int pid)
        {
            if (pid <= 4) return Array.Empty<string>();
            try
            {
                var psi = new ProcessStartInfo("tasklist", $"/svc /fi \"PID eq {pid}\" /fo csv /nh")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                var output = p?.StandardOutput.ReadToEnd() ?? "";
                p?.WaitForExit(5000);
                // "mysqld.exe","1234","MySQL80"   or   "x.exe","1234","N/A"
                var line = output.Split('\n').FirstOrDefault(l => l.Contains($"\"{pid}\""));
                if (line == null) return Array.Empty<string>();
                var cols = line.Trim().Split("\",\"");
                if (cols.Length < 3) return Array.Empty<string>();
                var svc = cols[2].Trim('"', '\r', ' ');
                if (svc.Equals("N/A", StringComparison.OrdinalIgnoreCase) || svc.Length == 0) return Array.Empty<string>();
                return svc.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            catch { return Array.Empty<string>(); }
        }

        private static bool IsServiceRunning(string service)
        {
            try
            {
                var psi = new ProcessStartInfo("sc", $"query \"{service}\"")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                var output = p?.StandardOutput.ReadToEnd() ?? "";
                p?.WaitForExit(5000);
                return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // Works across elevation boundaries, unlike Process.MainModule.
        private static string? GetProcessPath(int pid)
        {
            if (!OperatingSystem.IsWindows() || pid <= 4) return null;
            var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(handle); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
