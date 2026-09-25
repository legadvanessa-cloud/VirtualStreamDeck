using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Services;
using VirtualStreamDeck.UI.Controls;

namespace VirtualStreamDeck.UI.Windows;

/// <summary>Configuration / dashboard window: profiles, grid, editor, broadcast console, status bar.</summary>
public sealed class MainWindow : ChromeWindow
{
    private readonly Deck _deck;
    private readonly DeckSurface _surface;
    private readonly ListBox _profiles = new() { Padding = new Thickness(8, 0, 8, 0) };
    private readonly StackPanel _pages = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _profileTitle = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, FontFamily = IconVisual.Display };
    private readonly TextBlock _editHint = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    private readonly ComboBox _gridSize = new() { Width = 110, Margin = new Thickness(10, 0, 0, 0), ToolTip = "Grid size of this profile" };
    private readonly ToggleButton _editToggle = new() { Content = "EDIT", Margin = new Thickness(6, 0, 0, 0), ToolTip = "Edit mode (Ctrl+E)" };
    private readonly Border _side = new() { Width = 372, BorderThickness = new Thickness(1, 0, 0, 0) };
    private readonly ContentControl _sideContent = new();
    private readonly ToggleButton _tabBroadcast = new() { Content = "BROADCAST", Margin = new Thickness(0, 0, 6, 0) };
    private readonly ToggleButton _tabSounds = new() { Content = "SOUNDS" };
    private readonly StackPanel _sideTabs = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 12, 16, 6) };
    private readonly ButtonEditor _editor;
    private readonly BroadcastPanel _broadcast;
    private readonly SoundLibraryPanel _sounds;
    private readonly CommandPalette _palette;
    private readonly ToastHost _toasts = new();
    private readonly TextBlock _status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _conn = new() { FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly System.Windows.Shapes.Ellipse _connDot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _statusBar = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _statusTimer;
    private string _sideMode = "broadcast";
    public bool ReallyClose { get; set; }

    public MainWindow(Deck deck)
    {
        _deck = deck;
        Caption = "VIRTUAL STREAM DECK";
        Width = 1360;
        Height = 820;
        MinWidth = 960;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UiHost.Instance.Register(this, _toasts);

        _surface = new DeckSurface(deck) { Source = ActionSource.Grid, Margin = new Thickness(24, 8, 24, 24) };
        _editor = new ButtonEditor(deck);
        _broadcast = new BroadcastPanel(deck);
        _sounds = new SoundLibraryPanel(deck);
        _palette = new CommandPalette(deck)
        {
            ExtraCommands = () => new[]
            {
                new PaletteItem("Open control surface", "Window", "E7F4", () => { AppShell.ToggleSurface(); return Task.CompletedTask; }),
                new PaletteItem("Settings", "Window", "E713", () => { AppShell.OpenSettings(this); return Task.CompletedTask; }),
                new PaletteItem("Diagnostics", "Settings › Advanced", "E9D9", () => { AppShell.OpenSettings(this, "Advanced"); return Task.CompletedTask; }),
                new PaletteItem(_deck.EditMode ? "Leave edit mode" : "Enter edit mode", "Deck", "E70F", () => { SetEdit(!_deck.EditMode); return Task.CompletedTask; }),
            },
        };

        Header = BuildHeader();
        Body = BuildBody();

        _surface.ButtonSelected += b => OpenEditor(b);
        _surface.EditRequested += b => OpenEditor(b);
        _editor.Closed += () => { _surface.Select(null); ShowSide(_sideMode); };
        deck.ProfileChanged += RefreshProfileArea;
        deck.LayoutChanged += RefreshProfiles;
        deck.LayoutChanged += RefreshProfileArea;
        deck.EditModeChanged += OnEditModeChanged;
        deck.LiveChanged += RefreshStatus;
        deck.SettingsApplied += RefreshStatus;

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => { deck.Monitor.Sample(); RefreshStatus(); };
        IsVisibleChanged += (_, _) => UpdateTimer();
        StateChanged += (_, _) =>
        {
            UpdateTimer();
            if (WindowState == WindowState.Minimized && deck.Config.Settings.MinimizeToTray && AppShell.TrayAvailable) Hide();
        };

        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        RefreshProfiles();
        RefreshProfileArea();
        RefreshStatus();
        ShowSide("broadcast");
    }

    // ---- layout -----------------------------------------------------------------------------

    private UIElement BuildHeader()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
        var conn = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4, 12, 4), Margin = new Thickness(0, 0, 12, 0), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        conn.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        conn.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _connDot, _conn } };
        sp.Children.Add(conn);
        var search = F.Button("   Search   Ctrl+K", () => _palette.Show(), "Btn.Ghost", "Search commands and buttons (Ctrl+K)");
        search.FontSize = 12;
        search.Content = new TextBlock { Inlines = { new System.Windows.Documents.Run("") { FontFamily = IconVisual.Glyphs }, new System.Windows.Documents.Run("   Search   Ctrl+K") } };
        sp.Children.Add(search);
        _editToggle.SetResourceReference(StyleProperty, "Toggle.Pill");
        _editToggle.Click += (_, _) => SetEdit(_editToggle.IsChecked == true);
        sp.Children.Add(_editToggle);

        var surf = F.Button("", () => AppShell.ToggleSurface(), tip: "Open the full-screen control surface (Ctrl+Alt+S)");
        surf.Content = new TextBlock { Inlines = { new System.Windows.Documents.Run("  ") { FontFamily = IconVisual.Glyphs }, new System.Windows.Documents.Run("SURFACE") } };
        surf.Margin = new Thickness(8, 0, 0, 0);
        surf.FontSize = 12;
        sp.Children.Add(surf);
        var pick = F.IconButton("", "Open on display…", () => DisplayMenu(sp));
        pick.Width = 30;
        sp.Children.Add(pick);
        var settings = F.IconButton("", "Settings (Ctrl+,)", () => AppShell.OpenSettings(this));
        settings.Margin = new Thickness(6, 0, 0, 0);
        sp.Children.Add(settings);
        return sp;
    }

    private void DisplayMenu(FrameworkElement anchor)
    {
        var m = new ContextMenu();
        foreach (var d in Displays.All())
        {
            var mi = new MenuItem { Header = "Open on " + d.Label, IsCheckable = true, IsChecked = d.DeviceName == _deck.Config.Settings.SurfaceDisplay };
            var disp = d;
            mi.Click += (_, _) => AppShell.OpenSurfaceOn(disp);
            m.Items.Add(mi);
        }
        m.Items.Add(new Separator());
        var rem = new MenuItem { Header = "Remember display", IsCheckable = true, IsChecked = _deck.Config.Settings.RememberDisplay };
        rem.Click += (_, _) => { _deck.Config.Settings.RememberDisplay = rem.IsChecked; _deck.SaveSoon(); };
        m.Items.Add(rem);
        m.PlacementTarget = anchor;
        m.Placement = PlacementMode.Bottom;
        m.IsOpen = true;
    }

    private UIElement BuildBody()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var cols = new Grid();
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_deck.Config.Settings.CompactMode ? 180 : 214) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // sidebar
        var side = new DockPanel();
        var sideBorder = new Border { Child = side, BorderThickness = new Thickness(0, 0, 1, 0) };
        sideBorder.SetResourceReference(Border.BackgroundProperty, "Bg.Panel");
        sideBorder.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        var ph = F.Caps("Profiles", 16);
        ph.Margin = new Thickness(18, 16, 0, 8);
        DockPanel.SetDock(ph, Dock.Top);
        side.Children.Add(ph);
        var addRow = new StackPanel { Margin = new Thickness(12, 8, 12, 14) };
        var add = F.Button("+  New profile", () => NewProfileMenu(addRow), "Btn.Ghost");
        add.HorizontalContentAlignment = HorizontalAlignment.Left;
        addRow.Children.Add(add);
        var imp = F.Button("Import profile…", ImportProfile, "Btn.Ghost");
        imp.HorizontalContentAlignment = HorizontalAlignment.Left;
        imp.FontSize = 12;
        addRow.Children.Add(imp);
        DockPanel.SetDock(addRow, Dock.Bottom);
        side.Children.Add(addRow);
        _profiles.SelectionChanged += (_, _) =>
        {
            if (_profiles.SelectedItem is ListBoxItem { Tag: Profile p } && p.Id != _deck.ActiveProfile.Id) _deck.SwitchProfile(p, announce: false);
        };
        side.Children.Add(_profiles);
        cols.Children.Add(sideBorder);

        // centre
        var centre = new DockPanel();
        var bar = new DockPanel { Margin = new Thickness(26, 16, 24, 6) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_pages);
        _gridSize.Items.Clear();
        foreach (var (c, r) in GridLayout.Presets) _gridSize.Items.Add(new ComboBoxItem { Content = $"{c} × {r}", Tag = $"{c}x{r}" });
        _gridSize.SelectionChanged += (_, _) =>
        {
            if (_gridSize.SelectedItem is not ComboBoxItem { Tag: string t }) return;
            var parts = t.Split('x');
            var p = _deck.ActiveProfile;
            int c = int.Parse(parts[0]), r = int.Parse(parts[1]);
            if (p.Columns == c && p.Rows == r) return;
            p.Columns = c;
            p.Rows = r;
            _deck.ProfileEdited(p);
        };
        right.Children.Add(_gridSize);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(_profileTitle);
        _editHint.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        bar.Children.Add(_editHint);
        DockPanel.SetDock(bar, Dock.Top);
        centre.Children.Add(bar);
        centre.Children.Add(_surface);
        Grid.SetColumn(centre, 1);
        cols.Children.Add(centre);

        // right side panel
        var sideDock = new DockPanel();
        _tabBroadcast.SetResourceReference(StyleProperty, "Toggle.Pill");
        _tabSounds.SetResourceReference(StyleProperty, "Toggle.Pill");
        _tabBroadcast.Click += (_, _) => ShowSide("broadcast");
        _tabSounds.Click += (_, _) => ShowSide("sounds");
        _sideTabs.Children.Add(_tabBroadcast);
        _sideTabs.Children.Add(_tabSounds);
        var collapse = F.IconButton("", "Hide panel", () => _side.Visibility = Visibility.Collapsed);
        collapse.Width = collapse.Height = 32;
        collapse.Margin = new Thickness(10, 0, 0, 0);
        _sideTabs.Children.Add(collapse);
        DockPanel.SetDock(_sideTabs, Dock.Top);
        sideDock.Children.Add(_sideTabs);
        sideDock.Children.Add(_sideContent);
        _side.Child = sideDock;
        _side.SetResourceReference(Border.BackgroundProperty, "Bg.Panel");
        _side.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        Grid.SetColumn(_side, 2);
        cols.Children.Add(_side);

        root.Children.Add(cols);

        // status bar
        var status = new Border { Padding = new Thickness(18, 7, 18, 7), BorderThickness = new Thickness(0, 1, 0, 0) };
        status.SetResourceReference(Border.BackgroundProperty, "Bg.Panel");
        status.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        var sdp = new DockPanel();
        var showPanel = F.Button("PANEL", () => { _side.Visibility = Visibility.Visible; }, "Btn.Ghost", "Show the side panel");
        showPanel.MinHeight = 22;
        showPanel.Padding = new Thickness(8, 0, 8, 0);
        showPanel.FontSize = 11;
        DockPanel.SetDock(showPanel, Dock.Right);
        sdp.Children.Add(showPanel);
        sdp.Children.Add(_statusBar);
        status.Child = sdp;
        Grid.SetRow(status, 1);
        root.Children.Add(status);

        var overlay = new Grid();
        overlay.Children.Add(root);
        overlay.Children.Add(_toasts);
        overlay.Children.Add(_palette);
        return overlay;
    }

    // ---- profiles & pages -------------------------------------------------------------------

    private void RefreshProfiles()
    {
        var active = _deck.ActiveProfile.Id;
        _profiles.Items.Clear();
        var index = 1;
        foreach (var p in _deck.Config.Profiles)
        {
            var row = new DockPanel();
            var g = new TextBlock { Text = SafeGlyph(p.Glyph), FontFamily = IconVisual.Glyphs, FontSize = 15, Width = 26, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(g, Dock.Left);
            row.Children.Add(g);
            var hk = p.Hotkey.Length > 0 ? p.Hotkey : _deck.Config.Hotkeys.FirstOrDefault(h => h.Command == GlobalCommands.ProfilePrefix + p.Id && h.Enabled)?.Chord ?? "";
            if (hk.Length > 0)
            {
                var hint = new TextBlock { Text = ShortChord(hk), FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
                DockPanel.SetDock(hint, Dock.Right);
                row.Children.Add(hint);
            }
            row.Children.Add(new TextBlock { Text = p.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13.5 });
            var item = new ListBoxItem { Content = row, Tag = p, AllowDrop = true, Padding = new Thickness(10, 10, 10, 10), ToolTip = $"{p.Name} — {p.Columns}×{p.Rows}, {p.Pages.Sum(x => x.Buttons.Count)} buttons" };
            var prof = p;
            item.Drop += (_, e) =>
            {
                if (e.Data.GetData(DeckSurface.DragData) is string id && _deck.FindButton(id) is { } b) _deck.MoveToProfile(b, prof);
            };
            item.ContextMenu = ProfileMenu(p);
            _profiles.Items.Add(item);
            if (p.Id == active) _profiles.SelectedItem = item;
            index++;
        }
    }

    private static string ShortChord(string c) => c.Replace("CTRL", "Ctrl").Replace("ALT", "Alt").Replace("SHIFT", "Shift").Replace("WIN", "Win").Replace(" + ", "+");

    private static string SafeGlyph(string code)
    {
        try { return char.ConvertFromUtf32(Convert.ToInt32(code, 16)); } catch { return ""; }
    }

    private ContextMenu ProfileMenu(Profile p)
    {
        var m = new ContextMenu();
        void Item(string h, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = h, IsEnabled = enabled };
            mi.Click += (_, _) => a();
            m.Items.Add(mi);
        }
        Item("Rename…", () =>
        {
            var n = DialogWindow.Prompt(this, "Rename profile", "Profile name", p.Name);
            if (string.IsNullOrWhiteSpace(n)) return;
            p.Name = n;
            _deck.ProfileEdited(p);
        });
        Item("Icon…", () =>
        {
            var ic = IconPickerWindow.Pick(this, IconSpec.Glyph(p.Glyph));
            if (ic is { Kind: IconKinds.Glyph }) { p.Glyph = ic.Value; _deck.ProfileEdited(p); }
            else if (ic != null) DialogWindow.Info(this, "Profile icons", "Profiles use the built-in glyph icons; pick one from the categories.");
        });
        Item("Shortcut…", () =>
        {
            var d = DialogWindow.Prompt(this, "Profile shortcut", "Global shortcut that switches to this profile, e.g. CTRL + ALT + 1 (empty = none).", p.Hotkey);
            if (d == null) return;
            if (d.Length > 0 && !KeyChord.TryParse(d, out _)) { DialogWindow.Info(this, "Not a valid key combination", d); return; }
            p.Hotkey = KeyChord.Normalize(d);
            _deck.ProfileEdited(p);
            var reg = _deck.Hotkeys.Registrations.FirstOrDefault(r => r.Chord == p.Hotkey);
            if (reg is { Ok: false }) UiHost.Instance.Toast($"{reg.Chord}: {reg.Error}", ToastKind.Warning);
        });
        Item("Add page", () => _deck.AddPage(p));
        m.Items.Add(new Separator());
        Item("Duplicate", () => _deck.DuplicateProfile(p));
        Item("Move up", () => _deck.MoveProfile(p, -1));
        Item("Move down", () => _deck.MoveProfile(p, 1));
        Item("Export profile…", () => ExportProfile(p));
        m.Items.Add(new Separator());
        Item("Delete…", () =>
        {
            if (DialogWindow.Confirm(this, $"Delete profile \"{p.Name}\"?", $"Its {p.Pages.Sum(x => x.Buttons.Count)} button(s) will be removed. This cannot be undone.", "DELETE", true))
                _deck.DeleteProfile(p);
        }, _deck.Config.Profiles.Count > 1);
        return m;
    }

    private void NewProfileMenu(FrameworkElement anchor)
    {
        var m = new ContextMenu();
        foreach (var t in Defaults.Templates)
        {
            var mi = new MenuItem { Header = t.Name, ToolTip = t.Blurb };
            var id = t.Id;
            mi.Click += (_, _) =>
            {
                var p = _deck.AddProfile(id);
                _deck.SwitchProfile(p, announce: true);
            };
            m.Items.Add(mi);
        }
        m.PlacementTarget = anchor;
        m.Placement = PlacementMode.Top;
        m.IsOpen = true;
    }

    private void ExportProfile(Profile p)
    {
        var path = Pickers.Save(this, "Virtual Stream Deck profile|*.vsdprofile", p.Name + ".vsdprofile");
        if (path == null) return;
        try
        {
            ConfigStore.WriteExport(ConfigStore.BuildProfileExport(_deck.Config, p), path);
            UiHost.Instance.Toast($"Exported \"{p.Name}\"", ToastKind.Success);
        }
        catch (Exception ex) { DialogWindow.Info(this, "Export failed", ex.Message); }
    }

    private void ImportProfile()
    {
        var path = Pickers.File(this, "Virtual Stream Deck profile or configuration|*.vsdprofile;*.vsdconfig|All files|*.*");
        if (path == null) return;
        try
        {
            var pkg = ConfigStore.ReadExport(path);
            if (pkg.Kind == "config")
            {
                // A full configuration: offer to add its profiles.
                pkg.Profiles = pkg.Config!.Profiles;
            }
            var added = ConfigStore.ImportProfiles(pkg, _deck.Config);
            _deck.SaveNow();
            _deck.RegisterHotkeys();
            _deck.ReplaceConfig(_deck.Config);
            UiHost.Instance.Toast($"Imported {added.Count} profile(s)", ToastKind.Success);
        }
        catch (Exception ex) { DialogWindow.Info(this, "Import failed", ex.Message); }
    }

    private void RefreshProfileArea()
    {
        var p = _deck.ActiveProfile;
        _profileTitle.Text = p.Name.ToUpperInvariant();
        foreach (ListBoxItem it in _profiles.Items)
            if (it.Tag is Profile ip && ip.Id == p.Id && !it.IsSelected) it.IsSelected = true;
        foreach (ComboBoxItem it in _gridSize.Items)
            if ((string)it.Tag == $"{p.Columns}x{p.Rows}") _gridSize.SelectedItem = it;
        if (_gridSize.SelectedItem is ComboBoxItem { Tag: string t } && t != $"{p.Columns}x{p.Rows}") _gridSize.SelectedIndex = -1;
        _gridSize.Visibility = _deck.EditMode ? Visibility.Visible : Visibility.Collapsed;

        _pages.Children.Clear();
        if (p.Pages.Count > 1 || _deck.EditMode)
        {
            for (var i = 0; i < p.Pages.Count; i++)
            {
                var idx = i;
                var tb = new ToggleButton { Content = (i + 1).ToString(), IsChecked = i == _deck.PageIndex, MinWidth = 34, Margin = new Thickness(0, 0, 6, 0), AllowDrop = true, ToolTip = $"Page {i + 1}" };
                tb.SetResourceReference(StyleProperty, "Toggle.Pill");
                tb.Click += (_, _) => _deck.SetPage(idx);
                tb.Drop += (_, e) =>
                {
                    if (e.Data.GetData(DeckSurface.DragData) is string id && _deck.FindButton(id) is { } b) _deck.MoveToProfile(b, p, idx);
                };
                if (_deck.EditMode && p.Pages.Count > 1)
                {
                    var cm = new ContextMenu();
                    var del = new MenuItem { Header = $"Delete page {i + 1}…" };
                    del.Click += (_, _) =>
                    {
                        if (DialogWindow.Confirm(this, $"Delete page {idx + 1}?", $"Its {p.Pages[idx].Buttons.Count} button(s) will be removed.", "DELETE", true))
                            _deck.DeletePage(p, idx);
                    };
                    cm.Items.Add(del);
                    tb.ContextMenu = cm;
                }
                _pages.Children.Add(tb);
            }
            if (_deck.EditMode)
            {
                var add = F.IconButton("", "Add page", () => _deck.AddPage(p));
                add.Width = add.Height = 34;
                _pages.Children.Add(add);
            }
        }
        _editHint.Text = _deck.EditMode ? "EDIT MODE — click a button to edit, drag to move, + to add" : "";
    }

    // ---- edit mode / side panel -------------------------------------------------------------

    private void SetEdit(bool on)
    {
        if (!on && _sideContent.Content == _editor && !_editor.ConfirmLeave()) { _editToggle.IsChecked = true; return; }
        _deck.SetEditMode(on);
    }

    private void OnEditModeChanged()
    {
        _editToggle.IsChecked = _deck.EditMode;
        if (!_deck.EditMode) { _surface.Select(null); ShowSide(_sideMode); }
        RefreshProfileArea();
    }

    private void OpenEditor(DeckButton b)
    {
        if (_sideContent.Content == _editor && _editor.Original != null && _editor.Original.Id != b.Id && !_editor.ConfirmLeave()) return;
        _side.Visibility = Visibility.Visible;
        _editor.Load(b);
        _sideTabs.Visibility = Visibility.Collapsed;
        _sideContent.Content = _editor;
        _surface.Select(b);
    }

    private void ShowSide(string mode)
    {
        _sideMode = mode;
        _sideTabs.Visibility = Visibility.Visible;
        _tabBroadcast.IsChecked = mode == "broadcast";
        _tabSounds.IsChecked = mode == "sounds";
        _sideContent.Content = mode == "sounds" ? _sounds : _broadcast;
    }

    // ---- status bar -------------------------------------------------------------------------

    private void UpdateTimer()
    {
        var active = IsVisible && WindowState != WindowState.Minimized;
        if (active) { _deck.Monitor.Sample(); RefreshStatus(); _statusTimer.Start(); }
        else _statusTimer.Stop();
    }

    private void RefreshStatus()
    {
        var p = ThemeManager.Current;
        var m = _deck.Monitor;
        var mic = _deck.Audio.Microphone;
        var spk = _deck.Audio.Speaker;
        var cam = _deck.Camera.State;
        _statusBar.Children.Clear();
        void Item(string label, string value, System.Windows.Media.Color? dot = null, string? tip = null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 22, 0), ToolTip = tip };
            var l = new TextBlock { Text = label + " ", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
            l.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
            sp.Children.Add(l);
            if (dot != null) sp.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = ThemeManager.Brush(dot.Value), Margin = new Thickness(2, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = value, FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            _statusBar.Children.Add(sp);
        }
        Item("CPU", $"{m.CpuPercent}%");
        Item("RAM", $"{m.RamPercent}%");
        Item("NETWORK", m.Network);
        if (!mic.Available) Item("MIC", "NO DEVICE", p.Warning, "No recording device");
        else Item("MIC", mic.Muted ? "MUTED" : "ON AIR", mic.Muted ? p.Danger : p.Success, mic.DeviceName);
        if (!cam.AnyCamera) Item("CAM", "NONE", p.TextMuted, "No camera detected");
        else if (cam.AllDisabled) Item("CAM", "DISABLED", p.Danger, "Camera disabled in Device Manager");
        else Item("CAM", cam.InUse ? "IN USE" : "IDLE", cam.InUse ? p.Danger : p.TextMuted, cam.InUse ? "Used by " + string.Join(", ", cam.UsedBy) : string.Join(", ", cam.Devices.Select(d => d.Name)));
        if (spk.Available) Item("AUDIO", spk.Muted ? "MUTED" : $"{spk.VolumePercent}%", null, spk.DeviceName);
        if (_deck.Config.Settings.ObsEnabled) Item("OBS", _deck.Obs.Status == ObsStatus.Connected ? (_deck.Obs.Streaming ? "LIVE" : "READY") : "OFFLINE",
            _deck.Obs.Status == ObsStatus.Connected ? (_deck.Obs.Streaming ? p.Danger : p.Success) : p.TextMuted, _deck.Obs.StatusText);
        if (_deck.Config.Settings.AutoProfile) Item("AUTO", "ON", p.Accent, "Automatic profile switching");
        Item("", DateTime.Now.ToString("HH:mm"));

        // header indicator: overall health of the things the deck depends on
        var failedHotkeys = _deck.Hotkeys.Registrations.Count(r => !r.Ok);
        string text;
        System.Windows.Media.Color c;
        if (_deck.Config.Settings.ObsEnabled && _deck.Obs.Status == ObsStatus.Connected) { text = "OBS CONNECTED"; c = p.Success; }
        else if (_deck.Config.Settings.ObsEnabled) { text = "OBS OFFLINE"; c = p.Warning; }
        else if (failedHotkeys > 0) { text = $"{failedHotkeys} SHORTCUT CONFLICT{(failedHotkeys > 1 ? "S" : "")}"; c = p.Warning; }
        else { text = "READY"; c = p.Success; }
        _conn.Text = text;
        _connDot.Fill = ThemeManager.Brush(c);
        ((FrameworkElement)_conn.Parent).ToolTip = _deck.Config.Settings.ObsEnabled ? _deck.Obs.StatusText
            : failedHotkeys > 0 ? string.Join("\n", _deck.Hotkeys.Registrations.Where(r => !r.Ok).Select(r => $"{r.Chord}: {r.Error}")) : "Audio, hotkeys and devices are working";
    }

    // ---- keyboard / closing -----------------------------------------------------------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (ctrl && e.Key == Key.K) { e.Handled = true; if (_palette.IsOpen) _palette.Hide(); else _palette.Show(); return; }
        if (_palette.IsOpen) return;
        if (ctrl && e.Key == Key.E) { e.Handled = true; SetEdit(!_deck.EditMode); return; }
        if (ctrl && e.Key == Key.OemComma) { e.Handled = true; AppShell.OpenSettings(this); return; }
        if (e.Key == Key.F11) { e.Handled = true; AppShell.ToggleSurface(); return; }
        if (e.Key == Key.Escape && _deck.EditMode && _sideContent.Content != _editor) { e.Handled = true; SetEdit(false); return; }
        if (e.Key == Key.PageDown && Keyboard.FocusedElement is not TextBox) { e.Handled = true; _deck.ChangePage(1); return; }
        if (e.Key == Key.PageUp && Keyboard.FocusedElement is not TextBox) { e.Handled = true; _deck.ChangePage(-1); return; }
        if (ctrl && e.Key is >= Key.D1 and <= Key.D9)
        {
            var i = e.Key - Key.D1;
            if (i < _deck.Config.Profiles.Count) { e.Handled = true; _deck.SwitchProfile(_deck.Config.Profiles[i], announce: true); }
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SavePlacement();
        if (!ReallyClose && _deck.Config.Settings.CloseToTray && AppShell.TrayAvailable)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        if (!ReallyClose)
        {
            e.Cancel = true;
            AppShell.Exit();
        }
    }

    public void SavePlacement()
    {
        if (!_deck.Config.Settings.RememberWindowPosition || AppShell.Verifying) return;
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (r.IsEmpty || r.Width < 100) return;
        _deck.Config.Settings.MainWindow = new WindowPlacement { Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height, Maximized = WindowState == WindowState.Maximized };
        _deck.SaveSoon();
    }

    public void RestorePlacement()
    {
        var wp = _deck.Config.Settings.MainWindow;
        if (!_deck.Config.Settings.RememberWindowPosition || wp == null || wp.Width < 400) return;
        // Only restore onto a monitor that still exists (virtual screen bounds in DIPs).
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var r = new Rect(wp.Left, wp.Top, wp.Width, wp.Height);
        if (!vs.IntersectsWith(new Rect(r.Left + 40, r.Top + 10, Math.Max(1, r.Width - 80), 40))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = wp.Left; Top = wp.Top; Width = wp.Width; Height = wp.Height;
        if (wp.Maximized) Loaded += (_, _) => WindowState = WindowState.Maximized;
    }

    public DeckSurface Surface => _surface;
    public ButtonEditor Editor => _editor;
    public void EditButton(DeckButton b) { _deck.SetEditMode(true); OpenEditor(b); }
    public void ShowPalette() => _palette.Show();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _statusTimer.Stop();
        _surface.Detach();
        _broadcast.Detach();
        _sounds.Detach();
        _deck.ProfileChanged -= RefreshProfileArea;
        _deck.LayoutChanged -= RefreshProfiles;
        _deck.LayoutChanged -= RefreshProfileArea;
        _deck.EditModeChanged -= OnEditModeChanged;
        _deck.LiveChanged -= RefreshStatus;
        _deck.SettingsApplied -= RefreshStatus;
    }
}
