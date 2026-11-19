namespace Zenvix.ViewModels.Services
{
    public interface IThemeService
    {
        void SetTheme(string baseColor);
        void SyncWithSystem();
    }
}
