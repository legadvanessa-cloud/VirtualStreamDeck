using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using VirtualStreamDeck.Core;
using Windows.Devices.Radios;

namespace VirtualStreamDeck.Services;

public enum ActionSource { Grid, Surface, Hotkey, Palette, Remote, Tray, Test }

public sealed record ActionContext(ActionSource Source, Window? Owner = null, DeckButton? Button = null)
{
    /// <summary>The user is looking at the remote page, which already asked for confirmation.</summary>
    public bool PreConfirmed { get; init; }
}

public sealed record ActionResult(bool Ok, string? Message = null, bool Toast = false, bool WasCancelled = false)
{
    public static readonly ActionResult Done = new(true);
    public static ActionResult Info(string msg) => new(true, msg, true);
    public static ActionResult Fail(string msg) => new(false, msg, true);
    /// <summary>The user declined a confirmation or dialog: not an error, nothing ran.</summary>
    public static readonly ActionResult Cancelled = new(true, null, false, true);
}

public enum ToastKind { Info, Success, Warning, Error }

/// <summary>What the action engine needs from the UI (dialogs and notifications).</summary>
public interface IUiHost
{
    Task<bool> ConfirmAsync(ActionContext ctx, string title, string message, string confirmLabel, bool danger);
    /// <summary>Shows "Unable to launch… [CHANGE PATH] [CANCEL]"; returns the new path, or null.</summary>
    Task<string?> ResolveMissingPathAsync(ActionContext ctx, string title, string message, string path, bool folder);
    void Toast(string text, ToastKind kind = ToastKind.Info);
}

/// <summary>
/// ActionEngine: executes an <see cref="ActionSpec"/>. Every failure mode becomes an
/// <see cref="ActionResult"/> with a readable message - nothing here is allowed to throw into the UI.
/// </summary>
public sealed class ActionEngine
{
    private readonly Deck _deck;
    private readonly InputSender _input = new();
    private readonly MacroEngine _macros;

    public ActionEngine(Deck deck)
    {
        _deck = deck;
        _macros = new MacroEngine(_input, (path, args) => LaunchCore(path, args, "", false));
    }

    public MacroEngine Macros => _macros;
    public InputSender Input => _input;
    private IUiHost Ui => _deck.Ui;

    public async Task<ActionResult> ExecuteAsync(ActionSpec a, ActionContext ctx)
    {
        try
        {
            return a.Type switch
            {
                ActionTypes.None => ActionResult.Done,
                ActionTypes.LaunchApp => await LaunchAsync(a, ctx),
                ActionTypes.OpenUrl => OpenUrl(a),
                ActionTypes.OpenPath => await OpenPathAsync(a, ctx),
                ActionTypes.Hotkey => await HotkeyAsync(a, ctx),
                ActionTypes.Macro => await MacroAsync(a, ctx),
                ActionTypes.TypeText => await TypeTextAsync(a, ctx),
                ActionTypes.Power => await PowerAsync(a, ctx),
                ActionTypes.Media => await MediaAsync(a),
                ActionTypes.System => await SystemAsync(a, ctx),
                ActionTypes.Sound => await SoundAsync(a, ctx),
                ActionTypes.Deck => DeckCommand(a),
                ActionTypes.Obs => ActionResult.Info(await _deck.Obs.ExecuteAsync(a.Command, a.Param, a.Param2)),
                ActionTypes.Discord => await DiscordAsync(a, ctx),
                ActionTypes.RunCommand => RunCommand(a),
                _ => ActionResult.Fail($"This button uses an action type this version does not support ('{a.Type}')."),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or NotSupportedException or ArgumentException)
        {
            return ActionResult.Fail(ex.Message);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Log.Error("Action COM failure", ex);
            return ActionResult.Fail("Windows reported an error: " + ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error($"Action {a.Type}/{a.Command} failed", ex);
            return ActionResult.Fail("Unexpected error: " + ex.Message);
        }
    }

    // ---- targeting input at the right window --------------------------------------------------

    private async Task PrepareForInputAsync(ActionContext ctx)
    {
        if (ctx.Source == ActionSource.Hotkey) await InputSender.WaitForModifiersReleasedAsync();
        // Pressing a button in the main window focuses it; give focus back to the app the user was
        // using so the shortcut lands there. (The control surface never takes focus in the first place.)
        await _deck.Foreground.RestoreExternalFocusAsync();
    }

    // ---- launch / open --------------------------------------------------------------------------

    private static string Expand(string s) => Environment.ExpandEnvironmentVariables(s.Trim().Trim('"'));

    private async Task<ActionResult> LaunchAsync(ActionSpec a, ActionContext ctx)
    {
        var target = Expand(a.Target);
        if (target.Length == 0) return ActionResult.Fail("No application is configured for this button.");

        if (Path.IsPathRooted(target) && !File.Exists(target))
        {
            var fixedPath = await Ui.ResolveMissingPathAsync(ctx, "Unable to launch application.",
                "The configured executable no longer exists:", target, folder: false);
            if (fixedPath == null) return ActionResult.Cancelled;
            a.Target = fixedPath;
            if (string.Equals(Path.GetDirectoryName(target), a.WorkingDirectory, StringComparison.OrdinalIgnoreCase)) a.WorkingDirectory = "";
            _deck.SaveSoon();
            target = fixedPath;
        }

        var exeName = Path.GetFileName(target);
        if (a.FocusIfRunning && !a.RunAsAdmin && exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !exeName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
        {
            var hwnd = WindowFocus.FindMainWindow(exeName);
            if (hwnd != IntPtr.Zero && WindowFocus.Activate(hwnd)) return ActionResult.Done;
        }

        try
        {
            LaunchCore(target, a.Arguments, a.WorkingDirectory, a.RunAsAdmin);
            return ActionResult.Done;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return ActionResult.Info("Launch cancelled at the administrator prompt.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3)
        {
            var fixedPath = await Ui.ResolveMissingPathAsync(ctx, "Unable to launch application.",
                "Windows could not find the program:", target, folder: false);
            if (fixedPath == null) return ActionResult.Cancelled;
            a.Target = fixedPath;
            _deck.SaveSoon();
            LaunchCore(fixedPath, a.Arguments, "", a.RunAsAdmin);
            return ActionResult.Done;
        }
    }

    internal static void LaunchCore(string target, string args, string workingDir, bool admin)
    {
        target = Expand(target);
        var psi = new ProcessStartInfo(target) { UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(args)) psi.Arguments = Environment.ExpandEnvironmentVariables(args);
        // Many apps (OBS among them) only start correctly from their own folder.
        var wd = string.IsNullOrWhiteSpace(workingDir) ? (Path.IsPathRooted(target) ? Path.GetDirectoryName(target) : null) : Expand(workingDir);
        if (!string.IsNullOrEmpty(wd) && Directory.Exists(wd)) psi.WorkingDirectory = wd;
        if (admin) psi.Verb = "runas";
        Process.Start(psi)?.Dispose();
    }

    private static ActionResult OpenUrl(ActionSpec a)
    {
        var url = a.Target.Trim();
        if (url.Length == 0) return ActionResult.Fail("No URL is configured for this button.");
        if (!url.Contains(':')) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return ActionResult.Fail($"'{a.Target}' is not a valid URL.");
        try
        {
            Shell.Open(uri.OriginalString);
            return ActionResult.Done;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 1155 or 2 or 1223)
        {
            return ActionResult.Fail($"No application on this PC opens '{uri.Scheme}:' links.");
        }
    }

    private async Task<ActionResult> OpenPathAsync(ActionSpec a, ActionContext ctx)
    {
        var path = Expand(a.Target);
        if (path.Length == 0) return ActionResult.Fail("No file or folder is configured.");
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            var isFolder = !Path.HasExtension(path);
            var np = await Ui.ResolveMissingPathAsync(ctx, "Unable to open.", "The configured path no longer exists:", path, isFolder);
            if (np == null) return ActionResult.Cancelled;
            a.Target = np;
            _deck.SaveSoon();
            path = np;
        }
        Shell.Open(path);
        return ActionResult.Done;
    }

    // ---- keyboard ------------------------------------------------------------------------------

    private async Task<ActionResult> HotkeyAsync(ActionSpec a, ActionContext ctx)
    {
        if (!KeyChord.TryParse(a.Keys, out var chord)) return ActionResult.Fail($"'{a.Keys}' is not a valid key combination.");
        await PrepareForInputAsync(ctx);
        _input.Tap(chord);
        return ActionResult.Done;
    }

    /// <summary>Momentary buttons: key goes down on press and up on release (push-to-talk etc.).</summary>
    public async Task<ActionResult> HoldAsync(ActionSpec a, ActionContext ctx, bool down)
    {
        if (a.Type != ActionTypes.Hotkey || !KeyChord.TryParse(a.Keys, out var chord))
            return down ? await ExecuteAsync(a, ctx) : ActionResult.Done;
        try
        {
            if (down) { await PrepareForInputAsync(ctx); _input.Down(chord); }
            else _input.Up(chord);
            return ActionResult.Done;
        }
        catch (InvalidOperationException ex) { return ActionResult.Fail(ex.Message); }
    }

    private async Task<ActionResult> MacroAsync(ActionSpec a, ActionContext ctx)
    {
        if (a.Steps.Count == 0) return ActionResult.Fail("This macro has no steps yet.");
        await PrepareForInputAsync(ctx);
        await _macros.RunAsync(a.Steps);
        return ActionResult.Done;
    }

    private async Task<ActionResult> TypeTextAsync(ActionSpec a, ActionContext ctx)
    {
        if (string.IsNullOrEmpty(a.Text)) return ActionResult.Fail("No text is configured.");
        await PrepareForInputAsync(ctx);
        _input.Type(a.Text);
        return ActionResult.Done;
    }

    // ---- power ---------------------------------------------------------------------------------

    private async Task<ActionResult> PowerAsync(ActionSpec a, ActionContext ctx)
    {
        if (PowerCommands.IsDestructive(a.Command) && !ctx.PreConfirmed)
        {
            var (title, label) = a.Command switch
            {
                PowerCommands.Shutdown => ("Are you sure you want to shut down Windows?", "SHUT DOWN"),
                PowerCommands.Restart => ("Are you sure you want to restart Windows?", "RESTART"),
                PowerCommands.SignOut => ("Are you sure you want to sign out?", "SIGN OUT"),
                PowerCommands.Hibernate => ("Hibernate this PC now?", "HIBERNATE"),
                _ => ("Put this PC to sleep now?", "SLEEP"),
            };
            var msg = a.Command is PowerCommands.Shutdown or PowerCommands.Restart or PowerCommands.SignOut
                ? "Unsaved work in open applications may be lost."
                : "Open applications stay as they are.";
            if (!await Ui.ConfirmAsync(ctx, title, msg, label, danger: true)) return ActionResult.Cancelled;
        }
        PowerControl.Execute(a.Command);
        return ActionResult.Done;
    }

    // ---- media / audio -------------------------------------------------------------------------

    private async Task<ActionResult> MediaAsync(ActionSpec a)
    {
        var step = a.Value > 0 ? a.Value : _deck.Config.Settings.VolumeStep;
        switch (a.Command)
        {
            case MediaCmds.VolumeUp: return ActionResult.Info($"Volume {_deck.Audio.AdjustSpeakerVolume(step)}%");
            case MediaCmds.VolumeDown: return ActionResult.Info($"Volume {_deck.Audio.AdjustSpeakerVolume(-step)}%");
            case MediaCmds.Mute: return ActionResult.Info(_deck.Audio.ToggleSpeakerMute() ? "Speakers muted" : "Speakers unmuted");
        }
        await _deck.Media.SendAsync(a.Command, _input);
        return ActionResult.Done;
    }

    private async Task<ActionResult> SystemAsync(ActionSpec a, ActionContext ctx)
    {
        var audio = _deck.Audio;
        var step = a.Value > 0 ? a.Value : _deck.Config.Settings.VolumeStep;
        switch (a.Command)
        {
            case SysCommands.MicToggle:
                return ActionResult.Info(audio.ToggleMicMute() ? "Microphone muted" : "Microphone on air");
            case SysCommands.MicMute: audio.SetMicMute(true); return ActionResult.Info("Microphone muted");
            case SysCommands.MicUnmute: audio.SetMicMute(false); return ActionResult.Info("Microphone on air");
            case SysCommands.MicVolumeUp: return ActionResult.Info($"Microphone volume {_deck.AdjustMicVolume(step)}%");
            case SysCommands.MicVolumeDown: return ActionResult.Info($"Microphone volume {_deck.AdjustMicVolume(-step)}%");
            case SysCommands.MicVolumeSet: return ActionResult.Info($"Microphone volume {_deck.SetMicVolume(a.Value)}%");
            case SysCommands.MicMonitorToggle:
                return ActionResult.Info(_deck.ToggleMonitor() ? $"Monitoring on — {_deck.Meter.MonitorDeviceName}" : "Monitoring off");
            case SysCommands.MicCycle: return ActionResult.Info("Microphone: " + audio.CycleInput());
            case SysCommands.MasterMuteToggle: return ActionResult.Info(audio.ToggleSpeakerMute() ? "Speakers muted" : "Speakers unmuted");
            case SysCommands.MasterVolumeUp: return ActionResult.Info($"Volume {audio.AdjustSpeakerVolume(step)}%");
            case SysCommands.MasterVolumeDown: return ActionResult.Info($"Volume {audio.AdjustSpeakerVolume(-step)}%");
            case SysCommands.MasterVolumeSet:
                var v = audio.SetSpeakerVolume(a.Value);
                return ActionResult.Info(v == 0 ? "Speakers muted" : $"Volume {v}%");
            case SysCommands.OutputCycle: return ActionResult.Info("Output: " + audio.CycleOutput());
            case SysCommands.OutputSet:
            case SysCommands.InputSet:
                if (string.IsNullOrEmpty(a.Param)) return ActionResult.Fail("No audio device is selected for this button.");
                return ActionResult.Info("Default device: " + audio.SetDefaultDevice(a.Param));
            case SysCommands.CameraStatus:
                var cs = _deck.Camera.State;
                CameraService.OpenPrivacySettings();
                return ActionResult.Info(!cs.AnyCamera ? "No camera detected" :
                    cs.InUse ? $"Camera in use by {string.Join(", ", cs.UsedBy)}" : "Camera idle - privacy settings opened");
            case SysCommands.CameraDeviceToggle:
                var st = _deck.Camera.State;
                var enable = st.AllDisabled;
                if (!ctx.PreConfirmed && !await Ui.ConfirmAsync(ctx, enable ? "Enable the camera?" : "Disable the camera?",
                        (enable ? "Re-enables" : "Disables") + " every camera device in Windows Device Manager. Windows will ask for administrator permission.",
                        enable ? "ENABLE" : "DISABLE", danger: !enable))
                    return ActionResult.Cancelled;
                var done = await _deck.Camera.SetDevicesEnabledAsync(enable);
                return done ? ActionResult.Info(enable ? "Camera enabled" : "Camera disabled") : ActionResult.Info("Camera change cancelled at the administrator prompt.");
            case SysCommands.BrightnessUp:
            case SysCommands.BrightnessDown:
            case SysCommands.BrightnessSet:
                var delta = a.Command == SysCommands.BrightnessUp ? step * 2 : -step * 2;
                var res = await Task.Run(() => Brightness.Adjust(delta, a.Command == SysCommands.BrightnessSet ? a.Value : null));
                return res.Supported ? ActionResult.Info($"Brightness {res.Percent}%") : ActionResult.Fail(res.Detail);
            case SysCommands.WifiToggle:
                return ActionResult.Info((await _deck.Radios.ToggleAsync(RadioKind.WiFi)) ? "Wi-Fi on" : "Wi-Fi off");
            case SysCommands.BluetoothToggle:
                return ActionResult.Info((await _deck.Radios.ToggleAsync(RadioKind.Bluetooth)) ? "Bluetooth on" : "Bluetooth off");
            case SysCommands.NightLight:
                Shell.Open("ms-settings:nightlight");
                return ActionResult.Info("Windows has no public Night light API - settings opened");
            case SysCommands.WindowsSettings:
                Shell.Open("ms-settings:");
                return ActionResult.Done;
            case SysCommands.TaskManager:
                Shell.Open("taskmgr.exe");
                return ActionResult.Done;
        }
        return ActionResult.Fail($"Unknown system command '{a.Command}'.");
    }

    private async Task<ActionResult> SoundAsync(ActionSpec a, ActionContext ctx)
    {
        var item = _deck.Config.Sounds.FirstOrDefault(s => s.Id == a.Param);
        if (item == null) return ActionResult.Fail("The sound assigned to this button is no longer in the library.");
        var retrigger = string.IsNullOrEmpty(a.Retrigger) ? _deck.Config.Settings.SoundOverlap : a.Retrigger;
        var tag = ctx.Button?.Id ?? item.Id;
        try
        {
            await Task.Run(() => _deck.Soundboard.Play(item, tag, a.Volume / 100f, a.Loop, a.FadeInMs, a.FadeOutMs, retrigger));
            return ActionResult.Done;
        }
        catch (FileNotFoundException ex)
        {
            return ActionResult.Fail($"Sound file was deleted or moved:\n{ex.FileName}");
        }
    }

    // ---- deck / integrations -------------------------------------------------------------------

    private ActionResult DeckCommand(ActionSpec a)
    {
        switch (a.Command)
        {
            case DeckCommands.SwitchProfile:
                var p = _deck.Config.Profiles.FirstOrDefault(x => x.Id == a.Param);
                if (p == null) return ActionResult.Fail("The target profile was deleted.");
                _deck.SwitchProfile(p, announce: true);
                return ActionResult.Done;
            case DeckCommands.NextProfile: _deck.NextProfile(); return ActionResult.Done;
            case DeckCommands.NextPage: _deck.ChangePage(+1); return ActionResult.Done;
            case DeckCommands.PreviousPage: _deck.ChangePage(-1); return ActionResult.Done;
            case DeckCommands.GoToPage: _deck.SetPage(a.Value - 1); return ActionResult.Done;
            case DeckCommands.StopAllSounds: _deck.Soundboard.StopAll(Math.Max(60, _deck.Config.Settings.DefaultFadeMs)); return ActionResult.Done;
            case DeckCommands.ToggleSurface: _deck.RequestToggleSurface(); return ActionResult.Done;
            case DeckCommands.ToggleAutoProfile:
                _deck.SetAutoProfile(!_deck.Config.Settings.AutoProfile);
                return ActionResult.Info(_deck.Config.Settings.AutoProfile ? "Auto profile on" : "Auto profile off");
            case DeckCommands.OpenMainWindow: _deck.RequestShowMain(); return ActionResult.Done;
        }
        return ActionResult.Fail($"Unknown deck command '{a.Command}'.");
    }

    private async Task<ActionResult> DiscordAsync(ActionSpec a, ActionContext ctx)
    {
        switch (a.Command)
        {
            case DiscordCommands.Launch:
            case DiscordCommands.Focus:
                var hwnd = WindowFocus.FindMainWindow("Discord.exe");
                if (hwnd != IntPtr.Zero) { WindowFocus.Activate(hwnd); return ActionResult.Done; }
                if (a.Command == DiscordCommands.Focus) return ActionResult.Fail("Discord is not running.");
                var updater = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord", "Update.exe");
                if (File.Exists(updater)) { LaunchCore(updater, "--processStart Discord.exe", "", false); return ActionResult.Info("Starting Discord…"); }
                try { Shell.Open("discord://"); return ActionResult.Info("Starting Discord…"); }
                catch (Win32Exception) { return ActionResult.Fail("Discord does not appear to be installed for this user."); }
            case DiscordCommands.MuteKeybind:
            case DiscordCommands.DeafenKeybind:
                // Discord offers no local control API without a registered cloud application, so this
                // sends the user's own Discord keybind - and says exactly that instead of claiming a state.
                var keys = a.Command == DiscordCommands.MuteKeybind ? _deck.Config.Settings.DiscordMuteKeys : _deck.Config.Settings.DiscordDeafenKeys;
                if (!KeyChord.TryParse(keys, out var chord)) return ActionResult.Fail("Set your Discord keybind in Settings > Integrations.");
                await PrepareForInputAsync(ctx);
                _input.Tap(chord);
                return ActionResult.Info($"Sent {chord} to Discord");
        }
        return ActionResult.Fail($"Unknown Discord command '{a.Command}'.");
    }

    private static ActionResult RunCommand(ActionSpec a)
    {
        if (string.IsNullOrWhiteSpace(a.Text)) return ActionResult.Fail("No command is configured.");
        ProcessStartInfo psi;
        if (a.Shell == "cmd")
        {
            psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = !a.ShowWindow };
            psi.ArgumentList.Add(a.ShowWindow ? "/k" : "/c");
            psi.ArgumentList.Add(a.Text);
        }
        else
        {
            psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = !a.ShowWindow };
            psi.ArgumentList.Add("-NoProfile");
            if (a.ShowWindow) psi.ArgumentList.Add("-NoExit");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(a.Text)));
        }
        if (!string.IsNullOrWhiteSpace(a.WorkingDirectory) && Directory.Exists(Expand(a.WorkingDirectory))) psi.WorkingDirectory = Expand(a.WorkingDirectory);
        if (a.ShowWindow) psi.UseShellExecute = true;
        if (psi.UseShellExecute)
        {
            // ShellExecute does not take an ArgumentList; rebuild a quoted command line.
            psi.Arguments = string.Join(" ", psi.ArgumentList.Select(x => x.Contains(' ') ? "\"" + x.Replace("\"", "\\\"") + "\"" : x));
            psi.ArgumentList.Clear();
        }
        Process.Start(psi)?.Dispose();
        return ActionResult.Done;
    }
}

/// <summary>MacroEngine: runs steps in order with real delays; never overlaps two runs of input.</summary>
public sealed class MacroEngine
{
    private readonly InputSender _input;
    private readonly Action<string, string> _launch;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource _cts = new();
    public const int MaxDelayMs = 60_000;

    public MacroEngine(InputSender input, Action<string, string> launch)
    {
        _input = input;
        _launch = launch;
    }

    public bool Running => _gate.CurrentCount == 0;

    public void CancelAll()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
    }

    public async Task RunAsync(IReadOnlyList<MacroStep> steps)
    {
        var ct = _cts.Token;
        await _gate.WaitAsync(ct);
        var held = new List<KeyChord>();
        try
        {
            foreach (var s in steps)
            {
                ct.ThrowIfCancellationRequested();
                switch (s.Type)
                {
                    case MacroStepTypes.Combo:
                    case MacroStepTypes.Key:
                        _input.Tap(Chord(s));
                        await Task.Delay(15, ct); // let the target process the key before the next step
                        break;
                    case MacroStepTypes.KeyDown:
                        var c = Chord(s);
                        _input.Down(c);
                        held.Add(c);
                        break;
                    case MacroStepTypes.KeyUp:
                        var u = Chord(s);
                        _input.Up(u);
                        held.Remove(u);
                        break;
                    case MacroStepTypes.Text:
                        _input.Type(s.Value);
                        break;
                    case MacroStepTypes.Delay:
                        await Task.Delay(Math.Clamp(s.Number, 0, MaxDelayMs), ct);
                        break;
                    case MacroStepTypes.Move:
                        _input.MoveTo(s.X, s.Y);
                        break;
                    case MacroStepTypes.Click:
                        if (s.AtPosition) { _input.MoveTo(s.X, s.Y); await Task.Delay(10, ct); }
                        _input.Click(s.Button);
                        break;
                    case MacroStepTypes.Launch:
                        _launch(s.Value, "");
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown macro step '{s.Type}'.");
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Never leave a key stuck down, whatever happened.
            foreach (var c in held) { try { _input.Up(c); } catch { } }
            _gate.Release();
        }
    }

    private static KeyChord Chord(MacroStep s) =>
        KeyChord.TryParse(s.Value, out var c) ? c : throw new InvalidOperationException($"Macro step has an invalid key: '{s.Value}'.");
}
