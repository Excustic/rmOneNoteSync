using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace rmOneNoteSyncApp.Converters;

public class GreaterThanConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double doubleValue && double.TryParse(parameter?.ToString(), out double doubleParameter))
        {
            return doubleValue > doubleParameter;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}