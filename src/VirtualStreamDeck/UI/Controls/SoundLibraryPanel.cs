using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Services;
using VirtualStreamDeck.UI.Windows;

namespace VirtualStreamDeck.UI.Controls;

/// <summary>Sound library: import (button or drag &amp; drop), audition, favourite, rename, delete, drag onto the deck.</summary>
public sealed class SoundLibraryPanel : DockPanel
{
    public const string DragFormat = "VirtualStreamDeck.Sound";
    private readonly Deck _deck;
    private readonly StackPanel _list = new();
    private readonly TextBox _filter = new();
    private Point _dragStart;

    public SoundLibraryPanel(Deck deck)
    {
        _deck = deck;
        AllowDrop = true;
        var top = new StackPanel { Margin = new Thickness(16, 4, 16, 8) };
        var outputs = new List<(string, string)> { ("", "Windows default output") };
        outputs.AddRange(SoundboardEngine.OutputDevices().Select(d => (d.Id, d.Name)));
        if (deck.Config.Settings.SoundboardDevice.Length > 0 && outputs.All(o => o.Item1 != deck.Config.Settings.SoundboardDevice))
            outputs.Add((deck.Config.Settings.SoundboardDevice, "(disconnected device)"));
        top.Children.Add(F.Field("Soundboard output", F.Combo(outputs, deck.Config.Settings.SoundboardDevice, v =>
        {
            deck.Config.Settings.SoundboardDevice = v;
            deck.Soundboard.SetDevice(v);
            deck.SaveSoon();
        }), "Pick a virtual audio cable here to send sounds into OBS / Discord as their own source."));
        var imp = F.Button("+  IMPORT SOUND", Import, "Btn.Accent");
        imp.HorizontalAlignment = HorizontalAlignment.Stretch;
        top.Children.Add(imp);
        top.Children.Add(F.Hint("…or drop WAV / MP3 / OGG files here. Drag a sound onto the deck (in Edit mode) to make a button.", 6));
        top.Children.Add(F.Field("", _filter));
        _filter.ToolTip = "Filter sounds";
        _filter.TextChanged += (_, _) => Build();
        DockPanel.SetDock(top, Dock.Top);
        Children.Add(top);
        Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12, 0, 10, 12) });

        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) ImportFiles(files);
        };
        deck.SoundsChanged += Build;
        deck.LiveChanged += RefreshPlaying;
        Build();
    }

    public void Detach()
    {
        _deck.SoundsChanged -= Build;
        _deck.LiveChanged -= RefreshPlaying;
    }

    private void Import() => ImportFiles(Pickers.Files(Window.GetWindow(this), SoundFiles.DialogFilter));

    private void ImportFiles(IEnumerable<string> files)
    {
        int ok = 0;
        foreach (var f in files)
        {
            try { _deck.ImportSound(f); ok++; }
            catch (Exception ex) { UiHost.Instance.Toast($"{Path.GetFileName(f)}: {ex.Message}", ToastKind.Error); }
        }
        if (ok > 0) UiHost.Instance.Toast(ok == 1 ? "Sound imported" : $"{ok} sounds imported", ToastKind.Success);
    }

    private void Build()
    {
        _list.Children.Clear();
        var q = _filter.Text.Trim();
        var all = _deck.Config.Sounds.Where(s => q.Length == 0 || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        void Group(string title, IEnumerable<SoundItem> items)
        {
            var l = items.ToList();
            if (l.Count == 0) return;
            _list.Children.Add(F.Caps(title, _list.Children.Count == 0 ? 4 : 16));
            foreach (var s in l) _list.Children.Add(Row(s));
        }
        Group("Favorites", all.Where(s => s.Favorite));
        Group("Built-in", all.Where(s => s.IsBuiltIn && !s.Favorite));
        Group("Imported", all.Where(s => !s.IsBuiltIn && !s.Favorite));
        if (_list.Children.Count == 0) _list.Children.Add(F.Hint("No sounds match.", 8));
    }

    private readonly Dictionary<string, Button> _playButtons = new();

    private FrameworkElement Row(SoundItem s)
    {
        var tag = "lib-" + s.Id;
        var row = new Border { Padding = new Thickness(6, 4, 4, 4), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 1, 0, 1), Background = System.Windows.Media.Brushes.Transparent };
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "Bg.Hover");
        row.MouseLeave += (_, _) => row.Background = System.Windows.Media.Brushes.Transparent;
        var dp = new DockPanel();
        var play = F.IconButton(_deck.Soundboard.IsPlaying(tag) ? "" : "", "Play / stop", () =>
        {
            try
            {
                if (_deck.Soundboard.IsPlaying(tag)) _deck.Soundboard.Stop(tag, 80);
                else _deck.Soundboard.Play(s, tag, 1f, false, 0, 0, "restart");
            }
            catch (Exception ex) { UiHost.Instance.Toast(ex.Message, ToastKind.Error); }
        });
        play.Width = play.Height = 32;
        _playButtons[tag] = play;
        DockPanel.SetDock(play, Dock.Left);
        dp.Children.Add(play);
        var more = F.IconButton("", "More", () => Menu(s));
        more.Width = more.Height = 32;
        DockPanel.SetDock(more, Dock.Right);
        dp.Children.Add(more);
        var fav = F.IconButton(s.Favorite ? "" : "", s.Favorite ? "Remove from favorites" : "Add to favorites", () =>
        {
            s.Favorite = !s.Favorite;
            _deck.SoundEdited();
        });
        fav.Width = fav.Height = 32;
        if (s.Favorite) fav.SetResourceReference(Control.ForegroundProperty, "Warning");
        DockPanel.SetDock(fav, Dock.Right);
        dp.Children.Add(fav);
        var missing = !s.IsBuiltIn && !File.Exists(SoundboardEngine.ResolvePath(s));
        var name = new TextBlock { Text = s.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 0, 0) };
        if (missing) { name.Text += "  (file missing)"; name.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); }
        var refs = _deck.SoundReferences(s);
        name.ToolTip = (s.IsBuiltIn ? "Built-in (synthesised)" : s.File) + (refs > 0 ? $"\nUsed by {refs} button(s)" : "");
        dp.Children.Add(name);
        row.Child = dp;
        row.MouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(this);
        row.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _dragStart.X) < 8 && Math.Abs(p.Y - _dragStart.Y) < 8) return;
            DragDrop.DoDragDrop(row, new DataObject(DragFormat, s.Id), DragDropEffects.Copy);
        };
        return row;
    }

    private void RefreshPlaying()
    {
        foreach (var (tag, b) in _playButtons) b.Content = _deck.Soundboard.IsPlaying(tag) ? "" : "";
    }

    private void Menu(SoundItem s)
    {
        var m = new ContextMenu();
        void Item(string h, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = h, IsEnabled = enabled };
            mi.Click += (_, _) => a();
            m.Items.Add(mi);
        }
        Item("Add button to this page", () => AddButton(s));
        Item("Rename…", () =>
        {
            var n = DialogWindow.Prompt(Window.GetWindow(this), "Rename sound", "Name", s.Name);
            if (string.IsNullOrWhiteSpace(n)) return;
            s.Name = n;
            _deck.SoundEdited();
        });
        var vol = new MenuItem { Header = $"Volume ({s.Volume} %)" };
        foreach (var v in new[] { 100, 85, 70, 50, 30 })
        {
            var mi = new MenuItem { Header = $"{v} %", IsCheckable = true, IsChecked = s.Volume == v };
            mi.Click += (_, _) => { s.Volume = v; _deck.Soundboard.Invalidate(s.File); _deck.SoundEdited(); };
            vol.Items.Add(mi);
        }
        m.Items.Add(vol);
        m.Items.Add(new Separator());
        Item("Delete", () =>
        {
            var refs = _deck.SoundReferences(s);
            if (!DialogWindow.Confirm(Window.GetWindow(this), $"Delete \"{s.Name}\"?",
                    refs > 0 ? $"{refs} button(s) use this sound and will show NO SOUND until reassigned." : "The audio file is removed from the library folder.", "DELETE", true)) return;
            _deck.DeleteSound(s);
        }, !s.IsBuiltIn);
        m.IsOpen = true;
    }

    private void AddButton(SoundItem s)
    {
        var p = _deck.ActiveProfile;
        var slot = GridLayout.FindFree(_deck.ActivePage, p.Columns, p.Rows);
        if (slot == null) { UiHost.Instance.Toast("This page is full.", ToastKind.Warning); return; }
        var b = _deck.AddButton(slot.Value.Row, slot.Value.Col);
        var edited = SoundButton(b, s);
        _deck.CommitButton(b, edited);
        UiHost.Instance.Toast($"Added \"{s.Name}\"", ToastKind.Success);
    }

    public static DeckButton SoundButton(DeckButton b, SoundItem s)
    {
        var e = Json.Clone(b);
        e.Title = s.Name.ToUpperInvariant();
        e.Subtitle = "SOUND";
        e.Icon = IconSpec.Glyph("E189");
        e.Accent ??= "#F59E0B";
        e.Mode = ButtonModes.Standard;
        e.Action = new ActionSpec { Type = ActionTypes.Sound, Param = s.Id, Volume = 100 };
        return e;
    }
}
