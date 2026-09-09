using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SystemGuard.Desktop.Converters;

// ════════════════════════════════════════════════════════════════════════════
// ФИНАЛЬНАЯ ВЕРСИЯ — заменяет все предыдущие Converters.cs
// Единственный файл конвертеров в проекте.
// ════════════════════════════════════════════════════════════════════════════

// ── Bytes → "12.3 MB" ────────────────────────────────────────────────────────
public class BytesToMBConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long l) return Format(Math.Max(0, l));
        if (value is int i) return Format(Math.Max(0, i));
        if (value is double d) return Format((long)Math.Max(0, d));
        return "0 B";
    }

    private static string Format(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B"
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// ── bool → AccentBrush / TextMutedBrush ─────────────────────────────────────
public class BoolToAccentBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isTrue = value is bool b && b;
        var app = Application.Current;
        var key = isTrue ? "AccentBrush" : "TextMutedBrush";
        if (app?.Resources.TryGetResource(key, app.ActualThemeVariant, out var res) == true && res is IBrush brush)
            return brush;
        return isTrue ? Brushes.HotPink : Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// ── bool → BgBrush / TextBrush (для foreground selected item) ───────────────
public class BoolToForegroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isTrue = value is bool b && b;
        var app = Application.Current;
        var key = isTrue ? "BgBrush" : "TextBrush";
        if (app?.Resources.TryGetResource(key, app.ActualThemeVariant, out var res) == true && res is IBrush brush)
            return brush;
        return isTrue ? Brushes.Black : Brushes.White;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// ── bool IsEnabled → "Disable" / "Enable" ───────────────────────────────────
public class BoolToToggleTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? "Disable" : "Enable";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// ── bool → opacity double (selected slot = 1.0, normal = 0.0) ───────────────
public class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? 1.0 : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// ── Firewall Action → color brush ────────────────────────────────────────────
public class ActionToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string ?? "";
        if (text.Contains("Allow", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Разреш", StringComparison.OrdinalIgnoreCase))
            return new SolidColorBrush(Color.Parse("#34D399"));
        if (text.Contains("Block", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Блок", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Запрет", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Запрещ", StringComparison.OrdinalIgnoreCase))
            return new SolidColorBrush(Color.Parse("#FB7185"));
        return new SolidColorBrush(Color.Parse("#71798A"));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// ── Tab highlight: (currentTab, thisTabName) → Brush ─────────────────────────
public class TabBrushConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return Brushes.Transparent;
        var isActive = (values[0] as string) == (values[1] as string);
        var app = Application.Current;
        var key = isActive ? "AccentBrush" : "CardAltBrush";
        if (app?.Resources.TryGetResource(key, app.ActualThemeVariant, out var res) == true && res is IBrush brush)
            return brush;
        return isActive ? Brushes.HotPink : Brushes.Gray;
    }
}

// ── string → bool (непустая строка) ─────────────────────────────────────────
public class StringNotEmptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !string.IsNullOrWhiteSpace(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
public class HexToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try { return Color.Parse(value as string ?? "#FF0000"); }
        catch { return Colors.Red; }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}