namespace Zenvix.Core.Services
{
    public interface IDialogService
    {
        string? OpenFolderDialog(string? initialPath = null);
        bool ShowDeleteConfirmation(string websiteName);
        bool ShowRemoveConfirmation(string websiteName);
        void ShowMessage(string title, string message);
        string? OpenFileDialog(string title, string filter = "All files|*.*");
        bool Confirm(string title, string message);
    }
}
