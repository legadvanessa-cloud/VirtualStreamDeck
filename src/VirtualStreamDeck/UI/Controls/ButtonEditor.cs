using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Interop;
using VirtualStreamDeck.Services;
using VirtualStreamDeck.UI.Windows;

namespace VirtualStreamDeck.UI.Controls;

/// <summary>
/// Edit Mode side panel. Works on a deep copy of the button; SAVE commits it, CANCEL discards,
/// TEST runs the edited action without saving.
/// </summary>
public sealed class ButtonEditor : DockPanel
{
    private readonly Deck _deck;
    private DeckButton? _original;
    private DeckButton _edit = new();
    private readonly StackPanel _form = new();
    private readonly DeckButtonView _preview = new(new DeckButton()) { Width = 104, Height = 104, IsHitTestVisible = false };
    private readonly TextBlock _dirtyText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private bool _dirty;

    public event Action? Closed;
    public DeckButton? Original => _original;

    public ButtonEditor(Deck deck)
    {
        _deck = deck;
        LastChildFill = true;
        var header = new DockPanel { Margin = new Thickness(18, 16, 18, 8) };
        var close = F.IconButton("", "Close editor (Esc)", () => Close(confirmDiscard: true));
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var t = F.Caps("Edit mode · button", 0);
        t.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(t);
        DockPanel.SetDock(header, Dock.Top);
        Children.Add(header);

        var footer = new Border { Padding = new Thickness(18, 12, 18, 14), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        var fg = new Grid();
        fg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = F.Buttons(
            F.IconButton("", "Test the action (without saving)", Test),
            F.IconButton("", "Duplicate button", Duplicate),
            F.IconButton("", "Delete button", Delete));
        left.Margin = new Thickness(0);
        fg.Children.Add(left);
        var right = F.Buttons(F.Button("Cancel", () => Close(confirmDiscard: false)), F.Button("SAVE", Save, "Btn.Accent"));
        right.Margin = new Thickness(0);
        Grid.SetColumn(right, 1);
        fg.Children.Add(right);
        footer.Child = fg;
        DockPanel.SetDock(footer, Dock.Bottom);
        Children.Add(footer);

        var previewRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 4, 18, 6) };
        previewRow.Children.Add(_preview);
        _dirtyText.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
        previewRow.Children.Add(_dirtyText);
        DockPanel.SetDock(previewRow, Dock.Top);
        Children.Add(previewRow);

        var scroll = new ScrollViewer { Content = _form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(18, 0, 14, 12) };
        Children.Add(scroll);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(confirmDiscard: true); }
            else if (e.Key == System.Windows.Input.Key.S && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control) { e.Handled = true; Save(); }
        };
    }

    public void Load(DeckButton b)
    {
        _original = b;
        _edit = Json.Clone(b);
        _dirty = false;
        Build();
    }

    private void Changed(bool rebuild = false)
    {
        _dirty = true;
        _dirtyText.Text = "Unsaved changes";
        _preview.Update(_deck, false, _edit);
        if (rebuild) Build();
    }

    private void Build()
    {
        _form.Children.Clear();
        _preview.Update(_deck, false, _edit);
        _dirtyText.Text = _dirty ? "Unsaved changes" : "";
        var b = _edit;

        _form.Children.Add(F.Caps("Appearance", 6));
        _form.Children.Add(F.Field("Title", F.Text(b.Title, v => { b.Title = v; Changed(); })));
        _form.Children.Add(F.Field("Subtitle", F.Text(b.Subtitle, v => { b.Subtitle = v; Changed(); }),
            "Live buttons (microphone, volume, OBS…) show their real state here instead."));
        _form.Children.Add(F.Field("Icon", IconRow(b.Icon, ic => { b.Icon = ic; Changed(rebuild: true); })));
        _form.Children.Add(F.Field("Background", F.Color(b.Background, v => { b.Background = v; Changed(); })));
        _form.Children.Add(F.Field("Accent colour", F.Color(b.Accent, v => { b.Accent = v; Changed(); })));
        _form.Children.Add(F.Field("Text colour", F.Color(b.TextColor, v => { b.TextColor = v; Changed(); })));
        _form.Children.Add(F.Field("Size", F.Combo(new[] { ("1x1", "1 × 1"), ("2x1", "2 wide"), ("1x2", "2 tall"), ("2x2", "2 × 2") },
            $"{b.ColSpan}x{b.RowSpan}", v => { b.ColSpan = v[0] - '0'; b.RowSpan = v[2] - '0'; Changed(); }),
            "Larger sizes need free cells to the right / below."));

        _form.Children.Add(F.Caps("Behaviour"));
        _form.Children.Add(F.Field("Button type", F.Combo(ButtonModes.All, b.Mode, v =>
        {
            b.Mode = v;
            if (v == ButtonModes.Toggle && b.States.Count == 0) b.States.Add(new ButtonState { Title = b.Title, Subtitle = "OFF", Action = new ActionSpec() });
            if (v == ButtonModes.MultiState && b.States.Count == 0) b.States.Add(new ButtonState { Title = b.Title, Subtitle = "STATE 2", Action = new ActionSpec() });
            b.CurrentState = 0;
            Changed(rebuild: true);
        }), b.Mode switch
        {
            ButtonModes.Momentary => "Keyboard shortcuts are held down while you hold the button (push-to-talk). Other actions run on press.",
            ButtonModes.Toggle => "Each press switches to the other state and runs that state's action.",
            ButtonModes.MultiState => "Each press moves to the next state and runs its action, then wraps around.",
            _ => null,
        }));

        _form.Children.Add(F.Field(b.Mode is ButtonModes.Toggle or ButtonModes.MultiState ? "State 1 — action" : "Action",
            new ActionEditor(_deck, b.Action, () => Changed(), () => Changed(rebuild: true))));

        if (b.Mode is ButtonModes.Toggle or ButtonModes.MultiState)
        {
            var count = b.Mode == ButtonModes.Toggle ? Math.Min(1, b.States.Count) : b.States.Count;
            for (var i = 0; i < count; i++)
            {
                var s = b.States[i];
                var idx = i;
                var card = new StackPanel();
                var hdr = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                if (b.Mode == ButtonModes.MultiState && b.States.Count > 1)
                {
                    var rm = F.IconButton("", "Remove this state", () => { b.States.RemoveAt(idx); b.CurrentState = 0; Changed(rebuild: true); });
                    DockPanel.SetDock(rm, Dock.Right);
                    hdr.Children.Add(rm);
                }
                hdr.Children.Add(F.Caps($"State {i + 2}", 6));
                card.Children.Add(hdr);
                card.Children.Add(F.Field("Title", F.Text(s.Title, v => { s.Title = v; Changed(); })));
                card.Children.Add(F.Field("Subtitle", F.Text(s.Subtitle, v => { s.Subtitle = v; Changed(); })));
                card.Children.Add(F.Field("Icon (empty = same as state 1)", IconRow(s.Icon ?? new IconSpec(), ic => { s.Icon = ic; Changed(rebuild: true); })));
                card.Children.Add(F.Field("Accent", F.Color(s.Accent, v => { s.Accent = v; Changed(); })));
                card.Children.Add(F.Field("Action", new ActionEditor(_deck, s.Action, () => Changed(), () => Changed(rebuild: true))));
                _form.Children.Add(F.Card(card, 14));
            }
            if (b.Mode == ButtonModes.MultiState)
                _form.Children.Add(F.Buttons(F.Button("+ Add state", () =>
                {
                    b.States.Add(new ButtonState { Title = b.Title, Subtitle = $"STATE {b.States.Count + 2}" });
                    Changed(rebuild: true);
                })));
        }

        _form.Children.Add(F.Caps("Extras"));
        var sounds = new List<(string, string)> { ("", "None") };
        sounds.AddRange(_deck.Config.Sounds.Select(s => (s.Id, s.Name)));
        _form.Children.Add(F.Field("Sound on press", F.Combo(sounds, b.SoundId, v => { b.SoundId = v; Changed(); }), "Plays through the soundboard output as click feedback."));
        _form.Children.Add(F.Field("Global shortcut", new ChordBox(b.Hotkey, v => { b.Hotkey = v; Changed(); }, allowBareKeys: false),
            "Presses this button from any application."));
        _form.Children.Add(F.ToggleWithHint("Require confirmation", b.Confirm, v => { b.Confirm = v; Changed(rebuild: true); },
            "Asks before running. Shutdown, restart, sleep and sign-out always ask."));
        if (b.Confirm)
            _form.Children.Add(F.Field("Confirmation question", F.Text(b.ConfirmText, v => { b.ConfirmText = v; Changed(); })));
        _form.Children.Add(F.ToggleWithHint("Show live state", b.ShowLiveState, v => { b.ShowLiveState = v; Changed(); },
            "Display the real system state (MUTED, 64 %, LIVE…) as the subtitle."));
    }

    private FrameworkElement IconRow(IconSpec icon, Action<IconSpec> set)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var chip = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 10, 0) };
        chip.SetResourceReference(Border.BackgroundProperty, "Bg.Raised");
        chip.Child = IconVisual.Create(icon, 20, ThemeManager.Brush(ThemeManager.Current.Text));
        row.Children.Add(chip);
        row.Children.Add(F.Button("Choose icon…", () =>
        {
            var picked = IconPickerWindow.Pick(Window.GetWindow(this), icon);
            if (picked != null) set(picked);
        }));
        if (!icon.IsEmpty)
        {
            var clr = F.Button("Remove", () => set(new IconSpec()), "Btn.Ghost");
            clr.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(clr);
        }
        return row;
    }

    private async void Test()
    {
        var copy = Json.Clone(_edit);
        var (_, _, _, _, action) = Deck.Effective(copy);
        var r = await _deck.RunAsync(action, new ActionContext(ActionSource.Test, Window.GetWindow(this), copy));
        if (r.Ok && !r.Toast && r != ActionResult.Cancelled) UiHost.Instance.Toast("Action ran", ToastKind.Success);
    }

    private void Save()
    {
        if (_original == null) return;
        if (_edit.Hotkey.Length > 0 && KeyChord.TryParse(_edit.Hotkey, out var hk))
        {
            var clash = GridLayout.AllButtons(_deck.Config).FirstOrDefault(x => x.Button.Id != _original.Id && KeyChord.Normalize(x.Button.Hotkey) == hk.ToString());
            if (clash.Button != null)
            {
                DialogWindow.Info(Window.GetWindow(this), "Shortcut already used", $"{hk} is already assigned to \"{clash.Button.Title}\" in {clash.Profile.Name}. Choose another combination.");
                return;
            }
        }
        if (_deck.CommitButton(_original, _edit))
        {
            var saved = _deck.FindButton(_original.Id);
            _dirty = false;
            UiHost.Instance.Toast("Button saved", ToastKind.Success);
            var reg = _deck.Hotkeys.Registrations.FirstOrDefault(r => r.Chord == KeyChord.Normalize(_edit.Hotkey) && !r.Ok);
            if (_edit.Hotkey.Length > 0 && reg != null) UiHost.Instance.Toast($"Shortcut {reg.Chord} could not be registered: {reg.Error}", ToastKind.Warning);
            if (saved != null) Load(saved);
        }
    }

    private void Duplicate()
    {
        if (_original == null) return;
        var c = _deck.DuplicateButton(_original);
        if (c != null) { Load(c); UiHost.Instance.Toast("Button duplicated", ToastKind.Success); }
    }

    private void Delete()
    {
        if (_original == null) return;
        if (!DialogWindow.Confirm(Window.GetWindow(this), $"Delete \"{(_original.Title.Length > 0 ? _original.Title : "this button")}\"?", "This cannot be undone.", "DELETE", true)) return;
        _deck.DeleteButton(_original);
        _original = null;
        _dirty = false;
        Closed?.Invoke();
    }

    public bool ConfirmLeave()
    {
        if (!_dirty) return true;
        return DialogWindow.Confirm(Window.GetWindow(this), "Discard unsaved changes?", "The button keeps its previous settings.", "DISCARD", true);
    }

    private void Close(bool confirmDiscard)
    {
        if (confirmDiscard && !ConfirmLeave()) return;
        _dirty = false;
        _original = null;
        Closed?.Invoke();
    }
}

/// <summary>Per-type action fields. Rebuilds itself when the action type changes.</summary>
public sealed class ActionEditor : StackPanel
{
    private readonly Deck _deck;
    private readonly ActionSpec _a;
    private readonly Action _changed;
    private readonly Action _structural;

    public ActionEditor(Deck deck, ActionSpec a, Action changed, Action structural)
    {
        _deck = deck;
        _a = a;
        _changed = changed;
        _structural = structural;
        Build();
    }

    private void Set(Action apply) { apply(); _changed(); }

    private void Build()
    {
        Children.Clear();
        var a = _a;
        Children.Add(F.Combo(ActionTypes.All, a.Type, v =>
        {
            a.Type = v;
            a.Command = v switch
            {
                ActionTypes.Power => PowerCommands.Lock,
                ActionTypes.Media => MediaCmds.PlayPause,
                ActionTypes.System => SysCommands.MicToggle,
                ActionTypes.Deck => DeckCommands.SwitchProfile,
                ActionTypes.Obs => ObsCommands.ToggleStream,
                ActionTypes.Discord => DiscordCommands.Launch,
                _ => "",
            };
            if (v == ActionTypes.Sound && string.IsNullOrEmpty(a.Param)) a.Param = _deck.Config.Sounds.FirstOrDefault()?.Id ?? "";
            if (v == ActionTypes.Deck) a.Param = _deck.Config.Profiles.FirstOrDefault()?.Id ?? "";
            _structural();
        }));
        var body = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        Children.Add(body);
        void Add(UIElement e) => body.Children.Add(e);

        switch (a.Type)
        {
            case ActionTypes.LaunchApp:
                Add(F.Field("Program", F.Path(a.Target, v => Set(() => a.Target = v), "Programs|*.exe;*.bat;*.cmd;*.lnk;*.msc|All files|*.*"),
                    "A full path, or a name Windows can find such as chrome.exe or notepad.exe."));
                Add(F.Field("Pick a running app", RunningApps(v => { a.Target = v; _structural(); })));
                Add(F.Field("Arguments (optional)", F.Text(a.Arguments, v => Set(() => a.Arguments = v))));
                Add(F.Field("Start in (optional)", F.Path(a.WorkingDirectory, v => Set(() => a.WorkingDirectory = v), "", folder: true),
                    "Defaults to the program's own folder."));
                Add(F.Toggle("Bring to front if already running", a.FocusIfRunning, v => Set(() => a.FocusIfRunning = v)));
                Add(F.ToggleWithHint("Run as administrator", a.RunAsAdmin, v => Set(() => a.RunAsAdmin = v), "Windows asks for permission (UAC) every time."));
                break;
            case ActionTypes.OpenUrl:
                Add(F.Field("URL", F.Text(a.Target, v => Set(() => a.Target = v)),
                    "https://youtube.com, http://192.168.1.100, or app links such as steam://, spotify:, ms-settings:"));
                break;
            case ActionTypes.OpenPath:
                Add(F.Field("File", F.Path(a.Target, v => Set(() => a.Target = v), "All files|*.*")));
                Add(F.Buttons(F.Button("Choose a folder instead…", () =>
                {
                    var f = Pickers.Folder(Window.GetWindow(this), a.Target);
                    if (f != null) { a.Target = f; _structural(); }
                })));
                break;
            case ActionTypes.Hotkey:
                Add(F.Field("Keys", new ChordBox(a.Keys, v => Set(() => a.Keys = v)),
                    "Sent to the application you were using before you pressed the button. Examples: CTRL + SHIFT + M, ALT + TAB, F13."));
                break;
            case ActionTypes.Macro:
                Add(new MacroEditor(a.Steps, _changed));
                break;
            case ActionTypes.TypeText:
                Add(F.Field("Text to type", F.Text(a.Text, v => Set(() => a.Text = v), multiline: true), "New lines are typed as ENTER."));
                break;
            case ActionTypes.Sound:
                SoundFields(body);
                break;
            case ActionTypes.Media:
                Add(F.Field("Command", F.Combo(MediaCmds.All, a.Command, v => { a.Command = v; _structural(); }),
                    "Play/pause, next and previous use the Windows media session (the app shown in the volume flyout); media keys are the fallback."));
                if (a.Command is MediaCmds.VolumeUp or MediaCmds.VolumeDown)
                    Add(F.Field("Step (%)", F.Number(a.Value > 0 ? a.Value : _deck.Config.Settings.VolumeStep, 1, 25, v => Set(() => a.Value = v))));
                break;
            case ActionTypes.System:
                SystemFields(body);
                break;
            case ActionTypes.Power:
                Add(F.Field("Command", F.Combo(PowerCommands.All, a.Command, v => Set(() => a.Command = v)),
                    "Sleep, hibernate, restart, shut down and sign out always ask for confirmation."));
                break;
            case ActionTypes.Deck:
                Add(F.Field("Command", F.Combo(DeckCommands.All, a.Command, v => { a.Command = v; _structural(); })));
                if (a.Command == DeckCommands.SwitchProfile)
                    Add(F.Field("Profile", F.Combo(_deck.Config.Profiles.Select(p => (p.Id, p.Name)), a.Param, v => Set(() => a.Param = v))));
                if (a.Command == DeckCommands.GoToPage)
                    Add(F.Field("Page number", F.Number(Math.Max(1, a.Value), 1, 50, v => Set(() => a.Value = v))));
                break;
            case ActionTypes.Obs:
                ObsFields(body);
                break;
            case ActionTypes.Discord:
                Add(F.Field("Command", F.Combo(DiscordCommands.All, a.Command, v => { a.Command = v; _structural(); })));
                Add(F.Hint("Discord has no local API for mute state without a cloud-registered app, so mute and deafen send the keybind " +
                           "set in Settings › Integrations. Set the same keybind in Discord (User Settings › Keybinds) to make it work while Discord is in the background. " +
                           "The button does not claim to know Discord's mute state."));
                break;
            case ActionTypes.RunCommand:
                var warn = new Border { Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 12), BorderThickness = new Thickness(1) };
                warn.SetResourceReference(Border.BackgroundProperty, "Danger.Soft");
                warn.SetResourceReference(Border.BorderBrushProperty, "Danger");
                warn.Child = new TextBlock
                {
                    Text = "User-defined command. It runs with your Windows permissions exactly as written here. " +
                           "Only use commands you wrote or trust — this app never downloads or runs remote scripts.",
                    TextWrapping = TextWrapping.Wrap, FontSize = 12,
                };
                Add(warn);
                Add(F.Field("Shell", F.Combo(new[] { ("powershell", "Windows PowerShell"), ("cmd", "Command Prompt (cmd)") }, a.Shell, v => Set(() => a.Shell = v))));
                Add(F.Field("Command / script", F.Text(a.Text, v => Set(() => a.Text = v), multiline: true)));
                Add(F.Field("Start in (optional)", F.Path(a.WorkingDirectory, v => Set(() => a.WorkingDirectory = v), "", folder: true)));
                Add(F.Toggle("Show the console window", a.ShowWindow, v => Set(() => a.ShowWindow = v)));
                break;
        }
    }

    private FrameworkElement RunningApps(Action<string> pick)
    {
        var cb = new ComboBox { MaxDropDownHeight = 360 };
        cb.Items.Add(new ComboBoxItem { Content = "Choose…", Tag = "" });
        cb.SelectedIndex = 0;
        cb.DropDownOpened += (_, _) =>
        {
            if (cb.Items.Count > 1) return;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Native.EnumWindows((h, _) =>
            {
                if (!Native.IsWindowVisible(h) || Native.GetWindowTextLength(h) == 0) return true;
                var path = WindowFocus.ProcessPathOfWindow(h);
                if (path == null || path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase) && !path.EndsWith("notepad.exe", StringComparison.OrdinalIgnoreCase)) return true;
                if (path.Equals(AppInfo.ExePath, StringComparison.OrdinalIgnoreCase) || !seen.Add(path)) return true;
                cb.Items.Add(new ComboBoxItem { Content = $"{Path.GetFileNameWithoutExtension(path)}  —  {path}", Tag = path });
                return true;
            }, IntPtr.Zero);
        };
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is ComboBoxItem { Tag: string p } && p.Length > 0) pick(p); };
        return cb;
    }

    private void SoundFields(StackPanel body)
    {
        var a = _a;
        var items = _deck.Config.Sounds.Select(s => (s.Id, s.Name + (s.IsBuiltIn ? "  (built-in)" : ""))).ToList();
        body.Children.Add(F.Field("Sound", F.Combo(items, a.Param, v => Set(() => a.Param = v)), "Import your own WAV / MP3 / OGG files in the Sound Library panel."));
        body.Children.Add(F.Buttons(
            F.Button("▶ Preview", () =>
            {
                var s = _deck.Config.Sounds.FirstOrDefault(x => x.Id == a.Param);
                if (s == null) return;
                try { _deck.Soundboard.Play(s, "preview", a.Volume / 100f, false, a.FadeInMs, a.FadeOutMs, "restart"); }
                catch (Exception ex) { UiHost.Instance.Toast(ex.Message, ToastKind.Error); }
            }),
            F.Button("■ Stop", () => _deck.Soundboard.Stop("preview", 80))));
        body.Children.Add(F.Field("Volume", F.Slider(0, 100, a.Volume, 1, v => $"{v:0} %", v => Set(() => a.Volume = (int)v))));
        body.Children.Add(F.Toggle("Loop until pressed again", a.Loop, v => Set(() => { a.Loop = v; if (v && a.Retrigger == "") a.Retrigger = "toggle"; })));
        body.Children.Add(F.Field("Fade in (ms)", F.Number(a.FadeInMs, 0, 10000, v => Set(() => a.FadeInMs = v))));
        body.Children.Add(F.Field("Fade out (ms)", F.Number(a.FadeOutMs, 0, 10000, v => Set(() => a.FadeOutMs = v))));
        body.Children.Add(F.Field("When pressed while playing", F.Combo(new[]
        {
            ("", "Use global setting"), ("overlap", "Play again on top (overlap)"), ("restart", "Restart from the beginning"), ("toggle", "Stop it"),
        }, a.Retrigger, v => Set(() => a.Retrigger = v))));
    }

    private void SystemFields(StackPanel body)
    {
        var a = _a;
        body.Children.Add(F.Field("Command", F.Combo(SysCommands.All, a.Command, v => { a.Command = v; _structural(); })));
        switch (a.Command)
        {
            case SysCommands.MasterVolumeSet:
            case SysCommands.MicVolumeSet:
            case SysCommands.BrightnessSet:
                body.Children.Add(F.Field("Level", F.Slider(0, 100, a.Value, 1, v => $"{v:0} %", v => Set(() => a.Value = (int)v))));
                break;
            case SysCommands.MasterVolumeUp:
            case SysCommands.MasterVolumeDown:
            case SysCommands.MicVolumeUp:
            case SysCommands.MicVolumeDown:
            case SysCommands.BrightnessUp:
            case SysCommands.BrightnessDown:
                body.Children.Add(F.Field("Step (%)", F.Number(a.Value > 0 ? a.Value : _deck.Config.Settings.VolumeStep, 1, 25, v => Set(() => a.Value = v))));
                break;
            case SysCommands.OutputSet:
            case SysCommands.InputSet:
                var devs = _deck.Audio.Devices(a.Command == SysCommands.OutputSet ? DataFlow.Render : DataFlow.Capture);
                var items = devs.Select(d => (d.Id, d.Name + (d.IsDefault ? "  (current default)" : ""))).ToList();
                if (a.Param.Length > 0 && devs.All(d => d.Id != a.Param)) items.Insert(0, (a.Param, "(disconnected device)"));
                body.Children.Add(F.Field("Device", F.Combo(items, a.Param, v => Set(() => a.Param = v)),
                    "Windows has no documented API for this; the app uses the same interface as the Sound control panel."));
                break;
            case SysCommands.CameraStatus:
                body.Children.Add(F.Hint("Shows whether any app is using the camera right now and whether camera access is blocked. " +
                                         "Windows provides no supported way to switch a webcam off from an app, so pressing it opens the camera privacy page."));
                break;
            case SysCommands.CameraDeviceToggle:
                body.Children.Add(F.Hint("Disables / enables the camera in Device Manager — a real cut-off. Requires administrator permission (UAC prompt) each time."));
                break;
            case SysCommands.NightLight:
                body.Children.Add(F.Hint("Night light has no public Windows API. This opens its settings page rather than pretending to toggle it."));
                break;
            case SysCommands.WifiToggle:
            case SysCommands.BluetoothToggle:
                body.Children.Add(F.Hint("Uses the Windows radio API (the same switch as Quick Settings). Requires 'Let apps control device radios' in privacy settings."));
                break;
            case SysCommands.MicToggle:
                body.Children.Add(F.Hint("Mutes the Windows default recording device (or the one chosen in Settings › Audio). The state updates instantly when it changes anywhere."));
                break;
        }
    }

    private void ObsFields(StackPanel body)
    {
        var a = _a;
        if (!_deck.Config.Settings.ObsEnabled)
            body.Children.Add(F.Hint("OBS integration is off — enable it in Settings › Integrations (OBS 28+ with its WebSocket server on).", 0));
        body.Children.Add(F.Field("Command", F.Combo(ObsCommands.All, a.Command, v => { a.Command = v; _structural(); })));
        switch (a.Command)
        {
            case ObsCommands.Scene:
                var tb = F.Text(a.Param, v => Set(() => a.Param = v));
                body.Children.Add(F.Field("Scene name", tb));
                body.Children.Add(F.Buttons(F.Button("Load scenes from OBS", async () =>
                {
                    try
                    {
                        var names = await _deck.Obs.SceneNamesAsync();
                        if (names.Count == 0) { UiHost.Instance.Toast("OBS has no scenes", ToastKind.Warning); return; }
                        var m = new ContextMenu();
                        foreach (var n in names)
                        {
                            var mi = new MenuItem { Header = n };
                            mi.Click += (_, _) => tb.Text = n;
                            m.Items.Add(mi);
                        }
                        m.PlacementTarget = tb;
                        m.IsOpen = true;
                    }
                    catch (Exception ex) { UiHost.Instance.Toast(ex.Message, ToastKind.Error); }
                })));
                break;
            case ObsCommands.ToggleSource:
                body.Children.Add(F.Field("Scene name", F.Text(a.Param, v => Set(() => a.Param = v))));
                body.Children.Add(F.Field("Source name", F.Text(a.Param2, v => Set(() => a.Param2 = v))));
                break;
            case ObsCommands.ToggleMute:
                body.Children.Add(F.Field("Audio source name", F.Text(a.Param, v => Set(() => a.Param = v)), "e.g. Mic/Aux or Desktop Audio"));
                break;
        }
    }
}

/// <summary>Visual list of macro steps with reordering.</summary>
public sealed class MacroEditor : StackPanel
{
    private readonly List<MacroStep> _steps;
    private readonly Action _changed;
    private readonly StackPanel _list = new();

    public MacroEditor(List<MacroStep> steps, Action changed)
    {
        _steps = steps;
        _changed = changed;
        Children.Add(_list);
        var add = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        void AddBtn(string label, Func<MacroStep> make)
        {
            var b = F.Button(label, () => { _steps.Add(make()); Rebuild(); _changed(); });
            b.Margin = new Thickness(0, 0, 6, 6);
            b.Padding = new Thickness(10, 5, 10, 5);
            add.Children.Add(b);
        }
        AddBtn("+ Keys", () => new MacroStep { Type = MacroStepTypes.Combo });
        AddBtn("+ Text", () => new MacroStep { Type = MacroStepTypes.Text });
        AddBtn("+ Wait", () => new MacroStep { Type = MacroStepTypes.Delay, Number = 300 });
        AddBtn("+ Click", () => new MacroStep { Type = MacroStepTypes.Click });
        AddBtn("+ Move", () => new MacroStep { Type = MacroStepTypes.Move });
        AddBtn("+ Launch", () => new MacroStep { Type = MacroStepTypes.Launch });
        Children.Add(add);
        Children.Add(F.Hint("Steps run top to bottom. Keys are sent to the application you were using. Mouse positions are screen pixels and are kept on screen."));
        Rebuild();
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        if (_steps.Count == 0) _list.Children.Add(F.Hint("No steps yet.", 0));
        for (var i = 0; i < _steps.Count; i++) _list.Children.Add(Row(i));
    }

    private FrameworkElement Row(int i)
    {
        var s = _steps[i];
        var card = new StackPanel();
        var head = new DockPanel();
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(Small("", "Move up", () => Swap(i, i - 1), i > 0));
        tools.Children.Add(Small("", "Move down", () => Swap(i, i + 1), i < _steps.Count - 1));
        tools.Children.Add(Small("", "Remove step", () => { _steps.RemoveAt(i); Rebuild(); _changed(); }, true));
        DockPanel.SetDock(tools, Dock.Right);
        head.Children.Add(tools);
        var num = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        num.SetResourceReference(Border.BackgroundProperty, "Accent.Soft");
        num.Child = new TextBlock { Text = (i + 1).ToString(), FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(num);
        var summary = new TextBlock { Text = s.Describe(), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        head.Children.Add(summary);
        card.Children.Add(head);

        void Touch() { summary.Text = s.Describe(); _changed(); }
        var body = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        body.Children.Add(F.Combo(MacroStepTypes.All, s.Type, v => { s.Type = v; Rebuild(); _changed(); }));
        var detail = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        switch (s.Type)
        {
            case MacroStepTypes.Combo:
            case MacroStepTypes.Key:
            case MacroStepTypes.KeyDown:
            case MacroStepTypes.KeyUp:
                detail.Children.Add(new ChordBox(s.Value, v => { s.Value = v; Touch(); }));
                break;
            case MacroStepTypes.Text:
                detail.Children.Add(F.Text(s.Value, v => { s.Value = v; Touch(); }));
                break;
            case MacroStepTypes.Delay:
                detail.Children.Add(F.Number(s.Number, 0, MacroEngine.MaxDelayMs, v => { s.Number = v; Touch(); }));
                detail.Children.Add(F.Hint("Milliseconds (max 60 000)."));
                break;
            case MacroStepTypes.Click:
                detail.Children.Add(F.Combo(new[] { ("left", "Left button"), ("right", "Right button"), ("middle", "Middle button") }, s.Button, v => { s.Button = v; Touch(); }));
                detail.Children.Add(F.Toggle("Click at a position", s.AtPosition, v => { s.AtPosition = v; Rebuild(); _changed(); }));
                if (s.AtPosition) detail.Children.Add(XY(s, Touch));
                break;
            case MacroStepTypes.Move:
                detail.Children.Add(XY(s, Touch));
                break;
            case MacroStepTypes.Launch:
                detail.Children.Add(F.Path(s.Value, v => { s.Value = v; Touch(); }, "Programs|*.exe;*.bat;*.cmd;*.lnk|All files|*.*"));
                break;
        }
        body.Children.Add(detail);
        card.Children.Add(body);
        return F.Card(card, 10);
    }

    private FrameworkElement XY(MacroStep s, Action touch)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var x = F.Number(s.X, -32000, 32000, v => { s.X = v; touch(); });
        var y = F.Number(s.Y, -32000, 32000, v => { s.Y = v; touch(); });
        x.Width = y.Width = 80;
        y.Margin = new Thickness(6, 0, 6, 0);
        sp.Children.Add(x);
        sp.Children.Add(y);
        Button? cap = null;
        cap = F.Button("Capture in 3 s", async () =>
        {
            cap!.IsEnabled = false;
            for (var n = 3; n > 0; n--) { cap.Content = $"Move mouse… {n}"; await Task.Delay(1000); }
            Native.GetCursorPos(out var p);
            s.X = p.X; s.Y = p.Y;
            x.Text = p.X.ToString(); y.Text = p.Y.ToString();
            cap.Content = "Capture in 3 s";
            cap.IsEnabled = true;
            touch();
        }, tip: "Point at the target with the mouse; the position is captured after 3 seconds.");
        sp.Children.Add(cap);
        return sp;
    }

    private static Button Small(string glyph, string tip, Action click, bool enabled)
    {
        var b = F.IconButton(glyph, tip, click);
        b.Width = b.Height = 28;
        b.FontSize = 11;
        b.IsEnabled = enabled;
        return b;
    }

    private void Swap(int a, int b)
    {
        if (b < 0 || b >= _steps.Count) return;
        (_steps[a], _steps[b]) = (_steps[b], _steps[a]);
        Rebuild();
        _changed();
    }
}
