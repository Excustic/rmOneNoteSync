using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;

namespace rmOneNoteSyncApp.Converters;

public class BoolAndConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count == 2 && values.All(v => v is bool))
        {
            return (bool)(values[0] ?? false) && (bool)(values[1] ?? false);
        }

        return false;
    }
}