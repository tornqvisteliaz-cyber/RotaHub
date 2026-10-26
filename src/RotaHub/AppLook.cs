using System.Windows;
using System.Windows.Media;
using RotaHub.Core;

namespace RotaHub;

/// <summary>
/// The accent colour from the website (Admin → RotaHub → Look of the app). The theme's accent brushes
/// are recoloured in place, so everything that uses them changes at once, also on screens already open.
/// Without a colour from the website the theme's own colours come back.
/// </summary>
public static class AppLook
{
    private static readonly string[] Keys = { "Accent", "AccentHover", "AccentInk", "AccentGlow" };
    private static Dictionary<string, Color>? _original;

    public static void Apply(HubLook look)
    {
        var resources = Application.Current?.Resources;
        if (resources == null)
            return;
        _original ??= Keys.Where(k => resources[k] is SolidColorBrush).ToDictionary(k => k, k => ((SolidColorBrush)resources[k]).Color);
        Color accent;
        try
        {
            accent = look.AccentColor != null ? (Color)ColorConverter.ConvertFromString(look.AccentColor) : default;
        }
        catch (FormatException)
        {
            accent = default;
        }
        foreach (var key in _original.Keys)
        {
            var colour = look.AccentColor == null || accent == default ? _original[key] : key switch
            {
                "AccentHover" => Mix(accent, Colors.White, 0.35),
                "AccentInk" => Mix(accent, Colors.White, 0.6),
                "AccentGlow" => Color.FromArgb(0x40, accent.R, accent.G, accent.B),
                _ => accent
            };
            if (resources[key] is SolidColorBrush brush && !brush.IsFrozen)
                brush.Color = colour;
            else
                resources[key] = new SolidColorBrush(colour);
        }
    }

    private static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
