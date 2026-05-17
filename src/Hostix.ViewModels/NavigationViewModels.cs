using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using Hostix.Core.Models;
using Hostix.ViewModels.Services;

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
