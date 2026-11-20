using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Zenvix.Core.Models;
using Zenvix.Runtime.Services;

namespace Zenvix.Modules.Scaling
{
    public enum PoolKind { Php, Node }

    /// <summary>Saved per-project settings.</summary>
    public sealed class ScalingSettings
    {
        public int MinInstances { get; set; } = 1;
        public int MaxInstances { get; set; } = 5;
        /// <summary>Requests per minute one instance is expected to handle.</summary>
        public int CapacityPerInstance { get; set; } = 60;
        public bool AutoScale { get; set; } = true;
        public int BalancerPort { get; set; }
    }

    public sealed class ScaledInstance
    {
        public string Name { get; init; } = "";
        public int Port { get; init; }
        public Process? Process { get; set; }
        public int? ServerPid { get; set; }
        public string Status { get; set; } = "starting";
        public DateTime Started { get; } = DateTime.Now;
        public double CpuPercent { get; set; }
        public double RamMb { get; set; }
        public int Requests60 { get; set; }
        internal TimeSpan LastCpu;
        internal DateTime LastSample = DateTime.Now;
        internal readonly ConcurrentQueue<DateTime> Hits = new();
        public string Id => (ServerPid ?? Process?.Id)?.ToString() ?? "—";
    }

    /// <summary>One project's own load balancer (an Nginx server on its own local port) and instance pool.</summary>
    public sealed class ScalingPool
    {
        public Website Website { get; init; } = null!;
        public PoolKind Kind { get; init; }
        public ScalingSettings Settings { get; init; } = new();
        public List<ScaledInstance> Instances { get; } = new();
        public List<string> Events { get; } = new();
        public bool IsRunning { get; set; }
        public int SimulatedRps { get; set; }
        public int SimulatedSent { get; set; }
        public int SimulatedFailed { get; set; }
        public DateTime LastScale { get; set; } = DateTime.MinValue;
        public DateTime ManualHoldUntil { get; set; } = DateTime.MinValue;
        public string BalancerUrl => $"http://127.0.0.1:{Settings.BalancerPort}/";
        internal readonly object Gate = new();
        internal long LogOffset;
        internal readonly ConcurrentQueue<DateTime> AllHits = new();
        internal int NextIndex = 1;
        internal CancellationTokenSource? SimCts;
        /// <summary>One scaling action at a time per project.</summary>
        internal readonly SemaphoreSlim ScaleLock = new(1, 1);

        public string Slug => Regex.Replace(Website.Domain.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
    }

    public interface IScalingManager
    {
        IReadOnlyList<ScalingPool> Pools { get; }
        /// <summary>Creates a pool for every PHP or Node project that does not have one yet.</summary>
        void Refresh();
        Task<bool> StartAsync(ScalingPool pool);
        Task StopAsync(ScalingPool pool);
        Task ScaleToAsync(ScalingPool pool, int count, string reason);
        void ApplySettings(ScalingPool pool, int min, int max, int capacity, bool auto);
        void SetTraffic(ScalingPool pool, int requestsPerSecond);
        void StopAll();
    }

    /// <summary>
    /// Local auto scaling: every project gets its own Nginx load balancer on a 127.0.0.1 port, in front of
    /// N instances of the project (php-cgi workers for PHP, the dev/start command with PORT=... for Node).
    /// Requests are counted from the balancer's access log; instances are added when traffic or CPU exceeds
    /// capacity and removed after a cool-down. Everything runs on this machine only.
    /// </summary>
    public sealed class ScalingManager : IScalingManager
    {
        private const int BasePort = 18080;
        private static readonly TimeSpan ScaleDownCooldown = TimeSpan.FromSeconds(30);

        private readonly IWebsiteOrchestrator _websites;
        private readonly IServicesOrchestrator _services;
        private readonly IProcessManager _processes;
        private readonly string _base = AppDomain.CurrentDomain.BaseDirectory;
        private readonly List<ScalingPool> _pools = new();
        private readonly Dictionary<Guid, ScalingSettings> _saved;
        private readonly Timer _metrics;
        private readonly Timer _scaler;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
        private readonly SemaphoreSlim _nginxGate = new(1, 1);
        private readonly HashSet<int> _reservedPorts = new();

        private string SettingsFile => Path.Combine(_base, "config", "scaling.json");
        private string VhostDir => Path.Combine(_base, "config", "generated", "vhosts");
        private string LogDir => Path.Combine(_base, "logs", "scaling");

        public IReadOnlyList<ScalingPool> Pools { get { lock (_pools) return _pools.ToList(); } }

        public ScalingManager(IWebsiteOrchestrator websites, IServicesOrchestrator services, IProcessManager processes)
        {
            _websites = websites;
            _services = services;
            _processes = processes;
            _saved = LoadSettings();
            CleanupLeftovers();
            _metrics = new Timer(_ => SafeRun(SampleMetrics), null, 1000, 1000);
            _scaler = new Timer(_ => _ = AutoScaleTickAsync(), null, 5000, 5000);
        }

        // ───────────────────────── pools ─────────────────────────

        public void Refresh()
        {
            lock (_pools)
            {
                foreach (var site in _websites.Websites.ToList())
                {
                    if (_pools.Any(p => p.Website.Id == site.Id)) continue;
                    var kind = DetectKind(site);
                    if (kind == null) continue;
                    var settings = _saved.TryGetValue(site.Id, out var s) ? s : new ScalingSettings();
                    if (settings.BalancerPort == 0 || _pools.Any(p => p.Settings.BalancerPort == settings.BalancerPort))
                        settings.BalancerPort = NextFreePort(BasePort + _pools.Count);
                    _saved[site.Id] = settings;
                    _pools.Add(new ScalingPool { Website = site, Kind = kind.Value, Settings = settings });
                }
                _pools.RemoveAll(p => !p.IsRunning && !_websites.Websites.Contains(p.Website));
            }
            SaveSettings();
        }

        private static PoolKind? DetectKind(Website site)
        {
            if (!Directory.Exists(site.LocalPath)) return null;
            if (!string.IsNullOrWhiteSpace(site.DevCommand))
                return site.DevCommand.Contains("npm") || site.DevCommand.Contains("node") || site.DevCommand.Contains("yarn") || site.DevCommand.Contains("pnpm")
                    ? PoolKind.Node : null; // flutter / expo are not web servers
            if (File.Exists(Path.Combine(site.LocalPath, "package.json")) && !File.Exists(Path.Combine(site.LocalPath, "composer.json"))
                && !File.Exists(Path.Combine(site.LocalPath, "index.php")))
                return PoolKind.Node;
            return PoolKind.Php;
        }

        public async Task<bool> StartAsync(ScalingPool pool)
        {
            if (pool.IsRunning) return true;
            AddEvent(pool, $"Starting balancer on {pool.BalancerUrl}...");
            pool.IsRunning = true;
            var ok = await ScaleToAsync(pool, Math.Max(1, pool.Settings.MinInstances), "minimum instances", force: true);
            if (!ok)
            {
                AddEvent(pool, "✖ No instance could be started. Check the project and Logs.");
                await StopAsync(pool);
                return false;
            }
            AddEvent(pool, $"✔ Balancer ready: {pool.BalancerUrl} → {pool.Instances.Count} instance(s)");
            return true;
        }

        public async Task StopAsync(ScalingPool pool)
        {
            SetTraffic(pool, 0);
            pool.IsRunning = false;
            List<ScaledInstance> all;
            lock (pool.Gate) { all = pool.Instances.ToList(); pool.Instances.Clear(); }
            try { File.Delete(VhostFile(pool)); } catch { }
            await ReloadNginxAsync(pool);
            foreach (var i in all) KillInstance(i);
            AddEvent(pool, "Balancer stopped, all instances removed.");
        }

        public void StopAll()
        {
            foreach (var p in Pools.Where(p => p.IsRunning))
            {
                p.SimCts?.Cancel();
                lock (p.Gate) { foreach (var i in p.Instances) KillInstance(i); p.Instances.Clear(); }
                try { File.Delete(VhostFile(p)); } catch { }
            }
        }

        public Task ScaleToAsync(ScalingPool pool, int count, string reason) => ScaleToAsync(pool, count, reason, force: false);

        private async Task<bool> ScaleToAsync(ScalingPool pool, int count, string reason, bool force)
        {
            if (!pool.IsRunning) return false;
            await pool.ScaleLock.WaitAsync();
            try { return await ScaleCoreAsync(pool, count, reason, force); }
            finally { pool.ScaleLock.Release(); }
        }

        private async Task<bool> ScaleCoreAsync(ScalingPool pool, int count, string reason, bool force)
        {
            count = Math.Clamp(count, 1, Math.Max(1, pool.Settings.MaxInstances));
            if (!force && reason.StartsWith("manual")) pool.ManualHoldUntil = DateTime.Now.AddSeconds(60);
            int current;
            lock (pool.Gate) current = pool.Instances.Count;
            if (count == current && !force) return true;

            if (count > current)
            {
                AddEvent(pool, $"▲ Scaling up {current} → {count} ({reason})");
                var good = new List<ScaledInstance>();
                for (var k = current; k < count; k++)
                {
                    var inst = await StartInstanceAsync(pool);
                    if (inst == null) break; // the next one would fail the same way
                    good.Add(inst);
                    lock (pool.Gate) pool.Instances.Add(inst);
                }
                if (good.Count == 0 && current == 0) return false;
                await WriteBalancerAsync(pool);
            }
            else if (count < current)
            {
                List<ScaledInstance> removed;
                lock (pool.Gate)
                {
                    removed = pool.Instances.Skip(count).ToList();
                    pool.Instances.RemoveRange(count, pool.Instances.Count - count);
                }
                AddEvent(pool, $"▼ Scaling down {current} → {count} ({reason})");
                await WriteBalancerAsync(pool); // stop sending traffic first...
                await Task.Delay(1500);
                foreach (var i in removed) { KillInstance(i); AddEvent(pool, $"  {i.Name} (port {i.Port}) stopped"); }
            }
            else await WriteBalancerAsync(pool);

            pool.LastScale = DateTime.Now;
            return true;
        }

        public void ApplySettings(ScalingPool pool, int min, int max, int capacity, bool auto)
        {
            min = Math.Clamp(min, 1, 20);
            max = Math.Clamp(Math.Max(min, max), 1, 20);
            pool.Settings.MinInstances = min;
            pool.Settings.MaxInstances = max;
            pool.Settings.CapacityPerInstance = Math.Clamp(capacity, 1, 100000);
            pool.Settings.AutoScale = auto;
            SaveSettings();
            AddEvent(pool, $"Configuration: min {min}, max {max}, capacity {pool.Settings.CapacityPerInstance} req/min per instance, auto scaling {(auto ? "on" : "off")}");
            if (pool.IsRunning)
            {
                int n;
                lock (pool.Gate) n = pool.Instances.Count;
                if (n < min || n > max) _ = ScaleToAsync(pool, Math.Clamp(n, min, max), "configuration", force: true);
            }
        }

        // ───────────────────────── instances ─────────────────────────

        private async Task<ScaledInstance?> StartInstanceAsync(ScalingPool pool)
        {
            int index;
            lock (pool.Gate) index = pool.NextIndex++;
            var port = NextFreePort(pool.Kind == PoolKind.Php ? 19100 + pool.Settings.BalancerPort % 100 * 20 : 20100 + pool.Settings.BalancerPort % 100 * 20);
            var inst = new ScaledInstance { Name = $"app-{index}", Port = port };
            try
            {
                ProcessStartInfo psi;
                if (pool.Kind == PoolKind.Php)
                {
                    var meta = await _services.GetRuntimeMetadataAsync(RuntimeServiceType.PhpFpm);
                    var ini = PhpIni();
                    if (meta?.ExecutablePath == null) { AddEvent(pool, "✖ PHP is not installed."); return null; }
                    psi = new ProcessStartInfo(meta.ExecutablePath) { WorkingDirectory = Path.GetDirectoryName(meta.ExecutablePath)! };
                    foreach (var a in new[] { "-b", $"127.0.0.1:{port}", "-d", "cgi.fix_pathinfo=1" }) psi.ArgumentList.Add(a);
                    if (ini != null) { psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(ini); }
                    psi.Environment["PHP_FCGI_MAX_REQUESTS"] = "0";
                }
                else
                {
                    var cmd = string.IsNullOrWhiteSpace(pool.Website.DevCommand) ? "npm start" : pool.Website.DevCommand!;
                    psi = OperatingSystem.IsWindows()
                        ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/c", cmd } }
                        : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", cmd } };
                    psi.WorkingDirectory = pool.Website.LocalPath;
                    psi.Environment["PORT"] = port.ToString();
                    psi.Environment["HOST"] = "127.0.0.1";
                    psi.Environment["BROWSER"] = "none";
                }
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                var proc = Process.Start(psi);
                if (proc == null) return null;
                proc.OutputDataReceived += (_, _) => { };
                proc.ErrorDataReceived += (_, _) => { };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                inst.Process = proc;

                var timeout = pool.Kind == PoolKind.Php ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(60);
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < timeout)
                {
                    if (IsListening(port))
                    {
                        inst.ServerPid = _processes.GetPidUsingPort(port) ?? proc.Id;
                        inst.Status = "running";
                        AddEvent(pool, $"  {inst.Name} started on port {port} (PID {inst.Id})");
                        return inst;
                    }
                    if (proc.HasExited) break;
                    await Task.Delay(300);
                }
                AddEvent(pool, pool.Kind == PoolKind.Node
                    ? $"  ✖ {inst.Name} did not listen on port {port}. The start command must honour the PORT variable (Express, Next.js, Nuxt do)."
                    : $"  ✖ {inst.Name} (php-cgi) did not start on port {port}.");
                KillInstance(inst);
                return null;
            }
            catch (Exception ex)
            {
                AddEvent(pool, $"  ✖ {inst.Name}: {ex.Message}");
                KillInstance(inst);
                return null;
            }
        }

        private string? PhpIni()
        {
            var php = _services.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.PhpFpm);
            if (php != null && File.Exists(php.ConfigPath)) return php.ConfigPath;
            var dir = Path.Combine(_base, "config", "generated");
            return Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.ini").Where(f => File.ReadAllText(f).Contains("extension_dir")).OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
                : null;
        }

        private static void KillInstance(ScaledInstance i)
        {
            i.Status = "stopped";
            try { if (i.Process != null && !i.Process.HasExited) i.Process.Kill(entireProcessTree: true); } catch { }
            try { if (i.ServerPid is int pid && pid != i.Process?.Id) Process.GetProcessById(pid).Kill(true); } catch { }
        }

        // ───────────────────────── balancer (nginx) ─────────────────────────

        private string VhostFile(ScalingPool pool) => Path.Combine(VhostDir, $"zx-scale-{pool.Slug}.conf");
        private string AccessLog(ScalingPool pool) => Path.Combine(LogDir, $"{pool.Slug}.log");

        private async Task WriteBalancerAsync(ScalingPool pool)
        {
            List<ScaledInstance> live;
            lock (pool.Gate) live = pool.Instances.Where(i => i.Status == "running").ToList();
            if (live.Count == 0) return;

            Directory.CreateDirectory(VhostDir);
            Directory.CreateDirectory(LogDir);
            var up = $"zx_scale_{pool.Slug}";
            var sb = new StringBuilder();
            sb.AppendLine($"# Zenvix Auto Scaling – {pool.Website.Domain}. Generated; do not edit.");
            sb.AppendLine($"upstream {up} {{");
            sb.AppendLine("    least_conn;");
            foreach (var i in live) sb.AppendLine($"    server 127.0.0.1:{i.Port} max_fails=2 fail_timeout=5s;");
            sb.AppendLine("}");
            sb.AppendLine($"log_format {up} '$msec $upstream_addr $status';");
            sb.AppendLine("server {");
            sb.AppendLine($"    listen 127.0.0.1:{pool.Settings.BalancerPort};");
            sb.AppendLine("    server_name _;");
            sb.AppendLine($"    access_log \"{AccessLog(pool).Replace("\\", "/")}\" {up};");
            sb.AppendLine("    add_header X-Zenvix-Instance $upstream_addr always;");
            if (pool.Kind == PoolKind.Php)
            {
                var root = pool.Website.Type == ProjectType.Laravel || Directory.Exists(Path.Combine(pool.Website.LocalPath, "public")) && File.Exists(Path.Combine(pool.Website.LocalPath, "artisan"))
                    ? Path.Combine(pool.Website.LocalPath, "public")
                    : !string.IsNullOrEmpty(pool.Website.PublicDir) ? Path.Combine(pool.Website.LocalPath, pool.Website.PublicDir) : pool.Website.LocalPath;
                sb.AppendLine($"    root \"{root.Replace("\\", "/")}\";");
                sb.AppendLine("    index index.php index.html;");
                sb.AppendLine("    location / { try_files $uri $uri/ /index.php?$query_string; }");
                sb.AppendLine("    location ~ \\.php$ {");
                sb.AppendLine($"        fastcgi_pass {up};");
                sb.AppendLine("        fastcgi_next_upstream error timeout;");
                sb.AppendLine("        fastcgi_index index.php;");
                sb.AppendLine("        fastcgi_param SCRIPT_FILENAME $document_root$fastcgi_script_name;");
                sb.AppendLine("        include fastcgi_params;");
                sb.AppendLine("    }");
            }
            else
            {
                sb.AppendLine("    location / {");
                sb.AppendLine($"        proxy_pass http://{up};");
                sb.AppendLine("        proxy_http_version 1.1;");
                sb.AppendLine("        proxy_set_header Host $host;");
                sb.AppendLine("        proxy_set_header Upgrade $http_upgrade;");
                sb.AppendLine("        proxy_set_header Connection \"upgrade\";");
                sb.AppendLine("        proxy_set_header X-Forwarded-For $remote_addr;");
                sb.AppendLine("    }");
            }
            sb.AppendLine("}");
            await File.WriteAllTextAsync(VhostFile(pool), sb.ToString());
            await ReloadNginxAsync(pool);
        }

        private async Task ReloadNginxAsync(ScalingPool pool)
        {
            await _nginxGate.WaitAsync();
            try
            {
                var nginx = _services.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.Nginx);
                if (nginx == null) return;
                if (nginx.Status != ServiceStatus.Running || !File.Exists(nginx.ConfigPath))
                {
                    if (!pool.IsRunning) return;
                    AddEvent(pool, "Starting Nginx...");
                    if (nginx.Status == ServiceStatus.Running) await _services.StopAsync(nginx.Id);
                    await _services.StartAsync(nginx.Id);
                    AddEvent(pool, "Nginx started.");
                    return;
                }
                var meta = await _services.GetRuntimeMetadataAsync(RuntimeServiceType.Nginx);
                if (meta?.ExecutablePath == null) return;
                var prefix = (meta.RootDir ?? Path.GetDirectoryName(meta.ExecutablePath) ?? "").Replace("\\", "/").TrimEnd('/') + "/";
                var psi = new ProcessStartInfo(meta.ExecutablePath)
                {
                    WorkingDirectory = Path.GetDirectoryName(meta.ExecutablePath)!,
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
                };
                foreach (var a in new[] { "-p", prefix, "-c", Path.GetFullPath(nginx.ConfigPath).Replace("\\", "/"), "-s", "reload" }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                var err = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                if (p.ExitCode == 0) AddEvent(pool, "Nginx reloaded successfully.");
                else AddEvent(pool, $"✖ Nginx reload failed: {err.Split('\n').FirstOrDefault(l => l.Contains("emerg") || l.Contains("error"))?.Trim() ?? err.Trim()}");
            }
            catch (Exception ex) { AddEvent(pool, $"✖ Nginx reload: {ex.Message}"); }
            finally { _nginxGate.Release(); }
        }

        // ───────────────────────── metrics & auto scaling ─────────────────────────

        private void SampleMetrics()
        {
            var now = DateTime.Now;
            var cores = Environment.ProcessorCount;
            foreach (var pool in Pools.Where(p => p.IsRunning))
            {
                ReadAccessLog(pool);
                while (pool.AllHits.TryPeek(out var t) && now - t > TimeSpan.FromSeconds(60)) pool.AllHits.TryDequeue(out _);

                List<ScaledInstance> list;
                lock (pool.Gate) list = pool.Instances.ToList();
                foreach (var i in list)
                {
                    while (i.Hits.TryPeek(out var t) && now - t > TimeSpan.FromSeconds(60)) i.Hits.TryDequeue(out _);
                    i.Requests60 = i.Hits.Count;
                    try
                    {
                        using var p = Process.GetProcessById(i.ServerPid ?? i.Process!.Id);
                        p.Refresh();
                        var cpu = p.TotalProcessorTime;
                        var elapsed = (now - i.LastSample).TotalMilliseconds;
                        if (i.LastCpu != TimeSpan.Zero && elapsed > 0)
                            i.CpuPercent = Math.Clamp((cpu - i.LastCpu).TotalMilliseconds / (elapsed * cores) * 100, 0, 100);
                        i.LastCpu = cpu;
                        i.LastSample = now;
                        i.RamMb = p.WorkingSet64 / 1024.0 / 1024.0;
                        if (i.Status != "stopped") i.Status = "running";
                    }
                    catch { if (i.Status == "running") { i.Status = "crashed"; AddEvent(pool, $"✖ {i.Name} (port {i.Port}) stopped unexpectedly"); } }
                }
            }
        }

        private void ReadAccessLog(ScalingPool pool)
        {
            var file = AccessLog(pool);
            if (!File.Exists(file)) return;
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < pool.LogOffset) pool.LogOffset = 0;
                fs.Seek(pool.LogOffset, SeekOrigin.Begin);
                using var reader = new StreamReader(fs);
                var text = reader.ReadToEnd();
                var lastNewline = text.LastIndexOf('\n');
                if (lastNewline < 0) return;
                pool.LogOffset += Encoding.UTF8.GetByteCount(text[..(lastNewline + 1)]);

                List<ScaledInstance> list;
                lock (pool.Gate) list = pool.Instances.ToList();
                foreach (var line in text[..lastNewline].Split('\n'))
                {
                    var parts = line.Trim().Split(' ', 2);
                    if (parts.Length < 2 || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var msec)) continue;
                    var when = DateTimeOffset.FromUnixTimeMilliseconds((long)(msec * 1000)).LocalDateTime;
                    pool.AllHits.Enqueue(when);
                    var addr = parts[1].Split(' ')[0].Split(',').Last().Trim();
                    var m = Regex.Match(addr, @":(\d+)$");
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var port))
                        list.FirstOrDefault(i => i.Port == port)?.Hits.Enqueue(when);
                }
            }
            catch (IOException) { }
        }

        private async Task AutoScaleTickAsync()
        {
            foreach (var pool in Pools.Where(p => p.IsRunning && p.ScaleLock.CurrentCount > 0 && p.Instances.Count > 0))
            {
                try
                {
                    List<ScaledInstance> list;
                    lock (pool.Gate) list = pool.Instances.ToList();

                    // replace crashed instances
                    var dead = list.Where(i => i.Status == "crashed").ToList();
                    if (dead.Count > 0)
                    {
                        lock (pool.Gate) pool.Instances.RemoveAll(dead.Contains);
                        foreach (var d in dead) KillInstance(d);
                        await ScaleToAsync(pool, Math.Max(pool.Settings.MinInstances, list.Count), "replacing crashed instance", force: true);
                        continue;
                    }
                    if (!pool.Settings.AutoScale || DateTime.Now < pool.ManualHoldUntil) continue;

                    var current = list.Count;
                    var perMinute = pool.AllHits.Count;
                    var byTraffic = (int)Math.Ceiling(perMinute / (double)Math.Max(1, pool.Settings.CapacityPerInstance));
                    var avgCpu = list.Count == 0 ? 0 : list.Average(i => i.CpuPercent);
                    var byCpu = avgCpu > 75 ? current + 1 : 0;
                    var desired = Math.Clamp(Math.Max(byTraffic, byCpu), pool.Settings.MinInstances, pool.Settings.MaxInstances);

                    if (desired > current)
                        await ScaleToAsync(pool, desired, byCpu > byTraffic ? $"CPU {avgCpu:0}% > 75%" : $"{perMinute} req/min > capacity {current * pool.Settings.CapacityPerInstance}", force: false);
                    else if (desired < current && DateTime.Now - pool.LastScale > ScaleDownCooldown)
                        await ScaleToAsync(pool, current - 1, $"load {perMinute} req/min fits in {current - 1} instance(s)", force: false);
                }
                catch (Exception ex) { Log.Warning(ex, "[Scaling] tick failed for {Site}", pool.Website.Domain); }
            }
        }

        // ───────────────────────── traffic simulation ─────────────────────────

        public void SetTraffic(ScalingPool pool, int requestsPerSecond)
        {
            requestsPerSecond = Math.Clamp(requestsPerSecond, 0, 200);
            if (requestsPerSecond == pool.SimulatedRps) return;
            var was = pool.SimulatedRps;
            pool.SimulatedRps = requestsPerSecond;
            if (requestsPerSecond == 0)
            {
                pool.SimCts?.Cancel();
                pool.SimCts = null;
                if (was > 0) AddEvent(pool, "Traffic simulation stopped.");
                return;
            }
            AddEvent(pool, $"Traffic simulation: {requestsPerSecond} req/s through {pool.BalancerUrl}");
            if (pool.SimCts != null) return; // running loop picks up the new rate
            var cts = pool.SimCts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                using var inflight = new SemaphoreSlim(64);
                while (!cts.IsCancellationRequested)
                {
                    var rps = Math.Max(1, pool.SimulatedRps);
                    var delay = TimeSpan.FromMilliseconds(1000.0 / rps);
                    if (pool.IsRunning && await inflight.WaitAsync(0))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                using var res = await _http.GetAsync(pool.BalancerUrl, cts.Token);
                                pool.SimulatedSent++;
                                if ((int)res.StatusCode >= 500) pool.SimulatedFailed++;
                            }
                            catch { pool.SimulatedFailed++; }
                            finally { inflight.Release(); }
                        });
                    }
                    try { await Task.Delay(delay, cts.Token); } catch { break; }
                }
            });
        }

        // ───────────────────────── helpers ─────────────────────────

        private static void AddEvent(ScalingPool pool, string text)
        {
            lock (pool.Events)
            {
                pool.Events.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
                if (pool.Events.Count > 300) pool.Events.RemoveRange(0, pool.Events.Count - 300);
            }
            Log.Information("[Scaling] {Site}: {Text}", pool.Website.Domain, text);
        }

        private int NextFreePort(int start)
        {
            lock (_reservedPorts)
            {
                var used = new HashSet<int>(Pools.SelectMany(p => p.Instances.Select(i => i.Port).Append(p.Settings.BalancerPort)));
                _reservedPorts.IntersectWith(used.Concat(_reservedPorts.Where(IsListening)).ToList());
                for (var port = start; port < start + 2000; port++)
                    if (!used.Contains(port) && !_reservedPorts.Contains(port) && !IsListening(port))
                    {
                        _reservedPorts.Add(port);
                        return port;
                    }
                return start;
            }
        }

        private static bool IsListening(int port)
        {
            try { using var c = new TcpClient(); return c.ConnectAsync("127.0.0.1", port).Wait(250) && c.Connected; }
            catch { return false; }
        }

        private Dictionary<Guid, ScalingSettings> LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFile))
                    return JsonSerializer.Deserialize<Dictionary<Guid, ScalingSettings>>(File.ReadAllText(SettingsFile)) ?? new();
            }
            catch (Exception ex) { Log.Warning(ex, "[Scaling] could not read settings"); }
            return new();
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
                File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_saved, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { Log.Warning(ex, "[Scaling] could not save settings"); }
        }

        /// <summary>A crash can leave balancer files behind; they would point Nginx at dead ports.</summary>
        private void CleanupLeftovers()
        {
            try
            {
                if (Directory.Exists(VhostDir))
                    foreach (var f in Directory.GetFiles(VhostDir, "zx-scale-*.conf")) File.Delete(f);
            }
            catch { }
        }

        private static void SafeRun(Action a)
        {
            try { a(); } catch (Exception ex) { Log.Debug(ex, "[Scaling] metrics"); }
        }
    }
}
