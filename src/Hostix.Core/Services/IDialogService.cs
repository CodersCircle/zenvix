namespace Hostix.Core.Services
{
    public interface IDialogService
    {
        string? OpenFolderDialog(string? initialPath = null);
        bool ShowDeleteConfirmation(string websiteName);
        bool ShowRemoveConfirmation(string websiteName);
        void ShowMessage(string title, string message);
    }
}
