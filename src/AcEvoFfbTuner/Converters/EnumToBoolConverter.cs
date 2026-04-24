using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AcEvoFfbTuner.Converters;

public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        string enumValue = value.ToString()!;
        string targetValue = parameter.ToString()!;
        return enumValue == targetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return Binding.DoNothing;
        if ((bool)value)
            return Enum.Parse(targetType, parameter.ToString()!);
        return Binding.DoNothing;
    }
}
