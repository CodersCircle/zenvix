using Zenvix.Core.Services;
using System.Windows.Forms;

namespace Zenvix.UI.Services
{
    public class WpfDialogService : IDialogService
    {
        public string? OpenFolderDialog(string? initialPath = null)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select parent directory for your project",
                InitialDirectory = initialPath ?? string.Empty
            };

            if (dialog.ShowDialog() == true)
            {
                return dialog.FolderName;
            }

            return null;
        }

        public bool ShowDeleteConfirmation(string websiteName)
        {
            var dialog = new Views.DeleteConfirmationWindow(websiteName);
            return dialog.ShowDialog() == true && dialog.Confirmed;
        }

        public bool ShowRemoveConfirmation(string websiteName)
        {
            var dialog = new Views.RemoveConfirmationWindow(websiteName);
            return dialog.ShowDialog() == true && dialog.Confirmed;
        }

        public string? OpenFileDialog(string title, string filter = "All files|*.*")
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        public bool Confirm(string title, string message)
            => System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.YesNo,
                   System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;

        public void ShowMessage(string title, string message)
        {
            var dialog = new Views.CustomAlertWindow(title, message);
            dialog.ShowDialog();
        }
    }
}
