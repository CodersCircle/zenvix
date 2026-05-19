using System;
using System.IO;
using System.Linq;
using System.Text;
using Hostix.Core.Models;
using Hostix.Core.Services;
using Serilog;

namespace Hostix.Runtime.Services
{
    public interface IRuntimeConfigGenerator
    {
        string GenerateAndSave(RuntimeServiceInstance inst, RuntimeMetadata metadata);
        string GenerateVHost(Website website, RuntimeMetadata nginxMetadata, int phpPort = 9000);
    }

    public class RuntimeConfigGenerator : IRuntimeConfigGenerator
    {
        private readonly string _configRoot;
        private readonly ISSLManager _ssl;

        public RuntimeConfigGenerator(ISSLManager ssl)
        {
            _ssl = ssl;
            _configRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "generated");
            if (!Directory.Exists(_configRoot)) Directory.CreateDirectory(_configRoot);

            // Purge stale vhosts on startup to prevent Nginx from loading old/broken configs
            var vhostDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "generated", "vhosts");
            if (Directory.Exists(vhostDir))
            {
                try { Directory.Delete(vhostDir, true); } catch { }
            }
            Directory.CreateDirectory(vhostDir);
        }

        private string GetLocalIPAddress()
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        return ip.ToString();
                    }
                }
            }
            catch { }
            return "127.0.0.1";
        }

        public string GenerateAndSave(RuntimeServiceInstance inst, RuntimeMetadata metadata)
        {
            var content = inst.Type switch
            {
                RuntimeServiceType.Nginx => GenerateNginx(inst, metadata),
                RuntimeServiceType.Apache => GenerateApache(inst, metadata),
                RuntimeServiceType.MySQL => GenerateMySQL(inst, metadata),
                RuntimeServiceType.MariaDB => GenerateMySQL(inst, metadata),
                RuntimeServiceType.PhpFpm => GeneratePhp(inst, metadata),
                _ => ""
            };

            if (string.IsNullOrEmpty(content)) return metadata.ConfigPath ?? "";

            var fileName = $"{inst.Type.ToString().ToLower()}_{inst.Id.ToString().Substring(0, 8)}.conf";
            if (inst.Type == RuntimeServiceType.MySQL) fileName = fileName.Replace(".conf", ".ini");
            
            var fullPath = Path.Combine(_configRoot, fileName);
            File.WriteAllText(fullPath, content);
            
            Log.Debug("[ConfigGenerator] Generated {Type} config at {Path}", inst.Type, fullPath);
            return fullPath;
        }

        public string GenerateVHost(Website website, RuntimeMetadata nginxMetadata, int phpPort = 9000)
        {
            var vhostDir = Path.Combine(_configRoot, "vhosts");
            if (!Directory.Exists(vhostDir)) Directory.CreateDirectory(vhostDir);

            var sb = new StringBuilder();
            
            var localIp = GetLocalIPAddress();
            
            var isCertValid = false;
            var certPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ssl", "certs", website.Domain, "server.crt");
            var keyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ssl", "certs", website.Domain, "server.key");

            if (website.SslEnabled)
            {
                try
                {
                    Log.Information("[ConfigGenerator] Validating SSL assets for {Domain} at {Path}", website.Domain, certPath);
                    
                    var certInfo = new FileInfo(certPath);
                    var keyInfo = new FileInfo(keyPath);

                    if (certInfo.Exists && keyInfo.Exists && certInfo.Length > 0 && keyInfo.Length > 0)
                    {
                        // 1. Mandatory Header Validation
                        var certHeader = File.ReadLines(certPath).FirstOrDefault();
                        var keyHeader = File.ReadLines(keyPath).FirstOrDefault();

                        if (certHeader != null && certHeader.Contains("-----BEGIN CERTIFICATE-----") &&
                            keyHeader != null && (keyHeader.Contains("-----BEGIN RSA PRIVATE KEY-----") || keyHeader.Contains("-----BEGIN PRIVATE KEY-----")))
                        {
                            // 2. Cryptographic Integrity Check
                            using var testCert = new System.Security.Cryptography.X509Certificates.X509Certificate2(certPath);
                            isCertValid = true;
                            website.SslDiagnosticMessage = "SSL Active (Hostix CA Trusted)";
                            Log.Information("[ConfigGenerator] SSL validation PASSED for {Domain}.", website.Domain);
                        }
                        else
                        {
                            website.SslDiagnosticMessage = "SSL disabled: Invalid PEM headers.";
                            Log.Warning("[ConfigGenerator] SSL validation FAILED: Invalid PEM headers for {Domain}.", website.Domain);
                        }
                    }
                    else
                    {
                        website.SslDiagnosticMessage = "SSL disabled: Missing certificate files.";
                        Log.Warning("[ConfigGenerator] SSL validation FAILED: Missing or empty cert/key files for {Domain}.", website.Domain);
                    }
                }
                catch (Exception ex)
                {
                    website.SslDiagnosticMessage = $"SSL disabled: {ex.Message}";
                    Log.Warning("[ConfigGenerator] SSL validation FAILED: {Msg}. Falling back to HTTP.", ex.Message);
                    isCertValid = false;
                }
            }
            else
            {
                website.SslDiagnosticMessage = null;
            }

            Log.Warning("[ConfigGenerator] SSL VALIDATION RESULT for {Domain}: {Result}", website.Domain, isCertValid);

            var rootPath = website.LocalPath.Replace("\\", "/");
            if (website.Type == ProjectType.Laravel)
            {
                rootPath = Path.Combine(website.LocalPath, "public").Replace("\\", "/");
            }

            if (website.SslEnabled && isCertValid)
            {
                Log.Warning("[ConfigGenerator] GENERATING SSL BLOCK for {Domain}", website.Domain);
                
                // 1. HTTP Server - Redirect to HTTPS only for custom Domain
                sb.AppendLine("server {");
                sb.AppendLine("    listen 80;");
                sb.AppendLine($"    server_name {website.Domain};");
                sb.AppendLine("    return 301 https://$host$request_uri;");
                sb.AppendLine("}");
                sb.AppendLine();

                // 2. HTTP Server - Serve content directly over HTTP for Local IP
                sb.AppendLine("server {");
                sb.AppendLine("    listen 80;");
                sb.AppendLine($"    server_name {localIp};");
                sb.AppendLine($"    root \"{rootPath}\";");
                sb.AppendLine("    index index.php index.html;");
                sb.AppendLine("    location / { try_files $uri $uri/ /index.php?$query_string; }");
                sb.AppendLine("    location ~ \\.php$ {");
                sb.AppendLine($"        fastcgi_pass 127.0.0.1:{phpPort};");
                sb.AppendLine("        fastcgi_index index.php;");
                sb.AppendLine("        fastcgi_param SCRIPT_FILENAME $document_root$fastcgi_script_name;");
                sb.AppendLine("        include fastcgi_params;");
                sb.AppendLine("    }");
                sb.AppendLine("}");
                sb.AppendLine();

                // 3. HTTPS Server - Serve content over HTTPS for Domain and Local IP
                sb.AppendLine("server {");
                sb.AppendLine("    listen 443 ssl;");
                sb.AppendLine($"    server_name {website.Domain} {localIp};");

                var nxtCertPath = certPath.Replace("\\", "/");
                var nxtKeyPath = keyPath.Replace("\\", "/");

                sb.AppendLine($"    ssl_certificate \"{nxtCertPath}\";");
                sb.AppendLine($"    ssl_certificate_key \"{nxtKeyPath}\";");
                sb.AppendLine("    ssl_protocols TLSv1.2 TLSv1.3;");
                sb.AppendLine($"    root \"{rootPath}\";");
                sb.AppendLine("    index index.php index.html;");
                sb.AppendLine("    location / { try_files $uri $uri/ /index.php?$query_string; }");
                sb.AppendLine("    location ~ \\.php$ {");
                sb.AppendLine($"        fastcgi_pass 127.0.0.1:{phpPort};");
                sb.AppendLine("        fastcgi_index index.php;");
                sb.AppendLine("        fastcgi_param SCRIPT_FILENAME $document_root$fastcgi_script_name;");
                sb.AppendLine("        include fastcgi_params;");
                sb.AppendLine("    }");
                sb.AppendLine("}");
            }
            else
            {
                if (website.SslEnabled)
                {
                    Log.Warning("[ConfigGenerator] SSL validation FAILED for {Domain}. FALLING BACK TO HTTP-ONLY.", website.Domain);
                }

                // Plain HTTP Server for Domain and Local IP
                sb.AppendLine("server {");
                sb.AppendLine("    listen 80;");
                sb.AppendLine($"    server_name {website.Domain} {localIp};");
                sb.AppendLine($"    root \"{rootPath}\";");
                sb.AppendLine("    index index.php index.html;");
                sb.AppendLine("    location / { try_files $uri $uri/ /index.php?$query_string; }");
                sb.AppendLine("    location ~ \\.php$ {");
                sb.AppendLine($"        fastcgi_pass 127.0.0.1:{phpPort};");
                sb.AppendLine("        fastcgi_index index.php;");
                sb.AppendLine("        fastcgi_param SCRIPT_FILENAME $document_root$fastcgi_script_name;");
                sb.AppendLine("        include fastcgi_params;");
                sb.AppendLine("    }");
                sb.AppendLine("}");
            }

            var filePath = Path.Combine(vhostDir, $"{website.Domain}.conf");
            var content = sb.ToString();
            
            Log.Warning("[ConfigGenerator] FINAL VHOST CONTENT for {Domain}:\n{Content}", website.Domain, content);
            
            File.WriteAllText(filePath, content);
            Log.Information("[ConfigGenerator] Generated VHost for {Domain} at {Path}", website.Domain, filePath);
            return filePath;
        }

        private string GenerateNginx(RuntimeServiceInstance inst, RuntimeMetadata meta)
        {
            var runtimeRoot = meta.RootDir ?? Path.GetDirectoryName(meta.ExecutablePath) ?? "";
            var vhostDir = Path.Combine(_configRoot, "vhosts");
            
            if (!Directory.Exists(vhostDir)) Directory.CreateDirectory(vhostDir);

            var candidates = new[] {
                Path.Combine(runtimeRoot, "conf"),
                runtimeRoot,
                Path.Combine(meta.BinDir ?? "", "conf"),
                meta.BinDir ?? ""
            };

            // 1. Try standard locations for mime.types and fastcgi_params
            var mimePath = candidates.Select(c => Path.Combine(c, "mime.types")).FirstOrDefault(File.Exists);
            if (mimePath == null && Directory.Exists(runtimeRoot))
            {
                mimePath = Directory.GetFiles(runtimeRoot, "mime.types", SearchOption.AllDirectories).FirstOrDefault();
            }
            if (mimePath == null)
            {
                var checkPath = Path.Combine(_configRoot, "mime.types");
                if (File.Exists(checkPath)) mimePath = checkPath;
            }
            if (mimePath == null)
            {
                var runtimesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtimes");
                if (Directory.Exists(runtimesDir))
                {
                    mimePath = Directory.GetFiles(runtimesDir, "mime.types", SearchOption.AllDirectories).FirstOrDefault();
                }
            }

            var fastcgiParamsPath = candidates.Select(c => Path.Combine(c, "fastcgi_params")).FirstOrDefault(File.Exists);
            if (fastcgiParamsPath == null && Directory.Exists(runtimeRoot))
            {
                fastcgiParamsPath = Directory.GetFiles(runtimeRoot, "fastcgi_params", SearchOption.AllDirectories).FirstOrDefault();
            }
            if (fastcgiParamsPath == null)
            {
                var checkPath = Path.Combine(_configRoot, "fastcgi_params");
                if (File.Exists(checkPath)) fastcgiParamsPath = checkPath;
            }
            if (fastcgiParamsPath == null)
            {
                var runtimesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtimes");
                if (Directory.Exists(runtimesDir))
                {
                    fastcgiParamsPath = Directory.GetFiles(runtimesDir, "fastcgi_params", SearchOption.AllDirectories).FirstOrDefault();
                }
            }

            if (mimePath == null) throw new Exception("Nginx critical file 'mime.types' not found.");
            if (fastcgiParamsPath == null) throw new Exception("Nginx critical file 'fastcgi_params' not found.");

            // 2. Copy critical files to generated config directory for relative include stability
            var genMime = Path.Combine(_configRoot, "mime.types");
            var genFastCgi = Path.Combine(_configRoot, "fastcgi_params");
            
            try
            {
                if (!File.Exists(genMime) || new FileInfo(mimePath).LastWriteTime > new FileInfo(genMime).LastWriteTime)
                    File.Copy(mimePath, genMime, true);
                
                if (!File.Exists(genFastCgi) || new FileInfo(fastcgiParamsPath).LastWriteTime > new FileInfo(genFastCgi).LastWriteTime)
                    File.Copy(fastcgiParamsPath, genFastCgi, true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Nginx] Failed to copy critical files to generated dir. Falling back to absolute paths.");
            }

            mimePath = Path.GetFullPath(mimePath).Replace("\\", "/");
            fastcgiParamsPath = Path.GetFullPath(fastcgiParamsPath).Replace("\\", "/");
            
            Log.Information("[Nginx] Resolved paths: mime={Mime}, fastcgi={FCGI}", mimePath, fastcgiParamsPath);
            
            vhostDir = Path.GetFullPath(vhostDir).Replace("\\", "/");

            var sb = new StringBuilder();
            sb.AppendLine("worker_processes 1;");
            sb.AppendLine("events { worker_connections 1024; }");
            sb.AppendLine("http {");
            sb.AppendLine("    include mime.types;"); // Now relative to config file!
            sb.AppendLine("    default_type application/octet-stream;");
            sb.AppendLine("    sendfile on;");
            sb.AppendLine("    keepalive_timeout 65;");
            
            // Include virtual hosts FIRST for routing priority
            sb.AppendLine($"    include \"{vhostDir}/*.conf\";");
            
            // 3. System Default Server (Catch-all)
            sb.AppendLine($"    server {{");
            sb.AppendLine($"        listen {inst.Port} default_server;");
            sb.AppendLine("        server_name _;");
            sb.AppendLine("        location / {");
            sb.AppendLine("            return 404 \"Hostix: Site Not Found. The domain is reached Nginx but is not matched to any project.\";");
            sb.AppendLine("            add_header Content-Type text/plain;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");

            // 4. Localhost / System Panel Server
            var localIp = GetLocalIPAddress();
            sb.AppendLine($"    server {{ listen {inst.Port}; server_name localhost {localIp};");
            sb.AppendLine("        location / { root html; index index.php index.html; }");
 
            // ── phpMyAdmin Location ───────────────────────────────────────────
            var pmaRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "phpmyadmin").Replace("\\", "/");
            sb.AppendLine($"        location /phpmyadmin {{");
            sb.AppendLine($"            alias \"{pmaRoot}/\";");
            sb.AppendLine("            index index.php;");
            sb.AppendLine("        }");
            sb.AppendLine($"        location ~ ^/phpmyadmin/(.+\\.php)$ {{");
            sb.AppendLine($"            alias \"{pmaRoot}/$1\";");
            sb.AppendLine("            fastcgi_pass 127.0.0.1:9000;");
            sb.AppendLine("            fastcgi_index index.php;");
            sb.AppendLine("            fastcgi_param SCRIPT_FILENAME $request_filename;");
            sb.AppendLine("            include fastcgi_params;");
            sb.AppendLine("        }");
 
            sb.AppendLine("        location ~ \\.php$ {");
            sb.AppendLine("            fastcgi_pass 127.0.0.1:9000;");
            sb.AppendLine("            fastcgi_index index.php;");
            sb.AppendLine("            fastcgi_param SCRIPT_FILENAME $document_root$fastcgi_script_name;");
            sb.AppendLine("            include fastcgi_params;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string GenerateMySQL(RuntimeServiceInstance inst, RuntimeMetadata meta)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[mysqld]");
            sb.AppendLine($"port={inst.Port}");
            sb.AppendLine($"datadir=\"{inst.DataPath.Replace("\\", "/")}\"");
            sb.AppendLine("bind-address=127.0.0.1");
            sb.AppendLine("max_connections=100");
            sb.AppendLine("innodb_buffer_pool_size=128M");
            sb.AppendLine("innodb_flush_log_at_trx_commit=1");
            sb.AppendLine("innodb_log_buffer_size=8M");
            sb.AppendLine("innodb_lock_wait_timeout=50");
            return sb.ToString();
        }

        private string GenerateApache(RuntimeServiceInstance inst, RuntimeMetadata meta)
        {
            var serverRoot = meta.RootDir!.Replace("\\", "/");
            var htdocs = Path.Combine(meta.RootDir!, "htdocs").Replace("\\", "/");
            if (!Directory.Exists(htdocs)) Directory.CreateDirectory(htdocs);
            
            var logs = Path.Combine(meta.RootDir!, "logs").Replace("\\", "/");
            if (!Directory.Exists(logs)) Directory.CreateDirectory(logs);

            var sb = new StringBuilder();
            sb.AppendLine($"ServerRoot \"{serverRoot}\"");
            sb.AppendLine($"Listen {inst.Port}");
            var localIp = GetLocalIPAddress();
            sb.AppendLine($"ServerName localhost");
            
            // Essential modules for Hostix runtimes
            sb.AppendLine("LoadModule authz_core_module modules/mod_authz_core.so");
            sb.AppendLine("LoadModule authz_host_module modules/mod_authz_host.so");
            sb.AppendLine("LoadModule dir_module modules/mod_dir.so");
            sb.AppendLine("LoadModule mime_module modules/mod_mime.so");
            sb.AppendLine("LoadModule log_config_module modules/mod_log_config.so");
            sb.AppendLine("LoadModule alias_module modules/mod_alias.so");
            sb.AppendLine("LoadModule proxy_module modules/mod_proxy.so");
            sb.AppendLine("LoadModule proxy_fcgi_module modules/mod_proxy_fcgi.so");
            sb.AppendLine("LoadModule negotiation_module modules/mod_negotiation.so");
            sb.AppendLine("LoadModule rewrite_module modules/mod_rewrite.so");
            
            // FastCGI backend type set to GENERIC for standard Windows path handling
            sb.AppendLine("ProxyFCGIBackendType GENERIC");
            
            sb.AppendLine($"DocumentRoot \"{htdocs}\"");
            sb.AppendLine($"ErrorLog \"logs/error.log\"");
            sb.AppendLine("DirectoryIndex index.php index.html");
            sb.AppendLine("AddDefaultCharset UTF-8");
            
            // Diagnostic Logging for SCRIPT_FILENAME, DOCUMENT_ROOT, and PATH_TRANSLATED
            sb.AppendLine("LogFormat \"%h %l %u %t \\\"%r\\\" %>s %b \\\"%{SCRIPT_FILENAME}e\\\" \\\"%{DOCUMENT_ROOT}e\\\" \\\"%{PATH_TRANSLATED}e\\\"\" diag_format");
            sb.AppendLine("CustomLog \"logs/access_diag.log\" diag_format");
            
            sb.AppendLine($"<Directory \"{htdocs}\">");
            sb.AppendLine("    Options Indexes FollowSymLinks");
            sb.AppendLine("    AllowOverride All");
            sb.AppendLine("    Require all granted");
            sb.AppendLine("    <FilesMatch \\.php$>");
            sb.AppendLine("        SetHandler \"proxy:fcgi://127.0.0.1:9000/\"");
            sb.AppendLine("        ProxyFCGISetEnvIf \"reqenv('SCRIPT_FILENAME') =~ m#([A-Za-z]:/.*)$#\" SCRIPT_FILENAME \"$1\"");
            sb.AppendLine("    </FilesMatch>");
            sb.AppendLine("</Directory>");

            // ── phpMyAdmin Configuration ─────────────────────────────────────
            var pmaRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "phpmyadmin").Replace("\\", "/");
            var pmaIndex = Path.Combine(pmaRoot, "index.php").Replace("\\", "/");

            if (File.Exists(pmaIndex))
            {
                sb.AppendLine($"Alias /phpmyadmin \"{pmaRoot}\"");
                
                sb.AppendLine($"<Directory \"{pmaRoot}\">");
                sb.AppendLine("    Options Indexes FollowSymLinks MultiViews");
                sb.AppendLine("    AllowOverride All");
                sb.AppendLine("    Require all granted");
                sb.AppendLine("    DirectoryIndex index.php");
                sb.AppendLine("    <FilesMatch \\.php$>");
                sb.AppendLine("        SetHandler \"proxy:fcgi://127.0.0.1:9000/\"");
                sb.AppendLine("        ProxyFCGISetEnvIf \"reqenv('SCRIPT_FILENAME') =~ m#([A-Za-z]:/.*)$#\" SCRIPT_FILENAME \"$1\"");
                sb.AppendLine("    </FilesMatch>");
                sb.AppendLine("</Directory>");
            }
            else
            {
                Log.Warning("[Apache-Config] phpMyAdmin not found at {Path}. Alias skipped.", pmaRoot);
            }
            
            // Add mime.types if it exists
            var mimeFile = Path.Combine(meta.RootDir!, "conf", "mime.types").Replace("\\", "/");
            if (File.Exists(mimeFile))
                sb.AppendLine($"TypesConfig \"{mimeFile}\"");

            return sb.ToString();
        }

        private string GeneratePhp(RuntimeServiceInstance inst, RuntimeMetadata meta)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[PHP]");
            sb.AppendLine("memory_limit = 256M");
            sb.AppendLine("upload_max_filesize = 100M");
            sb.AppendLine("post_max_size = 100M");
            sb.AppendLine("max_execution_time = 360");
            sb.AppendLine("display_errors = On");
            sb.AppendLine("error_reporting = E_ALL");
            
            var binDir = meta.BinDir!.Replace("\\", "/");
            var extDir = Path.Combine(binDir, "ext").Replace("\\", "/");
            
            sb.AppendLine($"extension_dir = \"{extDir}\"");
            
            // Standard extensions (mysqlnd MUST be loaded before mysqli/pdo_mysql in some builds)
            sb.AppendLine("extension=mysqlnd");
            sb.AppendLine("extension=curl");
            sb.AppendLine("extension=mbstring");
            sb.AppendLine("extension=openssl");
            sb.AppendLine("extension=pdo_mysql");
            sb.AppendLine("extension=mysqli");
            sb.AppendLine("extension=fileinfo");
            sb.AppendLine("extension=gettext");
            sb.AppendLine("extension=gd");
            sb.AppendLine("extension=sqlite3");
            sb.AppendLine("extension=pdo_sqlite");
            sb.AppendLine("extension=zip");
            sb.AppendLine("extension=exif");
            sb.AppendLine("extension=sodium");
            sb.AppendLine("extension=intl");
            
            // Windows-specific explicit DLL loading (redundant but safe)
            sb.AppendLine("extension=php_mysqlnd.dll");
            sb.AppendLine("extension=php_curl.dll");
            sb.AppendLine("extension=php_mbstring.dll");
            sb.AppendLine("extension=php_openssl.dll");
            sb.AppendLine("extension=php_pdo_mysql.dll");
            sb.AppendLine("extension=php_mysqli.dll");
            sb.AppendLine("extension=php_sqlite3.dll");
            sb.AppendLine("extension=php_pdo_sqlite.dll");
            sb.AppendLine("extension=php_zip.dll");
            sb.AppendLine("extension=php_exif.dll");
            sb.AppendLine("extension=php_sodium.dll");
            sb.AppendLine("extension=php_intl.dll");

            return sb.ToString();
        }
    }
}
