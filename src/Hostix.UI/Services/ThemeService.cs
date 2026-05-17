using ControlzEx.Theming;
using System.Windows;
using Hostix.ViewModels.Services;

namespace Hostix.UI.Services
{
    public class ThemeService : IThemeService
    {
        public void SetTheme(string baseColor)
        {
            // Force specific premium light variant
            ThemeManager.Current.ChangeTheme(System.Windows.Application.Current, "Light.Purple");
        }

        public void SyncWithSystem()
        {
            // Disabled to keep Light theme only as requested
            ThemeManager.Current.ChangeTheme(System.Windows.Application.Current, "Light.Purple");
        }
    }
}
