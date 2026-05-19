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

    public interface IAppUpdaterService
    {
        string CurrentVersion { get; }
        Task<UpdateInfo?> CheckForUpdatesAsync();
        Task<bool> DownloadAndInstallUpdateAsync(UpdateInfo info, Action<double>? progressCallback = null);
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

        public async Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            try
            {
                Log.Information("[Updater] Checking for updates via GitHub API...");

                // GitHub API URL for the latest release
                var url = "https://api.github.com/repos/CodersCircle/zenvix/releases/latest";
                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning("[Updater] Failed to check for updates: {Status}", response.StatusCode);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);

                var tag = doc.RootElement.GetProperty("tag_name").GetString();
                if (string.IsNullOrEmpty(tag)) return null;

                var versionString = tag.TrimStart('v', 'V');
                var latestVersion = Version.Parse(versionString);
                var currentVer = Version.Parse(CurrentVersion);

                var updateInfo = new UpdateInfo
                {
                    Version = tag,
                    ReleaseNotes = doc.RootElement.GetProperty("body").GetString() ?? "",
                    IsUpdateAvailable = latestVersion > currentVer
                };

                if (doc.RootElement.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString();
                        if (name != null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            updateInfo.DownloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                            break;
                        }
                    }
                }

                return updateInfo;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Updater] Exception checking for updates");
                return null;
            }
        }

        public async Task<bool> DownloadAndInstallUpdateAsync(UpdateInfo info, Action<double>? progressCallback = null)
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

                Log.Information("[Updater] Download complete. Launching installer...");

                // Execute installer silently
                // /VERYSILENT: run completely silently
                // /SUPPRESSMSGBOXES: don't ask for any confirmation
                // /CLOSEAPPLICATIONS: close Zenvix to update
                // /RESTARTAPPLICATIONS: restart Zenvix after update
                // /SP-: disable the 'This will install...' prompt
                var psi = new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /SP- /NORESTART",
                    UseShellExecute = true,
                    Verb = "runas" // Request elevation for installer
                };

                Process.Start(psi);

                // Signal application to shut down gracefully
                Environment.Exit(0);

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Updater] Failed to download or install update");
                return false;
            }
        }
    }
}
