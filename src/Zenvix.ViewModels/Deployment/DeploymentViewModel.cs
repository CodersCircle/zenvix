using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zenvix.Core.Models;
using Zenvix.Core.Services;
using Zenvix.Modules.Deployment;
using Zenvix.Runtime.Services;
using Zenvix.ViewModels.Services;

namespace Zenvix.ViewModels.Deployment
{
    /// <summary>One local website in the "Share Demo" list.</summary>
    public partial class ShareItem : ObservableObject
    {
        public Website Website { get; }
        public ShareItem(Website website) => Website = website;

        public string Name => Website.Name;
        public string Domain => Website.Domain;

        [ObservableProperty] private bool _isSharing;
        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private string? _publicUrl;
    }

    public partial class DeploymentViewModel : ObservableObject
    {
        private readonly IDeploymentStore _store;
        private readonly ISshDeployService _ssh;
        private readonly IGitDeployService _git;
        private readonly IShareTunnelService _tunnels;
        private readonly IServicesOrchestrator _services;
        private readonly IRuntimeStateManager _state;
        private readonly IDialogService _dialogs;
        private readonly IClipboardService _clipboard;
        private readonly IDispatcherService _dispatcher;
        private CancellationTokenSource? _cts;

        public ObservableCollection<ShareItem> ShareItems { get; } = new();
        public ObservableCollection<DeploymentProfile> Profiles { get; } = new();
        public ObservableCollection<string> ActivityLog { get; } = new();
        public ObservableCollection<Website> Websites => _state.RunningWebsites;

        /// <summary>0 = Share Demo, 1 = Deploy to Server</summary>
        [ObservableProperty] private int _selectedTab;
        [ObservableProperty] private bool _isBusy;
        [ObservableProperty] private string _commitMessage = string.Empty;
        [ObservableProperty] private string? _gitVersion;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasProfile), nameof(IsSsh), nameof(IsGit), nameof(IsPasswordAuth),
            nameof(IsKeyAuth), nameof(SshPassword), nameof(KeyPassphrase))]
        private DeploymentProfile? _selectedProfile;

        [ObservableProperty] private Website? _selectedWebsiteForProfile;

        public bool HasProfile => SelectedProfile != null;

        public bool IsSsh
        {
            get => SelectedProfile?.Method == DeployMethod.Ssh;
            set { if (value && SelectedProfile != null) { SelectedProfile.Method = DeployMethod.Ssh; OnPropertyChanged(); OnPropertyChanged(nameof(IsGit)); } }
        }
        public bool IsGit
        {
            get => SelectedProfile?.Method == DeployMethod.Git;
            set { if (value && SelectedProfile != null) { SelectedProfile.Method = DeployMethod.Git; OnPropertyChanged(); OnPropertyChanged(nameof(IsSsh)); } }
        }
        public bool IsPasswordAuth
        {
            get => SelectedProfile?.AuthMode == SshAuthMode.Password;
            set { if (value && SelectedProfile != null) { SelectedProfile.AuthMode = SshAuthMode.Password; OnPropertyChanged(); OnPropertyChanged(nameof(IsKeyAuth)); } }
        }
        public bool IsKeyAuth
        {
            get => SelectedProfile?.AuthMode == SshAuthMode.PrivateKey;
            set { if (value && SelectedProfile != null) { SelectedProfile.AuthMode = SshAuthMode.PrivateKey; OnPropertyChanged(); OnPropertyChanged(nameof(IsPasswordAuth)); } }
        }
        public string SshPassword
        {
            get => SelectedProfile?.Password ?? "";
            set { if (SelectedProfile != null) SelectedProfile.Password = value; }
        }
        public string KeyPassphrase
        {
            get => SelectedProfile?.Passphrase ?? "";
            set { if (SelectedProfile != null) SelectedProfile.Passphrase = value; }
        }

        public DeploymentViewModel(
            IDeploymentStore store, ISshDeployService ssh, IGitDeployService git, IShareTunnelService tunnels,
            IServicesOrchestrator services, IRuntimeStateManager state, IDialogService dialogs,
            IClipboardService clipboard, IDispatcherService dispatcher)
        {
            _store = store; _ssh = ssh; _git = git; _tunnels = tunnels; _services = services;
            _state = state; _dialogs = dialogs; _clipboard = clipboard; _dispatcher = dispatcher;

            foreach (var p in _store.Load()) Profiles.Add(p);
            SelectedProfile = Profiles.FirstOrDefault();

            _state.RunningWebsites.CollectionChanged += (_, _) => _dispatcher.BeginInvoke(SyncShareItems);
            SyncShareItems();
            _ = Task.Run(async () =>
            {
                var v = await _git.GetGitVersionAsync();
                _dispatcher.BeginInvoke(() => GitVersion = v);
            });
        }

        private void SyncShareItems()
        {
            foreach (var w in _state.RunningWebsites.Where(w => ShareItems.All(s => s.Website.Id != w.Id)).ToList())
                ShareItems.Add(new ShareItem(w));
            foreach (var gone in ShareItems.Where(s => _state.RunningWebsites.All(w => w.Id != s.Website.Id)).ToList())
            {
                _tunnels.Stop(gone.Website.Id.ToString());
                ShareItems.Remove(gone);
            }
        }

        private IProgress<string> NewLog() => new Progress<string>(line => _dispatcher.BeginInvoke(() =>
        {
            ActivityLog.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
            while (ActivityLog.Count > 500) ActivityLog.RemoveAt(0);
        }));

        // ───────────────────────── Share Demo ─────────────────────────

        [RelayCommand]
        private async Task ToggleShare(ShareItem? item)
        {
            if (item == null || item.IsBusy) return;
            var key = item.Website.Id.ToString();

            if (item.IsSharing)
            {
                _tunnels.Stop(key);
                item.IsSharing = false;
                item.PublicUrl = null;
                NewLog().Report($"Stopped sharing {item.Name}.");
                return;
            }

            item.IsBusy = true;
            var log = NewLog();
            try
            {
                // The site must be served locally first.
                var nginx = _services.Instances.FirstOrDefault(i => i.Type == RuntimeServiceType.Nginx);
                if (nginx == null) { log.Report("✖ Nginx service not found."); return; }
                if (nginx.Status != ServiceStatus.Running)
                {
                    log.Report("Starting Nginx ...");
                    if (!await _services.StartAsync(nginx.Id)) { log.Report($"✖ Nginx could not start: {nginx.Notes}"); return; }
                }

                var origin = item.Website.SslEnabled ? "https://127.0.0.1:443" : $"http://127.0.0.1:{nginx.Port}";
                var result = await _tunnels.StartAsync(key, origin, item.Domain, log);
                item.IsSharing = result.Success;
                item.PublicUrl = result.PublicUrl;
                if (!result.Success) log.Report($"✖ {result.Message}");
            }
            finally { item.IsBusy = false; }
        }

        [RelayCommand]
        private void CopyShareUrl(ShareItem? item)
        {
            if (item?.PublicUrl == null) return;
            _clipboard.SetText(item.PublicUrl);
            NewLog().Report($"Copied {item.PublicUrl}");
        }

        [RelayCommand]
        private void OpenUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        // ───────────────────────── Profiles ─────────────────────────

        [RelayCommand]
        private void AddProfile()
        {
            var p = new DeploymentProfile { Name = $"Deployment {Profiles.Count + 1}" };
            var site = _state.RunningWebsites.FirstOrDefault();
            if (site != null) { p.Name = site.Name; p.LocalPath = site.LocalPath; }
            Profiles.Add(p);
            SelectedProfile = p;
            SaveProfiles();
        }

        [RelayCommand]
        private void RemoveProfile()
        {
            if (SelectedProfile == null) return;
            if (!_dialogs.Confirm("Remove deployment", $"Remove the deployment \"{SelectedProfile.Name}\"? Your files are not touched.")) return;
            Profiles.Remove(SelectedProfile);
            SelectedProfile = Profiles.FirstOrDefault();
            SaveProfiles();
        }

        [RelayCommand]
        private void SaveProfiles()
        {
            _store.Save(Profiles);
            // Refresh list captions after renames.
            var selected = SelectedProfile;
            var items = Profiles.ToList();
            Profiles.Clear();
            foreach (var p in items) Profiles.Add(p);
            SelectedProfile = selected;
        }

        partial void OnSelectedWebsiteForProfileChanged(Website? value)
        {
            if (value == null || SelectedProfile == null) return;
            SelectedProfile.LocalPath = value.LocalPath;
            if (SelectedProfile.Name.StartsWith("Deployment ") || string.IsNullOrWhiteSpace(SelectedProfile.Name))
                SelectedProfile.Name = value.Name;
            var p = SelectedProfile; SelectedProfile = null; SelectedProfile = p; // refresh bindings
        }

        [RelayCommand]
        private void BrowseLocalFolder()
        {
            if (SelectedProfile == null) return;
            var path = _dialogs.OpenFolderDialog(SelectedProfile.LocalPath);
            if (path == null) return;
            SelectedProfile.LocalPath = path;
            var p = SelectedProfile; SelectedProfile = null; SelectedProfile = p;
        }

        [RelayCommand]
        private void BrowseKey()
        {
            if (SelectedProfile == null) return;
            var file = _dialogs.OpenFileDialog("Select SSH private key", "Private keys|*.pem;*.key;id_*;*.ppk|All files|*.*");
            if (file == null) return;
            SelectedProfile.PrivateKeyPath = file;
            var p = SelectedProfile; SelectedProfile = null; SelectedProfile = p;
        }

        [RelayCommand]
        private void ForgetHostKey()
        {
            if (SelectedProfile == null) return;
            SelectedProfile.HostFingerprint = null;
            SaveProfiles();
            NewLog().Report("Saved server fingerprint cleared. It will be trusted again on next connect.");
        }

        // ───────────────────────── Deploy ─────────────────────────

        [RelayCommand]
        private Task TestConnection() => RunBusy(async (p, log, ct) =>
            p.Method == DeployMethod.Ssh
                ? await _ssh.TestConnectionAsync(p, log, ct)
                : await _git.ConnectAsync(p, log, ct));

        [RelayCommand]
        private Task Deploy() => RunBusy(async (p, log, ct) =>
        {
            var ok = p.Method == DeployMethod.Ssh
                ? await _ssh.DeployAsync(p, log, ct)
                : await _git.DeployAsync(p, CommitMessage, log, ct);
            p.LastDeployedAt = DateTime.Now;
            p.LastResult = ok ? "Success" : "Failed";
            if (ok) _dispatcher.BeginInvoke(() => CommitMessage = string.Empty);
            return ok;
        });

        [RelayCommand]
        private void Cancel() => _cts?.Cancel();

        [RelayCommand]
        private void ClearLog() => ActivityLog.Clear();

        private async Task RunBusy(Func<DeploymentProfile, IProgress<string>, CancellationToken, Task<bool>> work)
        {
            if (SelectedProfile == null || IsBusy) return;
            IsBusy = true;
            _cts = new CancellationTokenSource();
            var profile = SelectedProfile;
            try
            {
                await work(profile, NewLog(), _cts.Token);
            }
            catch (Exception ex)
            {
                NewLog().Report($"✖ {ex.Message}");
            }
            finally
            {
                _store.Save(Profiles); // keep fingerprint / last result
                IsBusy = false;
                _cts.Dispose();
                _cts = null;
            }
        }
    }
}
