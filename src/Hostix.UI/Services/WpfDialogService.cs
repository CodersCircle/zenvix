using Hostix.Core.Services;
using System.Windows.Forms;

namespace Hostix.UI.Services
{
    public class WpfDialogService : IDialogService
    {
        public string? OpenFolderDialog(string? initialPath = null)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select parent directory for your project",
                UseDescriptionForTitle = true,
                SelectedPath = initialPath ?? string.Empty
            };

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                return dialog.SelectedPath;
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
