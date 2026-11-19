using System.Windows;
using System.Windows.Controls;

namespace Zenvix.UI.Helpers
{
    /// <summary>Lets a PasswordBox take part in two-way binding (PasswordBox.Password is not a dependency property).</summary>
    public static class PasswordBoxAssist
    {
        public static readonly DependencyProperty BoundPasswordProperty =
            DependencyProperty.RegisterAttached("BoundPassword", typeof(string), typeof(PasswordBoxAssist),
                new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

        private static readonly DependencyProperty IsUpdatingProperty =
            DependencyProperty.RegisterAttached("IsUpdating", typeof(bool), typeof(PasswordBoxAssist));

        public static string GetBoundPassword(DependencyObject d) => (string)d.GetValue(BoundPasswordProperty);
        public static void SetBoundPassword(DependencyObject d, string value) => d.SetValue(BoundPasswordProperty, value);

        private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not PasswordBox box) return;
            box.PasswordChanged -= HandlePasswordChanged;
            if (!(bool)box.GetValue(IsUpdatingProperty)) box.Password = e.NewValue as string ?? string.Empty;
            box.PasswordChanged += HandlePasswordChanged;
        }

        private static void HandlePasswordChanged(object sender, RoutedEventArgs e)
        {
            var box = (PasswordBox)sender;
            box.SetValue(IsUpdatingProperty, true);
            SetBoundPassword(box, box.Password);
            box.SetValue(IsUpdatingProperty, false);
        }
    }
}
