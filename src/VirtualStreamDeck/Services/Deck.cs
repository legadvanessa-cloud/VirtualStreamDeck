using System.Windows.Threading;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Interop;

namespace VirtualStreamDeck.Services;

/// <summary>
/// The application core: owns the configuration and every service, and implements
/// ProfileManager / ButtonManager behaviour. All public members are UI-thread only; service
/// callbacks from other threads are marshalled and coalesced before events are raised.
/// </summary>
public sealed class Deck : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _progressTimer;
    private readonly HashSet<string> _running = new();
    private int _liveQueued;
    private bool _disposed;

    public AppConfig Config { get; private set; }
    public IUiHost Ui { get; set; } = new NullUi();
    public string? StartupWarning { get; }
    public bool IsFirstRun { get; }

    public SystemAudio Audio { get; }
    public SoundboardEngine Soundboard { get; } = new();
    public MicMeter Meter { get; } = new();
    public CameraService Camera { get; } = new();
    public RadioService Radios { get; } = new();
    public MediaService Media { get; } = new();
    public ObsClient Obs { get; } = new();
    public SystemMonitor Monitor { get; } = new();
    public ForegroundWatcher Foreground { get; }
    public MessageWindow MessageWindow { get; }
    public HotkeyManager Hotkeys { get; }
    public ActionEngine Actions { get; }

    public bool EditMode { get; private set; }
    public bool SurfaceOpen { get; set; }
    public int PageIndex { get; private set; }

    public event Action? ProfileChanged;      // active profile or page switched
    public event Action? LayoutChanged;       // buttons / profiles edited
    public event Action? LiveChanged;         // system state that buttons display changed
    public event Action? SoundProgress;       // ~15 Hz while any sound plays
    public event Action? SoundsChanged;       // library edited
    public event Action? EditModeChanged;
    public event Action? SettingsApplied;
    public event Action<string, bool>? ButtonFeedback; // button id, success
    public event Action? ToggleSurfaceRequested;
    public event Action? ShowMainRequested;

    public Deck(Dispatcher ui)
    {
        _ui = ui;
        var load = ConfigStore.Load();
        Config = load.Config;
        IsFirstRun = load.WasCreated || !Config.FirstRunCompleted;
        StartupWarning = load.Warning;
        Log.FileEnabled = Config.Settings.Logging;
        PageIndex = Math.Clamp(Config.ActivePage, 0, ActiveProfile.Pages.Count - 1);

        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => SaveNow(), ui) { IsEnabled = false };
        _progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(66), DispatcherPriority.Render, (_, _) => OnProgressTick(), ui) { IsEnabled = false };

        Audio = new SystemAudio();
        MessageWindow = new MessageWindow();
        Hotkeys = new HotkeyManager(MessageWindow);
        Foreground = new ForegroundWatcher();
        Actions = new ActionEngine(this);

        Audio.Changed += () => { Meter.SetDevice(Audio.Microphone.Id); QueueLive(); };
        Audio.MicrophoneSwitched += (a, b) => _ui.BeginInvoke(() => OnMicrophoneSwitched(a, b));
        Meter.StateChanged += QueueLive;
        Meter.SetDevice(Audio.Microphone.Id);
        Camera.Changed += QueueLive;
        Radios.Changed += QueueLive;
        Media.Changed += QueueLive;
        Obs.Changed += QueueLive;
        Soundboard.VoicesChanged += OnVoicesChanged;
        Soundboard.DeviceProblem += msg => _ui.BeginInvoke(() => Ui.Toast(msg, ToastKind.Warning));
        Foreground.ForegroundProcessChanged += p => _ui.BeginInvoke(() => OnForegroundProcess(p));
    }

    /// <summary>Starts the slower services in the background after the window is up.</summary>
    public void StartServices()
    {
        ApplySettings();
        RestoreMicVolume();
        _ = Task.Run(() => { try { Camera.Start(); } catch (Exception ex) { Log.Error("Camera service", ex); } });
        _ui.BeginInvoke(async () =>
        {
            await Radios.InitAsync();
            await Media.InitAsync();
        }, DispatcherPriority.Background);
        _ = Task.Run(() =>
        {
            // Pre-decode sounds used on buttons so the first press is instant.
            var used = GridLayout.AllButtons(Config).Select(x => x.Button)
                .SelectMany(b => new[] { b.Action }.Concat(b.States.Select(s => s.Action)))
                .Where(a => a.Type == ActionTypes.Sound).Select(a => a.Param).ToHashSet();
            foreach (var s in Config.Sounds.Where(s => used.Contains(s.Id)).ToList()) Soundboard.Preload(s);
        });
    }

    // ---- persistence -----------------------------------------------------------------------

    public void SaveSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        Config.ActivePage = PageIndex;
        try { ConfigStore.Save(Config); }
        catch (Exception ex)
        {
            Log.Error("Saving configuration failed", ex);
            Ui.Toast("Could not save settings: " + ex.Message, ToastKind.Error);
        }
    }

    public void ReplaceConfig(AppConfig cfg)
    {
        Config = cfg;
        PageIndex = 0;
        SaveNow();
        ApplySettings();
        LayoutChanged?.Invoke();
        ProfileChanged?.Invoke();
        SoundsChanged?.Invoke();
    }

    // ---- settings --------------------------------------------------------------------------

    public void ApplySettings()
    {
        var s = Config.Settings;
        Log.FileEnabled = s.Logging;
        Soundboard.MasterVolume = s.SoundboardVolume / 100f;
        Soundboard.SetDevice(s.SoundboardDevice);
        Audio.SetMicrophoneDevice(s.MicrophoneDevice);
        Meter.MonitorVolume = s.MicMonitorVolume / 100f;
        UpdateButtonMeters();
        Obs.Configure(s.ObsEnabled, s.ObsHost, s.ObsPort, Secret.Unprotect(s.ObsPasswordProtected));
        try { if (StartupRegistration.IsEnabled() != s.StartWithWindows) StartupRegistration.Set(s.StartWithWindows); }
        catch (Exception ex) { Log.Warn("Startup registration: " + ex.Message); }
        RegisterHotkeys();
        SettingsApplied?.Invoke();
        QueueLive();
    }

    // ---- broadcast microphone ---------------------------------------------------------------

    /// <summary>Sets the controlled microphone's Windows volume and remembers it for that device.</summary>
    public int SetMicVolume(int percent)
    {
        var v = Audio.SetMicVolume(percent);
        Remember(v);
        return v;
    }

    public int AdjustMicVolume(int delta)
    {
        var v = Audio.AdjustMicVolume(delta);
        Remember(v);
        return v;
    }

    private void Remember(int v)
    {
        var id = Audio.Microphone.Id;
        if (id.Length == 0) return;
        Config.Settings.MicVolumes[id] = v;
        SaveSoon();
    }

    private void RestoreMicVolume()
    {
        var mic = Audio.Microphone;
        if (!Config.Settings.MicRestoreVolume || !mic.Available || !Config.Settings.MicVolumes.TryGetValue(mic.Id, out var want)) return;
        if (Math.Abs(mic.VolumePercent - want) < 1) return;
        try
        {
            Audio.SetMicVolume(want);
            Log.Info($"Restored microphone volume {want}% on {mic.DeviceName}");
        }
        catch (Exception ex) { Log.Warn("Restoring mic volume: " + ex.Message); }
    }

    private void OnMicrophoneSwitched(string before, string after)
    {
        if (after.Length == 0) Ui.Toast($"Microphone disconnected: {before}", ToastKind.Warning);
        else if (before.Length == 0) Ui.Toast($"Microphone connected: {after}", ToastKind.Success);
        else Ui.Toast($"Microphone: {after}", ToastKind.Info);
        RestoreMicVolume();
    }

    public bool ToggleMonitor()
    {
        var on = !Meter.MonitorEnabled;
        Meter.SetMonitor(on, Config.Settings.MicMonitorDevice);
        if (on && !Meter.Monitoring) throw new InvalidOperationException(Meter.Error ?? "Monitoring could not start.");
        return on;
    }

    private bool _buttonMeters;

    /// <summary>Keeps the capture stream open while mic keys draw live meters (opt-in setting).</summary>
    public void UpdateButtonMeters()
    {
        var want = Config.Settings.MeterOnButtons;
        if (want == _buttonMeters) return;
        _buttonMeters = want;
        if (want) Meter.Acquire(); else Meter.Release();
    }

    public void SetAutoProfile(bool on)
    {
        Config.Settings.AutoProfile = on;
        SaveSoon();
        SettingsApplied?.Invoke();
        QueueLive();
        if (on) OnForegroundProcess(Foreground.LastExternalProcess);
    }

    // ---- profiles & pages ------------------------------------------------------------------

    public Profile ActiveProfile =>
        Config.Profiles.FirstOrDefault(p => p.Id == Config.ActiveProfileId) ?? Config.Profiles[0];

    public DeckPage ActivePage => ActiveProfile.Pages[Math.Clamp(PageIndex, 0, ActiveProfile.Pages.Count - 1)];

    public void SwitchProfile(Profile p, bool announce, string? reason = null)
    {
        if (p.Id == Config.ActiveProfileId && PageIndex == 0) return;
        var changed = p.Id != Config.ActiveProfileId;
        Config.ActiveProfileId = p.Id;
        PageIndex = 0;
        SaveSoon();
        ProfileChanged?.Invoke();
        if (announce && changed) Ui.Toast($"Profile switched to {p.Name}{(reason != null ? $" ({reason})" : "")}", ToastKind.Success);
    }

    public void NextProfile()
    {
        var i = Config.Profiles.IndexOf(ActiveProfile);
        SwitchProfile(Config.Profiles[(i + 1) % Config.Profiles.Count], announce: true);
    }

    public void SetPage(int index)
    {
        var n = ActiveProfile.Pages.Count;
        index = Math.Clamp(index, 0, n - 1);
        if (index == PageIndex) return;
        PageIndex = index;
        SaveSoon();
        ProfileChanged?.Invoke();
    }

    public void ChangePage(int delta)
    {
        var n = ActiveProfile.Pages.Count;
        if (n <= 1) return;
        SetPage(((PageIndex + delta) % n + n) % n);
    }

    public Profile AddProfile(string template, string? name = null)
    {
        var p = Defaults.CreateProfile(template, Config.Settings.DefaultColumns, Config.Settings.DefaultRows);
        if (name != null) p.Name = name;
        var baseName = p.Name;
        var k = 2;
        while (Config.Profiles.Any(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))) p.Name = $"{baseName} {k++}";
        Config.Profiles.Add(p);
        SaveSoon();
        RegisterHotkeys();
        LayoutChanged?.Invoke();
        return p;
    }

    public Profile DuplicateProfile(Profile src)
    {
        var p = Json.Clone(src);
        p.Id = DeckButton.NewId();
        p.Name = src.Name + " copy";
        p.Hotkey = "";
        foreach (var b in p.Pages.SelectMany(pg => pg.Buttons)) { b.Id = DeckButton.NewId(); b.Hotkey = ""; }
        Config.Profiles.Insert(Config.Profiles.IndexOf(src) + 1, p);
        SaveSoon();
        LayoutChanged?.Invoke();
        return p;
    }

    public bool DeleteProfile(Profile p)
    {
        if (Config.Profiles.Count <= 1) return false;
        var wasActive = p.Id == Config.ActiveProfileId;
        Config.Profiles.Remove(p);
        Config.AutoProfileRules.RemoveAll(r => r.ProfileId == p.Id);
        Config.Hotkeys.RemoveAll(h => h.Command == GlobalCommands.ProfilePrefix + p.Id);
        if (Config.Settings.AutoProfileFallback == p.Id) Config.Settings.AutoProfileFallback = "";
        if (wasActive) { Config.ActiveProfileId = Config.Profiles[0].Id; PageIndex = 0; }
        SaveSoon();
        RegisterHotkeys();
        LayoutChanged?.Invoke();
        if (wasActive) ProfileChanged?.Invoke();
        return true;
    }

    public void MoveProfile(Profile p, int delta)
    {
        var i = Config.Profiles.IndexOf(p);
        var j = Math.Clamp(i + delta, 0, Config.Profiles.Count - 1);
        if (i == j) return;
        Config.Profiles.RemoveAt(i);
        Config.Profiles.Insert(j, p);
        SaveSoon();
        LayoutChanged?.Invoke();
    }

    public void ProfileEdited(Profile p)
    {
        var moved = GridLayout.Repair(p);
        if (moved > 0) Ui.Toast($"{moved} button(s) did not fit the new grid and were moved to another page.", ToastKind.Warning);
        PageIndex = Math.Clamp(PageIndex, 0, ActiveProfile.Pages.Count - 1);
        SaveSoon();
        RegisterHotkeys();
        LayoutChanged?.Invoke();
        ProfileChanged?.Invoke();
    }

    public void AddPage(Profile p)
    {
        p.Pages.Add(new DeckPage());
        if (p == ActiveProfile) PageIndex = p.Pages.Count - 1;
        SaveSoon();
        LayoutChanged?.Invoke();
        ProfileChanged?.Invoke();
    }

    public bool DeletePage(Profile p, int index)
    {
        if (p.Pages.Count <= 1) return false;
        p.Pages.RemoveAt(index);
        PageIndex = Math.Clamp(PageIndex, 0, p.Pages.Count - 1);
        SaveSoon();
        RegisterHotkeys();
        LayoutChanged?.Invoke();
        ProfileChanged?.Invoke();
        return true;
    }

    // ---- buttons ---------------------------------------------------------------------------

    public (Profile Profile, DeckPage Page)? Locate(DeckButton b)
    {
        foreach (var p in Config.Profiles)
        foreach (var pg in p.Pages)
            if (pg.Buttons.Contains(b)) return (p, pg);
        return null;
    }

    public DeckButton? FindButton(string id) => GridLayout.AllButtons(Config).Select(x => x.Button).FirstOrDefault(b => b.Id == id);

    public DeckButton AddButton(int row, int col)
    {
        var b = new DeckButton { Row = row, Col = col, Title = "NEW BUTTON", Icon = IconSpec.Glyph("E710") };
        ActivePage.Buttons.Add(b);
        SaveSoon();
        LayoutChanged?.Invoke();
        return b;
    }

    /// <summary>Commits an edited copy of a button (from the editor) over the original.</summary>
    public bool CommitButton(DeckButton original, DeckButton edited)
    {
        var loc = Locate(original);
        if (loc == null) return false;
        var (profile, page) = loc.Value;
        edited.Id = original.Id;
        edited.Row = original.Row;
        edited.Col = original.Col;
        edited.Hotkey = KeyChord.Normalize(edited.Hotkey);
        if (!GridLayout.Fits(page, profile.Columns, profile.Rows, edited.Row, edited.Col, edited.RowSpan, edited.ColSpan, original.Id))
        {
            Ui.Toast("The new size does not fit here; kept the previous size.", ToastKind.Warning);
            edited.RowSpan = original.RowSpan;
            edited.ColSpan = original.ColSpan;
        }
        edited.CurrentState = Math.Clamp(edited.CurrentState, 0, Math.Max(0, edited.States.Count));
        page.Buttons[page.Buttons.IndexOf(original)] = edited;
        SaveSoon();
        RegisterHotkeys();
        LayoutChanged?.Invoke();
        return true;
    }

    public void DeleteButton(DeckButton b)
    {
        var loc = Locate(b);
        if (loc == null) return;
        loc.Value.Page.Buttons.Remove(b);
        SaveSoon();
        RegisterHotkeys();
        LayoutChanged?.Invoke();
    }

    public DeckButton? DuplicateButton(DeckButton b)
    {
        var loc = Locate(b);
        if (loc == null) return null;
        var (p, pg) = loc.Value;
        var copy = Json.Clone(b);
        copy.Id = DeckButton.NewId();
        copy.Hotkey = "";
        var slot = GridLayout.FindFree(pg, p.Columns, p.Rows, b.RowSpan, b.ColSpan) ?? GridLayout.FindFree(pg, p.Columns, p.Rows);
        if (slot == null) { Ui.Toast("This page is full - no room for a copy.", ToastKind.Warning); return null; }
        if (GridLayout.FindFree(pg, p.Columns, p.Rows, b.RowSpan, b.ColSpan) == null) { copy.RowSpan = 1; copy.ColSpan = 1; }
        (copy.Row, copy.Col) = slot.Value;
        pg.Buttons.Add(copy);
        SaveSoon();
        LayoutChanged?.Invoke();
        return copy;
    }

    /// <summary>Drag &amp; drop inside the visible page: onto a button swaps, onto a free cell moves.</summary>
    public bool MoveOrSwap(DeckButton b, int row, int col)
    {
        var loc = Locate(b);
        if (loc == null) return false;
        var (p, pg) = loc.Value;
        var other = GridLayout.ButtonAt(pg, row, col);
        bool ok;
        if (other != null && other != b) ok = GridLayout.Swap(pg, p.Columns, p.Rows, b, other);
        else if (other == b) return true;
        else ok = GridLayout.Move(pg, p.Columns, p.Rows, b, row, col);
        if (!ok) { Ui.Toast("That button does not fit there.", ToastKind.Warning); return false; }
        SaveSoon();
        LayoutChanged?.Invoke();
        return true;
    }

    public bool MoveToProfile(DeckButton b, Profile target, int pageIndex = 0)
    {
        var loc = Locate(b);
        if (loc == null) return false;
        var page = target.Pages[Math.Clamp(pageIndex, 0, target.Pages.Count - 1)];
        if (page == loc.Value.Page) return true;
        if (!GridLayout.Transfer(loc.Value.Page, page, target.Columns, target.Rows, b))
        {
            Ui.Toast($"No free slot on {target.Name}{(target.Pages.Count > 1 ? $" page {pageIndex + 1}" : "")}.", ToastKind.Warning);
            return false;
        }
        SaveSoon();
        LayoutChanged?.Invoke();
        Ui.Toast($"Moved \"{b.Title}\" to {target.Name}", ToastKind.Success);
        return true;
    }

    public void SetEditMode(bool on)
    {
        if (EditMode == on) return;
        EditMode = on;
        EditModeChanged?.Invoke();
    }

    // ---- sound library ---------------------------------------------------------------------

    public SoundItem ImportSound(string path)
    {
        var ext = Path.GetExtension(path);
        if (!SoundFiles.IsSupported(ext)) throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a supported audio format.");
        AppPaths.Ensure();
        var name = Path.GetFileNameWithoutExtension(path);
        var file = SoundFiles.UniqueName(name, ext);
        File.Copy(path, Path.Combine(AppPaths.Sounds, file));
        var item = new SoundItem { Name = name, File = file };
        try { Soundboard.Preload(item); }
        catch { /* reported on first play */ }
        Config.Sounds.Add(item);
        SaveSoon();
        SoundsChanged?.Invoke();
        return item;
    }

    public int SoundReferences(SoundItem s) =>
        GridLayout.AllButtons(Config).Count(x => x.Button.SoundId == s.Id
            || x.Button.Action.Type == ActionTypes.Sound && x.Button.Action.Param == s.Id
            || x.Button.States.Any(st => st.Action.Type == ActionTypes.Sound && st.Action.Param == s.Id));

    public void DeleteSound(SoundItem s)
    {
        if (s.IsBuiltIn) return;
        Soundboard.StopAll(30);
        Config.Sounds.Remove(s);
        try { File.Delete(Path.Combine(AppPaths.Sounds, s.File)); } catch (Exception ex) { Log.Warn("Deleting sound file: " + ex.Message); }
        Soundboard.Invalidate(s.File);
        SaveSoon();
        SoundsChanged?.Invoke();
        QueueLive();
    }

    public void SoundEdited()
    {
        SaveSoon();
        SoundsChanged?.Invoke();
    }

    private void OnVoicesChanged()
    {
        _ui.BeginInvoke(() =>
        {
            _progressTimer.IsEnabled = Soundboard.AnyPlaying;
            QueueLive();
        });
    }

    private void OnProgressTick()
    {
        if (!Soundboard.AnyPlaying) { _progressTimer.Stop(); QueueLive(); return; }
        SoundProgress?.Invoke();
    }

    // ---- pressing buttons --------------------------------------------------------------------

    /// <summary>The title/subtitle/icon/accent/action the button currently has (Toggle/MultiState aware).</summary>
    public static (string Title, string Subtitle, IconSpec Icon, string? Accent, ActionSpec Action) Effective(DeckButton b)
    {
        if (b.Mode is ButtonModes.Toggle or ButtonModes.MultiState && b.CurrentState > 0 && b.CurrentState <= b.States.Count)
        {
            var s = b.States[b.CurrentState - 1];
            return (s.Title.Length > 0 ? s.Title : b.Title, s.Subtitle, s.Icon is { IsEmpty: false } ? s.Icon : b.Icon, s.Accent ?? b.Accent, s.Action);
        }
        return (b.Title, b.Subtitle, b.Icon, b.Accent, b.Action);
    }

    public static int StateCount(DeckButton b) => b.Mode switch
    {
        ButtonModes.Toggle => 1 + Math.Min(1, b.States.Count),
        ButtonModes.MultiState => 1 + b.States.Count,
        _ => 1,
    };

    public async Task<ActionResult> PressAsync(DeckButton b, ActionContext ctx)
    {
        ctx = ctx with { Button = b };
        var n = StateCount(b);
        var nextState = n > 1 ? (b.CurrentState + 1) % n : 0;
        ActionSpec action;
        if (n > 1)
        {
            // The state you see is the state you are in: pressing enters the next state and runs its action.
            action = nextState == 0 ? b.Action : b.States[nextState - 1].Action;
        }
        else action = b.Action;

        if (action.Type != ActionTypes.Sound && !_running.Add(b.Id)) return ActionResult.Cancelled; // still running
        try
        {
            if (b.Confirm && !ctx.PreConfirmed)
            {
                var (title, _, _, _, _) = Effective(b);
                var text = string.IsNullOrWhiteSpace(b.ConfirmText) ? $"Run \"{(title.Length > 0 ? title : "this button")}\"?" : b.ConfirmText;
                if (!await Ui.ConfirmAsync(ctx, text, ActionTypes.Label(action.Type), "CONFIRM", danger: false))
                    return ActionResult.Cancelled;
                ctx = ctx with { PreConfirmed = action.Type != ActionTypes.Power || !PowerCommands.IsDestructive(action.Command) ? true : ctx.PreConfirmed };
            }

            if (!string.IsNullOrEmpty(b.SoundId))
            {
                var fb = Config.Sounds.FirstOrDefault(s => s.Id == b.SoundId);
                if (fb != null)
                    _ = Task.Run(() =>
                    {
                        try { Soundboard.Play(fb, "fb-" + b.Id, 1f, false, 0, 0, "overlap"); }
                        catch (Exception ex) { Log.Warn("Feedback sound: " + ex.Message); }
                    });
            }

            ActionResult result;
            if (b.Mode == ButtonModes.Momentary) result = await Actions.HoldAsync(action, ctx, down: true);
            else result = await Actions.ExecuteAsync(action, ctx);

            if (result.Ok && n > 1 && result != ActionResult.Cancelled)
            {
                b.CurrentState = nextState;
                SaveSoon();
                LayoutChanged?.Invoke();
            }
            Report(b, result);
            return result;
        }
        finally { _running.Remove(b.Id); }
    }

    public async Task ReleaseAsync(DeckButton b, ActionContext ctx)
    {
        if (b.Mode != ButtonModes.Momentary) return;
        var r = await Actions.HoldAsync(b.Action, ctx with { Button = b }, down: false);
        if (!r.Ok) Report(b, r);
    }

    private void Report(DeckButton b, ActionResult r)
    {
        if (r == ActionResult.Cancelled) return;
        ButtonFeedback?.Invoke(b.Id, r.Ok);
        if (!r.Ok && r.Message != null) Ui.Toast(r.Message, ToastKind.Error);
        else if (r.Toast && r.Message != null && Config.Settings.ShowToasts) Ui.Toast(r.Message, ToastKind.Success);
        QueueLive();
    }

    public async Task<ActionResult> RunAsync(ActionSpec a, ActionContext ctx)
    {
        var r = await Actions.ExecuteAsync(a, ctx);
        if (!r.Ok && r.Message != null) Ui.Toast(r.Message, ToastKind.Error);
        else if (r.Toast && r.Message != null && Config.Settings.ShowToasts) Ui.Toast(r.Message, ToastKind.Success);
        QueueLive();
        return r;
    }

    public void RequestRelayout() => LayoutChanged?.Invoke();
    public void RequestToggleSurface() => ToggleSurfaceRequested?.Invoke();
    public void RequestShowMain() => ShowMainRequested?.Invoke();

    // ---- live state ------------------------------------------------------------------------

    public void QueueLive()
    {
        if (_disposed || Interlocked.Exchange(ref _liveQueued, 1) == 1) return;
        _ui.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _liveQueued, 0);
            LiveChanged?.Invoke();
        }, DispatcherPriority.Background);
    }

    // ---- global hotkeys ----------------------------------------------------------------------

    public void RegisterHotkeys()
    {
        Hotkeys.UnregisterAll();
        foreach (var h in Config.Hotkeys.Where(h => h.Enabled && h.Chord.Length > 0))
        {
            var cmd = h.Command;
            Hotkeys.Register(h.Chord, DescribeCommand(cmd), () => RunGlobalCommand(cmd));
        }
        foreach (var p in Config.Profiles.Where(p => p.Hotkey.Length > 0))
        {
            var id = p.Id;
            Hotkeys.Register(p.Hotkey, "Profile: " + p.Name, () => RunGlobalCommand(GlobalCommands.ProfilePrefix + id));
        }
        foreach (var (p, _, b) in GridLayout.AllButtons(Config).Where(x => x.Button.Hotkey.Length > 0))
        {
            var id = b.Id;
            Hotkeys.Register(b.Hotkey, $"Button: {p.Name} › {(b.Title.Length > 0 ? b.Title : "(untitled)")}", () => RunGlobalCommand(GlobalCommands.ButtonPrefix + id));
        }
    }

    public string DescribeCommand(string cmd)
    {
        if (cmd.StartsWith(GlobalCommands.ProfilePrefix))
        {
            var p = Config.Profiles.FirstOrDefault(x => x.Id == cmd[GlobalCommands.ProfilePrefix.Length..]);
            return p != null ? "Profile: " + p.Name : "Profile (deleted)";
        }
        if (cmd.StartsWith(GlobalCommands.ButtonPrefix))
        {
            var b = FindButton(cmd[GlobalCommands.ButtonPrefix.Length..]);
            return b != null ? "Button: " + b.Title : "Button (deleted)";
        }
        return GlobalCommands.Fixed.FirstOrDefault(f => f.Id == cmd).Label ?? cmd;
    }

    public void RunGlobalCommand(string cmd)
    {
        var ctx = new ActionContext(ActionSource.Hotkey);
        switch (cmd)
        {
            case GlobalCommands.ToggleMic:
                _ = RunAsync(ActionSpec.Of(ActionTypes.System, SysCommands.MicToggle), ctx);
                return;
            case GlobalCommands.ToggleSurface: RequestToggleSurface(); return;
            case GlobalCommands.ShowMain: RequestShowMain(); return;
            case GlobalCommands.StopSounds: Soundboard.StopAll(Math.Max(60, Config.Settings.DefaultFadeMs)); return;
            case GlobalCommands.NextProfile: NextProfile(); return;
            case GlobalCommands.ToggleAutoProfile:
                SetAutoProfile(!Config.Settings.AutoProfile);
                Ui.Toast(Config.Settings.AutoProfile ? "Auto profile on" : "Auto profile off", ToastKind.Success);
                return;
        }
        if (cmd.StartsWith(GlobalCommands.ProfilePrefix))
        {
            var p = Config.Profiles.FirstOrDefault(x => x.Id == cmd[GlobalCommands.ProfilePrefix.Length..]);
            if (p != null) SwitchProfile(p, announce: true);
        }
        else if (cmd.StartsWith(GlobalCommands.ButtonPrefix))
        {
            var b = FindButton(cmd[GlobalCommands.ButtonPrefix.Length..]);
            if (b != null) _ = PressThenRelease(b, ctx);
        }
    }

    private async Task PressThenRelease(DeckButton b, ActionContext ctx)
    {
        await PressAsync(b, ctx);
        await ReleaseAsync(b, ctx);
    }

    // ---- auto profile ------------------------------------------------------------------------

    public static bool RuleMatches(AutoProfileRule r, string process)
    {
        if (!r.Enabled || string.IsNullOrWhiteSpace(r.Process) || string.IsNullOrEmpty(process)) return false;
        var want = r.Process.Trim();
        if (!want.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) want += ".exe";
        return string.Equals(Path.GetFileName(want), process, StringComparison.OrdinalIgnoreCase);
    }

    private void OnForegroundProcess(string process)
    {
        if (!Config.Settings.AutoProfile || EditMode) return;
        var rule = Config.AutoProfileRules.FirstOrDefault(r => RuleMatches(r, process));
        Profile? target = null;
        if (rule != null) target = Config.Profiles.FirstOrDefault(p => p.Id == rule.ProfileId);
        else if (Config.Settings.AutoProfileFallback.Length > 0)
            target = Config.Profiles.FirstOrDefault(p => p.Id == Config.Settings.AutoProfileFallback);
        if (target != null && target.Id != Config.ActiveProfileId)
            SwitchProfile(target, announce: true, reason: "auto: " + process);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveTimer.Stop();
        _progressTimer.Stop();
        try { SaveNow(); } catch { }
        Hotkeys.Dispose();
        Foreground.Dispose();
        Obs.Dispose();
        Camera.Dispose();
        Soundboard.Dispose();
        Meter.Dispose();
        Audio.Dispose();
        MessageWindow.Dispose();
        SleepBlocker.Set(false);
    }

    private sealed class NullUi : IUiHost
    {
        public Task<bool> ConfirmAsync(ActionContext ctx, string title, string message, string confirmLabel, bool danger) => Task.FromResult(false);
        public Task<string?> ResolveMissingPathAsync(ActionContext ctx, string title, string message, string path, bool folder) => Task.FromResult<string?>(null);
        public void Toast(string text, ToastKind kind = ToastKind.Info) => Log.Info("toast: " + text);
    }
}
