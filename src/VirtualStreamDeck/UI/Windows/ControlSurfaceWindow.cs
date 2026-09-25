using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Interop;
using VirtualStreamDeck.Services;
using VirtualStreamDeck.UI.Controls;

namespace VirtualStreamDeck.UI.Windows;

/// <summary>
/// Full-screen control surface for a second monitor / touchscreen. It never takes keyboard focus
/// (WS_EX_NOACTIVATE), so shortcuts fired from it land in the app the user is working in; exit
/// needs a deliberate press-and-hold so a stray touch can't close it.
/// </summary>
public sealed class ControlSurfaceWindow : Window
{
    private readonly Deck _deck;
    private readonly DeckSurface _surface;
    private readonly ToastHost _toasts = new();
    private readonly TextBlock _profile = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, FontFamily = IconVisual.Display, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _pages = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _statusBar = new() { Padding = new Thickness(22, 8, 22, 10) };
    private readonly Ellipse _holdRing = new() { Width = 46, Height = 46, StrokeThickness = 3, IsHitTestVisible = false };
    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly DispatcherTimer _cursorTimer = new();
    private readonly DispatcherTimer _statusTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    private readonly Grid _layout = new();
    private BroadcastPanel? _broadcast;
    private DateTime _holdStart;
    private bool _holding;
    public DisplayInfo Display { get; private set; }

    public ControlSurfaceWindow(Deck deck, DisplayInfo display)
    {
        _deck = deck;
        Display = display;
        var s = deck.Config.Settings;
        Title = "Virtual Stream Deck — Control Surface";
        Icon = ChromeWindow.AppIcon;
        SetResourceReference(BackgroundProperty, "Bg.App");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        SetResourceReference(FontFamilyProperty, "Font.UI");
        ShowInTaskbar = true;
        Topmost = s.SurfaceTopmost;
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (s.SurfaceFullscreen)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
        }
        Stylus.SetIsPressAndHoldEnabled(this, false);
        Stylus.SetIsFlicksEnabled(this, false);
        Stylus.SetIsTapFeedbackEnabled(this, false);
        UiHost.Instance.Register(this, _toasts);

        _surface = new DeckSurface(deck) { Source = ActionSource.Surface, SurfaceMode = true, Margin = new Thickness(28, 10, 28, 18) };
        Content = BuildLayout();

        deck.ProfileChanged += Refresh;
        deck.LayoutChanged += Refresh;
        deck.LiveChanged += RefreshStatus;
        _holdTimer.Tick += (_, _) => HoldTick();
        _statusTimer.Tick += (_, _) => { deck.Monitor.Sample(); RefreshStatus(); };
        _cursorTimer.Tick += (_, _) => { _cursorTimer.Stop(); if (_deck.Config.Settings.SurfaceAutoHideCursor) Cursor = Cursors.None; };
        MouseMove += (_, e) =>
        {
            if (e.StylusDevice != null) return;
            if (Cursor == Cursors.None) Cursor = null;
            RestartCursorTimer();
        };
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => { Refresh(); RefreshStatus(); _statusTimer.Start(); RestartCursorTimer(); };
        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        Closed += (_, _) => Cleanup();
    }

    private UIElement BuildLayout()
    {
        var s = _deck.Config.Settings;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new DockPanel { Margin = new Thickness(30, 16, 18, 0) };
        var exit = new Grid { Width = 52, Height = 52, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Press and hold to close the control surface" };
        var bg = new Ellipse();
        bg.SetResourceReference(Shape.FillProperty, "Bg.Raised");
        exit.Children.Add(bg);
        _holdRing.SetResourceReference(Shape.StrokeProperty, "Accent");
        _holdRing.StrokeDashArray = new DoubleCollection { 0, 100 };
        exit.Children.Add(_holdRing);
        exit.Children.Add(new TextBlock { Text = "", FontFamily = IconVisual.Glyphs, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        exit.MouseLeftButtonDown += (_, e) => { if (e.StylusDevice == null) { exit.CaptureMouse(); StartHold(); } };
        exit.MouseLeftButtonUp += (_, e) => { if (e.StylusDevice == null) { exit.ReleaseMouseCapture(); StopHold(); } };
        exit.TouchDown += (_, e) => { e.Handled = true; exit.CaptureTouch(e.TouchDevice); StartHold(); };
        exit.TouchUp += (_, e) => { e.Handled = true; exit.ReleaseTouchCapture(e.TouchDevice); StopHold(); };
        exit.LostMouseCapture += (_, _) => { if (!exit.AreAnyTouchesCaptured) StopHold(); };
        DockPanel.SetDock(exit, Dock.Right);
        top.Children.Add(exit);

        var prof = new Button { Content = "", FontFamily = IconVisual.Glyphs, FontSize = 12, Width = 44, Height = 44, Margin = new Thickness(10, 0, 0, 0), ToolTip = "Switch profile" };
        prof.SetResourceReference(StyleProperty, "Btn.Ghost");
        prof.Click += (_, _) => ProfileMenu(prof);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_profile);
        left.Children.Add(prof);
        left.Children.Add(new Border { Width = 18 });
        left.Children.Add(_pages);
        top.Children.Add(left);
        root.Children.Add(top);

        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _layout.Children.Add(_surface);
        if (s.SurfaceShowBroadcast)
        {
            _broadcast = new BroadcastPanel(_deck) { Width = 400, Margin = new Thickness(0, 10, 18, 18) };
            Grid.SetColumn(_broadcast, 1);
            _layout.Children.Add(_broadcast);
        }
        Grid.SetRow(_layout, 1);
        root.Children.Add(_layout);

        _statusBar.SetResourceReference(Border.BackgroundProperty, "Bg.Panel");
        _statusBar.Child = _status;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        _statusBar.Visibility = s.SurfaceShowStatus ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(_statusBar, 2);
        root.Children.Add(_statusBar);

        var overlay = new Grid();
        overlay.Children.Add(root);
        overlay.Children.Add(_toasts);
        return overlay;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (AppShell.Verifying) return; // self-test renders it off-screen; never move it onto a real display
        var h = new WindowInteropHelper(this).Handle;
        if (_deck.Config.Settings.SurfaceNoActivate)
        {
            var ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE));
        }
        Place();
    }

    private void Place()
    {
        var fs = _deck.Config.Settings.SurfaceFullscreen;
        if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
        Displays.MoveTo(this, Display, fill: false);
        if (fs) Dispatcher.BeginInvoke(() => WindowState = WindowState.Maximized, DispatcherPriority.Loaded);
    }

    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var all = Displays.All();
            var still = all.FirstOrDefault(d => d.DeviceName == Display.DeviceName);
            if (still != null) { Display = still; if (!_deck.Config.Settings.SurfaceFullscreen) return; Place(); return; }
            var fallback = Displays.Choose("", out _);
            UiHost.Instance.Toast($"The surface display was disconnected — moved to Display {fallback.Number}.", ToastKind.Warning);
            Display = fallback;
            Place();
        });
    }

    private void RestartCursorTimer()
    {
        _cursorTimer.Stop();
        if (!_deck.Config.Settings.SurfaceAutoHideCursor) return;
        _cursorTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, _deck.Config.Settings.SurfaceCursorHideSeconds));
        _cursorTimer.Start();
    }

    private void StartHold()
    {
        _holding = true;
        _holdStart = DateTime.UtcNow;
        _holdTimer.Start();
    }

    private void StopHold()
    {
        _holding = false;
        _holdTimer.Stop();
        _holdRing.StrokeDashArray = new DoubleCollection { 0, 100 };
    }

    private void HoldTick()
    {
        if (!_holding) return;
        var t = (DateTime.UtcNow - _holdStart).TotalMilliseconds / 1200.0;
        var circumference = Math.PI * (_holdRing.Width - _holdRing.StrokeThickness) / _holdRing.StrokeThickness;
        _holdRing.StrokeDashArray = new DoubleCollection { Math.Min(1, t) * circumference, 100 };
        if (t >= 1) { StopHold(); Close(); }
    }

    private void ProfileMenu(FrameworkElement anchor)
    {
        var m = new ContextMenu { FontSize = 16 };
        foreach (var p in _deck.Config.Profiles)
        {
            var mi = new MenuItem { Header = p.Name, IsCheckable = true, IsChecked = p.Id == _deck.ActiveProfile.Id, MinHeight = 48 };
            var prof = p;
            mi.Click += (_, _) => _deck.SwitchProfile(prof, announce: true);
            m.Items.Add(mi);
        }
        m.PlacementTarget = anchor;
        m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        m.IsOpen = true;
    }

    private void Refresh()
    {
        var p = _deck.ActiveProfile;
        _profile.Text = p.Name.ToUpperInvariant();
        _pages.Children.Clear();
        if (p.Pages.Count <= 1) return;
        var prev = SurfaceButton("", () => _deck.ChangePage(-1));
        _pages.Children.Add(prev);
        for (var i = 0; i < p.Pages.Count; i++)
        {
            var idx = i;
            var b = SurfaceButton((i + 1).ToString(), () => _deck.SetPage(idx), glyph: false);
            if (i == _deck.PageIndex) b.SetResourceReference(StyleProperty, "Btn.Accent");
            _pages.Children.Add(b);
        }
        _pages.Children.Add(SurfaceButton("", () => _deck.ChangePage(1)));
    }

    private static Button SurfaceButton(string text, Action click, bool glyph = true)
    {
        var b = new Button { Content = text, MinWidth = 48, Height = 48, Margin = new Thickness(0, 0, 8, 0), FontSize = glyph ? 14 : 16 };
        if (glyph) b.FontFamily = IconVisual.Glyphs;
        b.Click += (_, _) => click();
        return b;
    }

    private void RefreshStatus()
    {
        if (_statusBar.Visibility != Visibility.Visible) return;
        var m = _deck.Monitor;
        var mic = _deck.Audio.Microphone;
        var spk = _deck.Audio.Speaker;
        var cam = _deck.Camera.State;
        var parts = new List<string>
        {
            $"CPU {m.CpuPercent}%", $"RAM {m.RamPercent}%",
            mic.Available ? $"MIC {(mic.Muted ? "MUTED" : "ON AIR")}" : "MIC NO DEVICE",
            !cam.AnyCamera ? "CAM NONE" : cam.AllDisabled ? "CAM DISABLED" : cam.InUse ? "CAM IN USE" : "CAM IDLE",
            spk.Available ? (spk.Muted ? "AUDIO MUTED" : $"AUDIO {spk.VolumePercent}%") : "AUDIO —",
        };
        if (_deck.Config.Settings.ObsEnabled) parts.Add(_deck.Obs.Status == ObsStatus.Connected ? (_deck.Obs.Streaming ? "OBS LIVE" : "OBS READY") : "OBS OFFLINE");
        parts.Add(DateTime.Now.ToString("HH:mm"));
        _status.Text = string.Join("   |   ", parts);
    }

    private void Cleanup()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        _deck.ProfileChanged -= Refresh;
        _deck.LayoutChanged -= Refresh;
        _deck.LiveChanged -= RefreshStatus;
        _holdTimer.Stop();
        _statusTimer.Stop();
        _cursorTimer.Stop();
        _surface.Detach();
        _broadcast?.Detach();
    }
}
