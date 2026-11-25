using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace EyetrackerGlasses.ValueConverters;

public sealed class VideoWidthConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is not [double viewportWidth, double viewportHeight, double minimumWidth,
            double eyesRatio, double sceneRatio, double rowSpacing])
            return 0d;

        if (minimumWidth == 0)
            return viewportWidth;

        // Reserve both row gaps and one unit for rounding the two video heights.
        double fittingWidth = Math.Floor((viewportHeight - 2 * rowSpacing - 1) / (eyesRatio + sceneRatio));
        return Math.Min(viewportWidth, Math.Max(minimumWidth, fittingWidth));
    }
}
