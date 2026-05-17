using Hostix.Core.Services;
using System.Windows.Forms;

namespace Hostix.UI.Services
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
    }
}
