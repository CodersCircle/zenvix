using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using Serilog;
using Hostix.Core.Models;

namespace Hostix.Runtime.Services
{
    // ──────────────────────────────────────────────────────────────────────────
    // Embedded Tool Types
    // ──────────────────────────────────────────────────────────────────────────
    public enum EmbeddedTool
    {
        PhpMyAdmin,      // MySQL / MariaDB
        MongoExpress,    // MongoDB (Node.js based)
        RedisInsight,    // Redis (standalone binary)
        MailpitWeb,      // Mailpit built-in web UI
    }

    public record ToolSession(string Url, int Port, Process? Process, EmbeddedTool Tool);

    // ──────────────────────────────────────────────────────────────────────────
    // Interface
    // ──────────────────────────────────────────────────────────────────────────
    public interface IEmbeddedToolsOrchestrator
    {
        /// <summary>Current runtime status of the PHP server.</summary>
        string PhpStatus { get; }
        bool IsPhpAvailable { get; }

        Task<ToolSession?> OpenDatabasePanelAsync(string dbType, string host, int dbPort, string username = "root", string password = "", string database = "");
        Task StopToolAsync(EmbeddedTool tool);
        Task StopAllToolsAsync();

        event Action<string>? OnStatusMessage;
    }                                                                                                       

    // ──────────────────────────────────────────────────────────────────────────
    // Implementation
    // ──────────────────────────────────────────────────────────────────────────
    public class EmbeddedToolsOrchestrator : IEmbeddedToolsOrchestrator, IDisposable
    {
        private readonly string _toolsRoot;        // {AppDir}/tools/
        private readonly string _pmaDir;           // {toolsRoot}/phpmyadmin/
        private readonly string _phpDir;           // {toolsRoot}/php/
        private readonly string _pmaConfig;        // {pmaDir}/config.inc.php
        private readonly string _embeddedPhpExe;   // {phpDir}/php.exe

        private readonly IDatabaseCredentialsManager _credentialsManager;
        private readonly IServicesOrchestrator _servicesOrchestrator;

        private const string PhpDownloadPage     = "https://windows.php.net/download/";
        private const string PmaDownloadUrl      = "https://files.phpmyadmin.net/phpMyAdmin/5.2.3/phpMyAdmin-5.2.3-all-languages.zip";
        private const int MailpitWebPort    = 8025;

        // ── State ─────────────────────────────────────────────────────────────
        private readonly ConcurrentDictionary<EmbeddedTool, ToolSession> _sessions = new(); 
        private string? _resolvedPhpPath;

        public event Action<string>? OnStatusMessage;

        public string PhpStatus     { get; private set; } = "Not detected";
        public bool   IsPhpAvailable => _resolvedPhpPath != null;

        // ──────────────────────────────────────────────────────────────────────
        public EmbeddedToolsOrchestrator(
            IDatabaseCredentialsManager credentialsManager,
            IServicesOrchestrator servicesOrchestrator)
        {
            _credentialsManager   = credentialsManager;
            _servicesOrchestrator = servicesOrchestrator;

            _toolsRoot      = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools");
            _pmaDir         = Path.Combine(_toolsRoot, "phpmyadmin");
            _phpDir         = Path.Combine(_toolsRoot, "php");
            _pmaConfig      = Path.Combine(_pmaDir, "config.inc.php");
            _embeddedPhpExe = Path.Combine(_phpDir, "php.exe");

            CreateDirectories();
            _ = Task.Run(DetectPhpAsync);
        }

        private void CreateDirectories()
        {
            Directory.CreateDirectory(_toolsRoot);
            // Directory.CreateDirectory(_pmaDir); // DO NOT CREATE PREMATURELY - EnsurePhpMyAdminAsync owns this
            Directory.CreateDirectory(_phpDir);
            Directory.CreateDirectory(Path.Combine(_toolsRoot, "pgadmin"));
            Directory.CreateDirectory(Path.Combine(_toolsRoot, "redisinsight"));
            Directory.CreateDirectory(Path.Combine(_toolsRoot, "mongo-express"));
            Log.Information("EmbeddedToolsOrchestrator: tools directory initialized at {Root}", _toolsRoot);
            
            // Proactively ensure phpMyAdmin is present
            _ = Task.Run(async () => {
                try { await EnsurePhpMyAdminAsync(); }
                catch (Exception ex) { Log.Error(ex, "Failed to proactively provision phpMyAdmin."); }
            });
        }



        // ── PHP Detection ─────────────────────────────────────────────────────

        private async Task DetectPhpAsync()
        {
            // 1. Check Hostix-embedded PHP first (tools/php/php.exe)
            if (File.Exists(_embeddedPhpExe))
            {
                _resolvedPhpPath = _embeddedPhpExe;
                PhpStatus = $"Embedded — {GetPhpVersion(_embeddedPhpExe)}";
                Notify($"PHP runtime: Embedded ({PhpStatus})");
                Log.Information("PHP: using embedded runtime at {Path}", _embeddedPhpExe);
                return;
            }

            // 2. Check well-known Windows install paths (Laragon, WAMP, XAMPP, standalone)
            var candidates = BuildPhpCandidates();
            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    _resolvedPhpPath = path;
                    PhpStatus = $"System — {GetPhpVersion(path)} @ {path}";
                    Notify($"PHP runtime detected: {path}");
                    Log.Information("PHP: using system runtime at {Path}", path);
                    return;
                }
            }

            // 3. Try PATH via `where php`
            var pathPhp = await FindPhpInPathAsync();
            if (pathPhp != null)
            {
                _resolvedPhpPath = pathPhp;
                PhpStatus = $"PATH — {GetPhpVersion(pathPhp)} @ {pathPhp}";
                Notify($"PHP runtime found in PATH: {pathPhp}");
                Log.Information("PHP: using PATH runtime at {Path}", pathPhp);
                return;
            }

            // 4. PHP not found
            PhpStatus = "Not installed — place php.exe in tools/php/ or install PHP";
            Log.Warning("PHP runtime not found. phpMyAdmin panel unavailable without PHP.");
            Notify("PHP not found. Place php.exe in: " + _phpDir);
        }

        private List<string> BuildPhpCandidates()
        {
            var versions = new[] { "8.3", "8.2", "8.1", "8.0", "7.4" };
            var list = new List<string>();

            // Laragon (very common on Windows)
            foreach (var v in versions)
            {
                list.Add($@"C:\laragon\bin\php\php{v.Replace(".", "")}\php.exe");
                list.Add($@"C:\laragon\bin\php\php{v.Replace(".", "")}.0\php.exe");
                list.Add($@"C:\laragon\bin\php\php{v}\php.exe");
            }

            // Standalone
            list.AddRange(new[]
            {
                @"C:\php\php.exe", @"C:\php8\php.exe",
                @"C:\php83\php.exe", @"C:\php82\php.exe", @"C:\php81\php.exe",
            });

            // XAMPP
            list.AddRange(new[]
            {
                @"C:\xampp\php\php.exe", @"C:\xampp8\php\php.exe",
                @"D:\xampp\php\php.exe",
            });

            // WAMP64
            foreach (var v in versions)
                list.Add($@"C:\wamp64\bin\php\php{v}.0\php.exe");

            // Program Files
            list.AddRange(new[]
            {
                @"C:\Program Files\PHP\php.exe",
                @"C:\Program Files (x86)\PHP\php.exe",
            });

            return list;
        }

        private async Task<string?> FindPhpInPathAsync()
        {
            try
            {
                var psi = new ProcessStartInfo("where", "php")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                };
                using var proc = Process.Start(psi);
                var cts = new CancellationTokenSource(3000);
                await proc!.WaitForExitAsync(cts.Token);
                var line = await proc.StandardOutput.ReadLineAsync();
                return !string.IsNullOrEmpty(line) && File.Exists(line.Trim()) ? line.Trim() : null;
            }
            catch { return null; }
        }

        private static string GetPhpVersion(string phpPath)
        {
            try
            {
                var psi = new ProcessStartInfo(phpPath, "-v")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                };
                using var proc = Process.Start(psi);
                proc!.WaitForExit(2000);
                var line = proc.StandardOutput.ReadLine() ?? "";
                // "PHP 8.2.12 ..." → "8.2.12"
                var parts = line.Split(' ');
                return parts.Length >= 2 ? parts[1] : "unknown";
            }
            catch { return "unknown"; }
        }

        // ── Main Entry: Open DB Panel ─────────────────────────────────────────

        public async Task<ToolSession?> OpenDatabasePanelAsync(
            string dbType, string host, int dbPort,
            string username = "root", string password = "", string database = "")
        {
            Log.Information("EmbeddedTools: opening panel for {DbType} on {Host}:{Port}", dbType, host, dbPort);

            // Mailpit has its own built-in web UI — no PHP needed
            if (dbType.Equals("mailpit", StringComparison.OrdinalIgnoreCase))
            {
                var mailpitUrl = $"http://localhost:{MailpitWebPort}";
                OpenUrl(mailpitUrl);
                return new ToolSession(mailpitUrl, MailpitWebPort, null, EmbeddedTool.MailpitWeb);
            }

            // All other DBs → phpMyAdmin
            return await OpenPhpMyAdminAsync(dbPort);
        }

        private async Task<ToolSession?> OpenPhpMyAdminAsync(int dbPort)
        {
            // 1. VALIDATION: PHP binaries must be present
            if (!IsPhpAvailable)
            {
                Notify("PHP runtime is required for phpMyAdmin. Please check Settings.");
                return null;
            }

            // 1.1 ENSURE PHP-FPM SERVICE IS RUNNING
            var phpFpm = _servicesOrchestrator.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.PhpFpm);
            if (phpFpm == null)
            {
                Notify("PHP-FPM service is not registered in Hostix.");
                return null;
            }

            if (phpFpm.Status != ServiceStatus.Running)
            {
                Notify("Starting PHP-FPM service for phpMyAdmin...");
                var started = await _servicesOrchestrator.StartAsync(phpFpm.Id);
                if (!started)
                {
                    Notify("Failed to start PHP-FPM service. phpMyAdmin unavailable.");
                    return null;
                }
                // Wait a bit for the socket to be ready
                await Task.Delay(1000);
            }

            var webServer = _servicesOrchestrator.Instances.FirstOrDefault(i => 
                (i.Type == RuntimeServiceType.Apache || i.Type == RuntimeServiceType.Nginx) && 
                i.Status == ServiceStatus.Running);

            if (webServer == null)
            {
                Notify("A running Web Server (Apache or Nginx) is required to serve phpMyAdmin.");
                return null;
            }

            var mysql = _servicesOrchestrator.Instances.FirstOrDefault(i => 
                (i.Type == RuntimeServiceType.MySQL || i.Type == RuntimeServiceType.MariaDB) && 
                i.Status == ServiceStatus.Running && i.Port == dbPort);

            if (mysql == null)
            {
                Notify($"MySQL/MariaDB server is not running on port {dbPort}.");
                return null;
            }

            // 2. RESOLUTION: Ensure phpMyAdmin files are present (Download if missing)
            if (!await EnsurePhpMyAdminAsync())
            {
                Notify("Failed to resolve phpMyAdmin files. Please check internet connection.");
                return null;
            }

            // 3. CONFIGURATION: Generate config.inc.php dynamically

            var (user, pass) = _credentialsManager.GetCredentials(mysql.Type);
            var config = "<?php\n" +
                         "$cfg['Servers'][1]['auth_type'] = 'config';\n" +
                         $"$cfg['Servers'][1]['host'] = '127.0.0.1';\n" +
                         $"$cfg['Servers'][1]['port'] = '{dbPort}';\n" +
                         $"$cfg['Servers'][1]['user'] = '{user}';\n" +
                         $"$cfg['Servers'][1]['password'] = '{pass}';\n" +
                         "$cfg['Servers'][1]['AllowNoPassword'] = true;\n" +
                         "$cfg['DefaultLang'] = 'en';\n" +
                         "$cfg['ServerDefault'] = 1;\n" +
                         "$cfg['blowfish_secret'] = 'hostix_secret_key_32_chars_long_123';\n";
            
            try { await File.WriteAllTextAsync(_pmaConfig, config); }
            catch (Exception ex) { Log.Error(ex, "Failed to write phpMyAdmin config."); }

            // 3. LAUNCH: Use the active web server port
            var url = $"http://localhost:{webServer.Port}/phpmyadmin/";
            Notify($"Opening phpMyAdmin on {webServer.Name} (Port {webServer.Port})...");
            OpenUrl(url);

            return new ToolSession(url, webServer.Port, null, EmbeddedTool.PhpMyAdmin);
        }

        private async Task<bool> EnsurePhpMyAdminAsync()
        {
            // 1. Initial Validation: If index.php and key directories exist, we are good.
            var indexPath     = Path.Combine(_pmaDir, "index.php");
            var librariesPath = Path.Combine(_pmaDir, "libraries");
            var vendorPath    = Path.Combine(_pmaDir, "vendor");

            if (File.Exists(indexPath) && Directory.Exists(librariesPath) && Directory.Exists(vendorPath))
            {
                Log.Debug("[phpMyAdmin] Physically present and validated at {Dir}.", _pmaDir);
                return true;
            }

            Notify("phpMyAdmin not found or incomplete. Provisioning stable bundle...");
            
            try
            {
                var zipPath = Path.Combine(_toolsRoot, "pma.zip");
                
                // Only download if zip doesn't exist
                if (!File.Exists(zipPath))
                {
                    Log.Information("[phpMyAdmin] Downloading distribution from {Url}", PmaDownloadUrl);
                    using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
                    {
                        http.DefaultRequestHeaders.UserAgent.ParseAdd("Hostix/1.0");
                        var bytes = await http.GetByteArrayAsync(PmaDownloadUrl);
                        await File.WriteAllBytesAsync(zipPath, bytes);
                    }
                }

                Notify("Extracting phpMyAdmin distribution...");
                var extractPath = Path.Combine(_toolsRoot, "pma_temp");
                if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                Directory.CreateDirectory(extractPath);

                // Extract using PowerShell for maximum reliability on Windows
                var psi = new ProcessStartInfo("powershell", $"-Command \"Expand-Archive -Path '{zipPath}' -DestinationPath '{extractPath}' -Force\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                var proc = Process.Start(psi);
                if (proc != null) await proc.WaitForExitAsync();

                // phpMyAdmin zip contains a subfolder (e.g. phpMyAdmin-5.2.3-all-languages)
                var subDir = Directory.GetDirectories(extractPath).FirstOrDefault();
                if (subDir != null)
                {
                    // CRITICAL: Ensure target is GONE before move to prevent nesting
                    if (Directory.Exists(_pmaDir)) 
                    {
                        Log.Warning("[phpMyAdmin] Cleaning up existing target directory at {Dir}", _pmaDir);
                        Directory.Delete(_pmaDir, true);
                    }
                    
                    Directory.Move(subDir, _pmaDir);
                    Log.Information("[phpMyAdmin] Successfully moved files to {Dir}", _pmaDir);
                }

                // 2. Final Validation Check
                if (File.Exists(indexPath) && Directory.Exists(librariesPath) && Directory.Exists(vendorPath))
                {
                    Log.Information("[phpMyAdmin] Installation validated successfully.");
                    
                    // Cleanup temporary artifacts
                    if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                    if (File.Exists(zipPath)) File.Delete(zipPath);

                    Notify("phpMyAdmin is ready.");
                    return true;
                }
                else
                {
                    Log.Error("[phpMyAdmin] Extraction failed validation. Target directory structure is invalid.");
                    Notify("Error: phpMyAdmin installation failed validation.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to provision phpMyAdmin.");
                Notify("Error: phpMyAdmin provisioning failed. Check logs.");
                return false;
            }
        }



        // ── Process Management ────────────────────────────────────────────────

        private Process? StartPhpServer(string phpPath, int port, string scriptPath, string workDir)
        {
            try
            {
                var psi = new ProcessStartInfo(
                    phpPath,
                    $"-S 127.0.0.1:{port} \"{scriptPath}\"")
                {
                    WorkingDirectory       = workDir,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError  = false,
                };
                return Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start PHP server.");
                return null;
            }
        }

        public async Task StopToolAsync(EmbeddedTool tool)
        {
            if (_sessions.TryRemove(tool, out var session))
            {
                try
                {
                    if (session.Process != null && !session.Process.HasExited)
                        session.Process.Kill(entireProcessTree: true);
                    Notify($"{tool} stopped.");
                    Log.Information("Tool {Tool} stopped.", tool);
                }
                catch (Exception ex) { Log.Warning(ex, "Could not stop tool {Tool}.", tool); }
            }
            await Task.CompletedTask;
        }

        public async Task StopAllToolsAsync()
        {
            foreach (var tool in _sessions.Keys.ToList())
                await StopToolAsync(tool);
        }

        // ── Adminer Download ──────────────────────────────────────────────────



        // ── Utilities ─────────────────────────────────────────────────────────

        private static int FindFreePort(int startPort)
        {
            for (var port = startPort; port < startPort + 100; port++)
            {
                try
                {
                    using var listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    listener.Stop();
                    return port;
                }
                catch { /* port in use, try next */ }
            }
            return startPort; // fallback
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warning(ex, "Could not open URL: {Url}", url); }
        }

        private void Notify(string msg)
        {
            Log.Information("[EmbeddedTools] {Msg}", msg);
            OnStatusMessage?.Invoke(msg);
        }

        public void Dispose() => _ = StopAllToolsAsync();
    }
}
