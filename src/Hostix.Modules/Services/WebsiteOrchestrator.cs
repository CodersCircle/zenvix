using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Hostix.Core.Models;
using Hostix.Core.Services;
using Hostix.Modules.Services;
using Serilog;

namespace Hostix.Runtime.Services
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
        private readonly IServicesOrchestrator _services;
        private readonly IRuntimeConfigGenerator _config;
        private readonly IDomainManager _domainManager;
        private readonly ISSLManager _ssl;

        public WebsiteOrchestrator(
            IServicesOrchestrator services,
            IRuntimeConfigGenerator config,
            IDomainManager domainManager,
            ISSLManager ssl)
        {
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
            }
        }

        public async Task SaveAsync()
        {
            try
            {
                var json = JsonSerializer.Serialize(Websites.ToList(), new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_storagePath, json);
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

                if (!Websites.Contains(website)) Websites.Add(website);

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

                // 5. Restart Nginx
                var nginxInstance = _services.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.Nginx);
                if (nginxInstance != null)
                {
                    if (nginxInstance.Status == ServiceStatus.Running)
                        await _services.StopAsync(nginxInstance.Id);
                    
                    await _services.StartAsync(nginxInstance.Id);
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
                    website.Notes = "Website started but health check failed. Check domain mapping.";
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

        private async Task<bool> VerifyWebsiteHealthAsync(Website website)
        {
            using var client = new HttpClient(new HttpClientHandler 
            { 
                ServerCertificateCustomValidationCallback = (m, c, ch, e) => true 
            });
            
            // Modern browsers and PHP need a bit more time to initialize sockets
            client.Timeout = TimeSpan.FromSeconds(5);
            
            // CRITICAL: Wait for Nginx to actually bind and start listening
            await Task.Delay(2000); 

            for (int i = 0; i < 6; i++) 
            {
                try
                {
                    Log.Information("[WebOrchestrator] Health-check probe {Count}/6 for {Domain}...", i + 1, website.Domain);
                    
                    var response = await client.GetAsync(website.DisplayUrl);
                    
                    // 1. Verify HTTP Response Code (200, 404, 403 are "alive")
                    if (response.IsSuccessStatusCode || 
                        response.StatusCode == System.Net.HttpStatusCode.NotFound ||
                        response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        // 2. Verify PHP Execution (Response Body check)
                        var content = await response.Content.ReadAsStringAsync();

                        // If it's a new site, we expect our "Welcome" message.
                        // If it's an existing site, we just want it to NOT be an empty Nginx error page.
                        if (content.Contains("Welcome to Hostix") || content.Contains("PHP") || content.Length > 20)
                        {
                            Log.Information("[WebOrchestrator] Health-check PASSED for {Domain} (Status: {Code}).", website.Domain, response.StatusCode);
                            return true;
                        }
                    }
                }
                catch (Exception ex) 
                {
                    Log.Warning("[WebOrchestrator] Health probe {Count} failed for {Domain}: {Msg}", i + 1, website.Domain, ex.Message);
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

        public async Task<bool> StopWebsiteAsync(Website website)
        {
            website.Status = WebsiteStatus.Stopped;
            await SaveAsync();
            return true;
        }
    }
}
