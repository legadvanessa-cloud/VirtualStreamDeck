using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Services;

namespace VirtualStreamDeck.UI.Controls;

public static class IconVisual
{
    public static readonly FontFamily Glyphs = new(IconCatalog.GlyphFont);
    public static readonly FontFamily Emoji = new("Segoe UI Emoji");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");

    /// <summary>Builds the element for an icon; size is the nominal square in DIPs.</summary>
    public static FrameworkElement Create(IconSpec? icon, double size, Brush fg)
    {
        icon ??= new IconSpec();
        switch (icon.Kind)
        {
            case IconKinds.Image:
                var bmp = ThemeManager.DecodeImage(icon.ImageData);
                if (bmp != null)
                {
                    var img = new Image { Source = bmp, Width = size * 1.15, Height = size * 1.15, Stretch = Stretch.Uniform };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    return img;
                }
                return Text("?", size, fg, Display);
            case IconKinds.Emoji:
                return Text(icon.Value, size * 0.92, fg, Emoji);
            case IconKinds.Text:
                var t = icon.Value.Length > 4 ? icon.Value[..4] : icon.Value;
                return Text(t, t.Length <= 2 ? size * 0.82 : size * 0.56, fg, Display, FontWeights.Bold);
            default:
                if (string.IsNullOrEmpty(icon.Value)) return new FrameworkElement { Width = size, Height = size };
                string ch;
                try { ch = char.ConvertFromUtf32(Convert.ToInt32(icon.Value, 16)); }
                catch { ch = "?"; }
                return Text(ch, size, fg, Glyphs);
        }
    }

    private static TextBlock Text(string s, double size, Brush fg, FontFamily font, FontWeight? weight = null) => new()
    {
        Text = s,
        FontFamily = font,
        FontSize = Math.Max(1, size),
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = fg,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        LineHeight = Math.Max(1, size * 1.1),
    };
}

/// <summary>
/// One key of the deck. All content is laid out in a 100×100 design box inside a Viewbox, so the
/// key scales with the grid from 1280×720 up to 4K without any layout rules per size.
/// Colours come straight from <see cref="ThemeManager.Current"/> (not dynamic resources) so the
/// same control can be rendered off-screen for the tablet remote.
/// </summary>
public sealed class DeckButtonView : Grid
{
    private readonly Border _face = new();
    private readonly Border _flash = new() { IsHitTestVisible = false, Opacity = 0 };
    private readonly Border _progress = new() { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly ScaleTransform _progressScale = new(0, 1);
    private readonly Border _meterTrack = new() { Height = 5, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 12, 9), CornerRadius = new CornerRadius(2.5), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Border _meterFill = new() { HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(2.5) };
    private readonly ScaleTransform _meterScale = new(0, 1);
    private readonly Rectangle _editOutline = new() { IsHitTestVisible = false, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 }, Visibility = Visibility.Collapsed };
    private readonly Border _focusRing = new() { IsHitTestVisible = false, BorderThickness = new Thickness(2.5), Visibility = Visibility.Collapsed, Margin = new Thickness(-4) };
    private readonly Ellipse _dot = new() { Width = 7, Height = 7, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 7, 0), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Viewbox _box = new() { Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private readonly Grid _content = new() { Width = 100, Height = 100 };
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _shake = new();
    private bool _hover;
    private bool _selected;
    private bool _edit;
    private LiveInfo _live = LiveInfo.Empty;

    public DeckButton Model { get; private set; }

    public DeckButtonView(DeckButton model)
    {
        Model = model;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = new TransformGroup { Children = { _scale, _shake } };
        Stylus.SetIsPressAndHoldEnabled(this, false);
        Stylus.SetIsFlicksEnabled(this, false);
        AutomationProperties_SetName();

        _box.Child = _content;
        _face.Child = new Grid { Children = { _box, _progress, _meterTrack } };
        _progress.RenderTransform = _progressScale;
        _meterFill.RenderTransform = _meterScale;
        _meterTrack.Child = _meterFill;
        Children.Add(_face);
        Children.Add(_flash);
        Children.Add(_dot);
        Children.Add(_editOutline);
        Children.Add(_focusRing);
        MouseEnter += (_, _) => { _hover = true; ApplyFace(); };
        MouseLeave += (_, _) => { _hover = false; ApplyFace(); };
        GotKeyboardFocus += (_, _) => _focusRing.Visibility = Visibility.Visible;
        LostKeyboardFocus += (_, _) => _focusRing.Visibility = Visibility.Collapsed;
    }

    private void AutomationProperties_SetName() =>
        System.Windows.Automation.AutomationProperties.SetName(this, string.IsNullOrEmpty(Model.Title) ? "Deck button" : Model.Title);

    public bool Selected
    {
        get => _selected;
        set { _selected = value; ApplyFace(); }
    }

    /// <summary>Rebuilds the key's content from its model and the current live state.</summary>
    public void Update(Deck? deck, bool editMode, DeckButton? model = null)
    {
        if (model != null) Model = model;
        _edit = editMode;
        _live = deck != null ? LiveState.Resolve(deck, Model) : LiveInfo.Empty;
        var p = ThemeManager.Current;
        var r = ThemeManager.CornerRadius;
        var fs = ThemeManager.FontScale;
        var (title, subtitle, icon, accentHex, action) = Deck.Effective(Model);
        var accent = ThemeManager.ParseOr(accentHex, p.Accent);
        var hasCustomAccent = accentHex != null;
        var bg = ThemeManager.ParseOr(Model.Background, p.Key);
        var text = ThemeManager.ParseOr(Model.TextColor, Model.Background != null ? ThemeManager.ContrastText(bg) : p.Text);

        _face.CornerRadius = new CornerRadius(r);
        _flash.CornerRadius = new CornerRadius(r);
        _focusRing.CornerRadius = new CornerRadius(r + 3);
        _focusRing.BorderBrush = ThemeManager.Brush(p.Accent);
        _editOutline.RadiusX = _editOutline.RadiusY = r;
        _editOutline.Visibility = editMode ? Visibility.Visible : Visibility.Collapsed;

        var stateText = _live.State ?? subtitle;
        var toneColor = _live.Tone switch
        {
            LiveTone.Active => accent,
            LiveTone.Alert => p.Danger,
            _ => (Color?)null,
        };

        _content.Children.Clear();

var stateText = _live.State ?? subtitle;
var hasTitle = title.Length > 0;
var hasSub = stateText.Length > 0;

var iconBrush = ThemeManager.Brush(_live.Tone == LiveTone.Alert ? p.Danger : hasCustomAccent ? accent : text);
var iconEl = IconVisual.Create(_live.Icon != null && IsStockIcon(icon) ? _live.Icon : icon, 36, iconBrush);

var iconScale = !hasTitle && !hasSub ? 2.00 : 1.45;
iconEl.LayoutTransform = new ScaleTransform(iconScale, iconScale);
iconEl.HorizontalAlignment = HorizontalAlignment.Center;
iconEl.VerticalAlignment = VerticalAlignment.Center;

var group = new StackPanel
{
    VerticalAlignment = VerticalAlignment.Center,
    HorizontalAlignment = HorizontalAlignment.Stretch
};

var iconSlot = new Grid { Height = hasTitle || hasSub ? 60 : 82 };
iconSlot.Children.Add(iconEl);
group.Children.Add(iconSlot);

if (hasTitle)
{
    group.Children.Add(new TextBlock
    {
        Text = title,
        FontFamily = IconVisual.Display,
        FontSize = 10.5 * fs,
        FontWeight = FontWeights.SemiBold,
        Foreground = ThemeManager.Brush(text),
        TextAlignment = TextAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = title.Length > 14 ? TextWrapping.Wrap : TextWrapping.NoWrap,
        MaxHeight = 26 * fs,
    });
}

if (hasSub)
{
    group.Children.Add(new TextBlock
    {
        Text = stateText,
        FontSize = 8.6 * fs,
        FontWeight = toneColor != null ? FontWeights.Bold : FontWeights.Normal,
        Foreground = ThemeManager.Brush(toneColor ?? ThemeManager.Mix(text, bg, 0.4)),
        TextAlignment = TextAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(0, 2, 0, 0),
    });
}

_content.Children.Add(group);

        _content.Opacity = _live.Tone == LiveTone.Dim ? 0.55 : 1;
        _dot.Fill = ThemeManager.Brush(toneColor ?? accent);
        _dot.Visibility = toneColor != null ? Visibility.Visible : Visibility.Collapsed;
        _editOutline.Stroke = ThemeManager.Brush(p.TextMuted);
        _progress.Background = ThemeManager.Brush(accent);
        UpdateProgress(_live.Progress);

        var tip = _live.Tooltip;
        var actionLabel = ActionTypes.Label(action.Type);
        var hk = Model.Hotkey.Length > 0 ? $"\nShortcut: {Model.Hotkey}" : "";
        ToolTip = editMode ? $"{(hasTitle ? title : "Button")} — click to edit, drag to move" :
            $"{(hasTitle ? title : actionLabel)}{(tip != null ? "\n" + tip : "")}{hk}";
        System.Windows.Automation.AutomationProperties.SetName(this, $"{title} {stateText}".Trim());
        ApplyFace();
    }

    /// <summary>Live icons (mic muted, pause...) replace only the default glyphs, never a user's custom image or emoji.</summary>
    private static bool IsStockIcon(IconSpec icon) => icon.Kind == IconKinds.Glyph;

    /// <summary>Real microphone level on the key (dBFS), or null to hide the meter.</summary>
    public void SetMeter(float? db, bool clip)
    {
        if (db == null) { _meterTrack.Visibility = Visibility.Collapsed; return; }
        var p = ThemeManager.Current;
        _meterTrack.Visibility = Visibility.Visible;
        _meterTrack.Background = ThemeManager.Brush(p.Input);
        var frac = Math.Clamp((db.Value - LevelMeter.Min) / -LevelMeter.Min, 0, 1);
        _meterScale.ScaleX = frac;
        _meterFill.Background = ThemeManager.Brush(clip ? p.Danger : LevelMeter.ZoneColor(db.Value, p));
    }

    public void UpdateProgress(double? progress)
    {
        if (progress == null) { _progress.Visibility = Visibility.Collapsed; return; }
        _progress.Visibility = Visibility.Visible;
        _progress.Opacity = progress < 0 ? 0.5 : 1;
        _progressScale.ScaleX = progress < 0 ? 1 : Math.Clamp(progress.Value, 0, 1);
    }

    private void ApplyFace()
    {
        var p = ThemeManager.Current;
        var (_, _, _, accentHex, _) = Deck.Effective(Model);
        var accent = ThemeManager.ParseOr(accentHex, p.Accent);
        var bg = ThemeManager.ParseOr(Model.Background, p.Key);
        if (_hover && !_edit) bg = Model.Background != null ? ThemeManager.Mix(bg, Colors.White, 0.07) : p.KeyHover;
        var border = p.BorderSubtle;
        var thickness = 1.0;
        switch (_live.Tone)
        {
            case LiveTone.Active:
                bg = ThemeManager.Mix(bg, accent, 0.14);
                border = ThemeManager.Mix(accent, bg, 0.25);
                thickness = 1.5;
                break;
            case LiveTone.Alert:
                bg = ThemeManager.Mix(bg, p.Danger, 0.16);
                border = ThemeManager.Mix(p.Danger, bg, 0.2);
                thickness = 1.5;
                break;
        }
        if (_selected) { border = p.Accent; thickness = 2.5; }
        _face.Background = ThemeManager.Brush(bg);
        _face.BorderBrush = ThemeManager.Brush(border);
        _face.BorderThickness = new Thickness(thickness);
    }

    // ---- feedback animations -------------------------------------------------------------------

    private static Duration D(double ms) => new(TimeSpan.FromMilliseconds(ms));

    public void PressVisual(bool down)
    {
        var level = ThemeManager.AnimationLevel;
        var target = down ? (level == 2 ? 0.92 : 0.95) : 1.0;
        if (level == 0)
        {
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _scale.ScaleX = _scale.ScaleY = target;
            return;
        }
        var anim = new DoubleAnimation(target, D(down ? 55 : 140)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        anim.Freeze();
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    public void Flash(bool ok)
    {
        var p = ThemeManager.Current;
        _flash.Background = ThemeManager.Brush(ok ? p.Accent : p.Danger, (byte)(ok ? 0x55 : 0x77));
        if (ThemeManager.AnimationLevel == 0)
        {
            if (!ok) { _flash.Opacity = 1; var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) }; t.Tick += (_, _) => { _flash.Opacity = 0; t.Stop(); }; t.Start(); }
            return;
        }
        var fade = new DoubleAnimation(1, 0, D(ok ? 360 : 650)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        _flash.BeginAnimation(OpacityProperty, fade);
        if (!ok && ThemeManager.AnimationLevel == 2)
        {
            var shake = new DoubleAnimationUsingKeyFrames { Duration = D(320) };
            foreach (var (t, x) in new[] { (0.0, 0.0), (50.0, -5.0), (110.0, 5.0), (170.0, -3.0), (230.0, 2.0), (320.0, 0.0) })
                shake.KeyFrames.Add(new LinearDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t))));
            _shake.BeginAnimation(TranslateTransform.XProperty, shake);
        }
    }

    // ---- off-screen rendering (tablet remote) ------------------------------------------------

    public static byte[] RenderPng(Deck deck, DeckButton b, int width, int height)
    {
        var v = new DeckButtonView(b) { Width = width, Height = height };
        v.Update(deck, false);
        v.Measure(new Size(width, height));
        v.Arrange(new Rect(0, 0, width, height));
        v.UpdateLayout();
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}

/// <summary>A free cell. In edit mode it shows a "+" and accepts dropped buttons.</summary>
public sealed class EmptySlotView : Border
{
    public int Row { get; }
    public int Col { get; }
    private readonly TextBlock _plus;

    public EmptySlotView(int row, int col, bool edit)
    {
        Row = row;
        Col = col;
        var p = ThemeManager.Current;
        CornerRadius = new CornerRadius(ThemeManager.CornerRadius);
        BorderThickness = new Thickness(1);
        BorderBrush = ThemeManager.Brush(edit ? p.BorderStrong : p.BorderSubtle, (byte)(edit ? 0xFF : 0x70));
        Background = ThemeManager.Brush(p.Key, (byte)(edit ? 0x80 : 0x30));
        _plus = new TextBlock
        {
            Text = "", FontFamily = IconVisual.Glyphs, FontSize = 18, Foreground = ThemeManager.Brush(p.TextMuted),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Visibility = edit ? Visibility.Visible : Visibility.Collapsed,
        };
        Child = _plus;
        Cursor = edit ? Cursors.Hand : null;
        Focusable = edit;
        FocusVisualStyle = null;
        if (edit) ToolTip = "Add a button here";
        AllowDrop = edit;
        MouseEnter += (_, _) => { if (edit) Background = ThemeManager.Brush(p.KeyHover); };
        MouseLeave += (_, _) => { if (edit) Background = ThemeManager.Brush(p.Key, 0x80); };
    }

    public void DragHighlight(bool on) =>
        BorderBrush = ThemeManager.Brush(on ? ThemeManager.Current.Accent : ThemeManager.Current.BorderStrong);
}

/// <summary>Uniform grid with row/column spans, optional square cells and a cap on cell size.</summary>
public sealed class DeckGridPanel : Panel
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.RegisterAttached("Row", typeof(int), typeof(DeckGridPanel), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsParentArrange));
    public static readonly DependencyProperty ColProperty = DependencyProperty.RegisterAttached("Col", typeof(int), typeof(DeckGridPanel), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsParentArrange));
    public static readonly DependencyProperty RowSpanProperty = DependencyProperty.RegisterAttached("RowSpan", typeof(int), typeof(DeckGridPanel), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentArrange));
    public static readonly DependencyProperty ColSpanProperty = DependencyProperty.RegisterAttached("ColSpan", typeof(int), typeof(DeckGridPanel), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static void Place(UIElement e, int row, int col, int rs = 1, int cs = 1)
    {
        e.SetValue(RowProperty, row);
        e.SetValue(ColProperty, col);
        e.SetValue(RowSpanProperty, rs);
        e.SetValue(ColSpanProperty, cs);
    }

    public int Columns { get; set; } = 5;
    public int Rows { get; set; } = 3;
    public double Gap { get; set; } = 12;
    public double MaxCell { get; set; } = double.PositiveInfinity;
    public bool Square { get; set; } = true;

    private (double W, double H) Cell(Size avail)
    {
        var w = double.IsInfinity(avail.Width) ? 120 * Columns : avail.Width;
        var h = double.IsInfinity(avail.Height) ? 120 * Rows : avail.Height;
        var cw = Math.Max(8, (w - Gap * (Columns - 1)) / Columns);
        var ch = Math.Max(8, (h - Gap * (Rows - 1)) / Rows);
        if (Square) cw = ch = Math.Min(cw, ch);
        cw = Math.Min(cw, MaxCell);
        ch = Math.Min(ch, MaxCell);
        return (cw, ch);
    }

    protected override Size MeasureOverride(Size avail)
    {
        var (cw, ch) = Cell(avail);
        foreach (UIElement c in InternalChildren)
        {
            var rs = (int)c.GetValue(RowSpanProperty);
            var cs = (int)c.GetValue(ColSpanProperty);
            c.Measure(new Size(cw * cs + Gap * (cs - 1), ch * rs + Gap * (rs - 1)));
        }
        return new Size(double.IsInfinity(avail.Width) ? cw * Columns + Gap * (Columns - 1) : avail.Width,
                        double.IsInfinity(avail.Height) ? ch * Rows + Gap * (Rows - 1) : avail.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (cw, ch) = Cell(final);
        var gridW = cw * Columns + Gap * (Columns - 1);
        var gridH = ch * Rows + Gap * (Rows - 1);
        var ox = Math.Max(0, (final.Width - gridW) / 2);
        var oy = Math.Max(0, (final.Height - gridH) / 2);
        foreach (UIElement c in InternalChildren)
        {
            var r = (int)c.GetValue(RowProperty);
            var col = (int)c.GetValue(ColProperty);
            var rs = (int)c.GetValue(RowSpanProperty);
            var cs = (int)c.GetValue(ColSpanProperty);
            c.Arrange(new Rect(ox + col * (cw + Gap), oy + r * (ch + Gap), cw * cs + Gap * (cs - 1), ch * rs + Gap * (rs - 1)));
        }
        return final;
    }

    /// <summary>Grid cell under a point (panel coordinates), or null.</summary>
    public (int Row, int Col)? HitCell(Point pt)
    {
        var (cw, ch) = Cell(RenderSize);
        var gridW = cw * Columns + Gap * (Columns - 1);
        var gridH = ch * Rows + Gap * (Rows - 1);
        var x = pt.X - Math.Max(0, (RenderSize.Width - gridW) / 2);
        var y = pt.Y - Math.Max(0, (RenderSize.Height - gridH) / 2);
        if (x < 0 || y < 0) return null;
        var col = (int)(x / (cw + Gap));
        var row = (int)(y / (ch + Gap));
        return row < Rows && col < Columns ? (row, col) : null;
    }
}
