using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Hostix.UI.Converters
{
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isNull = value == null;
            bool inverse = parameter?.ToString() == "Inverse";

            if (inverse)
            {
                return isNull ? Visibility.Visible : Visibility.Collapsed;
            }

            return isNull ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
