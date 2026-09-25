using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Interop;
using VirtualStreamDeck.Services;
using VirtualStreamDeck.UI.Controls;

namespace VirtualStreamDeck.UI.Windows;

/// <summary>Settings. Every change applies immediately and is saved.</summary>
public sealed class SettingsWindow : ChromeWindow
{
    public static readonly string[] Sections =
        { "General", "Appearance", "Control Surface", "Audio", "Broadcast Mic", "Profiles", "Hotkeys", "Integrations", "Tablet Remote", "Advanced" };

    private readonly Deck _deck;
    private readonly ListBox _nav = new() { Width = 210, Padding = new Thickness(10, 8, 10, 8) };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(32, 18, 32, 28) };
    private readonly ToastHost _toasts = new();
    private AppSettings S => _deck.Config.Settings;
    public string CurrentSection { get; private set; } = "General";

    public SettingsWindow(Deck deck, Window? owner, string section = "General")
    {
        _deck = deck;
        Caption = "Settings";
        Width = 1000;
        Height = 720;
        MinWidth = 820;
        MinHeight = 520;
        if (owner is { IsVisible: true }) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UiHost.Instance.Register(this, _toasts);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var navBorder = new Border { Child = _nav, BorderThickness = new Thickness(0, 0, 1, 0) };
        navBorder.SetResourceReference(Border.BackgroundProperty, "Bg.Panel");
        navBorder.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        grid.Children.Add(navBorder);
        Grid.SetColumn(_scroll, 1);
        grid.Children.Add(_scroll);
        var overlay = new Grid();
        overlay.Children.Add(grid);
        overlay.Children.Add(_toasts);
        Body = overlay;

        foreach (var s in Sections) _nav.Items.Add(new ListBoxItem { Content = s, Tag = s, Padding = new Thickness(12, 10, 12, 10) });
        _nav.SelectionChanged += (_, _) => { if (_nav.SelectedItem is ListBoxItem { Tag: string t }) ShowSection(t); };
        _nav.SelectedIndex = Math.Max(0, Array.IndexOf(Sections, section));
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    private void Apply(bool theme = false)
    {
        if (theme) ThemeManager.Apply(S);
        _deck.SaveSoon();
        _deck.ApplySettings();
        if (theme) _deck.RequestRelayout();
    }

    public void ShowSection(string name)
    {
        CurrentSection = name;
        foreach (ListBoxItem it in _nav.Items)
            if ((string)it.Tag == name && !it.IsSelected) { it.IsSelected = true; return; } // selection change re-enters here
        var sp =new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        sp.Children.Add(F.Heading(name, 24));
        var body = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        sp.Children.Add(body);
        switch (name)
        {
            case "General": General(body); break;
            case "Appearance": Appearance(body); break;
            case "Control Surface": Surface(body); break;
            case "Audio": Audio(body); break;
            case "Broadcast Mic": Mic(body); break;
            case "Profiles": Profiles(body); break;
            case "Hotkeys": Hotkeys(body); break;
            case "Integrations": Integrations(body); break;
            case "Tablet Remote": Remote(body); break;
            case "Advanced": Advanced(body); break;
        }
        _scroll.Content = sp;
        _scroll.ScrollToTop();
    }

    private static void Add(Panel p, params UIElement[] els) { foreach (var e in els) p.Children.Add(e); }

    // ---- sections ---------------------------------------------------------------------------

    private void General(StackPanel b)
    {
        Add(b, F.Caps("Startup", 8));
        Add(b, F.ToggleWithHint("Start with Windows", S.StartWithWindows, v => { S.StartWithWindows = v; Apply(); },
            $"Adds {System.IO.Path.GetFileName(AppInfo.ExePath)} to your user's startup list (no administrator rights). If you move the exe, toggle this again."));
        Add(b, F.Toggle("Start minimized", S.StartMinimized, v => { S.StartMinimized = v; Apply(); }));
        Add(b, F.Caps("Window"));
        Add(b, F.Toggle("Minimize to the system tray", S.MinimizeToTray, v => { S.MinimizeToTray = v; Apply(); }));
        Add(b, F.ToggleWithHint("Close button keeps running in the tray", S.CloseToTray, v => { S.CloseToTray = v; Apply(); },
            "Global shortcuts and the control surface keep working. Use Exit in the tray menu to quit."));
        Add(b, F.Toggle("Remember window position", S.RememberWindowPosition, v => { S.RememberWindowPosition = v; Apply(); }));
        Add(b, F.Caps("Notifications"));
        Add(b, F.ToggleWithHint("Show confirmations (✓ Microphone muted, profile switched…)", S.ShowToasts, v => { S.ShowToasts = v; Apply(); },
            "Errors are always shown. Nothing is sent to the Windows notification centre."));
        Add(b, F.Caps("Language"));
        var lang = F.Combo(new[] { ("en", "English") }, S.Language, _ => { });
        lang.IsEnabled = false;
        Add(b, F.Field("Language", lang, "This version ships in English only."));
    }

    private void Appearance(StackPanel b)
    {
        Add(b, F.Field("Theme", F.Combo(ThemeManager.Themes, S.Theme, v => { S.Theme = v; Apply(theme: true); })));
        Add(b, F.Field("Accent colour", F.Color(S.AccentColor, v => { if (v != null) { S.AccentColor = v; Apply(theme: true); } }, allowTheme: false)));
        Add(b, F.Caps("Buttons"));
        var p = _deck.ActiveProfile;
        Add(b, F.Field($"Grid size — {p.Name}", F.Combo(GridLayout.Presets.Select(x => ($"{x.Cols}x{x.Rows}", $"{x.Cols} × {x.Rows}")), $"{p.Columns}x{p.Rows}", v =>
        {
            var parts = v.Split('x');
            p.Columns = int.Parse(parts[0]);
            p.Rows = int.Parse(parts[1]);
            _deck.ProfileEdited(p);
        }), "Applies to the active profile. Buttons that no longer fit move to a free slot or a new page — none are deleted."));
        Add(b, F.Field("Default grid for new profiles", F.Combo(GridLayout.Presets.Select(x => ($"{x.Cols}x{x.Rows}", $"{x.Cols} × {x.Rows}")), $"{S.DefaultColumns}x{S.DefaultRows}", v =>
        {
            var parts = v.Split('x');
            S.DefaultColumns = int.Parse(parts[0]);
            S.DefaultRows = int.Parse(parts[1]);
            Apply();
        })));
        Add(b, F.Field("Button size", F.Combo(new[] { ("fill", "Fill the screen"), ("large", "Large (max 210 px)"), ("medium", "Medium (max 150 px)"), ("small", "Small (max 110 px)") }, S.ButtonSize, v => { S.ButtonSize = v; Apply(theme: true); })));
        Add(b, F.Toggle("Square buttons", S.SquareButtons, v => { S.SquareButtons = v; Apply(theme: true); }));
        Add(b, F.Field("Corner radius", F.Slider(0, 30, S.CornerRadius, 1, v => $"{v:0} px", v => { S.CornerRadius = v; Apply(theme: true); })));
        Add(b, F.Field("Font size", F.Slider(0.8, 1.4, S.FontScale, 0.05, v => $"{v * 100:0} %", v => { S.FontScale = v; Apply(theme: true); })));
        Add(b, F.Field("Animation", F.Combo(new[] { ("0", "Off"), ("1", "Subtle"), ("2", "Full") }, S.AnimationLevel.ToString(), v => { S.AnimationLevel = int.Parse(v); Apply(theme: true); })));
        Add(b, F.ToggleWithHint("Compact mode", S.CompactMode, v => { S.CompactMode = v; Apply(theme: true); }, "Tighter gaps between buttons. The sidebar narrows the next time the main window opens."));
        Add(b, F.ToggleWithHint("Trigger on release", S.TriggerOnRelease, v => { S.TriggerOnRelease = v; Apply(); },
            "Off: buttons fire the instant they are pressed (like a hardware deck). On: they fire when released, and sliding off cancels."));
    }

    private void Surface(StackPanel b)
    {
        var displays = Displays.All();
        var items = new List<(string, string)> { ("", "Automatic (first secondary display)") };
        items.AddRange(displays.Select(d => (d.DeviceName, d.Label)));
        if (S.SurfaceDisplay.Length > 0 && displays.All(d => d.DeviceName != S.SurfaceDisplay)) items.Add((S.SurfaceDisplay, $"{S.SurfaceDisplay} (not connected)"));
        Add(b, F.Field("Display", F.Combo(items, S.SurfaceDisplay, v => { S.SurfaceDisplay = v; Apply(); }), "Used when you press SURFACE or the Ctrl+Alt+S shortcut."));
        Add(b, F.Buttons(displays.Select(d => (UIElement)F.Button($"Open on Display {d.Number}", () => AppShell.OpenSurfaceOn(d))).ToArray()));
        Add(b, F.Toggle("Remember the display I open it on", S.RememberDisplay, v => { S.RememberDisplay = v; Apply(); }));
        Add(b, F.Toggle("Full screen (no window frame)", S.SurfaceFullscreen, v => { S.SurfaceFullscreen = v; Apply(); }));
        Add(b, F.Toggle("Always on top", S.SurfaceTopmost, v => { S.SurfaceTopmost = v; Apply(); }));
        Add(b, F.Toggle("Open the control surface when the app starts", S.SurfaceOpenOnStart, v => { S.SurfaceOpenOnStart = v; Apply(); }));
        Add(b, F.Caps("Touch"));
        Add(b, F.ToggleWithHint("Touch mode", S.TouchMode, v => { S.TouchMode = v; if (v) { S.SurfaceAutoHideCursor = true; S.ButtonSize = "fill"; } Apply(theme: true); },
            "Buttons fill the screen and the cursor hides itself. Touch input always works; this just tunes the surface for it."));
        Add(b, F.ToggleWithHint("Don't take focus", S.SurfaceNoActivate, v => { S.SurfaceNoActivate = v; Apply(); },
            "Pressing the surface leaves your current app focused, so keyboard shortcuts reach it. Takes effect next time the surface opens."));
        Add(b, F.Toggle("Auto-hide the mouse cursor", S.SurfaceAutoHideCursor, v => { S.SurfaceAutoHideCursor = v; Apply(); }));
        Add(b, F.Field("Hide after (seconds)", F.Number(S.SurfaceCursorHideSeconds, 1, 60, v => { S.SurfaceCursorHideSeconds = v; Apply(); })));
        Add(b, F.Toggle("Keep the screen awake while the surface is open", S.SurfacePreventSleep, v => { S.SurfacePreventSleep = v; Apply(); }));
        Add(b, F.Toggle("Show the status bar on the surface", S.SurfaceShowStatus, v => { S.SurfaceShowStatus = v; Apply(); }));
        Add(b, F.ToggleWithHint("Show the broadcast console on the surface", S.SurfaceShowBroadcast, v => { S.SurfaceShowBroadcast = v; Apply(); },
            "Adds the mic meter, fader and ON AIR / MUTE controls beside the buttons. Takes effect next time the surface opens."));
        Add(b, F.Hint("To close the surface, press and hold the ✕ in its top-right corner for a moment — a single tap never closes it.", 8));
    }

    private void Audio(StackPanel b)
    {
        var outs = new List<(string, string)> { ("", "Windows default output") };
        outs.AddRange(SoundboardEngine.OutputDevices().Select(d => (d.Id, d.Name)));
        Add(b, F.Field("Soundboard output", F.Combo(outs, S.SoundboardDevice, v => { S.SoundboardDevice = v; Apply(); }),
            "Sounds play only on this device. Windows cannot route one app to several outputs natively; to feed sounds into OBS or Discord, " +
            "select a virtual audio cable (VB-CABLE, VoiceMeeter…) here and use it as their input."));
        Add(b, F.Field("Soundboard volume", F.Slider(0, 100, S.SoundboardVolume, 1, v => $"{v:0} %", v => { S.SoundboardVolume = (int)v; Apply(); })));
        Add(b, F.Field("Default fade (ms)", F.Number(S.DefaultFadeMs, 0, 10000, v => { S.DefaultFadeMs = v; Apply(); }), "Used by Stop all sounds."));
        Add(b, F.Field("When a playing sound is pressed again", F.Combo(new[] { ("overlap", "Play again on top (overlap)"), ("restart", "Restart it"), ("toggle", "Stop it") }, S.SoundOverlap, v => { S.SoundOverlap = v; Apply(); })));
        Add(b, F.Caps("Volume keys"));
        Add(b, F.Field("Volume step (%)", F.Number(S.VolumeStep, 1, 25, v => { S.VolumeStep = v; Apply(); }), "Also the step of the mic fader's mouse wheel and arrow keys."));
    }

    private void Mic(StackPanel b)
    {
        var ins = new List<(string, string)> { ("", "Windows default recording device") };
        ins.AddRange(_deck.Audio.Devices(DataFlow.Capture).Select(d => (d.Id, d.Name + (d.IsDefault ? "  (current default)" : ""))));
        if (S.MicrophoneDevice.Length > 0 && ins.All(i => i.Item1 != S.MicrophoneDevice)) ins.Add((S.MicrophoneDevice, "(disconnected microphone)"));
        Add(b, F.Field("Controlled microphone", F.Combo(ins, S.MicrophoneDevice, v => { S.MicrophoneDevice = v; Apply(); }),
            "The MIC button, meter and fader act on this device. \"Windows default\" follows whatever Windows uses, including newly plugged-in devices."));
        Add(b, F.ToggleWithHint("Restore my microphone volume when it (re)connects", S.MicRestoreVolume, v => { S.MicRestoreVolume = v; Apply(); },
            "Remembers the last volume you set here per microphone and re-applies it on start-up and reconnection."));
        Add(b, F.ToggleWithHint("Live meters on microphone buttons", S.MeterOnButtons, v => { S.MeterOnButtons = v; Apply(); },
            "Draws the real input level on MIC keys. This keeps a capture stream open, so Windows shows its microphone-in-use indicator while the app runs."));
        Add(b, F.Caps("Monitoring"));
        var outs = new List<(string, string)> { ("", "Windows default (communications)") };
        outs.AddRange(SoundboardEngine.OutputDevices().Select(d => (d.Id, d.Name)));
        Add(b, F.Field("Monitor output", F.Combo(outs, S.MicMonitorDevice, v => { S.MicMonitorDevice = v; Apply(); if (_deck.Meter.MonitorEnabled) _deck.Meter.SetMonitor(true, v); }),
            "Where you hear yourself when MONITOR is on. Use headphones — speakers will feed back into the microphone. Expect ~40–60 ms of delay (Windows shared-mode audio)."));
        Add(b, F.Field("Monitor volume", F.Slider(0, 100, S.MicMonitorVolume, 1, v => $"{v:0} %", v => { S.MicMonitorVolume = (int)v; _deck.Meter.MonitorVolume = (float)v / 100f; _deck.SaveSoon(); })));
        Add(b, F.Caps("Live console"));
        Add(b, new MicModuleView(_deck, compact: true));
    }

    private void Profiles(StackPanel b)
    {
        Add(b, F.ToggleWithHint("Switch profiles automatically by active application", S.AutoProfile, v => _deck.SetAutoProfile(v),
            "Watches which window is in front (event-driven, no polling) and switches using the rules below."));
        var fb = new List<(string, string)> { ("", "Stay on the current profile") };
        fb.AddRange(_deck.Config.Profiles.Select(p => (p.Id, p.Name)));
        Add(b, F.Field("When no rule matches", F.Combo(fb, S.AutoProfileFallback, v => { S.AutoProfileFallback = v; Apply(); })));
        Add(b, F.Caps("Rules"));
        var list = new StackPanel();
        void Rebuild()
        {
            list.Children.Clear();
            foreach (var r in _deck.Config.AutoProfileRules.ToList())
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var en = new CheckBox { IsChecked = r.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Rule enabled" };
                en.Checked += (_, _) => { r.Enabled = true; Apply(); };
                en.Unchecked += (_, _) => { r.Enabled = false; Apply(); };
                g.Children.Add(en);
                var proc = F.Text(r.Process, v => { r.Process = v.Trim(); _deck.SaveSoon(); });
                proc.ToolTip = "Process name, e.g. obs64.exe";
                Grid.SetColumn(proc, 1);
                g.Children.Add(proc);
                var arrow = new TextBlock { Text = "→", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(arrow, 2);
                g.Children.Add(arrow);
                var prof = F.Combo(_deck.Config.Profiles.Select(p => (p.Id, p.Name)), r.ProfileId, v => { r.ProfileId = v; _deck.SaveSoon(); });
                Grid.SetColumn(prof, 3);
                g.Children.Add(prof);
                var del = F.IconButton("", "Remove rule", () => { _deck.Config.AutoProfileRules.Remove(r); _deck.SaveSoon(); Rebuild(); });
                Grid.SetColumn(del, 4);
                g.Children.Add(del);
                list.Children.Add(g);
            }
            if (_deck.Config.AutoProfileRules.Count == 0) list.Children.Add(F.Hint("No rules yet.", 0));
        }
        Rebuild();
        Add(b, list);
        var running = new ComboBox { MinWidth = 280 };
        running.Items.Add(new ComboBoxItem { Content = "Add rule for a running app…", Tag = "" });
        running.SelectedIndex = 0;
        running.DropDownOpened += (_, _) =>
        {
            if (running.Items.Count > 1) return;
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            Native.EnumWindows((h, _) =>
            {
                if (!Native.IsWindowVisible(h) || Native.GetWindowTextLength(h) == 0) return true;
                var path = WindowFocus.ProcessPathOfWindow(h);
                if (path != null && !path.Equals(AppInfo.ExePath, StringComparison.OrdinalIgnoreCase)) names.Add(System.IO.Path.GetFileName(path));
                return true;
            }, IntPtr.Zero);
            foreach (var n in names) running.Items.Add(new ComboBoxItem { Content = n, Tag = n });
        };
        running.SelectionChanged += (_, _) =>
        {
            if (running.SelectedItem is not ComboBoxItem { Tag: string n } || n.Length == 0) return;
            _deck.Config.AutoProfileRules.Add(new AutoProfileRule { Process = n, ProfileId = _deck.ActiveProfile.Id });
            _deck.SaveSoon();
            Rebuild();
            running.SelectedIndex = 0;
        };
        Add(b, F.Buttons(F.Button("+ Add rule", () =>
        {
            _deck.Config.AutoProfileRules.Add(new AutoProfileRule { Process = "", ProfileId = _deck.ActiveProfile.Id });
            _deck.SaveSoon();
            Rebuild();
        }), running));
        Add(b, F.Hint($"Currently in front: {(_deck.Foreground.LastExternalProcess.Length > 0 ? _deck.Foreground.LastExternalProcess : "unknown")}"));
    }

    private void Hotkeys(StackPanel b)
    {
        Add(b, F.Hint("Global shortcuts work from any application. A combination another program already owns cannot be registered — it is marked below and the rest keep working.", 0));
        var list = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        var commands = new List<(string, string)>(GlobalCommands.Fixed);
        commands.AddRange(_deck.Config.Profiles.Select(p => (GlobalCommands.ProfilePrefix + p.Id, "Profile: " + p.Name)));
        void Rebuild()
        {
            list.Children.Clear();
            foreach (var h in _deck.Config.Hotkeys.ToList())
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var chord = new ChordBox(h.Chord, v => { h.Chord = v; Apply(); Dispatcher.BeginInvoke(Rebuild); }, allowBareKeys: false);
                g.Children.Add(chord);
                var cmd = F.Combo(commands, h.Command, v => { h.Command = v; Apply(); });
                cmd.Margin = new Thickness(10, 0, 0, 0);
                Grid.SetColumn(cmd, 1);
                g.Children.Add(cmd);
                var del = F.IconButton("", "Remove", () => { _deck.Config.Hotkeys.Remove(h); Apply(); Rebuild(); });
                Grid.SetColumn(del, 2);
                g.Children.Add(del);
                list.Children.Add(g);
                var reg = _deck.Hotkeys.Registrations.FirstOrDefault(r => r.Chord == KeyChord.Normalize(h.Chord) && r.Label == _deck.DescribeCommand(h.Command));
                var status = reg == null ? (h.Chord.Length == 0 ? "Not set" : "Not registered") : reg.Ok ? "✓ Registered" : "⚠ " + reg.Error;
                var st = F.Hint(status, 0);
                st.Margin = new Thickness(2, 0, 0, 10);
                if (reg is { Ok: false }) st.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
                list.Children.Add(st);
            }
        }
        Rebuild();
        Add(b, list);
        Add(b, F.Buttons(F.Button("+ Add shortcut", () =>
        {
            _deck.Config.Hotkeys.Add(new HotkeyBinding { Chord = "", Command = GlobalCommands.ToggleMic });
            _deck.SaveSoon();
            Rebuild();
        })));
        var perButton = _deck.Hotkeys.Registrations.Where(r => r.Label.StartsWith("Button:") || r.Label.StartsWith("Profile:") && _deck.Config.Profiles.Any(p => p.Hotkey == r.Chord)).ToList();
        if (perButton.Count > 0)
        {
            Add(b, F.Caps("Set on buttons and profiles"));
            foreach (var r in perButton) Add(b, F.Hint($"{r.Chord}  →  {r.Label}{(r.Ok ? "" : "   ⚠ " + r.Error)}", 2));
        }
        Add(b, F.Hint("In-app keys: Ctrl+K search · Ctrl+E edit mode · Ctrl+1…9 profiles · PageUp/PageDown pages · arrows + Enter to press buttons · F11 control surface.", 16));
    }

    private void Integrations(StackPanel b)
    {
        Add(b, F.Caps("OBS Studio", 8));
        var status = F.Hint(_deck.Obs.StatusText, 0);
        Add(b, F.ToggleWithHint("Enable OBS integration", S.ObsEnabled, v => { S.ObsEnabled = v; Apply(); },
            "Uses OBS's built-in WebSocket server (OBS 28+: Tools › WebSocket Server Settings). Optional — the deck works normally without OBS."));
        Add(b, F.Field("Host", F.Text(S.ObsHost, v => { S.ObsHost = v.Trim(); _deck.SaveSoon(); })));
        Add(b, F.Field("Port", F.Number(S.ObsPort, 1, 65535, v => { S.ObsPort = v; _deck.SaveSoon(); })));
        var pw = new PasswordBox { Password = Secret.Unprotect(S.ObsPasswordProtected) };
        pw.PasswordChanged += (_, _) => { S.ObsPasswordProtected = Secret.Protect(pw.Password); _deck.SaveSoon(); };
        Add(b, F.Field("Password", pw, "Stored encrypted for your Windows account only (DPAPI) and never included in exports."));
        Add(b, F.Buttons(F.Button("Connect / test", async () =>
        {
            Apply();
            status.Text = "Connecting…";
            await Task.Delay(50);
            var ok = await _deck.Obs.ConnectAsync();
            status.Text = _deck.Obs.StatusText;
            if (ok) UiHost.Instance.Toast("Connected to OBS", ToastKind.Success);
        }, "Btn.Accent")));
        Add(b, status);

        Add(b, F.Caps("Discord"));
        Add(b, F.Hint("Discord has no local control API without a cloud-registered application, so the Discord buttons launch / focus Discord and send your keybinds. " +
                      "Set the same combinations as global keybinds in Discord (User Settings › Keybinds) so they work while Discord is in the background.", 0));
        Add(b, F.Field("Mute keybind", new ChordBox(S.DiscordMuteKeys, v => { S.DiscordMuteKeys = v; _deck.SaveSoon(); })));
        Add(b, F.Field("Deafen keybind", new ChordBox(S.DiscordDeafenKeys, v => { S.DiscordDeafenKeys = v; _deck.SaveSoon(); })));
    }

    private void Remote(StackPanel b)
    {
        Add(b, F.Hint("Use a tablet or phone on the same network as a control surface — in its web browser, no app to install. " +
                      "Off by default. It listens on your local network only and every request needs the PIN; there is no cloud relay.", 0));
        var urls = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        void ShowUrls()
        {
            urls.Children.Clear();
            var srv = AppShell.Remote;
            if (!S.RemoteEnabled) { urls.Children.Add(F.Hint("Remote surface is off.", 0)); return; }
            if (srv?.Running != true) { urls.Children.Add(F.Hint("⚠ " + (srv?.Error ?? "Not running."), 0)); return; }
            urls.Children.Add(F.Caps("Open one of these on the tablet", 0));
            foreach (var u in RemoteServer.LocalUrls(S.RemotePort))
            {
                var tb = new TextBox { Text = u, IsReadOnly = true, FontSize = 15, Margin = new Thickness(0, 0, 0, 6) };
                urls.Children.Add(tb);
            }
            var pin = new TextBlock { Text = "PIN  " + S.RemotePin, FontSize = 26, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Margin = new Thickness(0, 10, 0, 0) };
            urls.Children.Add(pin);
            urls.Children.Add(F.Hint("Windows Firewall may ask once whether to allow the app on private networks — allow it for the tablet to connect.", 6));
        }
        Add(b, F.Toggle("Enable tablet remote surface", S.RemoteEnabled, v =>
        {
            S.RemoteEnabled = v;
            if (v && S.RemotePin.Length != 6) S.RemotePin = RemoteServer.NewPin();
            _deck.SaveSoon();
            AppShell.ApplyRemote();
            ShowUrls();
        }));
        Add(b, F.Field("Port", F.Number(S.RemotePort, 1024, 65535, v => { S.RemotePort = v; _deck.SaveSoon(); })));
        Add(b, F.Buttons(
            F.Button("Apply port / restart", () => { AppShell.ApplyRemote(); ShowUrls(); }),
            F.Button("New PIN", () => { S.RemotePin = RemoteServer.NewPin(); _deck.SaveSoon(); AppShell.ApplyRemote(); ShowUrls(); })));
        Add(b, urls);
        ShowUrls();
    }

    private void Advanced(StackPanel b)
    {
        Add(b, F.Caps("Configuration", 8));
        Add(b, F.Hint($"Stored in {AppPaths.Root} (profiles, settings, imported sounds). Nothing is written next to the exe.", 0));
        Add(b, F.Buttons(
            F.Button("Export configuration…", () =>
            {
                var path = Pickers.Save(this, "Virtual Stream Deck configuration|*.vsdconfig", $"VirtualStreamDeck-{DateTime.Now:yyyyMMdd}.vsdconfig");
                if (path == null) return;
                try
                {
                    _deck.SaveNow();
                    ConfigStore.WriteExport(ConfigStore.BuildExport(_deck.Config), path);
                    UiHost.Instance.Toast("Configuration exported (sounds and icons included)", ToastKind.Success);
                }
                catch (Exception ex) { DialogWindow.Info(this, "Export failed", ex.Message); }
            }),
            F.Button("Import configuration…", () =>
            {
                var path = Pickers.File(this, "Virtual Stream Deck configuration|*.vsdconfig;*.vsdprofile|All files|*.*");
                if (path == null) return;
                try
                {
                    var pkg = ConfigStore.ReadExport(path);
                    if (pkg.Kind == "profile")
                    {
                        var added = ConfigStore.ImportProfiles(pkg, _deck.Config);
                        _deck.ReplaceConfig(_deck.Config);
                        UiHost.Instance.Toast($"Imported {added.Count} profile(s)", ToastKind.Success);
                        return;
                    }
                    if (!DialogWindow.Confirm(this, "Replace the current configuration?", "All profiles, buttons, sounds and settings are replaced by the imported ones. Export first if you want a backup.", "REPLACE", true)) return;
                    _deck.ReplaceConfig(ConfigStore.ImportConfig(pkg, _deck.Config));
                    ThemeManager.Apply(S);
                    UiHost.Instance.Toast("Configuration imported", ToastKind.Success);
                    ShowSection("Advanced");
                }
                catch (Exception ex) { DialogWindow.Info(this, "Import failed", ex.Message); }
            }),
            F.Button("Open data folder", () => Shell.Open(AppPaths.Root))));
        Add(b, F.Caps("Logging"));
        Add(b, F.ToggleWithHint("Write a log file", S.Logging, v => { S.Logging = v; Apply(); }, $"Daily files in {AppPaths.Logs}. Off by default."));
        Add(b, F.Caps("Diagnostics"));
        var diag = new TextBox
        {
            Text = DiagnosticsReport.Build(_deck), IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5,
            Height = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top,
        };
        Add(b, diag);
        Add(b, F.Buttons(
            F.Button("Copy diagnostics", () => { try { Clipboard.SetText(diag.Text); UiHost.Instance.Toast("Diagnostics copied", ToastKind.Success); } catch (Exception ex) { UiHost.Instance.Toast(ex.Message, ToastKind.Error); } }, "Btn.Accent"),
            F.Button("Refresh", () => diag.Text = DiagnosticsReport.Build(_deck))));
        Add(b, F.Caps("Reset"));
        Add(b, F.Buttons(
            F.Button("Reset settings…", () =>
            {
                if (!DialogWindow.Confirm(this, "Reset all settings?", "Settings return to their defaults. Profiles, buttons and sounds are kept.", "RESET", true)) return;
                var keepObs = S.ObsPasswordProtected;
                _deck.Config.Settings = new AppSettings { ObsPasswordProtected = keepObs };
                ThemeManager.Apply(_deck.Config.Settings);
                Apply(theme: true);
                AppShell.ApplyRemote();
                ShowSection("Advanced");
            }, "Btn.Danger"),
            F.Button("Run setup wizard again…", () => AppShell.RunWizard(this))));
    }
}
