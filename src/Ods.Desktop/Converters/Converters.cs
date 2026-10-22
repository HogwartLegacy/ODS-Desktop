using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Ods.Desktop.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is Visibility.Visible) ^ Invert;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var present = value is not null && (value is not string text || text.Length > 0);
        return present ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when a failure's recovery actions include the action named by the parameter.</summary>
public sealed class RecoveryActionVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Ods.Core.Runtime.RecoveryAction actions
        && Enum.TryParse<Ods.Core.Runtime.RecoveryAction>(parameter as string, out var wanted)
        && actions.HasFlag(wanted)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when the bound value is set (non-null, non-empty text).</summary>
public sealed class HasValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && (value is not string text || text.Length > 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the bound value's text equals the converter parameter (e.g. an onboarding step).</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(System.Convert.ToString(value, CultureInfo.InvariantCulture), parameter as string, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Human-readable sizes and durations for the UI.</summary>
public static class Format
{
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0", CultureInfo.CurrentCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", CultureInfo.CurrentCulture) + " KB",
        _ => bytes.ToString(CultureInfo.CurrentCulture) + " B",
    };

    public static string Speed(double bytesPerSecond) =>
        bytesPerSecond <= 0 ? "" : (bytesPerSecond / (1 << 20)).ToString("0.0", CultureInfo.CurrentCulture) + " MB/s";

    public static string Duration(TimeSpan? span)
    {
        if (span is not TimeSpan value || value > TimeSpan.FromDays(2)) return "";
        if (value.TotalHours >= 1) return $"{(int)value.TotalHours} h {value.Minutes} min left";
        if (value.TotalMinutes >= 1) return $"{(int)value.TotalMinutes} min left";
        return $"{Math.Max(1, value.Seconds)} s left";
    }

    public static string Context(int tokens) => tokens >= 1024 ? $"{tokens / 1024}K" : tokens.ToString(CultureInfo.CurrentCulture);

    public static string Relative(DateTimeOffset time)
    {
        var age = DateTimeOffset.Now - time;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
        if (age < TimeSpan.FromDays(7)) return time.LocalDateTime.ToString("dddd", CultureInfo.CurrentCulture);
        return time.LocalDateTime.ToString("d", CultureInfo.CurrentCulture);
    }
}
