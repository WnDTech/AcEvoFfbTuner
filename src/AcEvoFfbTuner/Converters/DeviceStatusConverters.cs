using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using AcEvoFfbTuner.Models;
using AcEvoFfbTuner.Resources;

namespace AcEvoFfbTuner.Converters;

public sealed class DeviceIconTypeToGeometryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DeviceIconType type)
            return SilhouettePaths.GetGeometry(type);
        return Geometry.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool connected)
        {
            string? param = parameter as string;
            if (param == "invert")
                return connected ? 0.2 : 1.0;
            return connected ? 1.0 : 0.2;
        }
        return 0.2;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class BoolToGlowColorConverter : IValueConverter
{
    private static readonly Color ConnectedGlow = Color.FromRgb(121, 192, 255); // #FF79C0FF
    private static readonly Color ConnectedGreenGlow = Color.FromRgb(126, 231, 135); // #FF7EE787
    private static readonly Color DisconnectedGlow = Colors.Transparent;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool connected && connected)
        {
            string? param = parameter as string;
            return param == "green" ? ConnectedGreenGlow : ConnectedGlow;
        }
        return DisconnectedGlow;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
