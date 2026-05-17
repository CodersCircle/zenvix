using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MahApps.Metro.Controls;

namespace Hostix.UI.Views
{
    public partial class DeleteConfirmationWindow : MetroWindow, INotifyPropertyChanged
    {
        public bool Confirmed { get; private set; }
        public string WebsiteName { get; }

        private string _confirmationInput = "";
        public string ConfirmationInput
        {
            get => _confirmationInput;
            set
            {
                _confirmationInput = value;
                OnPropertyChanged(nameof(ConfirmationInput));
                OnPropertyChanged(nameof(IsConfirmEnabled));
            }
        }

        public bool IsConfirmEnabled => ConfirmationInput == WebsiteName;

        public DeleteConfirmationWindow(string websiteName)
        {
            InitializeComponent();
            WebsiteName = websiteName;
            DataContext = this;
        }

        [RelayCommand]
        private void CopyName()
        {
            System.Windows.Clipboard.SetText(WebsiteName);
            ConfirmationInput = WebsiteName; // Automatically fill the input field for convenience
        }

        [RelayCommand]
        private void ConfirmDelete()
        {
            Confirmed = true;
            DialogResult = true;
            Close();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
