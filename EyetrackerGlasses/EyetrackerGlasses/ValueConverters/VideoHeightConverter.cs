using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace EyetrackerGlasses.ValueConverters;

public sealed class VideoHeightConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [double videoWidth, double heightRatio]
            ? Math.Round(videoWidth * heightRatio)
            : 0d;
}
