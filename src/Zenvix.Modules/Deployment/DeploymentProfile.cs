using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace Zenvix.Modules.Deployment
{
    public enum DeployMethod { Ssh, Git }
    public enum SshAuthMode { Password, PrivateKey }

    /// <summary>A saved deploy target for one local project.</summary>
    public class DeploymentProfile
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "New deployment";
        public DeployMethod Method { get; set; } = DeployMethod.Ssh;
        public string LocalPath { get; set; } = string.Empty;

        // ── SSH / SFTP ──
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 22;
        public string Username { get; set; } = string.Empty;
        public SshAuthMode AuthMode { get; set; } = SshAuthMode.Password;
        public string PrivateKeyPath { get; set; } = string.Empty;
        public string RemotePath { get; set; } = "/var/www/html";
        public string Excludes { get; set; } = ".git, node_modules, .env, .idea, .vscode, *.log, storage/logs";
        public string PostDeployCommands { get; set; } = string.Empty;
        /// <summary>Trust-on-first-use host key fingerprint (SHA256).</summary>
        public string? HostFingerprint { get; set; }

        // Secrets are kept encrypted with Windows DPAPI (current user only).
        public string? PasswordProtected { get; set; }
        public string? PassphraseProtected { get; set; }

        [JsonIgnore] public string Password { get => Unprotect(PasswordProtected); set => PasswordProtected = Protect(value); }
        [JsonIgnore] public string Passphrase { get => Unprotect(PassphraseProtected); set => PassphraseProtected = Protect(value); }

        // ── Git ──
        public string GitRemoteUrl { get; set; } = string.Empty;
        public string GitBranch { get; set; } = "main";

        public DateTime? LastDeployedAt { get; set; }
        public string? LastResult { get; set; }

        public override string ToString() => Name;

        private static string? Protect(string? plain)
        {
            if (string.IsNullOrEmpty(plain) || !OperatingSystem.IsWindows()) return null;
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }

        private static string Unprotect(string? cipher)
        {
            if (string.IsNullOrEmpty(cipher) || !OperatingSystem.IsWindows()) return string.Empty;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return string.Empty; }
        }
    }

    public interface IDeploymentStore
    {
        List<DeploymentProfile> Load();
        void Save(IEnumerable<DeploymentProfile> profiles);
    }

    public class DeploymentStore : IDeploymentStore
    {
        private readonly string _path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "deployments.json");
        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public List<DeploymentProfile> Load()
        {
            try
            {
                if (!File.Exists(_path)) return new List<DeploymentProfile>();
                return JsonSerializer.Deserialize<List<DeploymentProfile>>(File.ReadAllText(_path), Json) ?? new();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Deploy] Could not read {Path}", _path);
                return new List<DeploymentProfile>();
            }
        }

        public void Save(IEnumerable<DeploymentProfile> profiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(profiles.ToList(), Json));
        }
    }
}
