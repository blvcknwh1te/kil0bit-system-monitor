using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Kil0bitSystemMonitor.Helpers
{
    /// <summary>values[0] * values[1] — для множителей из XAML-ресурсов.</summary>
    public sealed class MultiplyConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values is not { Length: >= 2 })
                return DependencyProperty.UnsetValue;
            if (!TryToDouble(values[0], culture, out double a) || !TryToDouble(values[1], culture, out double b))
                return DependencyProperty.UnsetValue;
            return a * b;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static bool TryToDouble(object? value, CultureInfo culture, out double result)
        {
            switch (value)
            {
                case double d:
                    result = d;
                    return true;
                case float f:
                    result = f;
                    return true;
                case int i:
                    result = i;
                    return true;
                case string s when double.TryParse(s, NumberStyles.Float, culture, out result):
                    return true;
                default:
                    result = 0;
                    return false;
            }
        }
    }

    /// <summary>CornerRadius = value / 2 (pill под высоту трека).</summary>
    public sealed class HalfCornerRadiusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not double d || double.IsNaN(d) || d <= 0)
                return new CornerRadius(0);
            return new CornerRadius(d / 2);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
