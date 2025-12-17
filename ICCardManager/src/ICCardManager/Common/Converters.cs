using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ICCardManager.Common;

/// <summary>
/// 整数値をVisibilityに変換するコンバーター
/// 0より大きい場合はVisible、それ以外はCollapsed
/// </summary>
public class IntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int intValue)
        {
            return intValue > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 真偽値を反転するコンバーター
/// </summary>
public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }
        return false;
    }
}

/// <summary>
/// 真偽値をVisibilityに変換するコンバーター
/// trueの場合はVisible、falseの場合はCollapsed
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            // parameterが指定されている場合は反転
            var invert = parameter?.ToString()?.ToLower() == "invert";
            var isVisible = invert ? !boolValue : boolValue;
            return isVisible ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// ズーム倍率（%）をScaleTransform用のスケール値に変換するコンバーター
/// 例: 100 → 1.0, 50 → 0.5, 200 → 2.0
/// </summary>
public class ZoomToScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double zoomPercent)
        {
            return zoomPercent / 100.0;
        }
        return 1.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double scale)
        {
            return scale * 100.0;
        }
        return 100.0;
    }
}
