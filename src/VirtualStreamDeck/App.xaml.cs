using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Interop;
using VirtualStreamDeck.Services;
using VirtualStreamDeck.UI;
using VirtualStreamDeck.UI.Controls;
using VirtualStreamDeck.UI.Windows;

namespace VirtualStreamDeck;

/// <summary>Static entry points the windows use to reach application-level behaviour.</summary>
public static class AppShell
{
    internal static App Current => (App)Application.Current;
    public static bool Verifying { get; internal set; }
    public static bool TrayAvailable => Current.Tray?.IsVisible == true;
    public static RemoteServer? Remote => Current.Remote;

    public static void ToggleSurface() => Current.ToggleSurface();
    public static void OpenSurfaceOn(DisplayInfo d) => Current.OpenSurface(d, remember: true);
    public static void OpenSettings(Window? owner, string section = "General") => Current.OpenSettings(owner, section);
    public static void ShowMain() => Current.ShowMain();
    public static void Exit() => Current.ExitApp();
    public static void ApplyRemote() => Current.ApplyRemote();
    public static void RunWizard(Window? owner) => Current.RunWizard(owner, firstRun: false);
}

public partial class App : Application
{
    private Mutex? _single;
    private EventWaitHandle? _activate;
    internal Deck? Deck { get; private set; }
    internal MainWindow? Dashboard { get; private set; }
    internal ControlSurfaceWindow? Surface { get; private set; }
    internal TrayIcon? Tray { get; private set; }
    internal RemoteServer? Remote { get; private set; }
    private SettingsWindow? _settings;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var data = Arg("--data");
        if (data != null) AppPaths.Override(data);

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled UI exception", ex.Exception);
            ex.Handled = true;
            try { UiHost.Instance.Toast("Something went wrong: " + ex.Exception.Message, ToastKind.Error); } catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Unhandled exception", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Unobserved task exception", ex.Exception); ex.SetObserved(); };

        if (args.Contains("--verify"))
        {
            AppShell.Verifying = true;
            var code = await SelfTest.RunAsync(this, Arg("--out"), Arg("--shots"));
            Shutdown(code);
            return;
        }

        // Single instance per data folder: a second launch just brings the first one forward.
        var key = "VirtualStreamDeck-" + ((uint)AppPaths.Root.ToLowerInvariant().GetHashCode()).ToString("x8");
        _single = new Mutex(true, @"Local\" + key, out var created);
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + key + "-activate");
        if (!created)
        {
            _activate.Set();
            Shutdown(0);
            return;
        }
        var t = new Thread(() =>
        {
            while (_activate.WaitOne())
            {
                if (_exiting) return;
                Dispatcher.BeginInvoke(ShowMain);
            }
        }) { IsBackground = true, Name = "activate-listener" };
        t.Start();

        StartApp(args.Contains("--autostart") || args.Contains("--minimized"), args.Contains("--surface"));
    }

    internal Deck StartDeck()
    {
        var deck = new Deck(Dispatcher) { Ui = UiHost.Instance };
        Deck = deck;
        ThemeManager.Apply(deck.Config.Settings);
        ChordBox.Recording += recording =>
        {
            if (recording) deck.Hotkeys.UnregisterAll();
            else deck.RegisterHotkeys();
        };
        deck.ToggleSurfaceRequested += ToggleSurface;
        deck.ShowMainRequested += ShowMain;
        return deck;
    }

    private void StartApp(bool autostart, bool openSurface)
    {
        var deck = StartDeck();
        if (deck.IsFirstRun && !RunWizard(null, firstRun: true))
        {
            // Wizard closed without finishing: start with the defaults rather than nagging again.
            deck.Config.FirstRunCompleted = true;
            deck.SaveNow();
        }

        Dashboard = new MainWindow(deck);
        Dashboard.RestorePlacement();
        MainWindow = Dashboard;

        Tray = new TrayIcon(deck.MessageWindow, AppInfo.Name);
        Tray.LeftClick += ShowMain;
        Tray.RightClick += ShowTrayMenu;
        Tray.Show();

        var s = deck.Config.Settings;
        var hidden = (autostart || s.StartMinimized) && Tray.IsVisible;
        if (!hidden) Dashboard.Show();
        else if (!Tray.IsVisible) { Dashboard.Show(); Dashboard.WindowState = WindowState.Minimized; }

        deck.StartServices();
        ApplyRemote();
        if (openSurface || s.SurfaceOpenOnStart) Dispatcher.BeginInvoke(() => OpenSurface(null, remember: false), DispatcherPriority.Background);
        if (deck.StartupWarning != null) Dispatcher.BeginInvoke(() => UiHost.Instance.Toast(deck.StartupWarning, ToastKind.Warning), DispatcherPriority.ApplicationIdle);
        var failed = deck.Hotkeys.Registrations.Where(r => !r.Ok).ToList();
        if (failed.Count > 0)
            Dispatcher.BeginInvoke(() => UiHost.Instance.Toast(
                $"{failed.Count} shortcut(s) are used by another program: {string.Join(", ", failed.Take(3).Select(f => f.Chord))}. See Settings › Hotkeys.", ToastKind.Warning),
                DispatcherPriority.ApplicationIdle);
    }

    // ---- wizard ----------------------------------------------------------------------------

    internal bool RunWizard(Window? owner, bool firstRun)
    {
        var deck = Deck!;
        var w = new WizardWindow();
        if (owner is { IsVisible: true }) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        if (w.ShowDialog() != true || w.Result == null) return false;
        var r = w.Result;
        if (firstRun)
        {
            var cfg = Defaults.CreateConfig(r.Templates, r.Cols, r.Rows);
            cfg.Settings = deck.Config.Settings;
            cfg.Sounds = deck.Config.Sounds.Count > 0 ? deck.Config.Sounds : cfg.Sounds;
            cfg.FirstRunCompleted = true;
            ApplySurfaceChoice(cfg.Settings, r);
            cfg.Settings.DefaultColumns = r.Cols;
            cfg.Settings.DefaultRows = r.Rows;
            deck.ReplaceConfig(cfg);
        }
        else
        {
            foreach (var t in r.Templates)
            {
                var p = Defaults.CreateProfile(t, r.Cols, r.Rows);
                if (deck.Config.Profiles.Any(x => x.Name == p.Name)) p.Name += " (new)";
                deck.Config.Profiles.Add(p);
            }
            ApplySurfaceChoice(deck.Config.Settings, r);
            deck.Config.Settings.DefaultColumns = r.Cols;
            deck.Config.Settings.DefaultRows = r.Rows;
            deck.ReplaceConfig(deck.Config);
            UiHost.Instance.Toast($"Added {r.Templates.Count} profile(s)", ToastKind.Success);
        }
        ThemeManager.Apply(deck.Config.Settings);
        return true;
    }

    private static void ApplySurfaceChoice(AppSettings s, WizardResult r)
    {
        switch (r.Surface)
        {
            case "secondary":
                s.SurfaceDisplay = r.DisplayDevice;
                s.SurfaceOpenOnStart = true;
                break;
            case "touch":
                s.SurfaceDisplay = r.DisplayDevice;
                s.SurfaceOpenOnStart = true;
                s.TouchMode = true;
                s.SurfaceAutoHideCursor = true;
                s.ButtonSize = "fill";
                break;
            default:
                s.SurfaceOpenOnStart = false;
                break;
        }
    }

    // ---- windows ---------------------------------------------------------------------------

    internal void ShowMain()
    {
        if (Dashboard == null) return;
        if (!Dashboard.IsVisible) Dashboard.Show();
        if (Dashboard.WindowState == WindowState.Minimized) Dashboard.WindowState = WindowState.Normal;
        Dashboard.Activate();
        var h = new WindowInteropHelper(Dashboard).Handle;
        WindowFocus.Activate(h);
    }

    internal void OpenSettings(Window? owner, string section)
    {
        if (_settings is { IsLoaded: true })
        {
            _settings.ShowSection(section);
            _settings.Activate();
            return;
        }
        _settings = new SettingsWindow(Deck!, owner ?? Dashboard, section);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
    }

    internal void ToggleSurface()
    {
        if (Surface != null) { Surface.Close(); return; }
        OpenSurface(null, remember: false);
    }

    internal void OpenSurface(DisplayInfo? display, bool remember)
    {
        var deck = Deck!;
        var s = deck.Config.Settings;
        bool fellBack = false;
        display ??= Displays.Choose(s.SurfaceDisplay, out fellBack);
        if (fellBack) UiHost.Instance.Toast($"The saved surface display is not connected — using Display {display.Number}.", ToastKind.Warning);
        if (remember && s.RememberDisplay) { s.SurfaceDisplay = display.DeviceName; deck.SaveSoon(); }
        Surface?.Close();
        var w = new ControlSurfaceWindow(deck, display);
        w.Closed += (_, _) =>
        {
            if (Surface == w) Surface = null;
            deck.SurfaceOpen = false;
            SleepBlocker.Set(false);
            deck.QueueLive();
        };
        Surface = w;
        deck.SurfaceOpen = true;
        if (s.SurfacePreventSleep) SleepBlocker.Set(true);
        w.Show();
        deck.QueueLive();
    }

    internal void ApplyRemote()
    {
        var deck = Deck!;
        var s = deck.Config.Settings;
        if (!s.RemoteEnabled)
        {
            Remote?.Stop();
            return;
        }
        if (s.RemotePin.Length != 6) { s.RemotePin = RemoteServer.NewPin(); deck.SaveSoon(); }
        Remote ??= new RemoteServer(new RemoteBridge(deck, Dispatcher));
        if (!Remote.Start(s.RemotePort, s.RemotePin))
            UiHost.Instance.Toast("Tablet remote: " + Remote.Error, ToastKind.Error);
    }

    // ---- tray ------------------------------------------------------------------------------

    private void ShowTrayMenu()
    {
        var deck = Deck!;
        var m = new ContextMenu();
        var header = new MenuItem { Header = AppInfo.Name, IsEnabled = false, FontWeight = FontWeights.SemiBold };
        m.Items.Add(header);
        m.Items.Add(new Separator());
        MenuItem Item(string h, Action a, bool? check = null)
        {
            var mi = new MenuItem { Header = h };
            if (check != null) { mi.IsCheckable = true; mi.IsChecked = check.Value; }
            mi.Click += (_, _) => a();
            m.Items.Add(mi);
            return mi;
        }
        Item("Open", ShowMain);
        Item("Control Surface", ToggleSurface, Surface != null);
        var profiles = new MenuItem { Header = "Profiles" };
        foreach (var p in deck.Config.Profiles)
        {
            var mi = new MenuItem { Header = p.Name, IsCheckable = true, IsChecked = p.Id == deck.ActiveProfile.Id };
            var prof = p;
            mi.Click += (_, _) => deck.SwitchProfile(prof, announce: true);
            profiles.Items.Add(mi);
        }
        m.Items.Add(profiles);
        var mic = deck.Audio.Microphone;
        var micItem = Item("Mute Microphone", () => _ = deck.RunAsync(ActionSpec.Of(ActionTypes.System, SysCommands.MicToggle), new ActionContext(ActionSource.Tray)), mic.Muted);
        micItem.IsEnabled = mic.Available;
        Item("Enable Auto Profile", () => deck.SetAutoProfile(!deck.Config.Settings.AutoProfile), deck.Config.Settings.AutoProfile);
        Item("Settings", () => OpenSettings(null, "General"));
        m.Items.Add(new Separator());
        Item("Exit", ExitApp);
        m.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        m.Opened += (_, _) =>
        {
            // Without owning the foreground the menu would not close when clicking elsewhere.
            if (PresentationSource.FromVisual(m) is HwndSource src) Native.SetForegroundWindow(src.Handle);
        };
        m.IsOpen = true;
    }

    // ---- exit ------------------------------------------------------------------------------

    internal void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            Dashboard?.SavePlacement();
            Surface?.Close();
            _settings?.Close();
            Remote?.Dispose();
            Tray?.Dispose();
            Deck?.Dispose();
            if (Dashboard != null) { Dashboard.ReallyClose = true; Dashboard.Close(); }
        }
        catch (Exception ex) { Log.Error("Shutdown", ex); }
        try { _activate?.Set(); } catch { }
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting && !AppShell.Verifying)
        {
            _exiting = true;
            try { Tray?.Dispose(); Remote?.Dispose(); Deck?.Dispose(); } catch { }
        }
        base.OnExit(e);
    }
}
