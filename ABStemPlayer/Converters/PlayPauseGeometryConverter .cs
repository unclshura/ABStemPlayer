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
            return Geometry.Parse("M 12 8 L 18 8 L 18 32 L 12 32 Z M 22 8 L 28 8 L 28 32 L 22 32 Z");

        // Play icon
        return Geometry.Parse("M 10 8 L 32 20 L 10 32 Z");
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

