using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Zenvix.DesignSystem
{
    /// <summary>
    /// A premium developer-focused Fluent Card component.
    /// </summary>
    public class ZenvixCard : Border
    {
        static ZenvixCard()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(ZenvixCard), new FrameworkPropertyMetadata(typeof(ZenvixCard)));
        }

        public ZenvixCard()
        {
            // Fallback default style application
            var dict = new ResourceDictionary { Source = new Uri("pack://application:,,,/Hostix.UI;component/DesignSystem/DesignSystemResources.xaml", UriKind.Absolute) };
            if (dict.Contains("ZenvixCard"))
            {
                Style = (Style)dict["ZenvixCard"];
            }
        }
    }

    /// <summary>
    /// An interactive, Win11-hover scale-animated Button.
    /// </summary>
    public class ZenvixButton : System.Windows.Controls.Button
    {
        public ZenvixButton()
        {
            var dict = new ResourceDictionary { Source = new Uri("pack://application:,,,/Hostix.UI;component/DesignSystem/DesignSystemResources.xaml", UriKind.Absolute) };
            if (dict.Contains("ZenvixButton"))
            {
                Style = (Style)dict["ZenvixButton"];
            }
        }
    }

    /// <summary>
    /// A sleek, customizable status indicator badge.
    /// </summary>
    public class ZenvixBadge : Border
    {
        public static readonly DependencyProperty StateProperty =
            DependencyProperty.Register("State", typeof(string), typeof(ZenvixBadge), new PropertyMetadata("Success", OnStateChanged));

        public string State
        {
            get => (string)GetValue(StateProperty);
            set => SetValue(StateProperty, value);
        }

        private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ZenvixBadge badge) badge.UpdateBadgeStyle();
        }

        public ZenvixBadge()
        {
            CornerRadius = new CornerRadius(8);
            Padding = new Thickness(10, 4, 10, 4);
            this.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            UpdateBadgeStyle();
        }

        private void UpdateBadgeStyle()
        {
            var dict = new ResourceDictionary { Source = new Uri("pack://application:,,,/Hostix.UI;component/DesignSystem/DesignSystemResources.xaml", UriKind.Absolute) };
            string styleKey = "ZenvixBadge";

            if (State != null && State.Equals("Success", StringComparison.OrdinalIgnoreCase)) styleKey = "ZenvixBadge.Success";
            else if (State != null && State.Equals("Warning", StringComparison.OrdinalIgnoreCase)) styleKey = "ZenvixBadge.Warning";
            else if (State != null && State.Equals("Danger", StringComparison.OrdinalIgnoreCase)) styleKey = "ZenvixBadge.Danger";

            if (dict.Contains(styleKey))
            {
                Style = (Style)dict[styleKey];
            }
        }
    }

    /// <summary>
    /// A modern high-fidelity textbox input with custom focus transitions.
    /// </summary>
    public class ZenvixInput : System.Windows.Controls.TextBox
    {
        public ZenvixInput()
        {
            var dict = new ResourceDictionary { Source = new Uri("pack://application:,,,/Hostix.UI;component/DesignSystem/DesignSystemResources.xaml", UriKind.Absolute) };
            if (dict.Contains("ZenvixInput"))
            {
                Style = (Style)dict["ZenvixInput"];
            }
        }
    }

    /// <summary>
    /// A premium Fluent Project workspace card showing status, actions, and rapid launchers.
    /// </summary>
    public class ZenvixProjectCard : System.Windows.Controls.ContentControl
    {
        public ZenvixProjectCard()
        {
            var dict = new ResourceDictionary { Source = new Uri("pack://application:,,,/Hostix.UI;component/DesignSystem/DesignSystemResources.xaml", UriKind.Absolute) };
            if (dict.Contains("ZenvixProjectCard"))
            {
                Style = (Style)dict["ZenvixProjectCard"];
            }
        }
    }

    /// <summary>
    /// A premium Fluent Card specifically designed for runtime service orchestration.
    /// </summary>
    public class ZenvixRuntimeCard : System.Windows.Controls.ContentControl
    {
        public ZenvixRuntimeCard()
        {
            var dict = new ResourceDictionary { Source = new Uri("pack://application:,,,/Hostix.UI;component/DesignSystem/DesignSystemResources.xaml", UriKind.Absolute) };
            if (dict.Contains("ZenvixRuntimeCard"))
            {
                Style = (Style)dict["ZenvixRuntimeCard"];
            }
        }
    }
}
