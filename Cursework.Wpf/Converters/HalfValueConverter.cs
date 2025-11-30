using System;
using System.Globalization;
using System.Windows.Data;

namespace Cursework.Wpf.Converters
{
    public class HalfValueConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is double d)
                return d / 2d;

            if (value is float f)
                return f / 2f;

            return 0d;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
