using System.Windows;
using CommunityToolkit.Mvvm.Input;
using MahApps.Metro.Controls;

namespace Hostix.UI.Views
{
    public partial class CustomAlertWindow : MetroWindow
    {
        public string AlertTitle { get; }
        public string AlertMessage { get; }
        public string IconCode { get; }
        public string IconColor { get; }

        public CustomAlertWindow(string title, string message)
        {
            InitializeComponent();
            AlertTitle = title;
            AlertMessage = message;

            // Dynamically assign icons based on typical words in titles
            var lowerTitle = title.ToLower();
            if (lowerTitle.Contains("fail") || lowerTitle.Contains("error") || lowerTitle.Contains("miss"))
            {
                IconCode = "\uE783"; // Error icon (warning badge)
                IconColor = "#EF4444"; // Red
            }
            else if (lowerTitle.Contains("success") || lowerTitle.Contains("complete") || lowerTitle.Contains("done"))
            {
                IconCode = "\uE73E"; // Success checkmark
                IconColor = "#22C55E"; // Green
            }
            else
            {
                IconCode = "\uE946"; // Info bubble
                IconColor = "#6366F1"; // Indigo
            }

            DataContext = this;
        }

        [RelayCommand]
        private void CloseAlert()
        {
            DialogResult = true;
            Close();
        }
    }
}
