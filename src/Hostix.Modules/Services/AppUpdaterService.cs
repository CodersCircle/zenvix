using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Hostix.Core.Models;
using Serilog;

namespace Hostix.Modules.Services
{
    public class UpdateInfo
    {
        public string Version { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
        public bool IsUpdateAvailable { get; set; }
    }

    public class GitVersionItem
    {
        public string VersionName { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    public interface IAppUpdaterService
    {
        string CurrentVersion { get; }
        Task<UpdateInfo?> CheckForUpdatesAsync();
        Task<bool> DownloadUpdateAsync(UpdateInfo info, Action<double>? progressCallback = null);
        void RunInstaller(string version);
        Task<List<GitVersionItem>> GetVersionsHistoryAsync();
    }

    public class AppUpdaterService : IAppUpdaterService
    {
        private readonly HttpClient _httpClient;

        public string CurrentVersion => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";

        public AppUpdaterService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Zenvix", CurrentVersion));
            _httpClient.Timeout = TimeSpan.FromSeconds(15);
        }

        private Version? ParseVersionFromFilename(string filename)
        {
            var cleaned = filename.Replace("Zenvix-Setup-", "").Replace(".exe", "");
            cleaned = cleaned.TrimStart('v', 'V');
            if (cleaned.Length == 3 && char.IsDigit(cleaned[0]) && char.IsDigit(cleaned[1]) && char.IsDigit(cleaned[2]))
            {
                return new Version(int.Parse(cleaned[0].ToString()), int.Parse(cleaned[1].ToString()), int.Parse(cleaned[2].ToString()));
            }
            if (Version.TryParse(cleaned, out var v))
            {
                return v;
            }
            return null;
        }

        public async Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            try
            {
                Log.Information("[Updater] Checking for updates via GitHub contents API...");

                var url = "https://api.github.com/repos/CodersCircle/zenvix/contents/versions";
                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning("[Updater] Failed to check for updates: {Status}", response.StatusCode);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    Log.Warning("[Updater] GitHub API returned unexpected format (not an array)");
                    return null;
                }

                Version? latestVersion = null;
                string downloadUrl = "";

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "file" &&
                        item.TryGetProperty("name", out var nameProp))
                    {
                        var name = nameProp.GetString();
                        if (name != null && name.StartsWith("Zenvix-Setup-V", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            var parsedVer = ParseVersionFromFilename(name);
                            if (parsedVer != null)
                            {
                                if (latestVersion == null || parsedVer > latestVersion)
                                {
                                    latestVersion = parsedVer;
                                    if (item.TryGetProperty("download_url", out var downloadUrlProp))
                                    {
                                        downloadUrl = downloadUrlProp.GetString() ?? "";
                                    }
                                }
                            }
                        }
                    }
                }

                if (latestVersion == null)
                {
                    Log.Information("[Updater] No valid setup files found in versions directory.");
                    return null;
                }

                var currentVer = Version.Parse(CurrentVersion);
                Log.Information("[Updater] Latest version found: {LatestVersion}, Current version: {CurrentVersion}", latestVersion, currentVer);

                var updateInfo = new UpdateInfo
                {
                    Version = $"v{latestVersion}",
                    DownloadUrl = downloadUrl,
                    ReleaseNotes = $"New version {latestVersion} is available in versions history.",
                    IsUpdateAvailable = latestVersion > currentVer
                };

                return updateInfo;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Updater] Exception checking for updates");
                return null;
            }
        }

        public async Task<bool> DownloadUpdateAsync(UpdateInfo info, Action<double>? progressCallback = null)
        {
            if (string.IsNullOrEmpty(info.DownloadUrl)) return false;

            var tempDir = Path.Combine(Path.GetTempPath(), "ZenvixUpdate");
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);

            var installerPath = Path.Combine(tempDir, $"Zenvix-Update-{info.Version}.exe");

            try
            {
                Log.Information("[Updater] Downloading update to {Path}", installerPath);

                // Download file
                using var response = await _httpClient.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                var canReportProgress = totalBytes != -1 && progressCallback != null;

                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
                var isMoreToRead = true;
                var totalRead = 0L;

                do
                {
                    var read = await contentStream.ReadAsync(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        isMoreToRead = false;
                    }
                    else
                    {
                        await fileStream.WriteAsync(buffer, 0, read);
                        totalRead += read;

                        if (canReportProgress)
                        {
                            var progress = (double)totalRead / totalBytes * 100;
                            progressCallback?.Invoke(progress);
                        }
                    }
                }
                while (isMoreToRead);

                Log.Information("[Updater] Download complete: {Path}", installerPath);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Updater] Failed to download update");
                return false;
            }
        }

        public void RunInstaller(string version)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "ZenvixUpdate");
            var installerPath = Path.Combine(tempDir, $"Zenvix-Update-{version}.exe");

            Log.Information("[Updater] Launching installer from {Path}", installerPath);

            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /SP- /NORESTART /DIR=\"C:\\Zenvix\"",
                UseShellExecute = true,
                Verb = "runas"
            };

            Process.Start(psi);
            Environment.Exit(0);
        }

        public async Task<List<GitVersionItem>> GetVersionsHistoryAsync()
        {
            var list = new List<GitVersionItem>();
            try
            {
                var url = "https://api.github.com/repos/CodersCircle/zenvix/contents/versions";
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) return list;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "file" &&
                            item.TryGetProperty("name", out var nameProp))
                        {
                            var name = nameProp.GetString();
                            if (name != null && name.StartsWith("Zenvix-Setup-V", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                var parsedVer = ParseVersionFromFilename(name);
                                if (parsedVer != null)
                                {
                                    var downloadUrl = "";
                                    if (item.TryGetProperty("download_url", out var downloadUrlProp))
                                    {
                                        downloadUrl = downloadUrlProp.GetString() ?? "";
                                    }

                                    list.Add(new GitVersionItem
                                    {
                                        VersionName = $"v{parsedVer}",
                                        DisplayName = $"Version {parsedVer}",
                                        DownloadUrl = downloadUrl
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Updater] Failed to get versions history");
            }

            return list.OrderByDescending(v => {
                Version.TryParse(v.VersionName.TrimStart('v', 'V'), out var parsed);
                return parsed ?? new Version(0, 0);
            }).ToList();
        }
    }
}
