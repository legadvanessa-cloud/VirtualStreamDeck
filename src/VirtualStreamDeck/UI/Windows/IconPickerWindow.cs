using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.UI.Controls;

namespace VirtualStreamDeck.UI.Windows;

/// <summary>Built-in icon picker: glyph categories, emoji, text icons and local images.</summary>
public sealed class IconPickerWindow : ChromeWindow
{
    private readonly WrapPanel _grid = new();
    private readonly TextBox _search = new();
    private string _category = "All";
    private IconSpec? _result;

    private IconPickerWindow(Window? owner, IconSpec current) : base(resizable: true, minMax: false)
    {
        Caption = "Choose icon";
        Width = 720;
        Height = 620;
        MinWidth = 520;
        MinHeight = 420;
        if (owner != null) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        ShowInTaskbar = false;

        var root = new DockPanel { Margin = new Thickness(20, 4, 20, 16) };
        var top = new StackPanel();
        top.Children.Add(F.Field("Search", _search));
        var cats = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var c in new[] { "All" }.Concat(IconCatalog.Categories).Concat(new[] { "Emoji" }))
        {
            var t = new ToggleButton { Content = c, IsChecked = c == _category, Margin = new Thickness(0, 0, 6, 6) };
            t.SetResourceReference(StyleProperty, "Toggle.Pill");
            t.Click += (_, _) =>
            {
                _category = c;
                foreach (var o in cats.Children.OfType<ToggleButton>()) o.IsChecked = o == t;
                Fill();
            };
            cats.Children.Add(t);
        }
        top.Children.Add(cats);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var textRow = new DockPanel();
        var textBox = new TextBox { Text = current.Kind == IconKinds.Text ? current.Value : "", MaxLength = 4, Width = 120 };
        var useText = F.Button("Use text", () =>
        {
            if (textBox.Text.Trim().Length == 0) return;
            _result = IconSpec.TextIcon(textBox.Text.Trim());
            Close();
        });
        useText.Margin = new Thickness(8, 0, 0, 0);
        var img = F.Button("Local image…", () =>
        {
            var path = Pickers.File(this, "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.tif;*.tiff|All files|*.*");
            if (path == null) return;
            try
            {
                _result = new IconSpec { Kind = IconKinds.Image, ImageData = ThemeManager.ImportImage(path), Value = System.IO.Path.GetFileName(path) };
                Close();
            }
            catch (Exception ex) { DialogWindow.Info(this, "Could not load the image", ex.Message); }
        }, tip: "Stored inside your configuration (resized to 256 px), so exports carry it along.");
        DockPanel.SetDock(img, Dock.Right);
        textRow.Children.Add(img);
        var tl = new TextBlock { Text = "Text icon (1–4 characters):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        tl.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        textRow.Children.Add(tl);
        var tsp = new StackPanel { Orientation = Orientation.Horizontal };
        tsp.Children.Add(textBox);
        tsp.Children.Add(useText);
        textRow.Children.Add(tsp);
        bottom.Children.Add(textRow);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        root.Children.Add(new ScrollViewer { Content = _grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Body = root;
        _search.TextChanged += (_, _) => Fill();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
        Loaded += (_, _) => _search.Focus();
        Fill();
    }

    private void Fill()
    {
        _grid.Children.Clear();
        var q = _search.Text.Trim();
        if (_category is "Emoji" or "All")
        {
            if (_category == "Emoji" || q.Length == 0)
                foreach (var e in IconCatalog.Emojis)
                    if (_category == "Emoji") _grid.Children.Add(Tile(IconSpec.Emoji(e), "Emoji " + e));
        }
        if (_category != "Emoji")
        {
            var seen = new HashSet<string>();
            foreach (var g in IconCatalog.Glyphs)
            {
                if (_category != "All" && g.Category != _category) continue;
                if (q.Length > 0 && !g.Name.Contains(q, StringComparison.OrdinalIgnoreCase) && !g.Category.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                if (_category == "All" && !seen.Add(g.Code)) continue;
                _grid.Children.Add(Tile(IconSpec.Glyph(g.Code), g.Name));
            }
        }
        if (_grid.Children.Count == 0) _grid.Children.Add(F.Hint("No icons match.", 10));
    }

    private Button Tile(IconSpec spec, string name)
    {
        var sp = new StackPanel();
        sp.Children.Add(IconVisual.Create(spec, 24, ThemeManager.Brush(ThemeManager.Current.Text)));
        sp.Children.Add(new TextBlock { Text = name, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), MaxWidth = 72 });
        var b = new Button { Content = sp, Width = 84, Height = 76, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(4), ToolTip = name };
        b.Click += (_, _) => { _result = spec; Close(); };
        return b;
    }

    public static IconSpec? Pick(Window? owner, IconSpec current)
    {
        var w = new IconPickerWindow(owner, current);
        w.ShowDialog();
        return w._result;
    }
}
