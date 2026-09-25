using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Services;
using VirtualStreamDeck.UI.Controls;
using VirtualStreamDeck.UI.Windows;

namespace VirtualStreamDeck.UI;

/// <summary>
/// --verify: exercises the real app headlessly - config persistence, services, hotkeys, actions,
/// soundboard, mic metering, remote server, tray and every window rendered off-screen - and writes
/// a report. Uses a throwaway data folder unless --data is given, never sends keystrokes to other
/// apps, never plays audible sound and never changes system state.
/// </summary>
public static class SelfTest
{
    private static StreamWriter? _out;
    private static int _pass, _fail, _warn;
    private static long _lastProgress;
    private static string? _shots;

    private static void Line(string s)
    {
        Interlocked.Exchange(ref _lastProgress, Environment.TickCount64);
        _out?.WriteLine(s);
        _out?.Flush();
        Debug.WriteLine(s);
    }

    private static void Pass(string name, string detail = "") { _pass++; Line($"PASS  {name}{(detail.Length > 0 ? " — " + detail : "")}"); }
    private static void Fail(string name, string detail) { _fail++; Line($"FAIL  {name} — {detail}"); }
    private static void Warn(string name, string detail) { _warn++; Line($"WARN  {name} — {detail}"); }
    private static void Check(string name, bool ok, string detail = "") { if (ok) Pass(name, detail); else Fail(name, detail.Length > 0 ? detail : "condition false"); }

    private static async Task Step(string name, Func<Task> body)
    {
        try { await body(); }
        catch (Exception ex) { Fail(name, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    public static async Task<int> RunAsync(App app, string? outPath, string? shots)
    {
        var usingTemp = !Environment.GetCommandLineArgs().Contains("--data");
        if (usingTemp) AppPaths.Override(Path.Combine(Path.GetTempPath(), "VirtualStreamDeck-verify-" + Guid.NewGuid().ToString("N")[..8]));
        outPath ??= Path.Combine(AppPaths.Root, "verify-report.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        _out = new StreamWriter(outPath, false, new UTF8Encoding(false));
        _shots = shots;
        if (_shots != null) Directory.CreateDirectory(_shots);
        UiHost.Instance.Silent = true;
        Line($"{AppInfo.FullName} {AppInfo.Version} self-test — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line($"Data folder: {AppPaths.Root}");

        // Watchdog: a hung check must still produce a report and end the process.
        _lastProgress = Environment.TickCount64;
        var watchdog = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(2000);
                if (Environment.TickCount64 - Interlocked.Read(ref _lastProgress) > 45000)
                {
                    try { Line("FAIL  watchdog — no progress for 45 s, aborting"); _out?.Flush(); } catch { }
                    Process.GetCurrentProcess().Kill();
                }
            }
        }) { IsBackground = true };
        watchdog.Start();

        await Step("config", ConfigChecks);
        Deck? deck = null;
        await Step("deck start", () =>
        {
            deck = app.StartDeck();
            deck.StartServices();
            Pass("deck start", $"{deck.Config.Profiles.Count} profiles");
            return Task.CompletedTask;
        });
        if (deck != null)
        {
            await Step("environment", () => EnvChecks(deck));
            await Step("theme", ThemeChecks);
            await Step("glyphs", GlyphChecks);
            await Step("audio", () => AudioChecks(deck));
            await Step("microphone meter", () => MicChecks(deck));
            await Step("camera/radios/media", () => DeviceChecks(deck));
            await Step("hotkeys", () => HotkeyChecks(deck));
            await Step("profiles", () => ProfileChecks(deck));
            await Step("actions", () => ActionChecks(deck));
            await Step("soundboard", () => SoundboardChecks(deck));
            await Step("remote", () => RemoteChecks(deck));
            await Step("tray", () => TrayChecks(deck));
            await Step("windows", () => WindowChecks(deck));
            await Step("persistence", () => PersistenceChecks(deck));
            await Step("diagnostics", () =>
            {
                var d = DiagnosticsReport.Build(deck);
                Check("diagnostics report", d.Contains("Registered hotkeys") && d.Contains("Microphone"), $"{d.Length} chars");
                return Task.CompletedTask;
            });
        }

        Line("");
        Line($"RESULT  {_pass} passed, {_warn} warnings, {_fail} failed");
        _out.Flush();
        try { deck?.Dispose(); } catch { }
        _out.Dispose();
        if (usingTemp) { try { Directory.Delete(AppPaths.Root, true); } catch { } }
        return _fail == 0 ? 0 : 1;
    }

    // ---- checks -----------------------------------------------------------------------------

    private static Task ConfigChecks()
    {
        var first = ConfigStore.Load();
        Check("config: first run creates defaults", first.WasCreated && first.Config.Profiles.Count >= 5, $"{first.Config.Profiles.Count} profiles");
        ConfigStore.Save(first.Config);
        var again = ConfigStore.Load();
        Check("config: save + reload", !again.WasCreated && again.Config.Profiles.Count == first.Config.Profiles.Count);
        ConfigStore.Save(again.Config); // creates config.bak.json
        File.WriteAllText(AppPaths.Config, "{ this is not json");
        var recovered = ConfigStore.Load();
        Check("config: corrupt file recovers from backup", recovered.Warning != null && recovered.Config.Profiles.Count == first.Config.Profiles.Count, recovered.Warning ?? "");
        Check("config: corrupt file set aside", Directory.GetFiles(AppPaths.Root, "*.corrupt-*").Length > 0);
        ConfigStore.Save(recovered.Config);
        return Task.CompletedTask;
    }

    private static Task EnvChecks(Deck deck)
    {
        Check("not elevated", !DiagnosticsReport.IsElevated(), DiagnosticsReport.IsElevated() ? "running as administrator" : "standard user token");
#pragma warning disable IL3000 // intentional: an empty Location is exactly how single-file bundling is detected
        var singleFile = string.IsNullOrEmpty(typeof(object).Assembly.Location);
#pragma warning restore IL3000
        if (singleFile) Pass("self-contained single-file", $"{AppInfo.ExePath} ({new FileInfo(AppInfo.ExePath).Length / 1048576} MB)");
        else Warn("self-contained single-file", "running from a build folder, not the published exe");
        var d = Displays.All();
        Check("displays enumerated", d.Count > 0, string.Join("; ", d.Select(x => x.Label)));
        Line($"INFO  Windows: {DiagnosticsReport.WindowsVersion()}");
        return Task.CompletedTask;
    }

    private static Task ThemeChecks()
    {
        var res = Application.Current.Resources;
        var missing = new List<string>();
        foreach (var k in new[] { "Bg.App", "Bg.Panel", "Accent", "Text.Primary", "Btn.Accent", "Btn.Icon", "Toggle.Pill", "Toggle.Card", "Radio.Card", "Slider.Fader", "Text.Caps", "FocusRing" })
            if (res[k] == null) missing.Add(k);
        foreach (var md in res.MergedDictionaries) foreach (var v in md.Values) _ = v; // force realisation
        Check("theme resources resolve", missing.Count == 0, missing.Count == 0 ? "" : string.Join(", ", missing));
        return Task.CompletedTask;
    }

    private static Task GlyphChecks()
    {
        var missing = new List<string>();
        var faces = new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }
            .Select(n => new Typeface(new FontFamily(n), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal))
            .Select(t => t.TryGetGlyphTypeface(out var g) ? g : null).Where(g => g != null).ToList();
        if (faces.Count == 0) { Fail("icon font", "neither Segoe Fluent Icons nor Segoe MDL2 Assets is installed"); return Task.CompletedTask; }
        var codes = IconCatalog.Glyphs.Select(g => g.Code).Concat(Defaults.Templates.Select(t => t.Glyph))
            .Concat(new[] { "E711", "E712", "E70D", "E70E", "E76B", "E76C", "E765", "E894", "E73E", "E7BA", "E783", "E946", "E921", "E922", "E923", "E8BB", "E7F6" }).Distinct();
        foreach (var c in codes)
        {
            var cp = Convert.ToInt32(c, 16);
            if (!faces.Any(f => f!.CharacterToGlyphMap.ContainsKey(cp))) missing.Add(c);
        }
        Check("icon glyphs present in system font", missing.Count == 0, missing.Count == 0 ? $"{codes.Count()} glyphs" : "missing: " + string.Join(", ", missing));
        return Task.CompletedTask;
    }

    private static Task AudioChecks(Deck deck)
    {
        var outs = deck.Audio.Devices(DataFlow.Render);
        var ins = deck.Audio.Devices(DataFlow.Capture);
        if (outs.Count > 0) Pass("audio outputs", string.Join(", ", outs.Select(o => o.Name + (o.IsDefault ? "*" : ""))));
        else Warn("audio outputs", "no playback device on this PC");
        if (ins.Count > 0) Pass("audio inputs", string.Join(", ", ins.Select(o => o.Name + (o.IsDefault ? "*" : ""))));
        else Warn("audio inputs", "no microphone on this PC");
        var spk = deck.Audio.Speaker;
        Check("speaker state read", !outs.Any() || spk.Available, $"{spk.DeviceName}: {(spk.Muted ? "muted" : spk.VolumePercent + "%")}");
        var mic = deck.Audio.Microphone;
        Check("microphone state read", !ins.Any() || mic.Available, mic.Available ? $"{mic.DeviceName}: {(mic.Muted ? "MUTED" : "ON AIR")}, {mic.VolumePercent}%{(mic.HasDbRange ? $", {mic.Db:0.0} dB [{mic.DbMin:0}..{mic.DbMax:0}]" : "")}" : "none");
        var live = LiveState.ResolveAction(deck, ActionSpec.Of(ActionTypes.System, SysCommands.MicToggle), null);
        Check("mic live state matches Windows", !mic.Available ? live.State == "NO DEVICE" : live.State == (mic.Muted ? "MUTED" : "ON AIR"), live.State ?? "");
        return Task.CompletedTask;
    }

    private static async Task MicChecks(Deck deck)
    {
        var mic = deck.Audio.Microphone;
        if (!mic.Available) { Warn("mic meter", "no microphone to meter"); return; }
        deck.Meter.Acquire();
        var sw = Stopwatch.StartNew();
        while (!deck.Meter.HasSignal && sw.ElapsedMilliseconds < 3000) await Task.Delay(50);
        var levels = deck.Meter.Levels();
        if (deck.Meter.HasSignal)
            Pass("mic meter receives real samples", $"{deck.Meter.Channels} ch, peak {string.Join(" / ", levels.Select(l => $"{l.PeakDb:0.0}"))} dBFS, first data after {sw.ElapsedMilliseconds} ms");
        else Fail("mic meter receives real samples", deck.Meter.Error ?? "no data within 3 s");
        Check("mic meter values within dBFS range", levels.All(l => l.PeakDb is >= MicMeter.FloorDb and <= 0.5f));
        deck.Meter.Release();
        await Task.Delay(200);
        Check("mic meter stream closes when unused", !deck.Meter.Running);
    }

    private static async Task DeviceChecks(Deck deck)
    {
        await Task.Delay(1500); // camera / radio / media services start in the background
        var cam = deck.Camera.State;
        Pass("camera state", cam.AnyCamera ? $"{cam.Devices.Count} device(s): {string.Join(", ", cam.Devices.Select(d => d.Name))}; in use: {(cam.InUse ? string.Join(",", cam.UsedBy) : "no")}; access {(cam.AccessAllowed ? "allowed" : "blocked")}" : "no camera device (reported as NO CAMERA)");
        if (!deck.Radios.Initialised) await deck.Radios.InitAsync();
        var wifi = deck.Radios.IsOn(global::Windows.Devices.Radios.RadioKind.WiFi);
        var bt = deck.Radios.IsOn(global::Windows.Devices.Radios.RadioKind.Bluetooth);
        Pass("radio API", $"Wi-Fi {(wifi == null ? "none" : wifi.Value ? "on" : "off")}, Bluetooth {(bt == null ? "none" : bt.Value ? "on" : "off")}{(deck.Radios.AccessProblem != null ? " — " + deck.Radios.AccessProblem : "")}");
        Pass("media session", deck.Media.HasSession ? $"'{deck.Media.Title}' {(deck.Media.Playing == true ? "playing" : "paused")}" : "no active session");
        var b = await Task.Run(() => Brightness.Adjust(0));
        Pass("brightness probe", b.Supported ? $"supported ({b.Detail}) at {b.Percent}%" : "no display accepts brightness control (reported as unsupported)");
    }

    private static async Task HotkeyChecks(Deck deck)
    {
        var failed = deck.Hotkeys.Registrations.Where(r => !r.Ok).ToList();
        Pass("configured hotkeys registered", $"{deck.Hotkeys.Registrations.Count(r => r.Ok)} ok, {failed.Count} conflicts{(failed.Count > 0 ? ": " + string.Join(", ", failed.Select(f => $"{f.Chord} ({f.Error})")) : "")}");
        var fired = new TaskCompletionSource<bool>();
        var reg = deck.Hotkeys.Register("CTRL + ALT + SHIFT + F24", "self-test", () => fired.TrySetResult(true));
        Check("register test hotkey", reg.Ok, reg.Error ?? "");
        if (reg.Ok)
        {
            deck.Hotkeys.Simulate("CTRL + ALT + SHIFT + F24");
            var done = await Task.WhenAny(fired.Task, Task.Delay(2000));
            Check("hotkey message reaches handler", done == fired.Task);
        }
        var dup = deck.Hotkeys.Register("CTRL + ALT + SHIFT + F24", "duplicate", () => { });
        Check("duplicate hotkey reported as conflict", !dup.Ok, dup.Error ?? "");
        var bad = deck.Hotkeys.Register("NOT A KEY", "bad", () => { });
        Check("invalid hotkey rejected gracefully", !bad.Ok);
        deck.RegisterHotkeys();
    }

    private static Task ProfileChecks(Deck deck)
    {
        var changes = 0;
        void H() => changes++;
        deck.ProfileChanged += H;
        foreach (var p in deck.Config.Profiles) deck.SwitchProfile(p, announce: false);
        deck.ProfileChanged -= H;
        Check("profile switching", changes >= deck.Config.Profiles.Count - 1 && deck.ActiveProfile == deck.Config.Profiles[^1], $"{changes} switches");
        var p0 = deck.Config.Profiles[0];
        deck.SwitchProfile(p0, announce: false);
        deck.AddPage(p0);
        deck.SetPage(0);
        deck.ChangePage(1);
        Check("page switching", deck.PageIndex == 1);
        deck.DeletePage(p0, 1);
        Check("page delete", p0.Pages.Count == 1 && deck.PageIndex == 0);
        var dup = deck.DuplicateProfile(p0);
        Check("profile duplicate", dup.Pages[0].Buttons.Count == p0.Pages[0].Buttons.Count && dup.Pages[0].Buttons.All(b => p0.Pages[0].Buttons.All(o => o.Id != b.Id)));
        deck.DeleteProfile(dup);
        var before = p0.Pages.Sum(x => x.Buttons.Count);
        p0.Columns = 3; p0.Rows = 3;
        deck.ProfileEdited(p0);
        Check("grid shrink keeps every button", p0.Pages.Sum(x => x.Buttons.Count) == before, $"{before} buttons over {p0.Pages.Count} pages");
        p0.Columns = 5; p0.Rows = 3;
        deck.ProfileEdited(p0);
        // auto profile rule matching
        Check("auto-profile rule match", Deck.RuleMatches(new AutoProfileRule { Process = "OBS64" }, "obs64.exe") && !Deck.RuleMatches(new AutoProfileRule { Process = "obs64.exe", Enabled = false }, "obs64.exe"));
        return Task.CompletedTask;
    }

    private static async Task ActionChecks(Deck deck)
    {
        var ctx = new ActionContext(ActionSource.Test);
        var r = await deck.Actions.ExecuteAsync(ActionSpec.Of(ActionTypes.Power, PowerCommands.Shutdown), ctx);
        Check("shutdown requires confirmation (declined → nothing happens)", r == ActionResult.Cancelled);
        r = await deck.Actions.ExecuteAsync(new ActionSpec { Type = ActionTypes.LaunchApp, Target = @"C:\Definitely\Missing\app.exe" }, ctx);
        Check("missing executable handled", r.Ok && r.Message == null, "shows CHANGE PATH dialog; cancelled");
        r = await deck.Actions.ExecuteAsync(new ActionSpec { Type = ActionTypes.OpenUrl, Target = "http://exa mple" }, ctx);
        Check("invalid URL handled", !r.Ok, r.Message ?? "");
        r = await deck.Actions.ExecuteAsync(new ActionSpec { Type = ActionTypes.Hotkey, Keys = "CTRL + NOPE" }, ctx);
        Check("invalid shortcut handled", !r.Ok, r.Message ?? "");
        r = await deck.Actions.ExecuteAsync(new ActionSpec { Type = "future-action" }, ctx);
        Check("unknown action type handled", !r.Ok);
        r = await deck.Actions.ExecuteAsync(new ActionSpec { Type = ActionTypes.Sound, Param = "no-such-sound" }, ctx);
        Check("removed sound handled", !r.Ok, r.Message ?? "");
        r = await deck.Actions.ExecuteAsync(new ActionSpec { Type = ActionTypes.Macro }, ctx);
        Check("empty macro handled", !r.Ok);
        var p = deck.ActiveProfile;
        var target = deck.Config.Profiles.First(x => x != p);
        r = await deck.Actions.ExecuteAsync(new ActionSpec { Type = ActionTypes.Deck, Command = DeckCommands.SwitchProfile, Param = target.Id }, ctx);
        Check("deck action: switch profile", r.Ok && deck.ActiveProfile == target);
        deck.SwitchProfile(p, announce: false);

        // Toggle / multi-state progression through the real press pipeline, using deck-only actions.
        var multi = new DeckButton
        {
            Mode = ButtonModes.MultiState, Title = "T",
            Action = new ActionSpec { Type = ActionTypes.None },
            States = { new ButtonState { Title = "B", Action = new ActionSpec { Type = ActionTypes.None } }, new ButtonState { Title = "C", Action = new ActionSpec { Type = ActionTypes.None } } },
        };
        var slot = GridLayout.FindFree(deck.ActivePage, p.Columns, p.Rows);
        if (slot != null)
        {
            (multi.Row, multi.Col) = slot.Value;
            deck.ActivePage.Buttons.Add(multi);
            await deck.PressAsync(multi, ctx);
            var s1 = multi.CurrentState;
            await deck.PressAsync(multi, ctx);
            await deck.PressAsync(multi, ctx);
            Check("multi-state button cycles", s1 == 1 && multi.CurrentState == 0, $"states 0→1→2→0");
            deck.DeleteButton(multi);
        }
        var confirmBtn = new DeckButton { Confirm = true, Action = new ActionSpec { Type = ActionTypes.Deck, Command = DeckCommands.NextProfile } };
        var before = deck.ActiveProfile;
        await deck.PressAsync(confirmBtn, ctx);
        Check("button confirmation gate", deck.ActiveProfile == before, "declined confirmation did not run the action");
    }

    private static async Task SoundboardChecks(Deck deck)
    {
        // Built-in synthesis
        foreach (var (key, _) in Defaults.BuiltInSounds)
        {
            var pcm = SoundSynth.Generate(key, 48000);
            if (pcm == null || pcm.Length < 2000 || pcm.Max(Math.Abs) < 0.1f) { Fail("built-in sound " + key, "empty"); return; }
        }
        Pass("built-in sounds synthesise", string.Join(", ", Defaults.BuiltInSounds.Select(s => s.Key)));

        // Import a WAV through the real import path, then play it silently through the real output.
        var wav = Path.Combine(AppPaths.Root, "selftest.wav");
        WriteWav(wav, SoundSynth.Generate("notification", 44100)!, 44100);
        var item = deck.ImportSound(wav);
        Check("import sound", File.Exists(SoundboardEngine.ResolvePath(item)), item.File);
        if (deck.Audio.Devices(DataFlow.Render).Count == 0) { Warn("soundboard playback", "no output device"); return; }
        deck.Soundboard.MasterVolume = 0f; // silent: the pipeline runs, nothing is heard
        deck.Soundboard.Play(item, "t1", 0f, false, 0, 0, "overlap");
        deck.Soundboard.Play(deck.Config.Sounds.First(s => s.IsBuiltIn), "t2", 0f, false, 20, 20, "overlap");
        Check("simultaneous voices", deck.Soundboard.IsPlaying("t1") && deck.Soundboard.IsPlaying("t2"), deck.Soundboard.ActiveDeviceName);
        var sw = Stopwatch.StartNew();
        while (deck.Soundboard.AnyPlaying && sw.ElapsedMilliseconds < 6000) await Task.Delay(50);
        Check("voices finish and are released", !deck.Soundboard.AnyPlaying, $"{sw.ElapsedMilliseconds} ms");
        deck.Soundboard.Play(item, "t3", 0f, true, 0, 0, "overlap");
        await Task.Delay(300);
        deck.Soundboard.Stop("t3", 50);
        sw.Restart();
        while (deck.Soundboard.IsPlaying("t3") && sw.ElapsedMilliseconds < 2000) await Task.Delay(20);
        Check("looping sound stops with fade", !deck.Soundboard.IsPlaying("t3"));
        File.Delete(SoundboardEngine.ResolvePath(item));
        var r = await deck.Actions.ExecuteAsync(new ActionSpec { Type = ActionTypes.Sound, Param = item.Id }, new ActionContext(ActionSource.Test));
        Check("deleted sound file reported", !r.Ok && (r.Message ?? "").Contains("deleted"), r.Message ?? "");
        deck.DeleteSound(item);
        deck.Soundboard.MasterVolume = deck.Config.Settings.SoundboardVolume / 100f;
    }

    internal static void WriteWav(string path, float[] stereo, int rate)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        var bytes = stereo.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (var s in stereo) w.Write((short)Math.Clamp(s * 32767, -32768, 32767));
    }

    private static async Task RemoteChecks(Deck deck)
    {
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var pin = RemoteServer.NewPin();
        using var srv = new RemoteServer(new RemoteBridge(deck, Application.Current.Dispatcher));
        Check("remote server starts", srv.Start(port, pin), srv.Error ?? $"port {port}");
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(8) };
        var page = await http.GetAsync("/");
        Check("remote page served", page.IsSuccessStatusCode && (await page.Content.ReadAsStringAsync()).Contains("VIRTUAL STREAM DECK"));
        var noPin = await http.GetAsync("/api/layout");
        Check("remote API requires PIN", (int)noPin.StatusCode == 401);
        http.DefaultRequestHeaders.Add("X-Pin", pin);
        var layout = await http.GetStringAsync("/api/layout");
        using var doc = JsonDocument.Parse(layout);
        var buttons = doc.RootElement.GetProperty("buttons");
        Check("remote layout JSON", buttons.GetArrayLength() == deck.ActivePage.Buttons.Count, $"{buttons.GetArrayLength()} buttons");
        if (buttons.GetArrayLength() > 0)
        {
            var id = buttons[0].GetProperty("id").GetString();
            var png = await http.GetByteArrayAsync($"/api/icon/{id}.png");
            Check("remote key image rendered", png.Length > 500 && png[1] == 'P' && png[2] == 'N' && png[3] == 'G', $"{png.Length} bytes");
        }
        var profile = deck.Config.Profiles.First(p => p != deck.ActiveProfile);
        var sw = await http.PostAsync($"/api/profile/{profile.Id}", null);
        Check("remote profile switch", sw.IsSuccessStatusCode && deck.ActiveProfile == profile);
        srv.Stop();
    }

    private static Task TrayChecks(Deck deck)
    {
        using var tray = new TrayIcon(deck.MessageWindow, "Virtual Stream Deck (self-test)");
        var ok = tray.Show();
        if (ok) Pass("tray icon added");
        else Warn("tray icon", "Explorer's notification area did not accept the icon (no shell?)");
        tray.Hide();
        return Task.CompletedTask;
    }

    private static Window Offscreen(Window w, double width, double height)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -32000;
        w.Top = -32000;
        w.Width = width;
        w.Height = height;
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        w.Show();
        w.UpdateLayout();
        return w;
    }

    private static async Task<bool> Render(Window w, string name)
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        w.UpdateLayout();
        if (w.Content is not FrameworkElement root || root.ActualWidth < 10) return false;
        if (_shots == null) return true;
        var dpi = VisualTreeHelper.GetDpi(root);
        var rtb = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(w.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(_shots, name + ".png"));
        enc.Save(fs);
        return true;
    }

    private static async Task WindowChecks(Deck deck)
    {
        var errorsBefore = Log.Recent.Count(e => e.Level == LogLevel.Error);
        foreach (var (cols, rows, size) in new[] { (5, 3, (1280.0, 720.0)), (8, 4, (1920.0, 1080.0)), (3, 3, (900.0, 1400.0)) })
        {
            var p = deck.ActiveProfile;
            var (oc, or) = (p.Columns, p.Rows);
            p.Columns = cols; p.Rows = rows;
            deck.ProfileEdited(p);
            var main = new MainWindow(deck);
            Offscreen(main, size.Item1, size.Item2);
            var ok = await Render(main, $"main-{cols}x{rows}-{size.Item1}x{size.Item2}");
            var views = main.Surface.Views;
            var minKey = views.Count == 0 ? 0 : views.Min(v => Math.Min(v.ActualWidth, v.ActualHeight));
            Check($"main window {size.Item1}×{size.Item2}, grid {cols}×{rows}", ok && views.Count > 0, $"{views.Count} keys, smallest {minKey:0} px");
            if (views.Count > 0 && minKey < 44) Warn("touch target size", $"{minKey:0} px keys at {cols}×{rows} in a {size.Item1}×{size.Item2} window (below 44 px)");

            if (cols == 5)
            {
                deck.SetEditMode(true);
                await Render(main, "main-editmode");
                // Open the editor for every action type through the real editor.
                var b = deck.ActivePage.Buttons.FirstOrDefault();
                var editor = main.Editor;
                if (b != null && editor != null)
                {
                    var fails = new List<string>();
                    foreach (var (type, _) in ActionTypes.All)
                    {
                        try
                        {
                            var clone = Json.Clone(b);
                            clone.Action = new ActionSpec { Type = type };
                            clone.Mode = type == ActionTypes.Macro ? ButtonModes.MultiState : ButtonModes.Standard;
                            if (type == ActionTypes.Macro) clone.Action.Steps.AddRange(new[] { new MacroStep { Type = MacroStepTypes.Combo, Value = "CTRL + C" }, new MacroStep { Type = MacroStepTypes.Delay, Number = 200 }, new MacroStep { Type = MacroStepTypes.Click, AtPosition = true } });
                            editor.Load(clone);
                            main.UpdateLayout();
                        }
                        catch (Exception ex) { fails.Add($"{type}: {ex.Message}"); }
                    }
                    Check("button editor for every action type", fails.Count == 0, fails.Count == 0 ? $"{ActionTypes.All.Length} types" : string.Join("; ", fails));
                    var macroBtn = GridLayout.AllButtons(deck.Config).Select(x => x.Button).FirstOrDefault(x => x.Action.Type == ActionTypes.Macro) ?? b;
                    main.EditButton(macroBtn);
                    await Render(main, "main-editor");
                    main.EditButton(b);
                    await Render(main, "main-editor-mic");
                }
                deck.SetEditMode(false);
            }
            main.ReallyClose = true;
            main.Close();
            p.Columns = oc; p.Rows = or;
            deck.ProfileEdited(p);
        }

        var settings = new SettingsWindow(deck, null);
        Offscreen(settings, 1000, 720);
        foreach (var s in SettingsWindow.Sections)
        {
            settings.ShowSection(s);
            var ok = await Render(settings, "settings-" + s.Replace(' ', '-').ToLowerInvariant());
            Check($"settings: {s}", ok);
        }
        settings.Close();

        var surface = new ControlSurfaceWindow(deck, Displays.All().First());
        Offscreen(surface, 1920, 1080);
        Check("control surface window", await Render(surface, "surface-1920x1080"));
        surface.Close();
        deck.Config.Settings.SurfaceShowBroadcast = true;
        var surface2 = new ControlSurfaceWindow(deck, Displays.All().First());
        Offscreen(surface2, 1920, 1080);
        await Task.Delay(700); // let the meter collect real samples for the shot
        Check("control surface with broadcast console", await Render(surface2, "surface-broadcast"));
        surface2.Close();
        deck.Config.Settings.SurfaceShowBroadcast = false;

        var wiz = new WizardWindow();
        Offscreen(wiz, 780, 600);
        Check("setup wizard", await Render(wiz, "wizard"));
        wiz.Close();

        var remoteKey = DeckButtonView.RenderPng(deck, deck.ActivePage.Buttons.First(), 200, 200);
        Check("off-screen key rendering", remoteKey.Length > 500);
        var errorsAfter = Log.Recent.Count(e => e.Level == LogLevel.Error);
        Check("no errors logged while opening windows", errorsAfter == errorsBefore,
            string.Join("; ", Log.Recent.Where(e => e.Level == LogLevel.Error).Skip(errorsBefore).Select(e => e.Message)));
    }

    private static Task PersistenceChecks(Deck deck)
    {
        deck.Config.Settings.CornerRadius = 9;
        deck.Config.Settings.MicVolumes["test-device"] = 42;
        deck.SaveNow();
        var reloaded = ConfigStore.Load().Config;
        Check("settings persist", reloaded.Settings.CornerRadius == 9 && reloaded.Settings.MicVolumes.GetValueOrDefault("test-device") == 42);

        var pkg = ConfigStore.BuildExport(deck.Config);
        var path = Path.Combine(AppPaths.Root, "export.vsdconfig");
        deck.Config.Settings.ObsPasswordProtected = "secret";
        pkg = ConfigStore.BuildExport(deck.Config);
        ConfigStore.WriteExport(pkg, path);
        var text = File.ReadAllText(path);
        Check("export excludes secrets", !text.Contains("secret"));
        var back = ConfigStore.ImportConfig(ConfigStore.ReadExport(path), deck.Config);
        Check("export → import round trip", back.Profiles.Count == deck.Config.Profiles.Count &&
            GridLayout.AllButtons(back).Count() == GridLayout.AllButtons(deck.Config).Count());
        deck.Config.Settings.ObsPasswordProtected = "";
        return Task.CompletedTask;
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            var r = FindChild<T>(c);
            if (r != null) return r;
        }
        if (root is ContentControl { Content: DependencyObject d }) return d as T ?? FindChild<T>(d);
        return null;
    }
}
