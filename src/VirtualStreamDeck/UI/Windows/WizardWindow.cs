using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Services;
using VirtualStreamDeck.UI.Controls;

namespace VirtualStreamDeck.UI.Windows;

public sealed record WizardResult(string Surface, string DisplayDevice, int Cols, int Rows, List<string> Templates);

/// <summary>Four-step first-run setup: welcome, control surface, layout, first profiles.</summary>
public sealed class WizardWindow : ChromeWindow
{
    private int _step;
    private readonly ContentControl _page = new();
    private readonly Button _back;
    private readonly Button _next;
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private string _surface = "main";
    private string _display = "";
    private (int C, int R) _layout = (5, 3);
    private readonly HashSet<string> _templates = new() { "general", "streaming", "radio" };
    public WizardResult? Result { get; private set; }

    public WizardWindow() : base(resizable: false, minMax: false)
    {
        Caption = "Welcome";
        Width = 780;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(36, 8, 36, 24) };
        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        _next = F.Button("Next", Next, "Btn.Accent");
        _next.MinWidth = 120;
        _back = F.Button("Back", () => { _step--; Show(); });
        _back.MinWidth = 100;
        _back.Margin = new Thickness(0, 0, 10, 0);
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        btns.Children.Add(_back);
        btns.Children.Add(_next);
        DockPanel.SetDock(btns, Dock.Right);
        footer.Children.Add(btns);
        footer.Children.Add(_dots);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(_page);
        Body = root;
        Show();
    }

    private new void Show()
    {
        _back.Visibility = _step == 0 ? Visibility.Hidden : Visibility.Visible;
        _next.Content = _step == 3 ? "Open dashboard" : _step == 0 ? "Get started" : "Next";
        _dots.Children.Clear();
        for (var i = 0; i < 4; i++)
        {
            var d = new Border { Width = i == _step ? 22 : 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 6, 0) };
            d.SetResourceReference(Border.BackgroundProperty, i == _step ? "Accent" : "Border.Strong");
            _dots.Children.Add(d);
        }
        _page.Content = _step switch
        {
            0 => Welcome(),
            1 => SurfaceStep(),
            2 => LayoutStep(),
            _ => ProfilesStep(),
        };
    }

    private void Next()
    {
        if (_step == 3)
        {
            if (_templates.Count == 0) { DialogWindow.Info(this, "Choose a profile", "Pick at least one profile to start with."); return; }
            var order = Defaults.Templates.Select(t => t.Id).Where(_templates.Contains).ToList();
            Result = new WizardResult(_surface, _display, _layout.C, _layout.R, order);
            DialogResult = true;
            Close();
            return;
        }
        _step++;
        Show();
    }

    private static UIElement Welcome()
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (AppIconLarge != null) sp.Children.Add(new Image { Source = AppIconLarge, Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 18) });
        sp.Children.Add(F.Heading("Welcome to Virtual Stream Deck", 28));
        sp.Children.Add(F.Hint("Turn any screen — a second monitor, a touchscreen or this one — into a programmable control surface: " +
                               "app launchers, shortcuts and macros, microphone and camera control, media keys, an Internet-radio style " +
                               "broadcast console and a soundboard. Everything runs locally; nothing needs an account or the internet.", 12));
        sp.Children.Add(F.Hint("This takes four quick steps. You can change every choice later in Settings.", 16));
        return sp;
    }

    private UIElement SurfaceStep()
    {
        var sp = new StackPanel();
        sp.Children.Add(F.Heading("Choose your control surface"));
        sp.Children.Add(F.Hint("Where will you press the buttons?", 6));
        var cards = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        var displays = Displays.All();
        var secondary = displays.FirstOrDefault(d => !d.IsPrimary);
        var displayPicker = new StackPanel { Margin = new Thickness(30, 0, 0, 10) };
        void Card(string id, string title, string text, bool enabled = true)
        {
            var rb = new RadioButton { GroupName = "surface", IsChecked = _surface == id, IsEnabled = enabled };
            rb.SetResourceReference(StyleProperty, "Radio.Card");
            var c = new StackPanel();
            c.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
            c.Children.Add(F.Hint(text, 4));
            rb.Content = c;
            rb.Checked += (_, _) =>
            {
                _surface = id;
                displayPicker.Visibility = id == "main" ? Visibility.Collapsed : Visibility.Visible;
                if (id != "main" && _display.Length == 0) _display = (secondary ?? displays.First()).DeviceName;
            };
            cards.Children.Add(rb);
        }
        Card("main", "Main monitor", "Use the dashboard window on this screen, next to your other apps.");
        Card("secondary", "Secondary monitor", secondary != null ? "Open a full-screen control surface on another display." : "Only one display is connected right now — you can still choose it.");
        Card("touch", "Touchscreen", "Full-screen surface with touch-first behaviour: larger targets, cursor hidden, screen kept awake.");
        sp.Children.Add(cards);
        displayPicker.Children.Add(F.Field("Display", F.Combo(displays.Select(d => (d.DeviceName, d.Label)),
            _display.Length > 0 ? _display : (secondary ?? displays.First()).DeviceName, v => _display = v)));
        displayPicker.Visibility = _surface == "main" ? Visibility.Collapsed : Visibility.Visible;
        sp.Children.Add(displayPicker);
        return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private UIElement LayoutStep()
    {
        var sp = new StackPanel();
        sp.Children.Add(F.Heading("Choose layout"));
        sp.Children.Add(F.Hint("Columns × rows. Buttons resize to fill the screen. Each profile can use its own layout later.", 6));
        var wrap = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        foreach (var (c, r) in GridLayout.Presets)
        {
            var rb = new RadioButton { GroupName = "layout", IsChecked = _layout == (c, r), Width = 210 };
            rb.SetResourceReference(StyleProperty, "Radio.Card");
            var inner = new StackPanel();
            var ug = new UniformGrid { Columns = c, Rows = r, Width = 170, Height = 170.0 * r / c * 0.9, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Left };
            for (var i = 0; i < c * r; i++)
            {
                var cell = new Border { CornerRadius = new CornerRadius(4), Margin = new Thickness(2) };
                cell.SetResourceReference(Border.BackgroundProperty, "Border.Strong");
                ug.Children.Add(cell);
            }
            inner.Children.Add(ug);
            inner.Children.Add(new TextBlock { Text = $"{c} × {r}", FontSize = 15, FontWeight = FontWeights.SemiBold });
            inner.Children.Add(F.Hint($"{c * r} buttons per page", 2));
            rb.Content = inner;
            var sel = (c, r);
            rb.Checked += (_, _) => _layout = sel;
            wrap.Children.Add(rb);
        }
        sp.Children.Add(wrap);
        return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private UIElement ProfilesStep()
    {
        var sp = new StackPanel();
        sp.Children.Add(F.Heading("Create your first profiles"));
        sp.Children.Add(F.Hint("Each profile has its own buttons. Pick as many as you like — they come with useful defaults.", 6));
        var wrap = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        foreach (var t in Defaults.Templates)
        {
            var tb = new ToggleButton { IsChecked = _templates.Contains(t.Id), Width = 330, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(14, 10, 14, 10) };
            tb.SetResourceReference(StyleProperty, "Toggle.Card");
            var row = new DockPanel();
            var g = new TextBlock { Text = char.ConvertFromUtf32(Convert.ToInt32(t.Glyph, 16)), FontFamily = IconVisual.Glyphs, FontSize = 20, Width = 34, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(g, Dock.Left);
            row.Children.Add(g);
            var c = new StackPanel();
            c.Children.Add(new TextBlock { Text = t.Name, FontSize = 14, FontWeight = FontWeights.SemiBold });
            c.Children.Add(F.Hint(t.Blurb, 2));
            row.Children.Add(c);
            tb.Content = row;
            var id = t.Id;
            tb.Checked += (_, _) => _templates.Add(id);
            tb.Unchecked += (_, _) => _templates.Remove(id);
            wrap.Children.Add(tb);
        }
        sp.Children.Add(wrap);
        return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
