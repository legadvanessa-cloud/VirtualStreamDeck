using NAudio.Wave;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Services;
using static VirtualStreamDeck.Interop.Native;

namespace VirtualStreamDeck.Tests;

public class KeyChordTests
{
    [Theory]
    [InlineData("CTRL + SHIFT + M", "CTRL + SHIFT + M")]
    [InlineData("ctrl+shift+m", "CTRL + SHIFT + M")]
    [InlineData("Shift + Ctrl + M", "CTRL + SHIFT + M")]
    [InlineData("control+alt+f", "CTRL + ALT + F")]
    [InlineData("ALT + TAB", "ALT + TAB")]
    [InlineData("F13", "F13")]
    [InlineData("f24", "F24")]
    [InlineData("WIN + SHIFT + S", "SHIFT + WIN + S")]
    [InlineData("CTRL + ALT + 1", "CTRL + ALT + 1")]
    [InlineData("CTRL+PGUP", "CTRL + PAGEUP")]
    [InlineData("WIN", "WIN")]
    public void ParsesAndNormalises(string input, string expected)
    {
        Assert.True(KeyChord.TryParse(input, out var c));
        Assert.Equal(expected, c.ToString());
        Assert.Equal(expected, KeyChord.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("CTRL + NOPE")]
    [InlineData("A + B")]
    [InlineData("   ")]
    public void RejectsInvalid(string input) => Assert.False(KeyChord.TryParse(input, out _));

    [Fact]
    public void ModifierFlagsMatchRegisterHotKey()
    {
        var c = KeyChord.Parse("CTRL + ALT + SHIFT + WIN + K");
        Assert.Equal(0x1 | 0x2 | 0x4 | 0x8, (int)c.Modifiers);
        Assert.Equal('K', c.VirtualKey);
    }

    [Fact]
    public void ExtendedKeysAreFlagged()
    {
        Assert.True(KeyChord.IsExtended(KeyChord.VkOf("DELETE")));
        Assert.True(KeyChord.IsExtended(KeyChord.VkOf("LEFT")));
        Assert.True(KeyChord.IsExtended(KeyChord.VkOf("MEDIAPLAYPAUSE")));
        Assert.False(KeyChord.IsExtended(KeyChord.VkOf("A")));
        Assert.False(KeyChord.IsExtended(KeyChord.VkOf("F13")));
    }
}

public class InputBuilderTests
{
    public InputBuilderTests() => InputBuilder.ScanCode = vk => (ushort)vk;

    private static string Describe(IEnumerable<INPUT> inputs) => string.Join(",", inputs.Select(i =>
        (i.U.ki.dwFlags & KEYEVENTF_KEYUP) != 0 ? $"-{i.U.ki.wVk:X2}" : $"+{i.U.ki.wVk:X2}"));

    [Fact]
    public void ChordPressesModifiersFirstAndReleasesInReverse()
    {
        var seq = Describe(InputBuilder.Tap(KeyChord.Parse("CTRL + SHIFT + M")));
        Assert.Equal("+11,+10,+4D,-4D,-10,-11", seq);
    }

    [Fact]
    public void HoldDownAndUpAreSeparate()
    {
        var c = KeyChord.Parse("CTRL + F13");
        Assert.Equal("+11,+7C", Describe(InputBuilder.ChordDown(c)));
        Assert.Equal("-7C,-11", Describe(InputBuilder.ChordUp(c)));
    }

    [Fact]
    public void TextIsSentAsUnicodeWithEnterForNewlines()
    {
        var seq = InputBuilder.Text("Hé\nx");
        Assert.Equal(8, seq.Count);
        Assert.All(new[] { 0, 1, 2, 3, 6, 7 }, i => Assert.True((seq[i].U.ki.dwFlags & KEYEVENTF_UNICODE) != 0));
        Assert.Equal('é', (char)seq[2].U.ki.wScan);
        Assert.Equal(0x0D, seq[4].U.ki.wVk);
    }

    private sealed class Recorder : IInputSink
    {
        public readonly List<INPUT> Sent = new();
        public void Send(INPUT[] inputs) => Sent.AddRange(inputs);
    }

    [Fact]
    public async Task MacroRunsStepsInOrderAndReleasesHeldKeys()
    {
        var rec = new Recorder();
        var launched = new List<string>();
        var eng = new MacroEngine(new InputSender(rec), (p, _) => launched.Add(p));
        await eng.RunAsync(new List<MacroStep>
        {
            new() { Type = MacroStepTypes.Combo, Value = "CTRL + SHIFT + M" },
            new() { Type = MacroStepTypes.Delay, Number = 10 },
            new() { Type = MacroStepTypes.KeyDown, Value = "SHIFT" },
            new() { Type = MacroStepTypes.Text, Value = "a" },
            new() { Type = MacroStepTypes.Launch, Value = "notepad.exe" },
        });
        var keys = rec.Sent.Where(i => i.type == INPUT_KEYBOARD && (i.U.ki.dwFlags & KEYEVENTF_UNICODE) == 0).ToList();
        // combo (6) + shift down (1) + shift auto-released at the end (1)
        Assert.Equal(8, keys.Count);
        Assert.Equal(0x10, keys[^1].U.ki.wVk);
        Assert.True((keys[^1].U.ki.dwFlags & KEYEVENTF_KEYUP) != 0);
        Assert.Equal(new[] { "notepad.exe" }, launched);
    }

    [Fact]
    public async Task InvalidMacroStepStillReleasesKeys()
    {
        var rec = new Recorder();
        var eng = new MacroEngine(new InputSender(rec), (_, _) => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => eng.RunAsync(new List<MacroStep>
        {
            new() { Type = MacroStepTypes.KeyDown, Value = "CTRL" },
            new() { Type = MacroStepTypes.Combo, Value = "BAD KEY" },
        }));
        Assert.True((rec.Sent[^1].U.ki.dwFlags & KEYEVENTF_KEYUP) != 0);
    }
}

public class GridLayoutTests
{
    private static DeckPage Page(params (int r, int c, int rs, int cs)[] cells) =>
        new() { Buttons = cells.Select(x => new DeckButton { Row = x.r, Col = x.c, RowSpan = x.rs, ColSpan = x.cs }).ToList() };

    [Fact]
    public void FitsRespectsSpansAndBounds()
    {
        var p = Page((0, 0, 1, 2));
        Assert.False(GridLayout.Fits(p, 5, 3, 0, 1, 1, 1));
        Assert.True(GridLayout.Fits(p, 5, 3, 0, 2, 1, 1));
        Assert.False(GridLayout.Fits(p, 5, 3, 2, 4, 2, 1));
        Assert.Equal((0, 2), GridLayout.FindFree(p, 5, 3));
    }

    [Fact]
    public void SwapExchangesPositions()
    {
        var p = Page((0, 0, 1, 1), (1, 3, 1, 1));
        var (a, b) = (p.Buttons[0], p.Buttons[1]);
        Assert.True(GridLayout.Swap(p, 5, 3, a, b));
        Assert.Equal((1, 3), (a.Row, a.Col));
        Assert.Equal((0, 0), (b.Row, b.Col));
    }

    [Fact]
    public void SwapRefusesWhenAWideButtonWouldNotFit()
    {
        var p = Page((0, 0, 1, 2), (0, 4, 1, 1));
        Assert.False(GridLayout.Swap(p, 5, 3, p.Buttons[0], p.Buttons[1]));
        Assert.Equal(0, p.Buttons[0].Col);
    }

    [Fact]
    public void TransferFailsCleanlyWhenFull()
    {
        var from = Page((0, 0, 1, 1));
        var to = Page((0, 0, 1, 1));
        Assert.False(GridLayout.Transfer(from, to, 1, 1, from.Buttons[0]));
        Assert.Single(from.Buttons);
    }

    [Fact]
    public void RepairNeverLosesButtons()
    {
        var profile = new Profile { Columns = 3, Rows = 1 };
        profile.Pages[0].Buttons = Enumerable.Range(0, 7).Select(i => new DeckButton { Row = 0, Col = i }).ToList();
        profile.Pages[0].Buttons.Add(new DeckButton { Row = 0, Col = 0 }); // overlap
        GridLayout.Repair(profile);
        Assert.Equal(8, profile.Pages.Sum(pg => pg.Buttons.Count));
        foreach (var pg in profile.Pages)
            Assert.All(pg.Buttons, b => Assert.True(b.Col < 3 && b.Row < 1));
        Assert.Equal(3, profile.Pages.Count);
    }

    [Fact]
    public void RepairFixesDuplicateIdsAndNulls()
    {
        var profile = new Profile { Columns = 5, Rows = 3 };
        profile.Pages[0].Buttons.Add(new DeckButton { Id = "same", Col = 0, Action = null!, Icon = null!, States = null! });
        profile.Pages[0].Buttons.Add(new DeckButton { Id = "same", Col = 1 });
        GridLayout.Repair(profile);
        Assert.Equal(2, profile.Pages[0].Buttons.Select(b => b.Id).Distinct().Count());
        Assert.NotNull(profile.Pages[0].Buttons[0].Action);
    }
}

public class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vsd-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public ConfigStoreTests()
    {
        AppPaths.Override(_dir);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void RoundTripsAndKeepsBackup()
    {
        var first = ConfigStore.Load();
        Assert.True(first.WasCreated);
        first.Config.Settings.CornerRadius = 7;
        ConfigStore.Save(first.Config);
        ConfigStore.Save(first.Config);
        Assert.True(File.Exists(AppPaths.ConfigBackup));
        var again = ConfigStore.Load();
        Assert.False(again.WasCreated);
        Assert.Equal(7, again.Config.Settings.CornerRadius);
    }

    [Fact]
    public void CorruptConfigFallsBackToBackupThenDefaults()
    {
        var cfg = Defaults.CreateConfig();
        ConfigStore.Save(cfg);
        ConfigStore.Save(cfg);
        File.WriteAllText(AppPaths.Config, "{{{ not json");
        var r = ConfigStore.Load();
        Assert.NotNull(r.Warning);
        Assert.Equal(cfg.Profiles.Count, r.Config.Profiles.Count);

        File.WriteAllText(AppPaths.Config, "garbage");
        File.WriteAllText(AppPaths.ConfigBackup, "garbage");
        var r2 = ConfigStore.Load();
        Assert.True(r2.WasCreated);
        Assert.NotEmpty(r2.Config.Profiles);
    }

    [Fact]
    public void SanitizeRepairsHandEditedConfig()
    {
        File.WriteAllText(AppPaths.Config, """{ "Profiles": [ { "Name": "", "Columns": 99, "Rows": 0, "Pages": null } ], "Sounds": null, "Settings": { "SoundboardVolume": 900 } }""");
        var r = ConfigStore.Load();
        var p = r.Config.Profiles.Single();
        Assert.Equal(GridLayout.MaxColumns, p.Columns);
        Assert.Equal(1, p.Rows);
        Assert.Single(p.Pages);
        Assert.Equal(100, r.Config.Settings.SoundboardVolume);
        Assert.Contains(r.Config.Sounds, s => s.IsBuiltIn);
        Assert.Equal(p.Id, r.Config.ActiveProfileId);
    }

    [Fact]
    public void ExportEmbedsSoundsAndStripsSecrets()
    {
        AppPaths.Ensure();
        var cfg = Defaults.CreateConfig(new[] { "general" });
        File.WriteAllBytes(Path.Combine(AppPaths.Sounds, "mine.wav"), new byte[] { 1, 2, 3, 4 });
        var s = new SoundItem { Name = "Mine", File = "mine.wav" };
        cfg.Sounds.Add(s);
        cfg.Profiles[0].Pages[0].Buttons[0].Action = new ActionSpec { Type = ActionTypes.Sound, Param = s.Id };
        cfg.Settings.ObsPasswordProtected = "SECRET-BLOB";
        cfg.Settings.RemotePin = "123456";
        var path = Path.Combine(_dir, "x.vsdconfig");
        ConfigStore.WriteExport(ConfigStore.BuildExport(cfg), path);
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("SECRET-BLOB", text);
        Assert.DoesNotContain("123456", text);

        File.Delete(Path.Combine(AppPaths.Sounds, "mine.wav"));
        var imported = ConfigStore.ImportConfig(ConfigStore.ReadExport(path), new AppConfig { Settings = new AppSettings { ObsPasswordProtected = "LOCAL" } });
        var si = imported.Sounds.Single(x => x.Id == s.Id);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(AppPaths.Sounds, si.File)));
        Assert.Equal("LOCAL", imported.Settings.ObsPasswordProtected);
    }

    [Fact]
    public void ProfileImportGetsFreshIdsAndNoHotkeys()
    {
        var cfg = Defaults.CreateConfig(new[] { "general" });
        var p = cfg.Profiles[0];
        p.Hotkey = "CTRL + ALT + 9";
        p.Pages[0].Buttons[0].Hotkey = "CTRL + ALT + 8";
        var pkg = ConfigStore.BuildProfileExport(cfg, p);
        var added = ConfigStore.ImportProfiles(pkg, cfg);
        var np = added.Single();
        Assert.NotEqual(p.Id, np.Id);
        Assert.Equal("", np.Hotkey);
        Assert.All(np.Pages[0].Buttons, b => Assert.Equal("", b.Hotkey));
        Assert.Contains("(imported)", np.Name);
        Assert.Empty(np.Pages[0].Buttons.Select(b => b.Id).Intersect(p.Pages[0].Buttons.Select(b => b.Id)));
    }

    [Fact]
    public void RejectsForeignFiles()
    {
        var path = Path.Combine(_dir, "bad.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, """{ "hello": 1 }""");
        Assert.Throws<InvalidDataException>(() => ConfigStore.ReadExport(path));
    }
}

public class DefaultsTests
{
    [Fact]
    public void EveryTemplateBuildsAndFits()
    {
        foreach (var (cols, rows) in GridLayout.Presets)
        foreach (var t in Defaults.Templates)
        {
            var p = Defaults.CreateProfile(t.Id, cols, rows);
            Assert.Equal(0, GridLayout.Repair(p));
        }
    }

    [Fact]
    public void NoDefaultButtonIsADestructivePowerAction()
    {
        var cfg = Defaults.CreateConfig(Defaults.Templates.Select(t => t.Id));
        Assert.DoesNotContain(GridLayout.AllButtons(cfg), x => x.Button.Action.Type == ActionTypes.Power && PowerCommands.IsDestructive(x.Button.Action.Command));
    }

    [Fact]
    public void DefaultSoundButtonsReferenceExistingSounds()
    {
        var cfg = Defaults.CreateConfig(Defaults.Templates.Select(t => t.Id));
        foreach (var (_, _, b) in GridLayout.AllButtons(cfg).Where(x => x.Button.Action.Type == ActionTypes.Sound))
            Assert.Contains(cfg.Sounds, s => s.Id == b.Action.Param);
    }

    [Fact]
    public void RuleMatchingIsCaseInsensitiveAndExeOptional()
    {
        Assert.True(Deck.RuleMatches(new AutoProfileRule { Process = "Discord" }, "discord.exe"));
        Assert.False(Deck.RuleMatches(new AutoProfileRule { Process = "obs" }, "obs64.exe"));
        Assert.False(Deck.RuleMatches(new AutoProfileRule { Process = "" }, "x.exe"));
    }

    [Fact]
    public void MultiStateEffectiveStateFollowsCurrentState()
    {
        var b = new DeckButton
        {
            Mode = ButtonModes.MultiState, Title = "AUDIO", Subtitle = "100%",
            States = { new ButtonState { Subtitle = "50%" }, new ButtonState { Subtitle = "MUTE" } },
        };
        Assert.Equal(3, Deck.StateCount(b));
        b.CurrentState = 2;
        Assert.Equal("MUTE", Deck.Effective(b).Subtitle);
        Assert.Equal("AUDIO", Deck.Effective(b).Title);
        b.Mode = ButtonModes.Toggle;
        Assert.Equal(2, Deck.StateCount(b));
    }
}

public class AudioMathTests
{
    [Fact]
    public void MeasuresFloatPeakAndRms()
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var samples = new float[] { 0.5f, -0.25f, -0.5f, 0.25f };
        var buf = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, buf, 0, buf.Length);
        var peak = new float[2];
        var sum = new double[2];
        var frames = MicMeter.Measure(buf, buf.Length, fmt, peak, sum);
        Assert.Equal(2, frames);
        Assert.Equal(0.5f, peak[0], 3);
        Assert.Equal(0.25f, peak[1], 3);
        Assert.Equal(0.5, Math.Sqrt(sum[0] / frames), 3);
    }

    [Fact]
    public void MeasuresPcm16()
    {
        var fmt = new WaveFormat(44100, 16, 1);
        var buf = new byte[4];
        BitConverter.GetBytes((short)16384).CopyTo(buf, 0);
        BitConverter.GetBytes((short)-32768).CopyTo(buf, 2);
        var peak = new float[1];
        var sum = new double[1];
        Assert.Equal(2, MicMeter.Measure(buf, 4, fmt, peak, sum));
        Assert.Equal(1f, peak[0], 3);
    }

    [Theory]
    [InlineData(1f, 0f)]
    [InlineData(0.5f, -6.02f)]
    [InlineData(0f, MicMeter.FloorDb)]
    public void ConvertsToDbfs(float linear, float db) => Assert.Equal(db, MicMeter.ToDb(linear), 1);

    [Fact]
    public void BuiltInSoundsAreValidStereoPcm()
    {
        foreach (var (key, _) in Defaults.BuiltInSounds)
        {
            var s = SoundSynth.Generate(key, 48000)!;
            Assert.True(s.Length % 2 == 0 && s.Length > 4800, key);
            Assert.All(s, v => Assert.InRange(v, -1f, 1f));
        }
        Assert.Null(SoundSynth.Generate("nope", 48000));
    }
}

public class IntegrationProtocolTests
{
    [Fact]
    public void ObsAuthIsDeterministicSha256Base64()
    {
        var a = ObsClient.AuthString("pw", "salt", "challenge");
        Assert.Equal(a, ObsClient.AuthString("pw", "salt", "challenge"));
        Assert.NotEqual(a, ObsClient.AuthString("pw2", "salt", "challenge"));
        Assert.Equal(32, Convert.FromBase64String(a).Length);
    }

    [Fact]
    public void ParsesHttpRequestHead()
    {
        var r = RemoteServer.Parse("POST /api/press/abc%20d?x=1 HTTP/1.1\r\nHost: tab\r\nX-Pin: 123456")!;
        Assert.Equal("POST", r.Method);
        Assert.Equal("/api/press/abc d", r.Path);
        Assert.Equal("x=1", r.Query);
        Assert.Equal("123456", r.Headers["x-pin"]);
        Assert.Null(RemoteServer.Parse("garbage"));
    }

    [Fact]
    public void PinsAreSixDigits()
    {
        for (var i = 0; i < 50; i++) Assert.Matches("^[0-9]{6}$", RemoteServer.NewPin());
    }

    [Fact]
    public void MacroStepsDescribeThemselves()
    {
        Assert.Equal("WAIT 300 ms", new MacroStep { Type = MacroStepTypes.Delay, Number = 300 }.Describe());
        Assert.Equal("TYPE \"Meeting started\"", new MacroStep { Type = MacroStepTypes.Text, Value = "Meeting started" }.Describe());
        Assert.Equal("CTRL + SHIFT + M", new MacroStep { Type = MacroStepTypes.Combo, Value = "CTRL + SHIFT + M" }.Describe());
    }
}
