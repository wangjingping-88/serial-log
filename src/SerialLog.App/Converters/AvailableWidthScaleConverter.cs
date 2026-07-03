using System.Globalization;
using System.Windows.Data;

namespace SerialLog.App.Converters;

public sealed class AvailableWidthScaleConverter : IValueConverter
{
    private const double MinimumScale = 0.55;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double availableWidth ||
            availableWidth <= 0 ||
            parameter is not string naturalWidthText ||
            !double.TryParse(naturalWidthText, NumberStyles.Float, CultureInfo.InvariantCulture, out var naturalWidth) ||
            naturalWidth <= 0)
        {
            return 1d;
        }

        return Math.Clamp(availableWidth / naturalWidth, MinimumScale, 1d);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
