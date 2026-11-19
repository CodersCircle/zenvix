using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Zenvix.Modules.Deployment
{
    public sealed record ShareResult(bool Success, string? PublicUrl, string Message);

    public interface IShareTunnelService
    {
        bool IsSharing(string key);
        string? GetPublicUrl(string key);
        /// <param name="key">Stable id of what is shared (e.g. the website id).</param>
        /// <param name="originUrl">Local origin, e.g. http://127.0.0.1:80</param>
        /// <param name="hostHeader">Local virtual host, e.g. myshop.test</param>
        Task<ShareResult> StartAsync(string key, string originUrl, string? hostHeader, IProgress<string> log, CancellationToken ct = default);
        void Stop(string key);
        void StopAll();
    }

    /// <summary>
    /// Shares a local site on the internet with a free Cloudflare Quick Tunnel
    /// (https://*.trycloudflare.com). No account, sign-up, port forwarding or router setup needed.
    /// </summary>
    public class ShareTunnelService : IShareTunnelService
    {
        private const string DownloadUrl =
            "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

        private static readonly Regex UrlRegex = new(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase);
        private readonly ConcurrentDictionary<string, (Process Proc, string Url)> _tunnels = new();
        private readonly string _exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtimes", "cloudflared", "cloudflared.exe");

        public bool IsSharing(string key) => _tunnels.TryGetValue(key, out var t) && !t.Proc.HasExited;
        public string? GetPublicUrl(string key) => IsSharing(key) ? _tunnels[key].Url : null;

        public async Task<ShareResult> StartAsync(string key, string originUrl, string? hostHeader, IProgress<string> log, CancellationToken ct = default)
        {
            if (IsSharing(key)) return new ShareResult(true, _tunnels[key].Url, "Already shared.");

            if (!await EnsureCloudflaredAsync(log, ct))
                return new ShareResult(false, null, "Could not download the tunnel tool (cloudflared).");

            var args = $"tunnel --no-autoupdate --url {originUrl}";
            if (!string.IsNullOrWhiteSpace(hostHeader)) args += $" --http-host-header {hostHeader}";
            if (originUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase)) args += " --no-tls-verify";

            log.Report($"Opening public tunnel to {hostHeader ?? originUrl} ...");
            var psi = new ProcessStartInfo(_exePath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var urlFound = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            DataReceivedEventHandler onLine = (_, e) =>
            {
                if (e.Data == null) return;
                Log.Debug("[cloudflared] {Line}", e.Data);
                var m = UrlRegex.Match(e.Data);
                if (m.Success) urlFound.TrySetResult(m.Value);
            };
            proc.ErrorDataReceived += onLine;
            proc.OutputDataReceived += onLine;
            proc.Exited += (_, _) => urlFound.TrySetException(new Exception("Tunnel process exited."));

            proc.Start();
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();

            try
            {
                var url = await urlFound.Task.WaitAsync(TimeSpan.FromSeconds(45), ct);
                _tunnels[key] = (proc, url);
                log.Report($"✔ Live at {url}");
                log.Report("Anyone with this link can open the site while sharing is on. Stop sharing to close it.");
                return new ShareResult(true, url, "Shared.");
            }
            catch (Exception ex)
            {
                try { proc.Kill(true); } catch { }
                Log.Warning(ex, "[Share] Tunnel failed");
                return new ShareResult(false, null, "Could not open a tunnel. Check your internet connection and try again.");
            }
        }

        public void Stop(string key)
        {
            if (_tunnels.TryRemove(key, out var t))
            {
                try { t.Proc.Kill(true); } catch { }
                t.Proc.Dispose();
            }
        }

        public void StopAll()
        {
            foreach (var key in _tunnels.Keys) Stop(key);
        }

        private async Task<bool> EnsureCloudflaredAsync(IProgress<string> log, CancellationToken ct)
        {
            if (File.Exists(_exePath) && new FileInfo(_exePath).Length > 1_000_000) return true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_exePath)!);
                log.Report("First use: downloading Cloudflare tunnel tool (~60 MB, one time) ...");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                var tmp = _exePath + ".download";
                await using (var src = await http.GetStreamAsync(DownloadUrl, ct))
                await using (var dst = File.Create(tmp))
                    await src.CopyToAsync(dst, ct);
                File.Move(tmp, _exePath, overwrite: true);
                log.Report("✔ Tunnel tool ready.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Share] cloudflared download failed");
                log.Report($"✖ Download failed: {ex.Message}");
                return false;
            }
        }
    }
}
