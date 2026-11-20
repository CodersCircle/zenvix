using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Zenvix.Core.Models;
using Zenvix.Core.Services;
using Zenvix.Modules.Services;
using Serilog;

namespace Zenvix.Runtime.Services
{
    public interface IWebsiteOrchestrator
    {
        ObservableCollection<Website> Websites { get; }
        Task<bool> DeployWebsiteAsync(Website website);
        Task<bool> StopWebsiteAsync(Website website);
        Task InitializeAsync();
        Task SaveAsync();
        ProjectType DetectProjectType(string path);
        Task<bool> UpdateSslAsync(Website website);
    }

    public class WebsiteOrchestrator : IWebsiteOrchestrator
    {
        public ObservableCollection<Website> Websites { get; } = new ObservableCollection<Website>();
        
        private readonly string _storagePath;
        private readonly Zenvix.Modules.Toolchain.IToolchainManager _tools;
        private readonly IServicesOrchestrator _services;
        private readonly IRuntimeConfigGenerator _config;
        private readonly IDomainManager _domainManager;
        private readonly ISSLManager _ssl;

        public WebsiteOrchestrator(
            IServicesOrchestrator services,
            IRuntimeConfigGenerator config,
            IDomainManager domainManager,
            ISSLManager ssl,
            Zenvix.Modules.Toolchain.IToolchainManager? tools = null)
        {
            _tools = tools ?? new Zenvix.Modules.Toolchain.ToolchainManager();
            _services = services;
            _config = config;
            _domainManager = domainManager;
            _ssl = ssl;
            _storagePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "projects.json");
            
            var dir = Path.GetDirectoryName(_storagePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);
        }

        public async Task InitializeAsync()
        {
            Log.Information("[WebOrchestrator] Initializing website engine...");
            if (File.Exists(_storagePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_storagePath);
                    var list = JsonSerializer.Deserialize<List<Website>>(json);
                    if (list != null)
                    {
                        var nginx = await _services.GetRuntimeMetadataAsync(RuntimeServiceType.Nginx);
                        foreach (var site in list)
                        {
                            Websites.Add(site);
                            
                            // Validate/Regenerate SSL cert if enabled (this handles purging old self-signed certs)
                            if (site.SslEnabled)
                            {
                                _ssl.GenerateCert(site.Domain);
                            }

                            // Regenerate config on startup to ensure latest logic/SSL fallback is applied
                            if (nginx != null)
                            {
                                var phpPort = _services.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.PhpFpm)?.Port ?? 9000;
                                _config.GenerateVHost(site, nginx, phpPort);
                            }
                        }
                    }
                }
                catch (Exception ex) { Log.Error(ex, "Failed to load projects"); }
                PruneStaleVHosts();
            }
        }

        /// <summary>Deletes generated vhost files for sites that are no longer in the list.</summary>
        private void PruneStaleVHosts()
        {
            try
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "generated", "vhosts");
                if (!Directory.Exists(dir)) return;
                var keep = new HashSet<string>(Websites.Select(w => w.Domain + ".conf"), StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.GetFiles(dir, "*.conf"))
                    if (!keep.Contains(Path.GetFileName(f)) && !Path.GetFileName(f).StartsWith("zx-scale-", StringComparison.OrdinalIgnoreCase))
                        File.Delete(f); // zx-scale-*.conf belong to Auto Scaling
            }
            catch (Exception ex) { Log.Warning(ex, "[WebOrchestrator] could not prune stale vhosts"); }
        }

        public async Task SaveAsync()
        {
            PruneStaleVHosts();
            try
            {
                var json = JsonSerializer.Serialize(Websites.ToList(), new JsonSerializerOptions { WriteIndented = true });
                // Keep the previous list and write atomically, so a crash or mistake never loses it.
                if (File.Exists(_storagePath)) File.Copy(_storagePath, _storagePath + ".bak", overwrite: true);
                var tmp = _storagePath + ".tmp";
                await File.WriteAllTextAsync(tmp, json);
                File.Move(tmp, _storagePath, overwrite: true);
                Log.Information("[WebOrchestrator] Saved {Count} website(s).", Websites.Count);
            }
            catch (Exception ex) { Log.Error(ex, "Failed to save projects"); }
        }

        public ProjectType DetectProjectType(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return ProjectType.PHP;

            if (File.Exists(Path.Combine(path, "artisan"))) return ProjectType.Laravel;
            if (File.Exists(Path.Combine(path, "wp-config.php"))) return ProjectType.WordPress;
            if (File.Exists(Path.Combine(path, "package.json")))
            {
                var packageJson = File.ReadAllText(Path.Combine(path, "package.json"));
                if (packageJson.Contains("\"vite\"")) return ProjectType.Vite;
                if (packageJson.Contains("\"react\"")) return ProjectType.React;
                if (packageJson.Contains("\"vue\"")) return ProjectType.Vue;
                return ProjectType.NodeJS;
            }
            if (Directory.GetFiles(path, "*.html").Any()) return ProjectType.Static;

            return ProjectType.PHP;
        }

        public async Task<bool> DeployWebsiteAsync(Website website)
        {
            try
            {
                Log.Information("[WebOrchestrator] Deploying website: {Domain}", website.Domain);
                website.Status = WebsiteStatus.Starting;
                website.Notes = null; // drop the reason from a previous failed start

                if (!Websites.Contains(website)) Websites.Add(website);

                // Node / mobile projects run their own dev server instead of Nginx + PHP.
                if (website.IsDevServerProject)
                    return await StartDevServerAsync(website);

                // 1. Get Nginx Metadata
                var nginx = await _services.GetRuntimeMetadataAsync(RuntimeServiceType.Nginx);
                if (nginx == null)
                {
                    Log.Error("[WebOrchestrator] Nginx not found. Deployment aborted.");
                    return false;
                }

                // 2. Update Hosts file
                bool hostsSuccess = _domainManager.RegisterDomain(website.Domain);
                if (!hostsSuccess)
                {
                    Log.Warning("[WebOrchestrator] Domain mapping failed for {Domain}. Site will only be accessible if hosts file is updated MANUALLY.", website.Domain);
                    website.Notes = "Manual hosts update required (127.0.0.1 " + website.Domain + ")";
                }

                // 3. Generate SSL if enabled
                if (website.SslEnabled)
                {
                    bool success = _ssl.GenerateCert(website.Domain);
                    if (!success)
                    {
                        Log.Warning("[WebOrchestrator] SSL Generation FAILED for {Domain}. Falling back to HTTP-only to prevent server crash.", website.Domain);
                        website.SslEnabled = false;
                        website.SslDiagnosticMessage = "SSL generation failed. Switched to HTTP-only.";
                    }
                }

                // 4. Generate VHost (Now guaranteed to have a valid SSL state or be HTTP-only)
                var phpPort = _services.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.PhpFpm)?.Port ?? 9000;
                _config.GenerateVHost(website, nginx, phpPort);

                // 4. Ensure PHP-FPM is running
                var phpInstance = _services.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.PhpFpm);
                if (phpInstance != null && phpInstance.Status != ServiceStatus.Running)
                {
                    await _services.StartAsync(phpInstance.Id);
                }

                // 4.5 Ensure MySQL is running and auto-create the database for the project
                var mysqlInstance = _services.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.MySQL || i.Type == RuntimeServiceType.MariaDB);
                if (mysqlInstance != null)
                {
                    if (mysqlInstance.Status != ServiceStatus.Running)
                    {
                        Log.Information("[WebOrchestrator] Auto-starting MySQL service for project database connection...");
                        await _services.StartAsync(mysqlInstance.Id);
                    }
                    
                    var dbName = website.Name.ToLower().Replace(" ", "_").Replace("-", "_").Trim();
                    await EnsureDatabaseCreatedAsync(dbName, mysqlInstance.Port);
                }

                // 5. Restart Nginx
                var nginxInstance = _services.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.Nginx);
                if (nginxInstance != null)
                {
                    if (nginxInstance.Status == ServiceStatus.Running)
                        await _services.StopAsync(nginxInstance.Id);
                    
                    await _services.StartAsync(nginxInstance.Id);
                }

                // 5.5 DATABASE PRE-FLIGHT
                // If the project's .env points at a local database port where nothing is listening, every
                // page waits on ~2s connection retries per query (Windows) until Nginx returns 504.
                // Say so immediately instead of letting the site (and the health check) hang.
                var dbProblem = CheckProjectDatabase(website, mysqlInstance);
                if (dbProblem != null)
                {
                    Log.Warning("[WebOrchestrator] {Domain}: {Problem}", website.Domain, dbProblem);
                    website.Status = WebsiteStatus.Error;
                    website.Notes = dbProblem;
                    await SaveAsync();
                    return false;
                }

                // 6. HEALTH CHECK
                website.Status = WebsiteStatus.Starting;
                var healthy = await VerifyWebsiteHealthAsync(website);
                
                if (healthy)
                {
                    website.Status = WebsiteStatus.Running;
                    Log.Information("[WebOrchestrator] {Domain} is live.", website.Domain);
                }
                else
                {
                    website.Status = WebsiteStatus.Error;
                    website.Notes = "The site did not answer within 45 seconds. Open Logs → PHP / Nginx, or check that the database in .env is running.";
                }

                await SaveAsync();
                return healthy;
            }
            catch (Exception ex)
            {
                Log.Error("[WebOrchestrator] Deployment failed: {Msg}", ex.Message);
                website.Status = WebsiteStatus.Error;
                return false;
            }
        }

        /// <summary>
        /// Reads DB_CONNECTION/DB_HOST/DB_PORT from the project's .env (Laravel and most PHP apps).
        /// Returns a user-facing message when a local database port has nothing listening, else null.
        /// </summary>
        private static string? CheckProjectDatabase(Website website, RuntimeServiceInstance? zenvixMySql)
        {
            try
            {
                var envPath = Path.Combine(website.LocalPath, ".env");
                if (!File.Exists(envPath)) return null;
                var env = File.ReadAllLines(envPath)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('='))
                    .Select(l => l.Split('=', 2))
                    .GroupBy(p => p[0].Trim()).ToDictionary(g => g.Key, g => g.Last()[1].Trim().Trim('"', '\''));

                env.TryGetValue("DB_CONNECTION", out var conn);
                if (conn is not ("mysql" or "mariadb" or "pgsql")) return null; // sqlite etc. need no server
                env.TryGetValue("DB_HOST", out var host);
                if (host is not ("127.0.0.1" or "localhost" or "") && host != null) return null; // remote DB: not our business
                var port = env.TryGetValue("DB_PORT", out var ps) && int.TryParse(ps, out var p) ? p : (conn == "pgsql" ? 5432 : 3306);

                var listening = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpListeners().Any(e => e.Port == port);
                if (listening) return null;

                var hint = zenvixMySql != null && zenvixMySql.Status == ServiceStatus.Running && conn != "pgsql"
                    ? $" Start the MySQL that holds this project's data on port {port} (e.g. XAMPP), or set DB_PORT={zenvixMySql.Port} in .env to use Zenvix MySQL."
                    : $" Start that database server, or update DB_HOST/DB_PORT in .env.";
                return $"Database not running: .env uses {conn} at 127.0.0.1:{port}, but nothing is listening there.{hint}";
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[WebOrchestrator] .env database check skipped for {Domain}", website.Domain);
                return null;
            }
        }

        private async Task<bool> VerifyWebsiteHealthAsync(Website website)
        {
            using var client = new HttpClient(new HttpClientHandler 
            { 
                ServerCertificateCustomValidationCallback = (m, c, ch, e) => true 
            });
            
            // The first request to a framework app (Laravel/Filament cold boot, OPcache warm-up) can take 10-20s.
            client.Timeout = TimeSpan.FromSeconds(45);
            
            // CRITICAL: Wait for Nginx to actually bind and start listening
            await Task.Delay(2000); 

            for (int i = 0; i < 2; i++) 
            {
                try
                {
                    Log.Information("[WebOrchestrator] Health-check probe {Count}/2 for {Domain}...", i + 1, website.Domain);
                    
                    var response = await client.GetAsync(website.DisplayUrl);
                    
                    // 1. Any answer from the app means the site is served (the app may legitimately 404 on "/").
                    //    Only gateway errors mean Nginx could not reach PHP.
                    var code = (int)response.StatusCode;
                    if (code != 502 && code != 503 && code != 504)
                    {
                        // 2. Verify PHP Execution (Response Body check)
                        var content = await response.Content.ReadAsStringAsync();

                        // If it's a new site, we expect our "Welcome" message.
                        // If it's an existing site, we just want it to NOT be an empty Nginx error page.
                        
                        bool isLaravel = website.Type == ProjectType.Laravel;
                        bool hasLaravelFiles = false;
                        if (isLaravel)
                        {
                            var artisanPath = Path.Combine(website.LocalPath, "artisan");
                            var vendorAutoload = Path.Combine(website.LocalPath, "vendor", "autoload.php");
                            var composerJson = Path.Combine(website.LocalPath, "composer.json");
                            hasLaravelFiles = File.Exists(artisanPath) && (File.Exists(vendorAutoload) || File.Exists(composerJson));
                        }

                        if (content.Contains("Welcome to Zenvix") || content.Contains("PHP") || content.Length > 20 || hasLaravelFiles)
                        {
                            Log.Information("[WebOrchestrator] Health-check PASSED for {Domain} (Status: {Code}).", website.Domain, response.StatusCode);
                            return true;
                        }
                    }
                }
                catch (Exception ex) 
                {
                    Log.Warning("[WebOrchestrator] Health probe {Count} failed for {Domain}: {Msg}", i + 1, website.Domain, ex.Message);
                    // A timeout means PHP is busy/stuck; another probe would only queue behind it.
                    if (ex is TaskCanceledException) break;
                }
                
                await Task.Delay(2500); // Wait for runtime stability
            }
            
            return false;
        }

        public async Task<bool> UpdateSslAsync(Website website)
        {
            Log.Information("[WebOrchestrator] Updating SSL state for {Domain} to {State}", website.Domain, website.SslEnabled);

            // 1. Handle certificate state
            if (website.SslEnabled)
            {
                bool success = _ssl.GenerateCert(website.Domain);
                if (!success)
                {
                    website.SslEnabled = false;
                    return false;
                }
            }
            else
            {
                _ssl.PurgeCert(website.Domain);
            }

            // 2. Regenerate VHost
            var nginx = await _services.GetRuntimeMetadataAsync(RuntimeServiceType.Nginx);
            if (nginx != null)
            {
                var phpPort = _services.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.PhpFpm)?.Port ?? 9000;
                _config.GenerateVHost(website, nginx, phpPort);
            }

            // 3. Reload Nginx
            var nginxInstance = _services.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.Nginx);
            if (nginxInstance != null && nginxInstance.Status == ServiceStatus.Running)
            {
                await _services.StopAsync(nginxInstance.Id);
                await _services.StartAsync(nginxInstance.Id);
            }

            await SaveAsync();
            return true;
        }

        private async Task EnsureDatabaseCreatedAsync(string databaseName, int mysqlPort)
        {
            var phpCgiPath = _services.GetBinaryPath(RuntimeServiceType.PhpFpm);
            if (string.IsNullOrEmpty(phpCgiPath)) return;

            var phpDir = Path.GetDirectoryName(phpCgiPath);
            if (string.IsNullOrEmpty(phpDir)) return;

            var phpExe = Path.Combine(phpDir, "php.exe");
            if (!File.Exists(phpExe)) return;

            var normalizedDb = databaseName.Replace("-", "_").Replace(" ", "_").Trim();

            Log.Information("[WebOrchestrator] Attempting to auto-create MySQL database: {DbName} on port {Port}", normalizedDb, mysqlPort);

            var code = $"try {{ $pdo = new PDO('mysql:host=127.0.0.1;port={mysqlPort}', 'root', ''); $pdo->exec('CREATE DATABASE IF NOT EXISTS `{normalizedDb}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;'); echo 'SUCCESS'; }} catch (Exception $e) {{ echo $e->getMessage(); }}";
            
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = phpExe,
                    Arguments = $"-r \"{code.Replace("\"", "\\\"")}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync();
                    var output = await proc.StandardOutput.ReadToEndAsync();
                    var error = await proc.StandardError.ReadToEndAsync();
                    Log.Information("[WebOrchestrator] Database creation result: {Out} | {Err}", output.Trim(), error.Trim());
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[WebOrchestrator] Failed to execute database creation command");
            }
        }

        /// <summary>
        /// Starts "npm run dev", "flutter run", "expo start"... in its own console window (so hot reload,
        /// QR codes and errors stay visible) with Zenvix's Node/Flutter/PHP on PATH.
        /// </summary>
        private async Task<bool> StartDevServerAsync(Website website)
        {
            try
            {
                if (website.DevProcessId is int old && IsAlive(old)) { website.Status = WebsiteStatus.Running; return true; }

                var psi = new ProcessStartInfo("cmd.exe", $"/d /k \"title {website.Name} - Zenvix && {website.DevCommand}\"")
                {
                    WorkingDirectory = website.LocalPath,
                    UseShellExecute = false,
                    CreateNoWindow = false,
                };
                foreach (var kv in _tools.BuildEnvironment()) psi.Environment[kv.Key] = kv.Value;
                psi.Environment["CI"] = "";          // dev servers should be interactive (Expo QR, hot reload keys)
                psi.Environment["BROWSER"] = "none"; // we open the browser ourselves once the port answers

                var proc = Process.Start(psi)!;
                website.DevProcessId = proc.Id;
                Log.Information("[WebOrchestrator] Dev server for {Name}: {Cmd} (PID {Pid})", website.Name, website.DevCommand, proc.Id);

                // Wait for the dev server port (first Flutter/Next build can take a while).
                var ready = false;
                if (website.DevPort is int port)
                {
                    var sw = Stopwatch.StartNew();
                    while (sw.Elapsed < TimeSpan.FromMinutes(3) && !proc.HasExited)
                    {
                        if (System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port))
                        { ready = true; break; }
                        await Task.Delay(1000);
                    }
                }

                website.Status = proc.HasExited ? WebsiteStatus.Error : WebsiteStatus.Running;
                website.Notes = proc.HasExited ? "Dev server exited – see its console window."
                              : ready ? $"Dev server on {website.DisplayUrl}" : "Dev server starting – watch its console window.";
                await SaveAsync();
                return !proc.HasExited;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[WebOrchestrator] dev server failed for {Name}", website.Name);
                website.Status = WebsiteStatus.Error;
                website.Notes = ex.Message;
                return false;
            }
        }

        private static bool IsAlive(int pid)
        {
            try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch { return false; }
        }

        public async Task<bool> StopWebsiteAsync(Website website)
        {
            if (website.DevProcessId is int pid)
            {
                try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
                website.DevProcessId = null;
            }
            website.Status = WebsiteStatus.Stopped;
            await SaveAsync();
            return true;
        }
    }
}
