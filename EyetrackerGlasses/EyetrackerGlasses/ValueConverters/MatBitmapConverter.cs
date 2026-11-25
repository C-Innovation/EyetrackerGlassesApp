using System;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenCvSharp;

namespace EyetrackerGlasses.ValueConverters;

public static class MatBitmapConverter
{
    public static WriteableBitmap Convert(Mat frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Empty())
            throw new ArgumentException("A video frame must contain pixels.", nameof(frame));

        var type = frame.Type();
        var conversion = type == MatType.CV_8UC1 ? ColorConversionCodes.GRAY2BGRA
            : type == MatType.CV_8UC3 ? ColorConversionCodes.BGR2BGRA
            : throw new ArgumentException("Expected an 8-bit grayscale or BGR frame.", nameof(frame));

        var bitmap = new WriteableBitmap(new PixelSize(frame.Cols, frame.Rows), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        try
        {
            using var pixels = bitmap.Lock();
            using var destination = Mat.FromPixelData(frame.Rows, frame.Cols, MatType.CV_8UC4,
                pixels.Address, pixels.RowBytes);
            Cv2.CvtColor(frame, destination, conversion);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
}
