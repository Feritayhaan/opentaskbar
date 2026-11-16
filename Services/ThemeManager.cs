using System.Windows;
using System.Windows.Media;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

public static class ThemeManager
{
    private static ResourceDictionary? _palette;
    private static readonly ResourceDictionary Materials = new();
    private static BarStyle _style = BarStyle.Glass;

    /// <summary>Gradient stops (without alpha) plus border stops that define each bar style.</summary>
    private sealed record StyleMaterial(
        (byte R, byte G, byte B, double Offset)[] Dock,
        (byte R, byte G, byte B, byte A, double Offset)[] Border,
        (byte R, byte G, byte B)[] Menu,
        double AlphaScale);

    private static readonly IReadOnlyDictionary<BarStyle, StyleMaterial> Styles = new Dictionary<BarStyle, StyleMaterial>
    {
        // iOS-style liquid glass: much more translucent, bright specular rim.
        [BarStyle.IosGlass] = new(
            [(0x34, 0x38, 0x46, 0), (0x26, 0x29, 0x35, 0.5), (0x1B, 0x1E, 0x28, 1)],
            [(0xFF, 0xFF, 0xFF, 0xA0, 0), (0xFF, 0xFF, 0xFF, 0x30, 0.35), (0xFF, 0xFF, 0xFF, 0x58, 1)],
            [(0x2A, 0x2D, 0x39), (0x20, 0x23, 0x2E)],
            0.62),
        [BarStyle.Midnight] = new(
            [(0x14, 0x20, 0x4A, 0), (0x0C, 0x14, 0x34, 0.5), (0x07, 0x0D, 0x25, 1)],
            [(0x7C, 0x9C, 0xFF, 0x7A, 0), (0x5A, 0x78, 0xE6, 0x26, 0.4), (0x5A, 0x78, 0xE6, 0x34, 1)],
            [(0x10, 0x19, 0x3C), (0x0A, 0x10, 0x2B)],
            1.0),
        [BarStyle.Black] = new(
            [(0x0A, 0x0A, 0x0A, 0), (0x03, 0x03, 0x03, 0.5), (0x00, 0x00, 0x00, 1)],
            [(0xFF, 0xFF, 0xFF, 0x34, 0), (0xFF, 0xFF, 0xFF, 0x18, 0.4), (0xFF, 0xFF, 0xFF, 0x22, 1)],
            [(0x0B, 0x0B, 0x0C), (0x00, 0x00, 0x00)],
            1.0),
    };

    public static void Apply(DockSettings settings) => Apply(settings.EffectiveTheme, settings.SurfaceOpacity, settings.Style);

    public static void Apply(AppTheme theme, double opacity = 0.9, BarStyle style = BarStyle.Glass)
    {
        _style = style;
        var source = $"/OpenTaskbar;component/Design/Palettes/{theme}.xaml";
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        if (_palette?.Source?.OriginalString != source)
        {
            var next = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };
            dictionaries.Remove(Materials);
            if (_palette is not null) dictionaries.Remove(_palette);
            dictionaries.Add(next);
            _palette = next;
        }
        if (!dictionaries.Contains(Materials)) dictionaries.Add(Materials);
        SetSurfaceOpacity(opacity);
    }

    public static void SetSurfaceOpacity(double opacity)
    {
        if (!double.IsFinite(opacity) || opacity is < 0.6 or > 1) throw new ArgumentOutOfRangeException(nameof(opacity));
        if (_palette is null) throw new InvalidOperationException("The theme must be initialized before its materials.");
        var alpha = (byte)Math.Round(opacity * 255);
        var settings = ((SolidColorBrush)_palette["SettingsBackground"]).Color;
        settings.A = alpha;
        var settingsBrush = new SolidColorBrush(settings);
        settingsBrush.Freeze();
        Materials["SettingsGlassMaterial"] = settingsBrush;

        Styles.TryGetValue(_style, out var style);
        foreach (var key in new[] { "DockMaterial", "MenuMaterial" })
        {
            Brush material;
            if (style is not null)
            {
                var stops = new GradientStopCollection();
                var styleAlpha = (byte)Math.Round(Math.Clamp(opacity * style.AlphaScale, 0, 1) * 255);
                if (key == "DockMaterial")
                    foreach (var (r, g, b, offset) in style.Dock)
                        stops.Add(new GradientStop(Color.FromArgb(styleAlpha, r, g, b), offset));
                else
                    for (var i = 0; i < style.Menu.Length; i++)
                    {
                        var (r, g, b) = style.Menu[i];
                        // Popups stay more opaque than the dock so text remains readable.
                        var menuAlpha = (byte)Math.Round(Math.Clamp(opacity * Math.Max(style.AlphaScale, 0.9), 0, 1) * 255);
                        stops.Add(new GradientStop(Color.FromArgb(menuAlpha, r, g, b), i / (double)Math.Max(1, style.Menu.Length - 1)));
                    }
                var gradient = new LinearGradientBrush(stops, new Point(0, 0), new Point(0, 1));
                gradient.Freeze();
                material = gradient;
            }
            else
            {
                var clone = ((LinearGradientBrush)_palette[key]).Clone();
                foreach (var stop in clone.GradientStops)
                {
                    var color = stop.Color;
                    color.A = alpha;
                    stop.Color = color;
                }
                clone.Freeze();
                material = clone;
            }
            Materials[key] = material;
        }

        if (style is not null)
        {
            var border = new GradientStopCollection();
            foreach (var (r, g, b, a, offset) in style.Border)
                border.Add(new GradientStop(Color.FromArgb(a, r, g, b), offset));
            var borderBrush = new LinearGradientBrush(border, new Point(0, 0), new Point(0, 1));
            borderBrush.Freeze();
            Materials["DockBorderMaterial"] = borderBrush;
        }
        else
        {
            Materials.Remove("DockBorderMaterial");
        }
    }
}
