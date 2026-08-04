using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BCSTool.Infrastructure;

/// <summary>
/// Presents boolean configuration values as the strings "Enabled" and
/// "Disabled" in ComboBox controls while keeping the underlying model boolean.
/// </summary>
public sealed class BooleanEnabledDisabledConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        return value is bool enabled && enabled
            ? "Enabled"
            : "Disabled";
    }


    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        if (value is not string text)
            return DependencyProperty.UnsetValue;

        if (
            text.Equals(
                "Enabled",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (
            text.Equals(
                "Disabled",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return DependencyProperty.UnsetValue;
    }
}
