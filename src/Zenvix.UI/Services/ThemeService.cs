using ControlzEx.Theming;
using System.Windows;
using Zenvix.ViewModels.Services;

namespace Zenvix.UI.Services
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
