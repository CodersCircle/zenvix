using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using Serilog;

namespace Zenvix.Modules.Deployment
{
    public interface ISshDeployService
    {
        Task<bool> TestConnectionAsync(DeploymentProfile profile, IProgress<string> log, CancellationToken ct = default);
        Task<bool> DeployAsync(DeploymentProfile profile, IProgress<string> log, CancellationToken ct = default);
    }

    /// <summary>Uploads a project over SFTP (changed files only) and runs post-deploy commands over SSH.</summary>
    public class SshDeployService : ISshDeployService
    {
        public Task<bool> TestConnectionAsync(DeploymentProfile profile, IProgress<string> log, CancellationToken ct = default)
            => Task.Run(() =>
            {
                try
                {
                    var info = BuildConnectionInfo(profile);
                    using var ssh = new SshClient(info);
                    AttachHostKeyCheck(ssh, profile, log);
                    log.Report($"Connecting to {profile.Username}@{profile.Host}:{profile.Port} ...");
                    ssh.Connect();
                    var who = ssh.RunCommand("uname -a 2>/dev/null || ver").Result.Trim();
                    log.Report($"✔ Connected. Server: {who}");

                    var path = ShellQuote(profile.RemotePath);
                    var check = ssh.RunCommand($"mkdir -p {path} && test -w {path} && echo WRITABLE");
                    log.Report(check.Result.Contains("WRITABLE")
                        ? $"✔ Remote folder {profile.RemotePath} exists and is writable."
                        : $"✖ Remote folder {profile.RemotePath} is not writable: {check.Error.Trim()}");
                    ssh.Disconnect();
                    return check.Result.Contains("WRITABLE");
                }
                catch (Exception ex)
                {
                    log.Report($"✖ {Describe(ex)}");
                    return false;
                }
            }, ct);

        public Task<bool> DeployAsync(DeploymentProfile profile, IProgress<string> log, CancellationToken ct = default)
            => Task.Run(() =>
            {
                if (!Directory.Exists(profile.LocalPath))
                {
                    log.Report($"✖ Local folder not found: {profile.LocalPath}");
                    return false;
                }

                try
                {
                    var info = BuildConnectionInfo(profile);
                    using var sftp = new SftpClient(info);
                    AttachHostKeyCheck(sftp, profile, log);
                    log.Report($"Connecting to {profile.Host} ...");
                    sftp.Connect();

                    var excludes = ParseExcludes(profile.Excludes);
                    var remoteRoot = profile.RemotePath.TrimEnd('/');
                    EnsureRemoteDir(sftp, remoteRoot);

                    int uploaded = 0, skipped = 0;
                    UploadDirectory(sftp, profile.LocalPath, remoteRoot, "", excludes, log, ref uploaded, ref skipped, ct);
                    sftp.Disconnect();
                    log.Report($"✔ Upload finished: {uploaded} file(s) uploaded, {skipped} unchanged.");

                    if (!string.IsNullOrWhiteSpace(profile.PostDeployCommands))
                    {
                        using var ssh = new SshClient(info);
                        AttachHostKeyCheck(ssh, profile, log);
                        ssh.Connect();
                        foreach (var raw in profile.PostDeployCommands.Split('\n'))
                        {
                            var cmd = raw.Trim();
                            if (cmd.Length == 0 || cmd.StartsWith('#')) continue;
                            ct.ThrowIfCancellationRequested();
                            log.Report($"$ {cmd}");
                            var r = ssh.RunCommand($"cd {ShellQuote(remoteRoot)} && {cmd}");
                            if (!string.IsNullOrWhiteSpace(r.Result)) log.Report(r.Result.TrimEnd());
                            if (!string.IsNullOrWhiteSpace(r.Error)) log.Report(r.Error.TrimEnd());
                            if (r.ExitStatus != 0)
                            {
                                log.Report($"✖ Command failed with exit code {r.ExitStatus}. Stopping.");
                                return false;
                            }
                        }
                        ssh.Disconnect();
                    }

                    log.Report("🚀 Deployment complete.");
                    return true;
                }
                catch (OperationCanceledException)
                {
                    log.Report("Deployment cancelled.");
                    return false;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[Deploy] SSH deploy failed");
                    log.Report($"✖ {Describe(ex)}");
                    return false;
                }
            }, ct);

        private static void UploadDirectory(SftpClient sftp, string localRoot, string remoteRoot, string relative,
            List<Regex> excludes, IProgress<string> log, ref int uploaded, ref int skipped, CancellationToken ct)
        {
            var localDir = Path.Combine(localRoot, relative);
            var remoteDir = relative.Length == 0 ? remoteRoot : $"{remoteRoot}/{relative.Replace('\\', '/')}";
            EnsureRemoteDir(sftp, remoteDir);

            // One listing per folder instead of one stat per file.
            var remoteFiles = sftp.ListDirectory(remoteDir)
                .Where(f => f.IsRegularFile)
                .ToDictionary(f => f.Name, f => (f.Length, f.LastWriteTimeUtc));

            foreach (var file in Directory.GetFiles(localDir))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(localRoot, file).Replace('\\', '/');
                if (IsExcluded(rel, excludes)) continue;

                var fi = new FileInfo(file);
                if (remoteFiles.TryGetValue(fi.Name, out var remote)
                    && remote.Length == fi.Length && remote.LastWriteTimeUtc >= fi.LastWriteTimeUtc)
                {
                    skipped++;
                    continue;
                }

                using var stream = File.OpenRead(file);
                sftp.UploadFile(stream, $"{remoteDir}/{fi.Name}", canOverride: true);
                uploaded++;
                log.Report($"↑ {rel}");
            }

            foreach (var dir in Directory.GetDirectories(localDir))
            {
                var rel = Path.GetRelativePath(localRoot, dir);
                if (IsExcluded(rel.Replace('\\', '/'), excludes)) continue;
                UploadDirectory(sftp, localRoot, remoteRoot, rel, excludes, log, ref uploaded, ref skipped, ct);
            }
        }

        private static void EnsureRemoteDir(SftpClient sftp, string path)
        {
            if (sftp.Exists(path)) return;
            var current = path.StartsWith('/') ? "" : ".";
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = $"{current}/{part}";
                if (!sftp.Exists(current)) sftp.CreateDirectory(current);
            }
        }

        // ".git, node_modules, *.log, storage/logs" → regexes matched against any path segment or full relative path
        private static List<Regex> ParseExcludes(string excludes) =>
            excludes.Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => new Regex("^" + Regex.Escape(p.Trim('/')).Replace(@"\*", "[^/]*") + "$", RegexOptions.IgnoreCase))
                .ToList();

        private static bool IsExcluded(string relPath, List<Regex> excludes)
        {
            if (excludes.Any(r => r.IsMatch(relPath))) return true;
            return relPath.Split('/').Any(seg => excludes.Any(r => r.IsMatch(seg)));
        }

        private static ConnectionInfo BuildConnectionInfo(DeploymentProfile p)
        {
            if (string.IsNullOrWhiteSpace(p.Host) || string.IsNullOrWhiteSpace(p.Username))
                throw new InvalidOperationException("Host and username are required.");

            AuthenticationMethod auth;
            if (p.AuthMode == SshAuthMode.PrivateKey)
            {
                if (!File.Exists(p.PrivateKeyPath)) throw new FileNotFoundException("Private key file not found.", p.PrivateKeyPath);
                var key = string.IsNullOrEmpty(p.Passphrase)
                    ? new PrivateKeyFile(p.PrivateKeyPath)
                    : new PrivateKeyFile(p.PrivateKeyPath, p.Passphrase);
                auth = new PrivateKeyAuthenticationMethod(p.Username, key);
            }
            else
            {
                auth = new PasswordAuthenticationMethod(p.Username, p.Password);
            }

            return new ConnectionInfo(p.Host.Trim(), p.Port, p.Username.Trim(), auth) { Timeout = TimeSpan.FromSeconds(20) };
        }

        private static void AttachHostKeyCheck(BaseClient client, DeploymentProfile profile, IProgress<string> log)
        {
            client.HostKeyReceived += (_, e) =>
            {
                var fp = e.FingerPrintSHA256;
                if (string.IsNullOrEmpty(profile.HostFingerprint))
                {
                    profile.HostFingerprint = fp;
                    log.Report($"Trusting server key on first connect: SHA256:{fp}");
                    e.CanTrust = true;
                }
                else
                {
                    e.CanTrust = profile.HostFingerprint == fp;
                    if (!e.CanTrust)
                        log.Report($"✖ SERVER KEY CHANGED (expected SHA256:{profile.HostFingerprint}, got SHA256:{fp}). " +
                                   "Connection refused. If the server was reinstalled, clear the saved fingerprint.");
                }
            };
        }

        private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

        private static string Describe(Exception ex) => ex switch
        {
            SshAuthenticationException => "Login failed – check username, password or key.",
            SshConnectionException c when c.DisconnectReason == Renci.SshNet.Messages.Transport.DisconnectReason.HostKeyNotVerifiable
                => "Server key did not match the saved fingerprint.",
            System.Net.Sockets.SocketException s => $"Could not reach server: {s.Message}",
            SshOperationTimeoutException => "Connection timed out.",
            _ => ex.Message,
        };
    }
}
