using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using Hostix.Core.Models;
using Hostix.ViewModels.Services;
using Hostix.Modules.Services;
using System.Threading;

namespace Hostix.ViewModels
{
    public partial class DashboardViewModel : ObservableObject
    {
        [ObservableProperty]
        private IRuntimeStateManager _stateManager;

        /// <summary>Only Apache, Nginx, MySQL, Mailpit — bound to Dashboard panel.</summary>
        public ObservableCollection<Service> Services => StateManager.DashboardServices;
        public ObservableCollection<Website> Websites => StateManager.RunningWebsites;
        public ObservableCollection<string> RuntimeEvents => StateManager.RuntimeEvents;

        public DashboardViewModel(IRuntimeStateManager stateManager)
        {
            _stateManager = stateManager;
        }

        public DashboardViewModel()
        {
            // Design-time or default initialization
            _stateManager = null!;
        }
    }





    public partial class LogsViewModel : ObservableObject { }
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly IAppUpdaterService? _updater;

        [ObservableProperty] private string _updateStatus = "";
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
        [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
        private bool _isCheckingUpdate = false;

        [ObservableProperty] private double _updateProgress = 0;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
        private bool _isUpdateAvailable = false;

        [ObservableProperty] private string _updateButtonText = "Update Now";
        [ObservableProperty] private bool _isDownloadComplete = false;
        [ObservableProperty] private ObservableCollection<GitVersionItem> _gitVersions = new();
        [ObservableProperty] private bool _isLoadingVersions = false;

        public bool IsNotCheckingUpdate => !IsCheckingUpdate;
        public bool CanInstallUpdate => IsUpdateAvailable && !IsCheckingUpdate;

        private readonly SynchronizationContext? _syncContext;
        private string? _pendingInstallVersion;

        public string AppVersion => _updater?.CurrentVersion ?? "1.0.0";

        public SettingsViewModel() { } // Design-time constructor

        public SettingsViewModel(IAppUpdaterService updater)
        {
            _updater = updater;
            _syncContext = SynchronizationContext.Current;
            _ = LoadVersionsHistoryAsync();
        }

        public async System.Threading.Tasks.Task LoadVersionsHistoryAsync()
        {
            if (_updater == null) return;
            IsLoadingVersions = true;
            try
            {
                var list = await _updater.GetVersionsHistoryAsync();
                if (_syncContext != null)
                {
                    _syncContext.Post(_ =>
                    {
                        GitVersions.Clear();
                        foreach (var item in list)
                        {
                            GitVersions.Add(item);
                        }
                    }, null);
                }
                else
                {
                    GitVersions.Clear();
                    foreach (var item in list)
                    {
                        GitVersions.Add(item);
                    }
                }
            }
            catch (System.Exception ex)
            {
                Serilog.Log.Error(ex, "[SettingsViewModel] Failed to load version history");
            }
            finally
            {
                IsLoadingVersions = false;
            }
        }

        [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(IsNotCheckingUpdate))]
        private async Task CheckForUpdatesAsync()
        {
            if (_updater == null) return;
            IsCheckingUpdate = true;
            UpdateStatus = "Checking...";

            var info = await _updater.CheckForUpdatesAsync();
            if (info != null && info.IsUpdateAvailable)
            {
                IsUpdateAvailable = true;
                UpdateStatus = $"Update Available: {info.Version}";
                UpdateButtonText = "Update Now";
                IsDownloadComplete = false;
            }
            else
            {
                UpdateStatus = "Up to date";
                IsUpdateAvailable = false;
            }
            IsCheckingUpdate = false;
        }

        [CommunityToolkit.Mvvm.Input.RelayCommand]
        private async Task InstallUpdateAsync()
        {
            if (_updater == null || !IsUpdateAvailable) return;

            if (IsDownloadComplete)
            {
                if (!string.IsNullOrEmpty(_pendingInstallVersion))
                {
                    _updater.RunInstaller(_pendingInstallVersion);
                }
                else
                {
                    var info = await _updater.CheckForUpdatesAsync();
                    if (info != null)
                    {
                        _updater.RunInstaller(info.Version);
                    }
                }
                return;
            }

            IsCheckingUpdate = true;
            UpdateButtonText = "Downloading...";
            UpdateStatus = "Downloading update...";

            var updateInfo = await _updater.CheckForUpdatesAsync();
            if (updateInfo != null)
            {
                _pendingInstallVersion = updateInfo.Version;
                var success = await _updater.DownloadUpdateAsync(updateInfo, p =>
                {
                    if (_syncContext != null)
                    {
                        _syncContext.Post(_ =>
                        {
                            UpdateProgress = p;
                            UpdateStatus = $"Downloading... {p:F1}%";
                        }, null);
                    }
                    else
                    {
                        UpdateProgress = p;
                        UpdateStatus = $"Downloading... {p:F1}%";
                    }
                });

                if (success)
                {
                    IsDownloadComplete = true;
                    UpdateButtonText = "Install";
                    UpdateStatus = "Download complete. Click Install to begin.";
                }
                else
                {
                    UpdateStatus = "Download failed. Please try again.";
                    UpdateButtonText = "Update Now";
                }
            }
            IsCheckingUpdate = false;
        }

        [CommunityToolkit.Mvvm.Input.RelayCommand]
        private async Task InstallSpecificVersionAsync(GitVersionItem item)
        {
            if (_updater == null || item == null) return;

            IsCheckingUpdate = true;
            UpdateProgress = 0;
            UpdateButtonText = "Downloading...";
            UpdateStatus = $"Downloading {item.VersionName}...";
            IsUpdateAvailable = true; // Show update banner/install UI for this version
            _pendingInstallVersion = item.VersionName;

            var success = await _updater.DownloadUpdateAsync(new UpdateInfo
            {
                Version = item.VersionName,
                DownloadUrl = item.DownloadUrl,
                IsUpdateAvailable = true
            }, p =>
            {
                if (_syncContext != null)
                {
                    _syncContext.Post(_ =>
                    {
                        UpdateProgress = p;
                        UpdateStatus = $"Downloading {item.VersionName}... {p:F1}%";
                    }, null);
                }
                else
                {
                    UpdateProgress = p;
                    UpdateStatus = $"Downloading {item.VersionName}... {p:F1}%";
                }
            });

            if (success)
            {
                IsDownloadComplete = true;
                UpdateButtonText = "Install";
                UpdateStatus = $"Downloaded {item.VersionName}. Click Install to run setup.";
            }
            else
            {
                UpdateStatus = $"Failed to download {item.VersionName}.";
                UpdateButtonText = "Update Now";
                IsDownloadComplete = false;
            }
            IsCheckingUpdate = false;
        }

        [ObservableProperty]
        private string _username = "admin";

        [ObservableProperty]
        private string _firstName = "John";

        [ObservableProperty]
        private string _lastName = "Smith";

        // Drafts for live preview
        [ObservableProperty]
        private string _draftUsername = "admin";

        [ObservableProperty]
        private string _draftFirstName = "John";

        [ObservableProperty]
        private string _draftLastName = "Smith";

        public string AvatarText => GenerateInitials(FirstName, LastName);
        public string DraftAvatarText => GenerateInitials(DraftFirstName, DraftLastName);

        private string GenerateInitials(string fn, string ln)
        {
            fn = (fn ?? "").Trim();
            ln = (ln ?? "").Trim();

            if (string.IsNullOrEmpty(fn))
            {
                return "U";
            }

            if (string.IsNullOrEmpty(ln))
            {
                if (fn.Length >= 2)
                    return fn.Substring(0, 2).ToUpper();
                return fn.Substring(0, 1).ToUpper();
            }

            return (fn.Substring(0, 1) + ln.Substring(0, 1)).ToUpper();
        }

        [CommunityToolkit.Mvvm.Input.RelayCommand]
        private void Save()
        {
            Username = DraftUsername;
            FirstName = DraftFirstName;
            LastName = DraftLastName;
            OnPropertyChanged(nameof(AvatarText));
        }

        partial void OnDraftFirstNameChanged(string value) => OnPropertyChanged(nameof(DraftAvatarText));
        partial void OnDraftLastNameChanged(string value) => OnPropertyChanged(nameof(DraftAvatarText));
    }
}
