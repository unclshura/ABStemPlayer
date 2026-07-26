using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ABStemPlayer.Converters;

public class PlayPauseGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isPlaying = value is bool b && b;

        // Pause icon
        if (isPlaying)
            return Geometry.Parse("""
                M10 5C10 3.34315 8.65686 2 7 2H5C3.34315 2 2 3.34315 2 5V19C2 20.6569 3.34315 22 5 22H7C8.65686 22 10 20.6569 10 19V5Z
                M8 5C8 4.44772 7.55229 4 7 4H5C4.44772 4 4 4.44772 4 5V19C4 19.5523 4.44772 20 5 20H7C7.55229 20 8 19.5523 8 19V5Z
                M22 5C22 3.34315 20.6569 2 19 2H17C15.3431 2 14 3.34315 14 5V19C14 20.6569 15.3431 22 17 22H19C20.6569 22 22 20.6569 22 19V5Z
                M20 5C20 4.44772 19.5523 4 19 4H17C16.4477 4 16 4.44772 16 5V19C16 19.5523 16.4477 20 17 20H19C19.5523 20 20 19.5523 20 19V5Z
                """);

        // Play icon
        return Geometry.Parse("M5.46484 3.92349C4.79896 3.5739 4 4.05683 4 4.80888V19.1911C4 19.9432 4.79896 20.4261 5.46483 20.0765L19.1622 12.8854C19.8758 12.5108 19.8758 11.4892 19.1622 11.1146L5.46484 3.92349ZM2 4.80888C2 2.55271 4.3969 1.10395 6.39451 2.15269L20.0919 9.34382C22.2326 10.4677 22.2325 13.5324 20.0919 14.6562L6.3945 21.8473C4.39689 22.8961 2 21.4473 2 19.1911V4.80888Z");
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

