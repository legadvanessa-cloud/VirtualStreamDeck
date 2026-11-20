using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.UI;
using VirtualStreamDeck.UI.Windows;

namespace VirtualStreamDeck.UI.Controls;

/// <summary>Code-built form controls with consistent spacing, used by the editor, settings and wizard.</summary>
public static class F
{
    public static TextBlock Caps(string text, double top = 18) => Styled(new TextBlock { Text = French.T(text).ToUpperInvariant(), Margin = new Thickness(0, top, 0, 8) }, "Text.Caps");

    public static TextBlock Heading(string text, double size = 20) => Styled(new TextBlock { Text = French.T(text), FontSize = size, TextWrapping = TextWrapping.Wrap }, "Text.Heading");

    public static TextBlock Hint(string text, double top = 4) => Styled(new TextBlock { Text = French.T(text), Margin = new Thickness(0, top, 0, 0) }, "Text.Hint");

    private static T Styled<T>(T el, string style) where T : FrameworkElement
    {
        el.SetResourceReference(FrameworkElement.StyleProperty, style);
        return el;
    }

    public static FrameworkElement Field(string label, UIElement control, string? hint = null)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var lb = new TextBlock { Text = French.T(label), FontSize = 12.5, Margin = new Thickness(0, 0, 0, 6) };
        lb.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        sp.Children.Add(lb);
        sp.Children.Add(control);
        if (hint != null) sp.Children.Add(Hint(hint));
        return sp;
    }

    public static TextBox Text(string value, Action<string> set, bool multiline = false)
    {
        var tb = new TextBox { Text = value };
        if (multiline)
        {
            tb.AcceptsReturn = true;
            tb.TextWrapping = TextWrapping.Wrap;
            tb.MinHeight = 70;
            tb.VerticalContentAlignment = VerticalAlignment.Top;
            tb.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            tb.FontSize = 12;
        }
        tb.TextChanged += (_, _) => set(tb.Text);
        return tb;
    }

    public static ComboBox Combo(IEnumerable<(string Id, string Label)> items, string selected, Action<string> set)
    {
        var cb = new ComboBox { MaxDropDownHeight = 420 };
        var list = items.ToList();
        foreach (var (id, label) in list) cb.Items.Add(new ComboBoxItem { Content = French.T(label), Tag = id });
        var idx = list.FindIndex(x => x.Id == selected);
        cb.SelectedIndex = idx >= 0 ? idx : (list.Count > 0 ? 0 : -1);
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is ComboBoxItem { Tag: string id }) set(id);
        };
        return cb;
    }

    public static CheckBox Toggle(string label, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = French.T(label), IsChecked = value, Margin = new Thickness(0, 4, 0, 10) };
        c.Checked += (_, _) => set(true);
        c.Unchecked += (_, _) => set(false);
        return c;
    }

    public static FrameworkElement ToggleWithHint(string label, bool value, Action<bool> set, string hint)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var t = Toggle(label, value, set);
        t.Margin = new Thickness(0, 4, 0, 0);
        sp.Children.Add(t);
        var h = Hint(hint, 2);
        h.Margin = new Thickness(50, 2, 0, 0);
        sp.Children.Add(h);
        return sp;
    }

    public static FrameworkElement Slider(double min, double max, double value, double step, Func<double, string> format, Action<double> set)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        var s = new Slider { Minimum = min, Maximum = max, Value = value, SmallChange = step, LargeChange = step * 5, TickFrequency = step, IsSnapToTickEnabled = true };
        var lbl = new TextBlock { Text = format(value), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        s.ValueChanged += (_, e) => { lbl.Text = format(e.NewValue); set(e.NewValue); };
        Grid.SetColumn(lbl, 1);
        g.Children.Add(s);
        g.Children.Add(lbl);
        return g;
    }

    public static TextBox Number(int value, int min, int max, Action<int> set)
    {
        var tb = new TextBox { Text = value.ToString(), Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
        tb.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        tb.TextChanged += (_, _) =>
        {
            if (int.TryParse(tb.Text, out var v)) set(Math.Clamp(v, min, max));
        };
        tb.LostFocus += (_, _) =>
        {
            var v = int.TryParse(tb.Text, out var x) ? Math.Clamp(x, min, max) : min;
            tb.Text = v.ToString();
        };
        return tb;
    }

    public static Button Button(string label, Action click, string style = "", string? tip = null)
    {
        var b = new Button { Content = French.T(label), ToolTip = tip == null ? null : French.T(tip) };
        if (style.Length > 0) b.SetResourceReference(FrameworkElement.StyleProperty, style);
        b.Click += (_, _) => click();
        return b;
    }

    public static Button IconButton(string glyph, string tip, Action click)
    {
        var translatedTip = French.T(tip);
        var b = new Button { Content = glyph, ToolTip = translatedTip };
        b.SetResourceReference(FrameworkElement.StyleProperty, "Btn.Icon");
        System.Windows.Automation.AutomationProperties.SetName(b, translatedTip);
        b.Click += (_, _) => click();
        return b;
    }

    public static StackPanel Buttons(params UIElement[] items)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 10) };
        foreach (var i in items)
        {
            if (i is FrameworkElement fe && sp.Children.Count > 0) fe.Margin = new Thickness(8, 0, 0, 0);
            sp.Children.Add(i);
        }
        return sp;
    }

    /// <summary>Path box + Browse button.</summary>
    public static FrameworkElement Path(string value, Action<string> set, string filter, bool folder = false)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tb = Text(value, set);
        var btn = Button("Browse…", () =>
        {
            var w = Window.GetWindow(g);
            var r = folder ? Pickers.Folder(w, tb.Text) : Pickers.File(w, filter, tb.Text);
            if (r != null) tb.Text = r;
        });
        btn.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(btn, 1);
        g.Children.Add(tb);
        g.Children.Add(btn);
        return g;
    }

    /// <summary>Colour swatches + hex box. null = "use theme".</summary>
    public static FrameworkElement Color(string? value, Action<string?> set, bool allowTheme = true)
    {
        var sp = new StackPanel();
        var wrap = new WrapPanel();
        var hex = new TextBox { Text = value ?? "", Width = 110, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), ToolTip = French.T("Hex colour, e.g. #3B82F6") };
        var updating = false;
        void Choose(string? v)
        {
            updating = true;
            hex.Text = v ?? "";
            updating = false;
            set(v);
            foreach (var b in wrap.Children.OfType<Border>()) b.BorderBrush = ThemeManager.Brush((b.Tag as string) == v ? ThemeManager.Current.Text : Colors.Transparent);
        }
        if (allowTheme)
        {
            var themeChip = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(2),
                BorderBrush = ThemeManager.Brush(value == null ? ThemeManager.Current.Text : Colors.Transparent),
                Background = ThemeManager.Brush(ThemeManager.Current.Raised), Cursor = Cursors.Hand, ToolTip = French.T("Theme default"), Tag = null,
                Child = new TextBlock { Text = "", FontFamily = IconVisual.Glyphs, FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeManager.Brush(ThemeManager.Current.Text) },
            };
            themeChip.MouseLeftButtonUp += (_, _) => Choose(null);
            wrap.Children.Add(themeChip);
        }
        foreach (var s in ThemeManager.Swatches)
        {
            var chip = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(2),
                BorderBrush = ThemeManager.Brush(string.Equals(s, value, StringComparison.OrdinalIgnoreCase) ? ThemeManager.Current.Text : Colors.Transparent),
                Background = ThemeManager.Brush(ThemeManager.ParseOr(s, Colors.Gray)), Cursor = Cursors.Hand, ToolTip = s, Tag = s,
            };
            chip.MouseLeftButtonUp += (_, _) => Choose(s);
            wrap.Children.Add(chip);
        }
        hex.TextChanged += (_, _) =>
        {
            if (updating) return;
            var n = ColorUtil.Normalize(hex.Text);
            if (n != null) set(n);
            else if (hex.Text.Trim().Length == 0 && allowTheme) set(null);
        };
        sp.Children.Add(wrap);
        sp.Children.Add(hex);
        return sp;
    }

    public static Border Card(UIElement child, double pad = 16)
    {
        var b = new Border { Child = child, Padding = new Thickness(pad), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12) };
        b.SetResourceReference(Border.BackgroundProperty, "Bg.Panel");
        b.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        return b;
    }
}

/// <summary>
/// Records a key combination by pressing it. Global hotkeys are suspended while it has focus so
/// that a chord the app already owns can be re-recorded instead of firing.
/// </summary>
public sealed class ChordBox : Grid
{
    public static event Action<bool>? Recording;
    private readonly TextBox _box = new() { IsReadOnly = true, IsReadOnlyCaretVisible = false, Cursor = Cursors.Hand };
    private string _value;
    private readonly Action<string> _set;
    private readonly bool _allowBare;

    public ChordBox(string value, Action<string> set, bool allowBareKeys = true)
    {
        _value = KeyChord.Normalize(value);
        _set = set;
        _allowBare = allowBareKeys;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _box.Text = Display();
        _box.ToolTip = French.T("Click, then press the key combination. Backspace clears.");
        _box.PreviewKeyDown += OnKey;
        _box.GotKeyboardFocus += (_, _) => { _box.Text = French.T("Press keys…"); Recording?.Invoke(true); };
        _box.LostKeyboardFocus += (_, _) => { _box.Text = Display(); Recording?.Invoke(false); };
        Children.Add(_box);
        var type = F.IconButton("", "Type it instead (for keys like F13–F24)", () =>
        {
            var r = DialogWindow.Prompt(Window.GetWindow(this), French.T("Key combination"), French.T("Type a combination such as CTRL + SHIFT + M or F13."), _value);
            if (r == null) return;
            if (r.Length > 0 && !KeyChord.TryParse(r, out _)) { DialogWindow.Info(Window.GetWindow(this), French.T("Not a valid key combination"), $"'{r}' {French.T("could not be understood.")}"); return; }
            Commit(KeyChord.Normalize(r));
        });
        Grid.SetColumn(type, 1);
        Children.Add(type);
        var clear = F.IconButton("", "Clear", () => Commit(""));
        Grid.SetColumn(clear, 2);
        Children.Add(clear);
    }

    private string Display() => _value.Length == 0 ? French.T("None") : _value;

    private void Commit(string v)
    {
        _value = v;
        _box.Text = _box.IsKeyboardFocused ? v : Display();
        _set(v);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key is Key.Tab && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = false; return; }
        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None) { Commit(""); return; }
        if (key is Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { Keyboard.ClearFocus(); return; }
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (KeyChord.IsModifierVk(vk)) { _box.Text = Mods() + " + …"; return; }
        var mods = ChordModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= ChordModifiers.Ctrl;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= ChordModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= ChordModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= ChordModifiers.Win;
        if (mods == ChordModifiers.None && !_allowBare && !(vk is >= 0x7C and <= 0x87)) { _box.Text = French.T("Add CTRL, ALT, SHIFT or WIN"); return; }
        if (KeyChord.NameOf(vk) == null) { _box.Text = French.T("That key is not supported"); return; }
        Commit(new KeyChord(mods, vk).ToString());
    }

    private static string Mods()
    {
        var parts = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("CTRL");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("ALT");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("SHIFT");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("WIN");
        return string.Join(" + ", parts);
    }
}
