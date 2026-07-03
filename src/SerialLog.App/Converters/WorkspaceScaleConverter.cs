using System.Globalization;
using System.Windows.Data;

namespace SerialLog.App.Converters;

public sealed class WorkspaceScaleConverter : IMultiValueConverter
{
    private const double DesignWidth = 1930;
    private const double DesignHeight = 1060;
    private const double MinimumScale = 0.55;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var scale = GetScale(values);
        if (parameter is string mode && string.Equals(mode, "Width", StringComparison.OrdinalIgnoreCase))
        {
            return values.Length >= 1 && values[0] is double availableWidth && availableWidth > 0
                ? availableWidth / scale
                : DesignWidth;
        }

        if (parameter is string heightMode && string.Equals(heightMode, "Height", StringComparison.OrdinalIgnoreCase))
        {
            return values.Length >= 2 && values[1] is double availableHeight && availableHeight > 0
                ? availableHeight / scale
                : DesignHeight;
        }

        return scale;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static double GetScale(object[] values)
    {
        if (values.Length < 2 ||
            values[0] is not double availableWidth ||
            values[1] is not double availableHeight ||
            availableWidth <= 0 ||
            availableHeight <= 0)
        {
            return 1d;
        }

        var widthScale = availableWidth / DesignWidth;
        var heightScale = availableHeight / DesignHeight;
        return Math.Clamp(Math.Min(widthScale, heightScale), MinimumScale, 1d);
    }
}
