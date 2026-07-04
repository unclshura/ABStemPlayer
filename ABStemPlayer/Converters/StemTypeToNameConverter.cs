using Avalonia.Data.Converters;
using System.Globalization;

namespace ABStemPlayer.Converters
{
    public sealed class StemTypeToNameConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is StemType type)
            {
                return type switch
                {
                    StemType.Drums  => "Drums",
                    StemType.Bass   => "Bass",
                    StemType.Vocals => "Vocals",
                    StemType.Guitar => "Guitar",
                    StemType.Piano  => "Piano",
                    StemType.Other  => "Other",
                    _               => type.ToString()
                };
            }

            return null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
