using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CoverageInsight;

/// <summary>Turns a 0..1 fraction into a star GridLength so the ribbon segments size themselves.</summary>
public sealed class FractionToStarConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fraction = value is double d ? d : 0d;
        if (double.IsNaN(fraction) || double.IsInfinity(fraction) || fraction < 0) fraction = 0;
        return new GridLength(fraction, GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Green / amber / red for a percentage, using the app's threshold bands.</summary>
public sealed class PercentToBrushConverter : IValueConverter
{
    public static double Threshold { get; set; } = 80;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var pct = value is double d ? d : 0d;
        var key = pct >= Threshold ? "CoveredBrush"
                : pct >= Threshold * 0.6 ? "PartialBrush"
                : "MissedBrush";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (parameter as string == "invert") flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasText = !string.IsNullOrWhiteSpace(value as string);
        if (parameter as string == "invert") hasText = !hasText;
        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
