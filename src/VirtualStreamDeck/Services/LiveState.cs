using VirtualStreamDeck.Core;
using Windows.Devices.Radios;

namespace VirtualStreamDeck.Services;

public enum LiveTone { None, Active, Alert, Dim }

/// <summary>What a button shows on top of its configuration, derived from real system state only.</summary>
public sealed record LiveInfo(string? State, LiveTone Tone, IconSpec? Icon, string? Tooltip, double? Progress = null)
{
    public static readonly LiveInfo Empty = new(null, LiveTone.None, null, null);
}

public static class LiveState
{
    private static IconSpec G(string code) => IconSpec.Glyph(code);

    public static LiveInfo Resolve(Deck deck, DeckButton b)
    {
        var (_, _, _, _, a) = Deck.Effective(b);
        // Multi-state buttons display their own configured state, so the live read is only used when
        // the button runs a single stateful action.
        if (Deck.StateCount(b) > 1) a = b.Action;
        var info = ResolveAction(deck, a, b);
        if (!b.ShowLiveState && info.State != null) info = info with { State = null };
        return info;
    }

    public static LiveInfo ResolveAction(Deck deck, ActionSpec a, DeckButton? b)
    {
        switch (a.Type)
        {
            case ActionTypes.System: return SystemState(deck, a);
            case ActionTypes.Media: return MediaState(deck, a);
            case ActionTypes.Obs: return ObsState(deck, a);
            case ActionTypes.Sound:
                var tag = b?.Id ?? a.Param;
                if (deck.Soundboard.IsPlaying(tag))
                    return new LiveInfo(null, LiveTone.Active, null, "Playing", deck.Soundboard.Progress(tag) ?? -1);
                var item = deck.Config.Sounds.FirstOrDefault(s => s.Id == a.Param);
                if (item == null) return new LiveInfo("NO SOUND", LiveTone.Dim, null, "The assigned sound was removed from the library.");
                if (!item.IsBuiltIn && !File.Exists(SoundboardEngine.ResolvePath(item)))
                    return new LiveInfo("MISSING", LiveTone.Alert, null, "Sound file not found: " + item.File);
                return new LiveInfo(null, LiveTone.None, null, item.Name);
            case ActionTypes.Deck:
                return a.Command switch
                {
                    DeckCommands.ToggleAutoProfile => deck.Config.Settings.AutoProfile
                        ? new LiveInfo("ON", LiveTone.Active, null, "Auto profile switching is on")
                        : new LiveInfo("OFF", LiveTone.None, null, "Auto profile switching is off"),
                    DeckCommands.ToggleSurface => deck.SurfaceOpen ? new LiveInfo("OPEN", LiveTone.Active, null, null) : LiveInfo.Empty,
                    DeckCommands.SwitchProfile => deck.Config.ActiveProfileId == a.Param ? new LiveInfo(null, LiveTone.Active, null, "Current profile") : LiveInfo.Empty,
                    DeckCommands.StopAllSounds => deck.Soundboard.AnyPlaying ? new LiveInfo(null, LiveTone.Active, null, "Sounds are playing") : LiveInfo.Empty,
                    _ => LiveInfo.Empty,
                };
            case ActionTypes.LaunchApp:
                var t = Environment.ExpandEnvironmentVariables(a.Target.Trim('"'));
                if (Path.IsPathRooted(t) && !File.Exists(t))
                    return new LiveInfo("NOT FOUND", LiveTone.Alert, null, "Executable not found: " + t);
                return new LiveInfo(null, LiveTone.None, null, t);
        }
        return LiveInfo.Empty;
    }

    private static LiveInfo SystemState(Deck deck, ActionSpec a)
    {
        var mic = deck.Audio.Microphone;
        var spk = deck.Audio.Speaker;
        switch (a.Command)
        {
            case SysCommands.MicToggle:
            case SysCommands.MicMute:
            case SysCommands.MicUnmute:
                if (!mic.Available) return new LiveInfo("NO DEVICE", LiveTone.Dim, G("E7BA"), "No recording device is connected");
                return mic.Muted
                    ? new LiveInfo("MUTED", LiveTone.Alert, G("EC54"), mic.DeviceName)
                    : new LiveInfo("ON AIR", LiveTone.Active, G("E720"), mic.DeviceName);
            case SysCommands.MicMonitorToggle:
                if (!mic.Available) return new LiveInfo("NO DEVICE", LiveTone.Dim, null, "No recording device is connected");
                return deck.Meter.Monitoring
                    ? new LiveInfo("ON", LiveTone.Active, null, $"Hearing {mic.DeviceName} on {deck.Meter.MonitorDeviceName}")
                    : new LiveInfo("OFF", LiveTone.None, null, "Hear your microphone through the monitor output (use headphones)");
            case SysCommands.MicCycle:
                return mic.Available ? new LiveInfo(Short(mic.DeviceName), LiveTone.None, null, mic.DeviceName) : new LiveInfo("NO DEVICE", LiveTone.Dim, null, null);
            case SysCommands.MicVolumeUp:
            case SysCommands.MicVolumeDown:
            case SysCommands.MicVolumeSet:
                if (!mic.Available) return new LiveInfo("NO MIC", LiveTone.Dim, null, "No recording device is connected");
                return new LiveInfo(mic.Muted ? "MUTED" : $"{mic.VolumePercent}%", mic.Muted ? LiveTone.Alert : LiveTone.None, null, mic.DeviceName);
            case SysCommands.MasterMuteToggle:
                if (!spk.Available) return new LiveInfo("NO OUTPUT", LiveTone.Dim, null, "No playback device");
                return spk.Muted
                    ? new LiveInfo("MUTED", LiveTone.Alert, G("E74F"), spk.DeviceName)
                    : new LiveInfo($"{spk.VolumePercent}%", LiveTone.None, G("E767"), spk.DeviceName);
            case SysCommands.MasterVolumeUp:
            case SysCommands.MasterVolumeDown:
                if (!spk.Available) return new LiveInfo("NO OUTPUT", LiveTone.Dim, null, "No playback device");
                return new LiveInfo(spk.Muted ? "MUTED" : $"{spk.VolumePercent}%", spk.Muted ? LiveTone.Alert : LiveTone.None, null, spk.DeviceName);
            case SysCommands.MasterVolumeSet:
                if (!spk.Available) return new LiveInfo("NO OUTPUT", LiveTone.Dim, null, null);
                var match = a.Value == 0 ? spk.Muted || spk.VolumePercent == 0 : !spk.Muted && spk.VolumePercent == a.Value;
                return new LiveInfo(null, match ? LiveTone.Active : LiveTone.None, null, $"{spk.DeviceName}: {(spk.Muted ? "muted" : spk.VolumePercent + "%")}");
            case SysCommands.OutputCycle:
                return new LiveInfo(spk.Available ? Short(spk.DeviceName) : "NO OUTPUT", LiveTone.None, null, spk.DeviceName);
            case SysCommands.OutputSet:
            case SysCommands.InputSet:
                var devs = deck.Audio.Devices(a.Command == SysCommands.OutputSet ? NAudio.CoreAudioApi.DataFlow.Render : NAudio.CoreAudioApi.DataFlow.Capture);
                var d = devs.FirstOrDefault(x => x.Id == a.Param);
                if (d == null) return new LiveInfo("DISCONNECTED", LiveTone.Dim, null, "The device is not connected");
                return new LiveInfo(null, d.IsDefault ? LiveTone.Active : LiveTone.None, null, d.Name);
            case SysCommands.CameraStatus:
                var c = deck.Camera.State;
                if (!c.AnyCamera) return new LiveInfo("NO CAMERA", LiveTone.Dim, null, "No camera device detected");
                if (c.AllDisabled) return new LiveInfo("DISABLED", LiveTone.Alert, null, "Camera disabled in Device Manager");
                if (!c.AccessAllowed) return new LiveInfo("BLOCKED", LiveTone.Alert, null, "Camera access is off in Windows privacy settings");
                return c.InUse
                    ? new LiveInfo("IN USE", LiveTone.Active, null, "Used by " + string.Join(", ", c.UsedBy))
                    : new LiveInfo("IDLE", LiveTone.None, null, string.Join(", ", c.Devices.Select(x => x.Name)));
            case SysCommands.CameraDeviceToggle:
                var cd = deck.Camera.State;
                if (!cd.AnyCamera) return new LiveInfo("NO CAMERA", LiveTone.Dim, null, null);
                return cd.AllDisabled
                    ? new LiveInfo("OFF", LiveTone.Alert, null, "Camera device disabled - press to enable (admin)")
                    : new LiveInfo(cd.InUse ? "IN USE" : "ON", LiveTone.Active, null, "Press to disable the camera device (admin)");
            case SysCommands.WifiToggle: return Radio(deck, RadioKind.WiFi, "NO WI-FI");
            case SysCommands.BluetoothToggle: return Radio(deck, RadioKind.Bluetooth, "NO BLUETOOTH");
            case SysCommands.NightLight:
                return new LiveInfo(null, LiveTone.None, null, "Windows has no public Night light API; this opens its settings page.");
        }
        return LiveInfo.Empty;
    }

    private static LiveInfo Radio(Deck deck, RadioKind kind, string none)
    {
        if (!deck.Radios.Initialised) return LiveInfo.Empty;
        var on = deck.Radios.IsOn(kind);
        if (on == null) return new LiveInfo(none, LiveTone.Dim, null, deck.Radios.AccessProblem);
        return on.Value ? new LiveInfo("ON", LiveTone.Active, null, deck.Radios.NameOf(kind)) : new LiveInfo("OFF", LiveTone.None, null, deck.Radios.NameOf(kind));
    }

    private static LiveInfo MediaState(Deck deck, ActionSpec a)
    {
        var spk = deck.Audio.Speaker;
        switch (a.Command)
        {
            case MediaCmds.PlayPause:
                var m = deck.Media;
                if (!m.HasSession || m.Playing == null) return LiveInfo.Empty;
                var track = string.IsNullOrEmpty(m.Artist) ? m.Title : $"{m.Title} — {m.Artist}";
                return m.Playing.Value
                    ? new LiveInfo(Short(m.Title), LiveTone.Active, G("E769"), track)
                    : new LiveInfo("PAUSED", LiveTone.None, G("E768"), track);
            case MediaCmds.Mute:
                return spk.Muted ? new LiveInfo("MUTED", LiveTone.Alert, G("E74F"), spk.DeviceName) : new LiveInfo($"{spk.VolumePercent}%", LiveTone.None, null, spk.DeviceName);
            case MediaCmds.VolumeUp:
            case MediaCmds.VolumeDown:
                return new LiveInfo(spk.Muted ? "MUTED" : $"{spk.VolumePercent}%", spk.Muted ? LiveTone.Alert : LiveTone.None, null, spk.DeviceName);
        }
        return LiveInfo.Empty;
    }

    private static LiveInfo ObsState(Deck deck, ActionSpec a)
    {
        var o = deck.Obs;
        if (o.Status == ObsStatus.Disabled) return new LiveInfo("OBS OFF", LiveTone.Dim, null, "Enable OBS in Settings > Integrations");
        if (o.Status != ObsStatus.Connected) return new LiveInfo("OFFLINE", LiveTone.Dim, null, o.StatusText);
        return a.Command switch
        {
            ObsCommands.StartStream or ObsCommands.ToggleStream =>
                o.Streaming ? new LiveInfo("LIVE", LiveTone.Alert, null, "Streaming") : new LiveInfo("READY", LiveTone.None, null, "Not streaming"),
            ObsCommands.StopStream => o.Streaming ? new LiveInfo("LIVE", LiveTone.None, null, "Streaming") : new LiveInfo("OFF AIR", LiveTone.Dim, null, null),
            ObsCommands.StartRecord or ObsCommands.ToggleRecord =>
                o.RecordingPaused ? new LiveInfo("PAUSED", LiveTone.Active, null, "Recording paused")
                : o.Recording ? new LiveInfo("REC", LiveTone.Alert, null, "Recording") : new LiveInfo("READY", LiveTone.None, null, "Not recording"),
            ObsCommands.StopRecord => o.Recording ? new LiveInfo("REC", LiveTone.None, null, null) : new LiveInfo("STOPPED", LiveTone.Dim, null, null),
            ObsCommands.PauseRecord =>
                !o.Recording ? new LiveInfo("NOT REC", LiveTone.Dim, null, "Not recording")
                : o.RecordingPaused ? new LiveInfo("PAUSED", LiveTone.Active, G("E768"), "Press to resume") : new LiveInfo("REC", LiveTone.None, null, "Press to pause"),
            ObsCommands.Scene => o.CurrentScene == a.Param ? new LiveInfo("ON AIR", LiveTone.Active, null, a.Param) : new LiveInfo(null, LiveTone.None, null, a.Param),
            _ => LiveInfo.Empty,
        };
    }

    private static string Short(string s)
    {
        s = s.Trim();
        var paren = s.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) s = s[..paren];
        return (s.Length > 18 ? s[..17] + "…" : s).ToUpperInvariant();
    }
}
