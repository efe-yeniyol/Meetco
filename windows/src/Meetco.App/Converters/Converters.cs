using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MeetcoApp.Converters;

/// <summary>True → Visible. ConverterParameter "invert" flips it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is true;
        if (IsInverted(parameter)) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility visibility && (visibility == Visibility.Visible) != IsInverted(parameter);

    internal static bool IsInverted(object? parameter) =>
        parameter is string text && text.Equals("invert", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Visible when the value has content: a non-null object, a non-empty string, or a non-empty
/// collection. ConverterParameter "invert" flips it.
/// </summary>
public sealed class HasContentToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            int count => count > 0,
            IEnumerable sequence => sequence.GetEnumerator().MoveNext(),
            _ => true,
        };
        if (BoolToVisibilityConverter.IsInverted(parameter)) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>DateTimeOffset → local "HH:mm:ss".</summary>
public sealed class TimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTimeOffset time ? time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Language code ("en") → badge text ("EN").</summary>
public sealed class UpperCaseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text ? text.ToUpperInvariant() : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>float/double level 0…1 → double for ProgressBar.Value.</summary>
public sealed class LevelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        float level => Math.Clamp((double)level, 0, 1),
        double level => Math.Clamp(level, 0, 1),
        _ => 0.0,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Negates a bool (e.g. disable a button while busy).</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
