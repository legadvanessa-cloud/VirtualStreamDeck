using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Services;

namespace VirtualStreamDeck.UI.Controls;

public sealed record PaletteItem(string Title, string Detail, string Glyph, Func<Task> Run);

/// <summary>CTRL+K command search across every button, profile, sound and built-in command.</summary>
public sealed class CommandPalette : Grid
{
    private readonly Deck _deck;
    private readonly TextBox _box = new() { FontSize = 16, MinHeight = 44, Padding = new Thickness(12, 8, 12, 8) };
    private readonly ListBox _list = new() { MaxHeight = 420 };
    private List<PaletteItem> _all = new();
    public Func<IEnumerable<PaletteItem>>? ExtraCommands { get; set; }

    public CommandPalette(Deck deck)
    {
        _deck = deck;
        Visibility = Visibility.Collapsed;
        var shade = new Border();
        shade.SetResourceReference(Border.BackgroundProperty, "Overlay");
        shade.MouseLeftButtonDown += (_, _) => Hide();
        Children.Add(shade);
        var card = new Border
        {
            Width = 620, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 80, 0, 0),
            CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(10),
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg.Raised");
        card.SetResourceReference(Border.BorderBrushProperty, "Border.Strong");
        var sp = new StackPanel();
        sp.Children.Add(_box);
        var hint = F.Hint("Search commands…   ↑ ↓ to choose · Enter to run · Esc to close", 8);
        hint.Margin = new Thickness(4, 8, 0, 6);
        sp.Children.Add(hint);
        sp.Children.Add(_list);
        card.Child = sp;
        Children.Add(card);
        System.Windows.Automation.AutomationProperties.SetName(_box, "Search commands");

        _box.TextChanged += (_, _) => Filter();
        _box.PreviewKeyDown += OnKey;
        _list.MouseDoubleClick += (_, _) => RunSelected();
        _list.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; RunSelected(); } else if (e.Key == Key.Escape) { e.Handled = true; Hide(); } };
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    public void Show()
    {
        _all = Collect().ToList();
        _box.Text = "";
        Filter();
        Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => { _box.Focus(); Keyboard.Focus(_box); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    public void Hide() => Visibility = Visibility.Collapsed;

    private IEnumerable<PaletteItem> Collect()
    {
        var ctx = () => new ActionContext(ActionSource.Palette, Window.GetWindow(this));
        foreach (var (p, pg, b) in GridLayout.AllButtons(_deck.Config))
        {
            var (title, sub, icon, _, action) = Deck.Effective(b);
            var name = title.Length > 0 ? title : ActionTypes.Label(action.Type);
            var page = p.Pages.Count > 1 ? $" · page {p.Pages.IndexOf(pg) + 1}" : "";
            yield return new PaletteItem(Pretty(name), $"{p.Name}{page} — {ActionTypes.Label(action.Type)}", icon.Kind == IconKinds.Glyph ? icon.Value : "E8A7",
                async () => { await _deck.PressAsync(b, ctx()); await _deck.ReleaseAsync(b, ctx()); });
        }
        foreach (var p in _deck.Config.Profiles)
        {
            var prof = p;
            yield return new PaletteItem($"Profile: {p.Name}", "Switch profile", p.Glyph, () => { _deck.SwitchProfile(prof, announce: true); return Task.CompletedTask; });
        }
        foreach (var s in _deck.Config.Sounds)
        {
            var snd = s;
            yield return new PaletteItem($"Play {s.Name}", "Soundboard", "E189", () =>
                _deck.RunAsync(new ActionSpec { Type = ActionTypes.Sound, Param = snd.Id, Volume = 100 }, ctx()));
        }
        foreach (var (id, label) in SysCommands.All.Where(c => c.Id is not (SysCommands.OutputSet or SysCommands.InputSet or SysCommands.MasterVolumeSet or SysCommands.MicVolumeSet or SysCommands.BrightnessSet)))
            yield return new PaletteItem(label, "System", "E770", () => _deck.RunAsync(ActionSpec.Of(ActionTypes.System, id), ctx()));
        foreach (var (id, label) in MediaCmds.All)
            yield return new PaletteItem(label, "Media", "E768", () => _deck.RunAsync(ActionSpec.Of(ActionTypes.Media, id), ctx()));
        foreach (var (id, label) in PowerCommands.All)
            yield return new PaletteItem(label, "Power (asks first)", "E7E8", () => _deck.RunAsync(ActionSpec.Of(ActionTypes.Power, id), ctx()));
        if (_deck.Config.Settings.ObsEnabled)
            foreach (var (id, label) in ObsCommands.All.Where(c => c.Id is not (ObsCommands.Scene or ObsCommands.ToggleSource or ObsCommands.ToggleMute)))
                yield return new PaletteItem("OBS: " + label, "OBS Studio", "E714", () => _deck.RunAsync(ActionSpec.Of(ActionTypes.Obs, id), ctx()));
        yield return new PaletteItem("Stop all sounds", "Soundboard", "E71A", () => { _deck.Soundboard.StopAll(); return Task.CompletedTask; });
        if (ExtraCommands != null) foreach (var e in ExtraCommands()) yield return e;
    }

    private static string Pretty(string s) => s.Length > 1 && s.All(c => !char.IsLetter(c) || char.IsUpper(c))
        ? System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.ToLowerInvariant())
        : s;

    private static int Score(PaletteItem i, string q)
    {
        if (q.Length == 0) return 1;
        var t = i.Title;
        if (t.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 100;
        if (t.Split(' ').Any(w => w.StartsWith(q, StringComparison.OrdinalIgnoreCase))) return 80;
        if (t.Contains(q, StringComparison.OrdinalIgnoreCase)) return 60;
        if (i.Detail.Contains(q, StringComparison.OrdinalIgnoreCase)) return 30;
        // all words present
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.All(w => (t + " " + i.Detail).Contains(w, StringComparison.OrdinalIgnoreCase)) ? 20 : 0;
    }

    private void Filter()
    {
        var q = _box.Text.Trim();
        _list.Items.Clear();
        foreach (var it in _all.Select(i => (i, s: Score(i, q))).Where(x => x.s > 0).OrderByDescending(x => x.s).Take(60).Select(x => x.i))
        {
            var row = new DockPanel();
            var g = new TextBlock { Text = SafeGlyph(it.Glyph), FontFamily = IconVisual.Glyphs, FontSize = 15, Width = 28, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(g, Dock.Left);
            row.Children.Add(g);
            var d = new TextBlock { Text = it.Detail, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 };
            d.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
            DockPanel.SetDock(d, Dock.Right);
            row.Children.Add(d);
            row.Children.Add(new TextBlock { Text = it.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            _list.Items.Add(new ListBoxItem { Content = row, Tag = it });
        }
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private static string SafeGlyph(string code)
    {
        try { return char.ConvertFromUtf32(Convert.ToInt32(code, 16)); } catch { return ""; }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: e.Handled = true; Hide(); break;
            case Key.Down: e.Handled = true; if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(_list.Items.Count - 1, _list.SelectedIndex + 1); _list.ScrollIntoView(_list.SelectedItem); break;
            case Key.Up: e.Handled = true; if (_list.Items.Count > 0) _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1); _list.ScrollIntoView(_list.SelectedItem); break;
            case Key.Enter: e.Handled = true; RunSelected(); break;
        }
    }

    private async void RunSelected()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: PaletteItem it }) return;
        Hide();
        try { await it.Run(); }
        catch (Exception ex) { UiHost.Instance.Toast(ex.Message, ToastKind.Error); }
    }
}
