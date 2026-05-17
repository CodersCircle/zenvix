using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Hostix.Core.Models;

namespace Hostix.UI.Converters
{
    /// <summary>Maps ServiceStatus to a SolidColorBrush for the status dot.</summary>
    public class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ServiceStatus s)
            {
                return s switch
                {
                    ServiceStatus.Running => System.Windows.Application.Current.TryFindResource("SuccessBrush") ?? System.Windows.Media.Brushes.Green,
                    ServiceStatus.Starting => System.Windows.Application.Current.TryFindResource("WarningBrush") ?? System.Windows.Media.Brushes.Gold,
                    ServiceStatus.Stopping => new SolidColorBrush(System.Windows.Media.Color.FromRgb(249, 115, 22)),    // orange
                    ServiceStatus.Stopped => System.Windows.Application.Current.TryFindResource("ErrorBrush") ?? System.Windows.Media.Brushes.Red,
                    ServiceStatus.Error => new SolidColorBrush(System.Windows.Media.Color.FromRgb(249, 115, 22)),    // orange (not red)
                    _ => System.Windows.Media.Brushes.Gray
                };
            }
            if (value is WebsiteStatus ws)
            {
                return ws switch
                {
                    WebsiteStatus.Running => System.Windows.Application.Current.TryFindResource("SuccessBrush") ?? System.Windows.Media.Brushes.Green,
                    WebsiteStatus.Starting => System.Windows.Application.Current.TryFindResource("WarningBrush") ?? System.Windows.Media.Brushes.Gold,
                    WebsiteStatus.Stopped => System.Windows.Application.Current.TryFindResource("ErrorBrush") ?? System.Windows.Media.Brushes.Red,
                    WebsiteStatus.Error => new SolidColorBrush(System.Windows.Media.Color.FromRgb(249, 115, 22)),
                    _ => System.Windows.Media.Brushes.Gray
                };
            }
            return System.Windows.Media.Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Maps ServiceStatus to the action button label.</summary>
    public class StatusToButtonTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ServiceStatus s)
            {
                return s switch
                {
                    ServiceStatus.Running => "STOP",
                    ServiceStatus.Starting => "STARTING",
                    ServiceStatus.Stopping => "STOPPING",
                    ServiceStatus.Stopped => "START",
                    ServiceStatus.Error => "RETRY",
                    _ => "START"
                };
            }
            if (value is WebsiteStatus ws)
            {
                return ws switch
                {
                    WebsiteStatus.Running  => "Run Website",
                    WebsiteStatus.Starting => "Starting...",
                    WebsiteStatus.Stopped  => "START",
                    WebsiteStatus.Error    => "RETRY",
                    _                      => "START"
                };
            }
            return "START";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Disables the button while a transition is in progress.</summary>
    public class StatusToIsEnabledConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ServiceStatus s)
                return s != ServiceStatus.Starting && s != ServiceStatus.Stopping;
            if (value is WebsiteStatus ws)
                return ws != WebsiteStatus.Starting;
            return true;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Shows a spinner only during Starting or Stopping transitions.</summary>
    public class StatusToSpinnerVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ServiceStatus s)
                return (s == ServiceStatus.Starting || s == ServiceStatus.Stopping)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            
            if (value is WebsiteStatus ws)
                return (ws == WebsiteStatus.Starting)
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Hides the text label during spinner states.</summary>
    public class StatusToLabelVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ServiceStatus s)
                return (s == ServiceStatus.Starting || s == ServiceStatus.Stopping)
                    ? Visibility.Collapsed
                    : Visibility.Visible;

            if (value is WebsiteStatus ws)
                return (ws == WebsiteStatus.Starting)
                    ? Visibility.Collapsed
                    : Visibility.Visible;

            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Simple bool → Visibility (true=Visible, false=Collapsed).</summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Shows element only when status matches the parameter string.</summary>
    public class StatusToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null) return Visibility.Collapsed;

            var valueString = value.ToString();
            var target = parameter.ToString();

            return string.Equals(valueString, target, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Shows element if string is not null or whitespace.</summary>
    public class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return string.IsNullOrWhiteSpace(value?.ToString())
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Simple bool → Visibility (true=Collapsed, false=Visible).</summary>
    public class BoolToVisibilityInvertedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Maps a technology name or ProjectType to its dynamic branding SVG DrawingImage.</summary>
    public class TechIconConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value == null) return System.Windows.DependencyProperty.UnsetValue;

            string? techName = null;
            if (value is string str)
            {
                techName = str;
            }
            else if (value is Hostix.Core.Models.ProjectType type)
            {
                techName = type switch
                {
                    Hostix.Core.Models.ProjectType.Laravel => "Laravel",
                    Hostix.Core.Models.ProjectType.WordPress => "WordPress",
                    Hostix.Core.Models.ProjectType.PHP => "PHP",
                    Hostix.Core.Models.ProjectType.React => "React",
                    Hostix.Core.Models.ProjectType.Vue => "Vue",
                    Hostix.Core.Models.ProjectType.NodeJS => "Next.js",
                    Hostix.Core.Models.ProjectType.Static => "HTML/CSS",
                    Hostix.Core.Models.ProjectType.Vite => "Tailwind",
                    _ => "PHP"
                };
            }
            else if (value is Hostix.Core.Models.DbEngineType engineType)
            {
                techName = engineType switch
                {
                    Hostix.Core.Models.DbEngineType.MariaDB => "MariaDB",
                    Hostix.Core.Models.DbEngineType.MySQL => "MySQL",
                    Hostix.Core.Models.DbEngineType.PostgreSQL => "PostgreSQL",
                    Hostix.Core.Models.DbEngineType.SQLite => "SQLite",
                    Hostix.Core.Models.DbEngineType.MongoDB => "MongoDB",
                    Hostix.Core.Models.DbEngineType.Redis => "Redis",
                    Hostix.Core.Models.DbEngineType.Meilisearch => "Meilisearch",
                    Hostix.Core.Models.DbEngineType.Supabase => "Supabase",
                    Hostix.Core.Models.DbEngineType.Firebase => "Firebase",
                    Hostix.Core.Models.DbEngineType.PlanetScale => "PlanetScale",
                    Hostix.Core.Models.DbEngineType.Neon => "Neon",
                    _ => "MySQL"
                };
            }
            else if (value is Hostix.Core.Models.RuntimeServiceType serviceType)
            {
                techName = serviceType switch
                {
                    Hostix.Core.Models.RuntimeServiceType.Nginx => "Nginx",
                    Hostix.Core.Models.RuntimeServiceType.Apache => "Apache",
                    Hostix.Core.Models.RuntimeServiceType.PhpFpm => "PHP",
                    Hostix.Core.Models.RuntimeServiceType.NodeRuntime => "NodeJS",
                    Hostix.Core.Models.RuntimeServiceType.ViteRuntime => "Vite",
                    Hostix.Core.Models.RuntimeServiceType.MariaDB => "MariaDB",
                    Hostix.Core.Models.RuntimeServiceType.MySQL => "MySQL",
                    Hostix.Core.Models.RuntimeServiceType.PostgreSQL => "PostgreSQL",
                    Hostix.Core.Models.RuntimeServiceType.MongoDB => "MongoDB",
                    Hostix.Core.Models.RuntimeServiceType.Redis => "Redis",
                    Hostix.Core.Models.RuntimeServiceType.Mailpit => "Mailpit",
                    Hostix.Core.Models.RuntimeServiceType.QueueWorker => "Laravel",
                    Hostix.Core.Models.RuntimeServiceType.Scheduler => "Laravel",
                    Hostix.Core.Models.RuntimeServiceType.SSL => "SSL",
                    _ => "Nginx"
                };
            }

            if (!string.IsNullOrEmpty(techName))
            {
                var icon = Hostix.UI.Services.TechIconService.GetIcon(techName);
                if (icon != null) return icon;
            }
            return System.Windows.DependencyProperty.UnsetValue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
