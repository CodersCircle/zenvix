namespace Hostix.Core.Services
{
    public interface IDialogService
    {
        string? OpenFolderDialog(string? initialPath = null);
        bool ShowDeleteConfirmation(string websiteName);
    }
}
