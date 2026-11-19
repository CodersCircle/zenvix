using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Zenvix.Core.Models;
using Zenvix.Core.Services;
using Serilog;

namespace Zenvix.Runtime.Services
{
    public class RuntimeLocator : IRuntimeLocator
    {
        private readonly List<string> _discoveryLogs = new();
        private readonly string _runtimesRoot;

        public RuntimeLocator()
        {
            _runtimesRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtimes");
        }

        // Scans are cached until the runtimes folder layout changes (a runtime installed or removed).
        private List<RuntimeMetadata>? _cache;
        private string? _cacheFingerprint;
        private readonly SemaphoreSlim _scanLock = new(1, 1);

        private string ComputeFingerprint()
        {
            if (!Directory.Exists(_runtimesRoot)) return "";
            // runtimes/<type>/<version> folder names + binary presence are enough to detect installs.
            return string.Join("|", Directory.GetDirectories(_runtimesRoot)
                .SelectMany(t => Directory.GetDirectories(t).Select(v => v + ":" + Directory.GetFileSystemEntries(v).Length))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        }

        public async Task<List<RuntimeMetadata>> DiscoverAllAsync()
        {
            await _scanLock.WaitAsync();
            try
            {
                var fingerprint = ComputeFingerprint();
                if (_cache != null && fingerprint == _cacheFingerprint) return _cache.ToList();

                _cache = await ScanAllAsync();
                _cacheFingerprint = fingerprint;
                return _cache.ToList();
            }
            finally { _scanLock.Release(); }
        }

        private async Task<List<RuntimeMetadata>> ScanAllAsync()
        {
            _discoveryLogs.Clear();
            var results = new List<RuntimeMetadata>();

            // 1. Zenvix Internal Runtimes (THE Authoritative Source)
            results.AddRange(await ScanDirectoryAsync(_runtimesRoot, "Zenvix (Internal)"));

            // 2. External stacks (XAMPP, system PATH) are NOT used by default: Zenvix runs its own
            //    runtimes and stops other stacks that block its ports. Borrowing e.g. XAMPP's
            //    MariaDB with MySQL-only init flags (--initialize-insecure) breaks database setup.
            bool allowExternal = false; // TODO: expose as a setting

            if (allowExternal)
            {
                results.AddRange(await ScanXamppAsync());
                results.AddRange(await ScanSystemPathsAsync());
            }

            return results.OrderBy(r => GetPriority(r.InstallSource)).ToList();
        }

        public async Task<RuntimeMetadata?> FindBestAsync(RuntimeServiceType type)
        {
            var all = await DiscoverAllAsync();
            var best = all.Where(r => r.Type == type)
                          .OrderBy(r => GetPriority(r.InstallSource))
                          .ThenByDescending(r => {
                              if (Version.TryParse(r.Version, out var pv)) return pv;
                              // Try to match partial versions like "8.3" -> "8.3.0"
                              var clean = System.Text.RegularExpressions.Regex.Replace(r.Version, @"[^\d\.]", "");
                              if (Version.TryParse(clean, out var pv2)) return pv2;
                              return new Version(0, 0);
                          })
                          .FirstOrDefault();
            
            if (best != null)
                LogDiscovery($"[SELECTED] Found best {type} (Version: {best.Version}): {best.ExecutablePath} (Source: {best.InstallSource})");
            else
                LogDiscovery($"[MISSING] No runtime found for {type}");

            return best;
        }

        public List<string> GetDiscoveryLogs() => _discoveryLogs.ToList();

        private void LogDiscovery(string msg)
        {
            _discoveryLogs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
            Log.Debug(msg);
        }

        private async Task<List<RuntimeMetadata>> ScanLaragonAsync()
        {
            var laragonRoot = @"C:\laragon\bin";
            if (!Directory.Exists(laragonRoot)) return new List<RuntimeMetadata>();

            LogDiscovery($"Scanning Laragon at {laragonRoot}...");
            return await ScanDirectoryAsync(laragonRoot, "Laragon");
        }

        private async Task<List<RuntimeMetadata>> ScanXamppAsync()
        {
            var xamppRoot = @"C:\xampp";
            if (!Directory.Exists(xamppRoot)) return new List<RuntimeMetadata>();

            LogDiscovery($"Scanning XAMPP at {xamppRoot}...");
            var results = new List<RuntimeMetadata>();
            
            // XAMPP is less version-structured, check known subfolders
            results.AddRange(await CheckBinaryAsync(Path.Combine(xamppRoot, @"apache\bin\httpd.exe"), RuntimeServiceType.Apache, "XAMPP"));
            results.AddRange(await CheckBinaryAsync(Path.Combine(xamppRoot, @"mysql\bin\mysqld.exe"), RuntimeServiceType.MySQL, "XAMPP"));
            results.AddRange(await CheckBinaryAsync(Path.Combine(xamppRoot, @"php\php-cgi.exe"), RuntimeServiceType.PhpFpm, "XAMPP"));
            
            return results;
        }

        private async Task<List<RuntimeMetadata>> ScanSystemPathsAsync()
        {
            var results = new List<RuntimeMetadata>();
            var common = new[]
            {
                @"C:\Program Files\MySQL",
                @"C:\Program Files\MariaDB",
                @"C:\Program Files\nginx",
                @"C:\nginx",
                @"C:\Apache24",
                @"C:\php"
            };

            foreach (var path in common)
            {
                if (Directory.Exists(path))
                    results.AddRange(await ScanDirectoryAsync(path, "System"));
            }

            return results;
        }

        private async Task<List<RuntimeMetadata>> ScanDirectoryAsync(string root, string source)
        {
            var results = new List<RuntimeMetadata>();
            if (!Directory.Exists(root)) return results;

            try
            {
                var subDirs = Directory.GetDirectories(root);
                foreach (var dir in subDirs)
                {
                    var dirName = Path.GetFileName(dir).ToLower();
                    var type = DetectTypeFromFolderName(dirName);

                    if (type.HasValue)
                    {
                        var folderResults = new List<RuntimeMetadata>();

                        // 1. Check for versioned subfolders FIRST (Zenvix style: root/php/8.3.6/php-cgi.exe)
                        var versionDirs = Directory.GetDirectories(dir);
                        foreach (var vDir in versionDirs)
                        {
                            folderResults.AddRange(await DiscoverInFolderAsync(vDir, type.Value, source));
                        }

                        // 2. Fallback to immediate binary ONLY if no versioned ones found
                        if (!folderResults.Any())
                        {
                            folderResults.AddRange(await DiscoverInFolderAsync(dir, type.Value, source));
                        }

                        results.AddRange(folderResults);
                    }
                }
            }
            catch (Exception ex)
            {
                LogDiscovery($"Error scanning {root}: {ex.Message}");
            }

            return results;
        }

        private RuntimeServiceType? DetectTypeFromFolderName(string dirName)
        {
            dirName = dirName.ToLower();
            if (dirName.Contains("nginx")) return RuntimeServiceType.Nginx;
            if (dirName.Contains("apache") || dirName.Contains("httpd")) return RuntimeServiceType.Apache;
            if (dirName.Contains("mysql")) return RuntimeServiceType.MySQL;
            if (dirName.Contains("mariadb")) return RuntimeServiceType.MariaDB;
            if (dirName.Contains("php")) return RuntimeServiceType.PhpFpm;
            if (dirName.Contains("redis")) return RuntimeServiceType.Redis;
            if (dirName.Contains("mailpit")) return RuntimeServiceType.Mailpit;
            return null;
        }

        private async Task<List<RuntimeMetadata>> DiscoverInFolderAsync(string folder, RuntimeServiceType type, string source)
        {
            var results = new List<RuntimeMetadata>();
            
            // 1. Search for binary
            var binaryName = GetBinaryNameForType(type);
            var searchPaths = new[]
            {
                Path.Combine(folder, binaryName),
                Path.Combine(folder, "bin", binaryName)
            };

            foreach (var binPath in searchPaths)
            {
                if (File.Exists(binPath))
                {
                    var meta = new RuntimeMetadata
                    {
                        Name = type.ToString(),
                        Type = type,
                        ExecutablePath = binPath,
                        InstallSource = source,
                        Version = ExtractVersion(folder)
                    };
                    meta.ConfigPath = DetectConfig(meta);
                    
                    results.Add(meta);
                    LogDiscovery($"[FOUND] {type} ({meta.Version}) at {binPath} [Source: {source}]");
                }
            }

            return results;
        }

        private async Task<List<RuntimeMetadata>> CheckBinaryAsync(string path, RuntimeServiceType type, string source)
        {
            var list = new List<RuntimeMetadata>();
            if (File.Exists(path))
            {
                var meta = new RuntimeMetadata
                {
                    Name = type.ToString(),
                    Type = type,
                    ExecutablePath = path,
                    InstallSource = source,
                    Version = ExtractVersion(Path.GetDirectoryName(path) ?? "")
                };
                meta.ConfigPath = DetectConfig(meta);
                list.Add(meta);
                LogDiscovery($"[FOUND] {type} at {path} [Source: {source}]");
            }
            return list;
        }

        private string GetBinaryNameForType(RuntimeServiceType type)
        {
            return type switch
            {
                RuntimeServiceType.Nginx => "nginx.exe",
                RuntimeServiceType.Apache => "httpd.exe",
                RuntimeServiceType.MySQL => "mysqld.exe",
                RuntimeServiceType.MariaDB => "mysqld.exe",
                RuntimeServiceType.PhpFpm => "php-cgi.exe",
                RuntimeServiceType.Redis => "redis-server.exe",
                RuntimeServiceType.Mailpit => "mailpit.exe",
                _ => "unknown.exe"
            };
        }

        private string ExtractVersion(string path)
        {
            var name = Path.GetFileName(path);
            // Match things like 8.4.3, 9.6.0, 1.28 etc.
            var match = Regex.Match(name, @"(\d+\.\d+(\.\d+)?)");
            return match.Success ? match.Value : "Unknown";
        }

        private string? DetectConfig(RuntimeMetadata meta)
        {
            var binDir = meta.BinDir!;
            var rootDir = meta.RootDir!;

            var candidates = meta.Type switch
            {
                RuntimeServiceType.Nginx => new[] { Path.Combine(rootDir, "conf", "nginx.conf"), Path.Combine(binDir, "nginx.conf") },
                RuntimeServiceType.Apache => new[] { Path.Combine(rootDir, "conf", "httpd.conf"), Path.Combine(binDir, "httpd.conf") },
                RuntimeServiceType.MySQL => new[] { Path.Combine(rootDir, "my.ini"), Path.Combine(binDir, "my.ini") },
                RuntimeServiceType.MariaDB => new[] { Path.Combine(rootDir, "my.ini"), Path.Combine(binDir, "my.ini") },
                RuntimeServiceType.PhpFpm => new[] { Path.Combine(rootDir, "php.ini"), Path.Combine(binDir, "php.ini") },
                _ => Array.Empty<string>()
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        private int GetPriority(string source)
        {
            return source switch
            {
                "Zenvix (Internal)" => 1,
                "Laragon" => 2,
                "XAMPP" => 3,
                "System" => 4,
                _ => 10
            };
        }
    }
}
