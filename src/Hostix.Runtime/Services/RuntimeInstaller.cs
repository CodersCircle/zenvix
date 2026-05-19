using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Hostix.Core.Models;
using Hostix.Core.Services;
using Serilog;

namespace Hostix.Runtime.Services
{
    public class RuntimeInstaller : IRuntimeInstaller
    {
        private readonly string _runtimesRoot;
        private string _manifestPath = string.Empty;
        private readonly HttpClient _httpClient;
        private RuntimeManifest? _manifest;
        private readonly StringBuilder _report = new();

        public event Action<string>? LogMessage;

        public RuntimeInstaller()
        {
            _runtimesRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtimes");
            _httpClient = new HttpClient(new HttpClientHandler 
            { 
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            });
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
            _httpClient.Timeout = TimeSpan.FromMinutes(10);
            
            FindManifest();
            EnsureStructure();
        }

        private void FindManifest()
        {
            var possiblePaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime-manifest.json"),
                @"c:\allymechProject\New3.7\allymechworking\Zenvix\runtime-manifest.json",
                @"d:\RuningProjects\Hostix\runtime-manifest.json",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "runtime-manifest.json")
            };

            foreach (var p in possiblePaths)
            {
                if (File.Exists(p))
                {
                    _manifestPath = p;
                    LoadManifest();
                    break;
                }
            }

            if (string.IsNullOrEmpty(_manifestPath))
                _manifestPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime-manifest.json");
        }

        private void LoadManifest()
        {
            try
            {
                if (File.Exists(_manifestPath))
                {
                    var json = File.ReadAllText(_manifestPath);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    options.Converters.Add(new JsonStringEnumConverter());
                    _manifest = JsonSerializer.Deserialize<RuntimeManifest>(json, options);
                    var keys = string.Join(", ", _manifest?.Runtimes.Keys ?? Enumerable.Empty<RuntimeServiceType>());
                    Log.Information("[Installer] Manifest loaded from {Path}. Runtimes found: {Keys}", _manifestPath, keys);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[Installer] Manifest Load Error: {Msg}", ex.Message);
            }
        }

        private void Notify(string msg)
        {
            _report.AppendLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
            Log.Information($"[Installer] {msg}");
            LogMessage?.Invoke(msg);
        }

        public void EnsureStructure()
        {
            if (!Directory.Exists(_runtimesRoot)) Directory.CreateDirectory(_runtimesRoot);
            var cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "downloads");
            if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);
        }

        public bool IsInstalled(RuntimeServiceType type, string version)
        {
            var info = GetVersionInfo(type, version);
            if (info == null) return false;
            var binPath = Path.Combine(GetRuntimeDirectory(type, version), info.RelativeBinaryPath);
            return File.Exists(binPath);
        }

        public string GetRuntimeDirectory(RuntimeServiceType type, string version)
        {
            return Path.Combine(_runtimesRoot, type.ToString().ToLower(), version);
        }

        public async Task<bool> InstallAsync(RuntimeServiceType type, string version, IProgress<double>? progress = null)
        {
            _report.Clear();
            Notify($"INSTALLATION START: {type} {version}");
            
            try
            {
                var info = GetVersionInfo(type, version);
                if (info == null) throw new Exception($"Version {version} not found in manifest.");

                var cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "downloads");
                var zipPath = Path.Combine(cacheDir, $"{type.ToString().ToLower()}_{version.Replace(".", "_")}.zip");

                // 1. Download / Cache Check
                bool needsDownload = !File.Exists(zipPath) || new FileInfo(zipPath).Length < 1024;
                if (needsDownload)
                {
                    Notify($"Downloading from: {info.DownloadUrl}");
                    
                    var request = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
                    request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
                    request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
                    
                    // Bot-protection bypass for specialized hosts
                    if (info.DownloadUrl.Contains("apachelounge.com", StringComparison.OrdinalIgnoreCase))
                        request.Headers.Referrer = new Uri("https://www.apachelounge.com/download/");
                    else if (info.DownloadUrl.Contains("mysql.com", StringComparison.OrdinalIgnoreCase))
                        request.Headers.Referrer = new Uri("https://dev.mysql.com/downloads/mysql/");

                    using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    Notify($"HTTP Status: {response.StatusCode} ({(int)response.StatusCode})");
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        var errorBody = "";
                        try { errorBody = await response.Content.ReadAsStringAsync(); } catch { }
                        Log.Error("[Installer] Download failed for {Url}. Status: {Status}. Body: {Body}", info.DownloadUrl, response.StatusCode, errorBody.Take(200));
                        throw new Exception($"Download failed: {response.StatusCode}");
                    }

                    // Check for vignette/redirect (HTML instead of ZIP)
                    var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                    if (contentType.Contains("text/html") && !info.DownloadUrl.EndsWith(".html"))
                        throw new Exception("Server returned HTML instead of ZIP. Bot protection likely triggered.");

                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    if (totalBytes > 0 && totalBytes < 100000) // Less than 100KB for an Apache ZIP? suspicious.
                         throw new Exception($"Download too small ({totalBytes / 1024} KB). Likely an error page.");

                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                    var buffer = new byte[16384];
                    var bytesRead = 0L;
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, read);
                        bytesRead += read;
                        if (totalBytes > 0) progress?.Report((double)bytesRead / totalBytes);
                    }
                    Notify($"Download complete. Size: {bytesRead / 1024} KB");
                }
                else
                {
                    Notify($"Checking cached ZIP: {zipPath}");
                }

                // ── 1b. ZIP INTEGRITY CHECK (MAGIC BYTES) ──
                using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read))
                {
                    if (fs.Length < 4) throw new Exception("ZIP file too small.");
                    var sig = new byte[2];
                    fs.Read(sig, 0, 2);
                    if (sig[0] != 0x50 || sig[1] != 0x4B) // "PK"
                    {
                        fs.Close();
                        Notify("CRITICAL: ZIP signature invalid. Purging cache.");
                        File.Delete(zipPath);
                        throw new Exception("The cached/downloaded package is not a valid ZIP. Please try again.");
                    }
                }

                // 2. Extraction & Normalization
                var targetDir = GetRuntimeDirectory(type, version);
                var tempExtractDir = Path.Combine(Path.GetTempPath(), "hostix_extract_" + Guid.NewGuid().ToString("N"));
                
                try
                {
                    Notify($"Extracting ZIP to temp: {tempExtractDir}");
                    Directory.CreateDirectory(tempExtractDir);
                    ZipFile.ExtractToDirectory(zipPath, tempExtractDir, true);

                    // ── 2a. Recursive Discovery ──
                    Notify($"Scanning for binary: {info.RelativeBinaryPath}");
                    var binaryName = Path.GetFileName(info.RelativeBinaryPath);
                    var foundBinPath = Directory.GetFiles(tempExtractDir, binaryName, SearchOption.AllDirectories).FirstOrDefault();

                    if (foundBinPath == null)
                        throw new Exception($"Could not find {binaryName} anywhere in the extracted ZIP.");

                    // Determine the "True Root" of the extracted runtime (where the binary or its 'bin' parent lives)
                    var discoveredBinDir = Path.GetDirectoryName(foundBinPath)!;
                    var trueRoot = discoveredBinDir;
                    if (discoveredBinDir.EndsWith("\\bin", StringComparison.OrdinalIgnoreCase))
                        trueRoot = Path.GetDirectoryName(discoveredBinDir)!;

                    Notify($"Discovered true runtime root: {trueRoot}");

                    // ── 2b. Normalization (Move to final location) ──
                    if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
                    
                    Notify($"Moving normalized runtime to: {targetDir}");
                    MoveDirectory(trueRoot, targetDir);

                    // 3. Final Validation
                    var finalBinPath = Path.Combine(targetDir, info.RelativeBinaryPath);
                    if (File.Exists(finalBinPath))
                    {
                        Notify($"SUCCESS: Binary verified at {finalBinPath}");
                        return true;
                    }
                    else
                    {
                        // Fallback: check root if RelativeBinaryPath was nested but we flattened
                        var rootBinPath = Path.Combine(targetDir, Path.GetFileName(info.RelativeBinaryPath));
                        if (File.Exists(rootBinPath))
                        {
                            Notify($"SUCCESS: Binary verified in root: {rootBinPath}");
                            return true;
                        }
                        throw new Exception($"Verification failed. Binary missing after normalization.");
                    }
                }
                finally
                {
                    if (Directory.Exists(tempExtractDir)) 
                        try { Directory.Delete(tempExtractDir, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                Notify($"ERROR: {ex.Message}");
                return false;
            }
        }

        private void MoveDirectory(string source, string target)
        {
            if (!Directory.Exists(target)) Directory.CreateDirectory(target);

            foreach (var file in Directory.GetFiles(source))
            {
                var dest = Path.Combine(target, Path.GetFileName(file));
                File.Move(file, dest, true);
            }

            foreach (var dir in Directory.GetDirectories(source))
            {
                var dest = Path.Combine(target, Path.GetFileName(dir));
                MoveDirectory(dir, dest);
            }
        }

        public string GetLastReport() => _report.ToString();

        private RuntimeVersionInfo? GetVersionInfo(RuntimeServiceType type, string version)
        {
            if (_manifest == null || !_manifest.Runtimes.TryGetValue(type, out var versions)) return null;
            if (version == "latest" || string.IsNullOrEmpty(version))
                return versions.FirstOrDefault(v => v.IsDefault) ?? versions.FirstOrDefault();
            return versions.FirstOrDefault(v => v.Version == version);
        }

        private void CleanupNestedFolders(string dir)
        {
            var subDirs = Directory.GetDirectories(dir);
            var files = Directory.GetFiles(dir);

            if (subDirs.Length == 1 && files.Length == 0)
            {
                var nestedDir = subDirs[0];
                Notify($"Flattening nested root: {Path.GetFileName(nestedDir)}");
                
                foreach (var d in Directory.GetDirectories(nestedDir))
                    Directory.Move(d, Path.Combine(dir, Path.GetFileName(d)));
                    
                foreach (var f in Directory.GetFiles(nestedDir))
                    File.Move(f, Path.Combine(dir, Path.GetFileName(f)));
                    
                Directory.Delete(nestedDir);
            }
        }
    }
}
