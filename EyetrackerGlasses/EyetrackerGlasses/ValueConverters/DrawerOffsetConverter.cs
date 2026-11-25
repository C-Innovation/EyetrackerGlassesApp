using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace EyetrackerGlasses.ValueConverters;

public sealed class DrawerOffsetConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [false, double width] ? width : 0d;
}
