using System.Windows;
using System.Windows.Media;
using Meetco.Core.Settings;

namespace MeetcoApp.Themes;

/// <summary>
/// Recolours the shared brushes for the chosen glass tint. Views reference these brushes with
/// DynamicResource, so every open window updates at once.
/// </summary>
public static class GlassTheme
{
    private static readonly Color GlassBase = Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1E);

    public static void Apply(GlassTint tint)
    {
        var resources = Application.Current.Resources;
        var (r, g, b) = tint.Rgb();
        var color = Color.FromRgb(r, g, b);

        if (tint == GlassTint.Neutral)
        {
            resources["GlassBrush"] = Brush(GlassBase);
            resources["GlassBorderBrush"] = Brush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            resources["WindowTintBrush"] = Brush(Colors.White);
        }
        else
        {
            // Dark enough that the light text stays legible over meeting video.
            resources["GlassBrush"] = Brush(Mix(GlassBase, color, 0.38, GlassBase.A));
            resources["GlassBorderBrush"] = Brush(Color.FromArgb(0x80, r, g, b));
            resources["WindowTintBrush"] = Brush(Mix(Colors.White, color, 0.10, 0xFF));
        }
        resources["AccentBrush"] = Brush(Color.FromArgb(0xFF, r, g, b));
        resources["QuestionHighlightBrush"] = Brush(Color.FromArgb(0x26, r, g, b));
    }

    private static Color Mix(Color from, Color to, double amount, byte alpha) => Color.FromArgb(
        alpha,
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
