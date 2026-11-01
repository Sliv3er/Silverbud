using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Silverbud.Controls;

/// <summary>int ⇄ bool for radio groups: checked when the value equals ConverterParameter.</summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int v && int.TryParse(parameter as string, out int p) && v == p;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && int.TryParse(parameter as string, out int p) ? p : Binding.DoNothing;
}

public sealed class IntEqualsVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int v && int.TryParse(parameter as string, out int p) && v == p ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class TextVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Formats a slider gain as "+3" / "0" / "−4".</summary>
public sealed class GainConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int g ? g > 0 ? $"+{g}" : g < 0 ? $"−{-g}" : "0" : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
