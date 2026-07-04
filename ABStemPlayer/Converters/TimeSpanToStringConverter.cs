using Avalonia.Data.Converters;
using System.Globalization;

namespace ABStemPlayer.Converters
{
    public sealed class TimeSpanToStringConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is TimeSpan time)
            {
                return time.ToString("mm\\:ss");
            }

            return null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
