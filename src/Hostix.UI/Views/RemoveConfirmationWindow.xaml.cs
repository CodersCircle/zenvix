using System.Windows;
using CommunityToolkit.Mvvm.Input;
using MahApps.Metro.Controls;

namespace Hostix.UI.Views
{
    public partial class RemoveConfirmationWindow : MetroWindow
    {
        public bool Confirmed { get; private set; }
        public string WebsiteName { get; }

        public RemoveConfirmationWindow(string websiteName)
        {
            InitializeComponent();
            WebsiteName = websiteName;
            DataContext = this;
        }

        [RelayCommand]
        private void Confirm()
        {
            Confirmed = true;
            DialogResult = true;
            Close();
        }

        [RelayCommand]
        private void Cancel()
        {
            Confirmed = false;
            DialogResult = false;
            Close();
        }
    }
}
