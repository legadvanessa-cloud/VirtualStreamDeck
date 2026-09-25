using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VirtualStreamDeck.Core;

namespace VirtualStreamDeck.UI;

public sealed record Palette(
    Color App, Color Panel, Color Raised, Color Hover, Color Pressed, Color Input,
    Color Key, Color KeyHover, Color BorderSubtle, Color BorderStrong,
    Color Text, Color TextSecondary, Color TextMuted, Color Accent, Color Danger, Color Success, Color Warning);

/// <summary>
/// Applies theme + accent by swapping the brush resources the XAML styles reference dynamically,
/// and exposes the same palette as plain colours for controls that render off-tree (remote icons).
/// </summary>
public static class ThemeManager
{
    public static readonly (string Id, string Label)[] Themes =
    {
        ("charcoal", "Charcoal"), ("graphite", "Graphite"), ("midnight", "Midnight"), ("oled", "OLED black"),
    };

    public static readonly (string Hex, string Name)[] Accents =
    {
        ("#3B82F6", "Blue"), ("#06B6D4", "Cyan"), ("#14B8A6", "Teal"), ("#22C55E", "Green"), ("#F59E0B", "Amber"),
        ("#F97316", "Orange"), ("#EF4444", "Red"), ("#EC4899", "Pink"), ("#8B5CF6", "Violet"), ("#94A3B8", "Slate"),
    };

    public static readonly string[] Swatches =
    {
        "#3B82F6", "#06B6D4", "#14B8A6", "#22C55E", "#84CC16", "#F59E0B", "#F97316", "#EF4444", "#EC4899", "#8B5CF6",
        "#5865F2", "#1DB954", "#FFFFFF", "#94A3B8", "#475569", "#1E293B", "#0F172A", "#000000",
    };

    public static Palette Current { get; private set; } = Build("charcoal", "#3B82F6");
    public static double CornerRadius { get; private set; } = 14;
    public static int AnimationLevel { get; private set; } = 1;
    public static double FontScale { get; private set; } = 1;
    public static event Action? Changed;

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static Color ParseOr(string? hex, Color fallback) =>
        ColorUtil.TryParse(hex, out var r, out var g, out var b) ? Color.FromRgb(r, g, b) : fallback;

    public static Palette Build(string theme, string accent)
    {
        var a = ParseOr(accent, C("#3B82F6"));
        var (text, sec, muted) = (C("#E8EAED"), C("#A3A9B4"), C("#6B7280"));
        var danger = C("#EF4444");
        var ok = C("#22C55E");
        var warn = C("#F59E0B");
        return theme switch
        {
            "graphite" => new Palette(C("#1A1B1E"), C("#202226"), C("#26292E"), C("#2E3137"), C("#363A41"), C("#17181B"),
                C("#25282D"), C("#2C2F35"), C("#2F3238"), C("#40444C"), text, sec, C("#7A808B"), a, danger, ok, warn),
            "midnight" => new Palette(C("#0B0F1A"), C("#111727"), C("#172036"), C("#1E2942"), C("#25324F"), C("#0A0E18"),
                C("#141B2D"), C("#1A2338"), C("#1E2740"), C("#2C3856"), text, C("#A0AAC0"), C("#66708A"), a, danger, ok, warn),
            "oled" => new Palette(C("#000000"), C("#08080A"), C("#111113"), C("#19191C"), C("#222226"), C("#050506"),
                C("#0D0D0F"), C("#141417"), C("#1B1B1F"), C("#2A2A30"), text, sec, muted, a, danger, ok, warn),
            _ => new Palette(C("#0F1115"), C("#15181D"), C("#1B1F26"), C("#232833"), C("#2A303C"), C("#101318"),
                C("#1A1D24"), C("#20242C"), C("#242933"), C("#353B48"), text, sec, muted, a, danger, ok, warn),
        };
    }

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static SolidColorBrush Brush(Color c, byte alpha) => Brush(Color.FromArgb(alpha, c.R, c.G, c.B));

    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>Readable text colour (dark or light) for an arbitrary background.</summary>
    public static Color ContrastText(Color bg)
    {
        var l = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255;
        return l > 0.6 ? C("#111318") : C("#F3F4F6");
    }

    public static void Apply(AppSettings s)
    {
        var p = Build(s.Theme, s.AccentColor);
        Current = p;
        CornerRadius = s.CornerRadius;
        AnimationLevel = s.AnimationLevel;
        FontScale = s.FontScale;
        var res = Application.Current?.Resources;
        if (res != null)
        {
            void Set(string key, Color c) => res[key] = Brush(c);
            Set("Bg.App", p.App); Set("Bg.Panel", p.Panel); Set("Bg.Raised", p.Raised); Set("Bg.Hover", p.Hover);
            Set("Bg.Pressed", p.Pressed); Set("Bg.Input", p.Input); Set("Bg.Key", p.Key); Set("Bg.KeyHover", p.KeyHover);
            Set("Border.Subtle", p.BorderSubtle); Set("Border.Strong", p.BorderStrong);
            Set("Text.Primary", p.Text); Set("Text.Secondary", p.TextSecondary); Set("Text.Muted", p.TextMuted);
            Set("Accent", p.Accent);
            res["Accent.Soft"] = Brush(p.Accent, 0x30);
            Set("Accent.Text", ContrastText(p.Accent));
            Set("Danger", p.Danger);
            res["Danger.Soft"] = Brush(p.Danger, 0x2E);
            Set("Success", p.Success); Set("Warning", p.Warning);
        }
        Changed?.Invoke();
    }

    // ---- icon images -------------------------------------------------------------------------

    private static readonly Dictionary<int, BitmapSource> ImageCache = new();

    public static BitmapSource? DecodeImage(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        var key = base64.GetHashCode() ^ base64.Length;
        if (ImageCache.TryGetValue(key, out var hit)) return hit;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
            }
            bmp.Freeze();
            if (ImageCache.Count > 256) ImageCache.Clear();
            ImageCache[key] = bmp;
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>Loads a local image, downsizes it to at most 256 px and returns base64 PNG for the config.</summary>
    public static string ImportImage(string path)
    {
        var src = new BitmapImage();
        src.BeginInit();
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.UriSource = new Uri(path);
        src.EndInit();
        BitmapSource img = src;
        var max = Math.Max(src.PixelWidth, src.PixelHeight);
        if (max > 256)
        {
            var scale = 256.0 / max;
            img = new TransformedBitmap(src, new ScaleTransform(scale, scale));
        }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }
}
