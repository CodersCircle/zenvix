using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using Hostix.Core.Models;
using Hostix.ViewModels.Services;
using Hostix.Modules.Services;
using System.Threading.Tasks;

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
        private readonly IDispatcherService? _dispatcher;

        [ObservableProperty] private string _updateStatus = "Check for Updates";
        
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
        [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
        [NotifyPropertyChangedFor(nameof(IsNotCheckingUpdate))]
        [NotifyPropertyChangedFor(nameof(CanInstallUpdate))]
        private bool _isCheckingUpdate = false;

        [ObservableProperty] private double _updateProgress = 0;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
        [NotifyPropertyChangedFor(nameof(CanInstallUpdate))]
        private bool _isUpdateAvailable = false;

        public bool IsNotCheckingUpdate => !IsCheckingUpdate;
        public bool CanInstallUpdate => IsUpdateAvailable && !IsCheckingUpdate;

        public string AppVersion => _updater?.CurrentVersion ?? "1.0.0";

        public SettingsViewModel() { } // Design-time constructor

        public SettingsViewModel(IAppUpdaterService updater, IDispatcherService dispatcher)
        {
            _updater = updater;
            _dispatcher = dispatcher;
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
            }
            else
            {
                UpdateStatus = "Up to date";
            }
            IsCheckingUpdate = false;
        }

        [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(CanInstallUpdate))]
        private async Task InstallUpdateAsync()
        {
            if (_updater == null || !IsUpdateAvailable || _dispatcher == null) return;
            IsCheckingUpdate = true;
            UpdateStatus = "Downloading...";

            var info = await _updater.CheckForUpdatesAsync();
            if (info != null)
            {
                await _updater.DownloadAndInstallUpdateAsync(info, p =>
                {
                    _dispatcher.Invoke(() =>
                    {
                        UpdateProgress = p;
                        UpdateStatus = $"Downloading... {p:F1}%";
                    });
                });
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
