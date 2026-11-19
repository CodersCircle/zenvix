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
            // Style comes from the app-wide DesignSystemResources merged in App.xaml (no per-instance file load).
            SetResourceReference(StyleProperty, "ZenvixCard");
        }
    }

    /// <summary>
    /// An interactive, Win11-hover scale-animated Button.
    /// </summary>
    public class ZenvixButton : System.Windows.Controls.Button
    {
        public ZenvixButton()
        {
            // Style comes from the app-wide DesignSystemResources merged in App.xaml (no per-instance file load).
            SetResourceReference(StyleProperty, "ZenvixButton");
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
            string styleKey = "ZenvixBadge";

            if (State != null && State.Equals("Success", StringComparison.OrdinalIgnoreCase)) styleKey = "ZenvixBadge.Success";
            else if (State != null && State.Equals("Warning", StringComparison.OrdinalIgnoreCase)) styleKey = "ZenvixBadge.Warning";
            else if (State != null && State.Equals("Danger", StringComparison.OrdinalIgnoreCase)) styleKey = "ZenvixBadge.Danger";

            SetResourceReference(StyleProperty, styleKey);
        }
    }

    /// <summary>
    /// A modern high-fidelity textbox input with custom focus transitions.
    /// </summary>
    public class ZenvixInput : System.Windows.Controls.TextBox
    {
        public ZenvixInput()
        {
            // Style comes from the app-wide DesignSystemResources merged in App.xaml (no per-instance file load).
            SetResourceReference(StyleProperty, "ZenvixInput");
        }
    }

    /// <summary>
    /// A premium Fluent Project workspace card showing status, actions, and rapid launchers.
    /// </summary>
    public class ZenvixProjectCard : System.Windows.Controls.ContentControl
    {
        public ZenvixProjectCard()
        {
            // Style comes from the app-wide DesignSystemResources merged in App.xaml (no per-instance file load).
            SetResourceReference(StyleProperty, "ZenvixProjectCard");
        }
    }

    /// <summary>
    /// A premium Fluent Card specifically designed for runtime service orchestration.
    /// </summary>
    public class ZenvixRuntimeCard : System.Windows.Controls.ContentControl
    {
        public ZenvixRuntimeCard()
        {
            // Style comes from the app-wide DesignSystemResources merged in App.xaml (no per-instance file load).
            SetResourceReference(StyleProperty, "ZenvixRuntimeCard");
        }
    }
}
