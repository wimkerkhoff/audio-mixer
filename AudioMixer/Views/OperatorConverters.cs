using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AudioMixer.Services;

namespace AudioMixer.Views;

/// <summary>Alert severity to banner colour.</summary>
public sealed class SeverityBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value switch
    {
        AlertSeverity.Critical => new SolidColorBrush(Color.FromRgb(0x8B, 0x1E, 0x26)),
        AlertSeverity.Warning => new SolidColorBrush(Color.FromRgb(0x7A, 0x55, 0x0E)),
        _ => new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x4A)),
    };

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>True -> Collapsed (the inverse of the built-in BooleanToVisibilityConverter).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Null -> Collapsed, so a card can hide when it has nothing to show.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value == null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>
/// Mic health dot colour. Deliberately coarse — Simple mode answers "is this mic OK", and the operator
/// drops into Advanced or Diagnostics for anything finer.
/// </summary>
public sealed class MicDotConverter : IMultiValueConverter
{
    private static readonly SolidColorBrush Live = new(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly SolidColorBrush Ducked = new(Color.FromRgb(0xF2, 0xA9, 0x3B));
    private static readonly SolidColorBrush Dead = new(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly SolidColorBrush Off = new(Color.FromRgb(0x44, 0x48, 0x52));

    // values: [0] hasDevice, [1] routed, [2] muted, [3] levelDb, [4] isDucking
    public object Convert(object[] v, Type t, object? p, CultureInfo c)
    {
        if (v.Length < 5) return Off;
        bool hasDevice = v[0] is true;
        bool routed = v[1] is true;
        bool muted = v[2] is true;
        double db = v[3] is float f ? f : v[3] is double d ? d : -120;
        bool ducking = v[4] is true;

        if (!hasDevice) return Dead;
        if (!routed || muted) return Off;
        if (db <= -80) return Dead;
        if (ducking) return Ducked;
        return Live;
    }

    public object[] ConvertBack(object? v, Type[] t, object? p, CultureInfo c) => throw new NotSupportedException();
}
