namespace VirtualStreamDeck.Core;

/// <summary>Profile templates offered by the first-run wizard and "+ New profile".</summary>
public static class Defaults
{
    public static readonly (string Id, string Name, string Glyph, string Blurb)[] Templates =
    {
        ("general", "General", "E80F", "Microphone, camera, volume, media and quick launches"),
        ("streaming", "Streaming", "E93E", "OBS stream / record controls, mic, camera, soundboard"),
        ("radio", "Radio / DJ", "E720", "Broadcast console: mic on air, levels, monitoring, jingles and music"),
        ("discord", "Discord", "E8F2", "Launch, focus and keybind-based mute / deafen"),
        ("gaming", "Gaming", "E7FC", "Game Bar, screenshots, clips, voice chat, sounds"),
        ("work", "Work", "E821", "Meetings, screenshots, clipboard and productivity apps"),
        ("presentation", "Presentation", "E7F4", "Slide navigation, blank screen, laser-free control"),
        ("music", "Music", "EC4F", "Transport, volume levels and music apps"),
        ("custom", "Custom", "E710", "An empty profile to build from scratch"),
    };

    public static readonly (string Key, string Name)[] BuiltInSounds =
    {
        ("airhorn", "Airhorn"), ("applause", "Applause"), ("laugh", "Laugh"), ("error", "Error"),
        ("notification", "Notification"), ("tada", "Tada"), ("rimshot", "Rimshot"), ("alarm", "Alarm"),
    };

    public static List<SoundItem> CreateBuiltInSounds() =>
        BuiltInSounds.Select(s => new SoundItem { Id = "builtin-" + s.Key, Name = s.Name, File = "builtin:" + s.Key, Favorite = true }).ToList();

    public static AppConfig CreateConfig(IEnumerable<string>? templates = null, int cols = 5, int rows = 3)
    {
        var cfg = new AppConfig { Sounds = CreateBuiltInSounds() };
        cfg.Settings.DefaultColumns = cols;
        cfg.Settings.DefaultRows = rows;
        foreach (var t in templates ?? new[] { "general", "streaming", "radio", "discord", "gaming", "work" })
            cfg.Profiles.Add(CreateProfile(t, cols, rows));
        if (cfg.Profiles.Count == 0) cfg.Profiles.Add(CreateProfile("general", cols, rows));
        cfg.ActiveProfileId = cfg.Profiles[0].Id;

        var hk = 1;
        foreach (var p in cfg.Profiles.Take(9))
            cfg.Hotkeys.Add(new HotkeyBinding { Chord = $"CTRL + ALT + {hk++}", Command = GlobalCommands.ProfilePrefix + p.Id });
        cfg.Hotkeys.Add(new HotkeyBinding { Chord = "CTRL + ALT + M", Command = GlobalCommands.ToggleMic });
        cfg.Hotkeys.Add(new HotkeyBinding { Chord = "CTRL + ALT + S", Command = GlobalCommands.ToggleSurface });
        cfg.Hotkeys.Add(new HotkeyBinding { Chord = "CTRL + ALT + X", Command = GlobalCommands.StopSounds });

        var streaming = cfg.Profiles.FirstOrDefault(p => p.Name == "Streaming");
        if (streaming != null) cfg.AutoProfileRules.Add(new AutoProfileRule { Process = "obs64.exe", ProfileId = streaming.Id });
        var discord = cfg.Profiles.FirstOrDefault(p => p.Name == "Discord");
        if (discord != null) cfg.AutoProfileRules.Add(new AutoProfileRule { Process = "Discord.exe", ProfileId = discord.Id });
        return cfg;
    }

    public static Profile CreateProfile(string template, int cols, int rows)
    {
        var t = Templates.FirstOrDefault(x => x.Id == template);
        if (t.Id == null) t = Templates[^1];
        var p = new Profile { Name = t.Name, Glyph = t.Glyph, Columns = cols, Rows = rows };
        var buttons = template switch
        {
            "general" => General(),
            "streaming" => Streaming(),
            "radio" => Radio(),
            "discord" => Discord(),
            "gaming" => Gaming(),
            "work" => Work(),
            "presentation" => Presentation(),
            "music" => Music(),
            _ => new List<DeckButton>(),
        };
        Place(p, buttons);
        return p;
    }

    /// <summary>Lays buttons out left-to-right, top-to-bottom, spilling onto extra pages.</summary>
    private static void Place(Profile p, List<DeckButton> buttons)
    {
        var page = p.Pages[0];
        foreach (var b in buttons)
        {
            var slot = GridLayout.FindFree(page, p.Columns, p.Rows);
            if (slot == null)
            {
                page = new DeckPage();
                p.Pages.Add(page);
                slot = (0, 0);
            }
            (b.Row, b.Col) = slot.Value;
            page.Buttons.Add(b);
        }
    }

    // ---- builders ---------------------------------------------------------------------------

    private static DeckButton B(string title, string glyph, ActionSpec action, string subtitle = "", string? accent = null) =>
        new() { Title = title, Subtitle = subtitle, Icon = IconSpec.Glyph(glyph), Action = action, Accent = accent };

    private static ActionSpec Sys(string cmd, int value = 0) => new() { Type = ActionTypes.System, Command = cmd, Value = value };
    private static ActionSpec Media(string cmd) => new() { Type = ActionTypes.Media, Command = cmd };
    private static ActionSpec Keys(string chord) => new() { Type = ActionTypes.Hotkey, Keys = chord };
    private static ActionSpec Url(string url) => new() { Type = ActionTypes.OpenUrl, Target = url };
    private static ActionSpec Launch(string exe, string args = "") => new() { Type = ActionTypes.LaunchApp, Target = exe, Arguments = args };
    private static ActionSpec Obs(string cmd, string param = "") => new() { Type = ActionTypes.Obs, Command = cmd, Param = param };
    private static ActionSpec Snd(string key) => new() { Type = ActionTypes.Sound, Param = "builtin-" + key, Volume = 100 };
    private static ActionSpec Power(string cmd) => new() { Type = ActionTypes.Power, Command = cmd };

    private static DeckButton Mic() => B("MICROPHONE", "E720", Sys(SysCommands.MicToggle));
    private static DeckButton Camera() => B("CAMERA", "E722", Sys(SysCommands.CameraStatus));
    private static DeckButton SysMute() => B("SYSTEM MUTE", "E74F", Sys(SysCommands.MasterMuteToggle));
    private static DeckButton Sound(string key, string title, string glyph = "E189") =>
        B(title.ToUpperInvariant(), glyph, Snd(key), "SOUND", "#F59E0B");

    private static string ObsExe => @"C:\Program Files\obs-studio\bin\64bit\obs64.exe";

    private static List<DeckButton> General() => new()
    {
        Mic(),
        Camera(),
        SysMute(),
        B("VOLUME +", "E995", Sys(SysCommands.MasterVolumeUp, 5)),
        B("VOLUME -", "E993", Sys(SysCommands.MasterVolumeDown, 5)),
        B("PLAY / PAUSE", "E768", Media(MediaCmds.PlayPause)),
        B("PREVIOUS", "E892", Media(MediaCmds.Previous)),
        B("NEXT", "E893", Media(MediaCmds.Next)),
        B("LOCK PC", "E72E", Power(PowerCommands.Lock)),
        B("SETTINGS", "E713", Sys(SysCommands.WindowsSettings), "WINDOWS"),
        B("BROWSER", "E774", Url("https://www.google.com")),
        B("EXPLORER", "E8B7", Launch("explorer.exe")),
        B("TASK MANAGER", "E9D9", Sys(SysCommands.TaskManager)),
        Sound("airhorn", "Airhorn"),
        Sound("applause", "Applause", "E76E"),
    };

    private static List<DeckButton> Streaming() => new()
    {
        B("MIC", "E720", Sys(SysCommands.MicToggle)),
        Camera(),
        B("START STREAM", "E93E", Obs(ObsCommands.StartStream), "OBS", "#EF4444"),
        B("STOP STREAM", "E71A", Obs(ObsCommands.StopStream), "OBS"),
        B("RECORD", "E7C8", Obs(ObsCommands.ToggleRecord), "OBS", "#EF4444"),
        B("PAUSE", "E769", Obs(ObsCommands.PauseRecord), "RECORDING"),
        B("OBS", "E714", Launch(ObsExe), "LAUNCH"),
        B("CHAT", "E8F2", new ActionSpec { Type = ActionTypes.Discord, Command = DiscordCommands.Launch }, "DISCORD"),
        SysMute(),
        Sound("airhorn", "Airhorn"),
        Sound("applause", "Applause", "E76E"),
        Sound("tada", "Tada", "E735"),
        Sound("rimshot", "Rimshot", "E7FC"),
        B("STOP SOUNDS", "E71A", new ActionSpec { Type = ActionTypes.Deck, Command = DeckCommands.StopAllSounds }, "SOUNDBOARD"),
        B("PLAY / PAUSE", "E768", Media(MediaCmds.PlayPause)),
    };

    private static List<DeckButton> Radio() => new()
    {
        B("MICROPHONE", "E720", Sys(SysCommands.MicToggle), "", "#22C55E"),
        B("MIC -", "E993", Sys(SysCommands.MicVolumeDown, 5), "MIC VOLUME"),
        B("MIC +", "E995", Sys(SysCommands.MicVolumeUp, 5), "MIC VOLUME"),
        B("MONITOR", "E7F6", Sys(SysCommands.MicMonitorToggle), "HEAR MIC"),
        B("NEXT MIC", "E720", Sys(SysCommands.MicCycle), "SWITCH DEVICE"),
        Sound("tada", "Jingle", "EC4F"),
        Sound("applause", "Applause", "E76E"),
        Sound("airhorn", "Airhorn"),
        Sound("rimshot", "Rimshot", "E7FC"),
        B("STOP FX", "E71A", new ActionSpec { Type = ActionTypes.Deck, Command = DeckCommands.StopAllSounds }, "SOUNDBOARD", "#EF4444"),
        B("PLAY / PAUSE", "E768", Media(MediaCmds.PlayPause), "MUSIC", "#1DB954"),
        B("NEXT TRACK", "E893", Media(MediaCmds.Next), "MUSIC"),
        B("MUSIC -", "E993", Sys(SysCommands.MasterVolumeDown, 5), "MASTER"),
        B("MUSIC +", "E995", Sys(SysCommands.MasterVolumeUp, 5), "MASTER"),
        SysMute(),
    };

    private static List<DeckButton> Discord() => new()
    {
        B("DISCORD", "E8F2", new ActionSpec { Type = ActionTypes.Discord, Command = DiscordCommands.Launch }, "LAUNCH / FOCUS", "#5865F2"),
        B("DISCORD MUTE", "EC54", new ActionSpec { Type = ActionTypes.Discord, Command = DiscordCommands.MuteKeybind }, "KEYBIND", "#5865F2"),
        B("DEAFEN", "E7F6", new ActionSpec { Type = ActionTypes.Discord, Command = DiscordCommands.DeafenKeybind }, "KEYBIND", "#5865F2"),
        Mic(),
        SysMute(),
        Sound("laugh", "Laugh", "E76E"),
        Sound("airhorn", "Airhorn"),
        Sound("rimshot", "Rimshot", "E7FC"),
        B("VOLUME +", "E995", Sys(SysCommands.MasterVolumeUp, 5)),
        B("VOLUME -", "E993", Sys(SysCommands.MasterVolumeDown, 5)),
    };

    private static List<DeckButton> Gaming() => new()
    {
        B("GAME BAR", "E7FC", Keys("WIN + G")),
        B("SCREENSHOT", "E91B", Keys("WIN + ALT + PRINTSCREEN"), "GAME BAR"),
        B("RECORD CLIP", "E7C8", Keys("WIN + ALT + G"), "LAST 30 S", "#EF4444"),
        B("SNIP", "E8C6", Keys("WIN + SHIFT + S"), "SCREEN AREA"),
        Mic(),
        B("DISCORD", "E8F2", new ActionSpec { Type = ActionTypes.Discord, Command = DiscordCommands.Launch }, "", "#5865F2"),
        B("STEAM", "E7FC", Url("steam://open/main")),
        SysMute(),
        B("VOLUME +", "E995", Sys(SysCommands.MasterVolumeUp, 5)),
        B("VOLUME -", "E993", Sys(SysCommands.MasterVolumeDown, 5)),
        Sound("airhorn", "Airhorn"),
        Sound("laugh", "Laugh", "E76E"),
        Sound("alarm", "Alarm", "E7BA"),
        Sound("error", "Fail", "E783"),
        B("PLAY / PAUSE", "E768", Media(MediaCmds.PlayPause)),
    };

    private static List<DeckButton> Work() => new()
    {
        B("TEAMS MUTE", "E720", Keys("CTRL + SHIFT + M"), "SENDS CTRL+SHIFT+M"),
        B("TEAMS CAMERA", "E714", Keys("CTRL + SHIFT + O"), "SENDS CTRL+SHIFT+O"),
        new DeckButton
        {
            Title = "START MEETING", Subtitle = "MACRO", Icon = IconSpec.Glyph("E716"),
            Action = new ActionSpec
            {
                Type = ActionTypes.Macro,
                Steps = new()
                {
                    new MacroStep { Type = MacroStepTypes.Combo, Value = "CTRL + SHIFT + M" },
                    new MacroStep { Type = MacroStepTypes.Delay, Number = 300 },
                    new MacroStep { Type = MacroStepTypes.Combo, Value = "CTRL + SHIFT + O" },
                    new MacroStep { Type = MacroStepTypes.Delay, Number = 500 },
                    new MacroStep { Type = MacroStepTypes.Text, Value = "Meeting started" },
                    new MacroStep { Type = MacroStepTypes.Key, Value = "ENTER" },
                },
            },
        },
        Mic(),
        Camera(),
        B("SNIP", "E8C6", Keys("WIN + SHIFT + S")),
        B("COPY", "E8C8", Keys("CTRL + C")),
        B("PASTE", "E77F", Keys("CTRL + V")),
        B("SHOW DESKTOP", "E7F4", Keys("WIN + D")),
        B("LOCK PC", "E72E", Power(PowerCommands.Lock)),
        B("CALCULATOR", "E8EF", Launch("calc.exe")),
        B("EXPLORER", "E8B7", Launch("explorer.exe")),
        B("TERMINAL", "E756", Launch("cmd.exe")),
        B("TASK VIEW", "E7C4", Keys("WIN + TAB")),
        B("CLIPBOARD", "E77F", Keys("WIN + V"), "HISTORY"),
    };

    private static List<DeckButton> Presentation() => new()
    {
        B("PREVIOUS", "E892", Keys("LEFT"), "SLIDE"),
        B("NEXT", "E893", Keys("RIGHT"), "SLIDE", "#22C55E"),
        B("START SHOW", "E768", Keys("F5"), "FROM START"),
        B("FROM HERE", "E768", Keys("SHIFT + F5"), "CURRENT SLIDE"),
        B("END SHOW", "E71A", Keys("ESC")),
        B("BLACK SCREEN", "E7F4", Keys("B"), "TOGGLE"),
        B("WHITE SCREEN", "E7F4", Keys("W"), "TOGGLE"),
        Mic(),
        B("TIMER", "E916", Url("ms-clock:"), "CLOCK APP"),
        B("PROJECT", "E7F4", Keys("WIN + P"), "DISPLAY MODE"),
    };

    private static List<DeckButton> Music()
    {
        var level = new DeckButton
        {
            Title = "AUDIO", Subtitle = "100%", Icon = IconSpec.Glyph("E995"), Mode = ButtonModes.MultiState,
            Action = Sys(SysCommands.MasterVolumeSet, 100),
            States = new()
            {
                new ButtonState { Title = "AUDIO", Subtitle = "50%", Icon = IconSpec.Glyph("E994"), Action = Sys(SysCommands.MasterVolumeSet, 50) },
                new ButtonState { Title = "AUDIO", Subtitle = "25%", Icon = IconSpec.Glyph("E993"), Action = Sys(SysCommands.MasterVolumeSet, 25) },
                new ButtonState { Title = "AUDIO", Subtitle = "MUTE", Icon = IconSpec.Glyph("E74F"), Action = Sys(SysCommands.MasterVolumeSet, 0) },
            },
            ShowLiveState = false,
        };
        return new()
        {
            B("PREVIOUS", "E892", Media(MediaCmds.Previous)),
            B("PLAY / PAUSE", "E768", Media(MediaCmds.PlayPause), "", "#22C55E"),
            B("NEXT", "E893", Media(MediaCmds.Next)),
            B("VOLUME -", "E993", Sys(SysCommands.MasterVolumeDown, 5)),
            B("VOLUME +", "E995", Sys(SysCommands.MasterVolumeUp, 5)),
            level,
            SysMute(),
            B("SPOTIFY", "EC4F", Url("spotify:"), "", "#1DB954"),
            B("OUTPUT", "E7F5", Sys(SysCommands.OutputCycle), "NEXT DEVICE"),
            B("STOP", "E71A", Media(MediaCmds.Stop)),
        };
    }
}
