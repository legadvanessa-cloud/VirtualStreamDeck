using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Services;

namespace VirtualStreamDeck.UI.Controls;

/// <summary>
/// Segmented broadcast meter, -60..0 dBFS, drawn directly (no element per segment). Zones:
/// low/silent (below -40), normal broadcast level (-40..-12), high (-12..-3), clipping (above -3).
/// It only ever draws the values it is given - the source is the real capture stream.
/// </summary>
public sealed class LevelMeter : FrameworkElement
{
    public const float Min = -60f;
    private (float Peak, float Rms, float Hold)[] _levels = { (Min, Min, Min) };
    private bool _clip;
    private bool _muted;
    private bool _active;
    private static readonly float[] Ticks = { -60, -40, -24, -12, -6, -3, 0 };
    private static readonly Typeface Face = new("Segoe UI");

    public bool ShowScale { get; set; } = true;
    public double BarHeight { get; set; } = 12;

    public void Set((float Peak, float Rms, float Hold)[] levels, bool clip, bool muted, bool active)
    {
        _levels = levels.Length == 0 ? new[] { (Min, Min, Min) } : levels;
        _clip = clip;
        _muted = muted;
        _active = active;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available)
    {
        var rows = Math.Min(2, _levels.Length);
        var h = rows * BarHeight + (rows - 1) * 5 + (ShowScale ? 16 : 0);
        return new Size(double.IsInfinity(available.Width) ? 240 : available.Width, h);
    }

    public static Color ZoneColor(float db, Palette p) =>
        db >= -3 ? p.Danger : db >= -12 ? p.Warning : db >= -40 ? p.Success : ThemeManager.Mix(p.Success, p.Raised, 0.55);

    protected override void OnRender(DrawingContext dc)
    {
        var p = ThemeManager.Current;
        var labelW = 16.0;
        var dbW = 52.0;
        var w = Math.Max(20, ActualWidth - labelW - dbW);
        var rows = Math.Min(2, _levels.Length);
        const int segs = 40;
        var gap = 2.0;
        var segW = (w - gap * (segs - 1)) / segs;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var r = 0; r < rows; r++)
        {
            var y = r * (BarHeight + 5);
            var lv = rows == 1 && _levels.Length > 1 ? _levels.MaxBy(l => l.Peak) : _levels[r];
            var name = rows == 1 ? "" : r == 0 ? "L" : "R";
            if (name.Length > 0)
                dc.DrawText(new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, ThemeManager.Brush(p.TextMuted), dpi), new Point(0, y + BarHeight / 2 - 7));
            for (var i = 0; i < segs; i++)
            {
                var segDb = Min + (i + 1) * (-Min / segs);
                var lit = _active && !_muted && lv.Peak >= segDb - 0.01f;
                var c = ZoneColor(segDb, p);
                var brush = lit ? ThemeManager.Brush(c) : ThemeManager.Brush(ThemeManager.Mix(p.Input, c, 0.12));
                dc.DrawRoundedRectangle(brush, null, new Rect(labelW + i * (segW + gap), y, Math.Max(1, segW), BarHeight), 1.5, 1.5);
            }
            if (_active && !_muted && lv.Hold > Min + 1)
            {
                var hx = labelW + (lv.Hold - Min) / -Min * w;
                dc.DrawRectangle(ThemeManager.Brush(p.Text), null, new Rect(Math.Min(labelW + w - 2, hx), y - 1, 2, BarHeight + 2));
            }
            var txt = !_active ? "—" : _muted ? "MUTED" : lv.Peak <= Min + 0.5f ? "-∞ dB" : $"{lv.Peak:0} dB";
            var col = _muted ? p.Danger : _clip ? p.Danger : p.TextSecondary;
            var ft = new FormattedText(txt, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 11, ThemeManager.Brush(col), dpi) { TextAlignment = TextAlignment.Right };
            dc.DrawText(ft, new Point(ActualWidth, y + BarHeight / 2 - 8));
        }
        if (!ShowScale) return;
        var sy = rows * BarHeight + (rows - 1) * 5 + 3;
        foreach (var t in Ticks)
        {
            var x = labelW + (t - Min) / -Min * w;
            var ft = new FormattedText(t == 0 ? "0" : t.ToString("0"), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9, ThemeManager.Brush(p.TextMuted), dpi)
            { TextAlignment = t == Min ? TextAlignment.Left : t == 0 ? TextAlignment.Right : TextAlignment.Center };
            dc.DrawText(ft, new Point(x, sy));
        }
    }
}

/// <summary>Large touch-friendly fader: drag (mouse / touch), tap-to-position, wheel and arrow keys.</summary>
public sealed class Fader : Slider
{
    public Fader(double value, int step, Action<int> changed)
    {
        Minimum = 0;
        Maximum = 100;
        Value = value;
        SmallChange = Math.Max(1, step);
        LargeChange = Math.Max(5, step * 4);
        IsMoveToPointEnabled = true;
        SetResourceReference(StyleProperty, "Slider.Fader");
        Stylus.SetIsPressAndHoldEnabled(this, false);
        ValueChanged += (_, e) => { if (!_suppress) changed((int)Math.Round(e.NewValue)); };
        PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            Value = Math.Clamp(Value + Math.Sign(e.Delta) * SmallChange, Minimum, Maximum);
        };
    }

    private bool _suppress;

    /// <summary>Updates the position from outside (a change made in Windows) without echoing it back.</summary>
    public void SetQuiet(double v)
    {
        if (IsMouseCaptureWithin || IsStylusCaptureWithin || Math.Abs(Value - v) < 0.5) return;
        _suppress = true;
        Value = v;
        _suppress = false;
    }
}

/// <summary>
/// Broadcast microphone module: status (ON AIR / MUTED / NO DEVICE), real input meter with clip
/// indicator, volume fader, device switcher, and monitoring.
/// </summary>
public sealed class MicModuleView : Border
{
    private readonly Deck _deck;
    private readonly LevelMeter _meter = new();
    private readonly TextBlock _status = new() { FontSize = 22, FontWeight = FontWeights.Bold, FontFamily = IconVisual.Display };
    private readonly TextBlock _statusDot = new() { Text = "●", FontSize = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _sub = new() { FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
    private readonly Border _clip = new() { CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 2, 7, 2), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _volText = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _dbText = new() { FontSize = 11.5 };
    private readonly TextBlock _device = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ComboBox _devices = new() { MaxDropDownHeight = 320 };
    private readonly Button _onAir = new() { MinHeight = 48, FontSize = 14, FontWeight = FontWeights.Bold };
    private readonly Button _mute = new() { MinHeight = 48, FontSize = 14, FontWeight = FontWeights.Bold };
    private readonly ToggleButton _monitor = new() { MinHeight = 48, FontSize = 13 };
    private readonly Fader _fader;
    private readonly DispatcherTimer _timer;
    private bool _acquired;
    private bool _loadingDevices;
    private readonly bool _compact;

    public MicModuleView(Deck deck, bool compact = false)
    {
        _deck = deck;
        _compact = compact;
        Padding = new Thickness(16);
        CornerRadius = new CornerRadius(14);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "Bg.Panel");
        SetResourceReference(BorderBrushProperty, "Border.Subtle");

        var root = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var t = new TextBlock { Text = "", FontFamily = IconVisual.Glyphs, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        head.Children.Add(t);
        head.Children.Add(new TextBlock { Text = "MICROPHONE", FontWeight = FontWeights.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        _clip.Child = new TextBlock { Text = "CLIP", FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
        DockPanel.SetDock(_clip, Dock.Right);
        head.Children.Insert(0, _clip);
        root.Children.Add(head);

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        statusRow.Children.Add(_statusDot);
        statusRow.Children.Add(_status);
        root.Children.Add(statusRow);
        _sub.HorizontalAlignment = HorizontalAlignment.Center;
        _sub.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        root.Children.Add(_sub);

        root.Children.Add(F.Caps("Mic input", 14));
        _meter.Margin = new Thickness(0, 0, 0, 6);
        root.Children.Add(_meter);

        var volHead = new DockPanel { Margin = new Thickness(0, 16, 0, 6) };
        DockPanel.SetDock(_volText, Dock.Right);
        volHead.Children.Add(_volText);
        volHead.Children.Add(F.Caps("Mic volume", 0));
        root.Children.Add(volHead);
        _fader = new Fader(deck.Audio.Microphone.VolumePercent, deck.Config.Settings.VolumeStep, v =>
        {
            try { _deck.SetMicVolume(v); } catch (Exception ex) { UiHost.Instance.Toast(ex.Message, ToastKind.Error); }
        });
        System.Windows.Automation.AutomationProperties.SetName(_fader, "Microphone volume");
        root.Children.Add(_fader);
        _dbText.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
        _dbText.Margin = new Thickness(0, 6, 0, 0);
        root.Children.Add(_dbText);

        var buttons = new UniformGrid { Columns = 2, Margin = new Thickness(0, 16, 0, 0) };
        _onAir.Content = Label("", "ON AIR");
        _onAir.Click += (_, _) => SetMute(false);
        _mute.Content = Label("", "MUTE");
        _mute.Click += (_, _) => SetMute(true);
        _monitor.Content = Label("", "MONITOR");
        _monitor.SetResourceReference(StyleProperty, "Toggle.Pill");
        _monitor.ToolTip = "Hear your microphone through the monitor output. Use headphones to avoid feedback.";
        _monitor.Click += (_, _) =>
        {
            try { _deck.ToggleMonitor(); } catch (Exception ex) { UiHost.Instance.Toast(ex.Message, ToastKind.Error); }
            Refresh();
        };
        _onAir.Margin = new Thickness(0, 0, 5, 0);
        _mute.Margin = new Thickness(5, 0, 0, 0);
        _monitor.Margin = new Thickness(0, 10, 0, 0);
        buttons.Children.Add(_onAir);
        buttons.Children.Add(_mute);
        root.Children.Add(buttons);
        root.Children.Add(_monitor);

        if (!compact)
        {
            root.Children.Add(F.Caps("Active microphone", 16));
            _devices.SelectionChanged += OnDevicePicked;
            _devices.DropDownOpened += (_, _) => LoadDevices();
            root.Children.Add(_devices);
            var mon = F.Caps("Monitor output", 14);
            root.Children.Add(mon);
            var outs = new List<(string, string)> { ("", "Windows default (communications)") };
            outs.AddRange(SoundboardEngine.OutputDevices().Select(d => (d.Id, d.Name)));
            root.Children.Add(F.Combo(outs, deck.Config.Settings.MicMonitorDevice, v =>
            {
                deck.Config.Settings.MicMonitorDevice = v;
                deck.SaveSoon();
                if (deck.Meter.MonitorEnabled) deck.Meter.SetMonitor(true, v);
            }));
            var mvHead = F.Caps("Monitor volume", 12);
            root.Children.Add(mvHead);
            root.Children.Add(new Fader(deck.Config.Settings.MicMonitorVolume, 5, v =>
            {
                deck.Config.Settings.MicMonitorVolume = v;
                deck.Meter.MonitorVolume = v / 100f;
                deck.SaveSoon();
            }));
        }
        _device.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        _device.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(_device);
        var note = F.Hint("The meter reads the real input stream while this panel is visible, so Windows shows its microphone-in-use icon.", 4);
        note.FontSize = 11;
        root.Children.Add(note);
        Child = root;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();
        IsVisibleChanged += (_, _) => UpdateActive();
        Loaded += (_, _) =>
        {
            var w = Window.GetWindow(this);
            if (w != null && !ReferenceEquals(w, _host)) { _host = w; w.StateChanged += (_, _) => UpdateActive(); }
            UpdateActive();
        };
        Unloaded += (_, _) => Deactivate();
        deck.LiveChanged += Refresh;
        Refresh();
    }

    private static UIElement Label(string glyph, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = glyph, FontFamily = IconVisual.Glyphs, FontSize = 14, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private void SetMute(bool mute)
    {
        try { _deck.Audio.SetMicMute(mute); }
        catch (Exception ex) { UiHost.Instance.Toast(ex.Message, ToastKind.Error); }
    }

    private Window? _host;

    /// <summary>The meter holds the microphone open only while it can actually be seen.</summary>
    private void UpdateActive()
    {
        var seen = IsVisible && _host?.WindowState != WindowState.Minimized;
        if (seen && !_acquired) { _deck.Meter.Acquire(); _acquired = true; _timer.Start(); }
        else if (!seen) Deactivate();
    }

    private void Deactivate()
    {
        if (!_acquired) return;
        _acquired = false;
        _timer.Stop();
        _deck.Meter.Release();
    }

    public void Detach()
    {
        Deactivate();
        _deck.LiveChanged -= Refresh;
    }

    private void Tick()
    {
        var mic = _deck.Audio.Microphone;
        var m = _deck.Meter;
        var levels = m.Levels();
        _meter.Set(levels, m.Clipping, mic.Muted, mic.Available && m.HasSignal);
        _clip.Background = ThemeManager.Brush(m.Clipping ? ThemeManager.Current.Danger : ThemeManager.Mix(ThemeManager.Current.Raised, ThemeManager.Current.Danger, 0.15));
        _clip.Opacity = m.Clipping ? 1 : 0.6;
        _clip.ToolTip = m.Clipping ? "Input is clipping — lower the mic volume" : "Lights when the input reaches 0 dBFS";
        UpdateStatus(levels.Length > 0 ? levels.Max(l => l.PeakDb) : LevelMeter.Min);
    }

    private void UpdateStatus(float peakDb)
    {
        var mic = _deck.Audio.Microphone;
        var p = ThemeManager.Current;
        Color c;
        if (!mic.Available) { _status.Text = "NO DEVICE"; c = p.Warning; _sub.Text = "Connect a microphone or enable one in Windows sound settings"; }
        else if (mic.Muted) { _status.Text = "MUTED"; c = p.Danger; _sub.Text = "Nobody can hear you"; }
        else if (_deck.Meter.Error != null && !_deck.Meter.HasSignal) { _status.Text = "ON AIR"; c = p.Success; _sub.Text = _deck.Meter.Error; }
        else if (!_deck.Meter.HasSignal) { _status.Text = "ON AIR"; c = p.Success; _sub.Text = "Waiting for input…"; }
        else if (peakDb < -50) { _status.Text = "ON AIR"; c = p.Success; _sub.Text = "Live · silent input"; }
        else { _status.Text = "ON AIR"; c = p.Success; _sub.Text = "Live · signal"; }
        _status.Foreground = ThemeManager.Brush(c);
        _statusDot.Foreground = ThemeManager.Brush(c);
    }

    private void Refresh()
    {
        var mic = _deck.Audio.Microphone;
        _fader.IsEnabled = mic.Available;
        _fader.SetQuiet(mic.VolumePercent);
        _volText.Text = mic.Available ? $"{mic.VolumePercent} %" : "—";
        _dbText.Text = !mic.Available ? "" : mic.HasDbRange
            ? $"Device level {mic.Db:+0.0;-0.0;0.0} dB  ·  range {mic.DbMin:0} to {mic.DbMax:+0;-0;0} dB"
            : "This device does not report a dB range.";
        _device.Text = mic.Available ? mic.DeviceName : "No recording device";
        _device.ToolTip = mic.Available ? mic.DeviceName + (_deck.Config.Settings.MicrophoneDevice.Length > 0 ? "\n(pinned in Settings › Audio)" : "\n(Windows default recording device)") : null;
        var p = ThemeManager.Current;
        _onAir.Background = ThemeManager.Brush(mic.Available && !mic.Muted ? ThemeManager.Mix(p.Raised, p.Success, 0.35) : p.Raised);
        _onAir.BorderBrush = ThemeManager.Brush(mic.Available && !mic.Muted ? p.Success : p.BorderStrong);
        _mute.Background = ThemeManager.Brush(mic.Muted ? ThemeManager.Mix(p.Raised, p.Danger, 0.4) : p.Raised);
        _mute.BorderBrush = ThemeManager.Brush(mic.Muted ? p.Danger : p.BorderStrong);
        _onAir.IsEnabled = _mute.IsEnabled = _monitor.IsEnabled = mic.Available;
        _monitor.IsChecked = _deck.Meter.Monitoring;
        if (!_compact && !_devices.IsDropDownOpen) LoadDevices();
        if (!_acquired) UpdateStatus(LevelMeter.Min);
    }

    private void LoadDevices()
    {
        _loadingDevices = true;
        try
        {
            var pinned = _deck.Config.Settings.MicrophoneDevice;
            var devs = _deck.Audio.Devices(DataFlow.Capture);
            var items = new List<(string Id, string Label)>();
            foreach (var d in devs) items.Add((d.Id, d.Name + (d.IsDefault ? "  ★ default" : "")));
            var sig = string.Join("|", items.Select(i => i.Id + i.Label));
            if ((string?)_devices.Tag == sig && _devices.SelectedItem != null) return;
            _devices.Tag = sig;
            _devices.Items.Clear();
            foreach (var (id, label) in items) _devices.Items.Add(new ComboBoxItem { Content = label, Tag = id });
            var current = _deck.Audio.Microphone.Id;
            foreach (ComboBoxItem it in _devices.Items)
                if ((string)it.Tag == current) _devices.SelectedItem = it;
            _devices.ToolTip = pinned.Length > 0 ? "Pinned in Settings › Audio" : "Choosing a device makes it the Windows default recording device";
        }
        finally { _loadingDevices = false; }
    }

    private void OnDevicePicked(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingDevices || _devices.SelectedItem is not ComboBoxItem { Tag: string id } || id == _deck.Audio.Microphone.Id) return;
        try
        {
            if (_deck.Config.Settings.MicrophoneDevice.Length > 0)
            {
                _deck.Config.Settings.MicrophoneDevice = id;
                _deck.SaveSoon();
                _deck.Audio.SetMicrophoneDevice(id);
                UiHost.Instance.Toast("Controlling " + (e.AddedItems[0] as ComboBoxItem)?.Content, ToastKind.Success);
            }
            else
            {
                var name = _deck.Audio.SetDefaultDevice(id);
                UiHost.Instance.Toast("Default microphone: " + name, ToastKind.Success);
            }
        }
        catch (Exception ex) { UiHost.Instance.Toast(ex.Message, ToastKind.Error); }
    }
}

/// <summary>
/// The broadcast console panel: microphone module plus the other on-air sources - soundboard
/// (jingles / effects) and music (the Windows media session).
/// </summary>
public sealed class BroadcastPanel : ScrollViewer
{
    private readonly Deck _deck;
    private readonly MicModuleView _mic;
    private readonly TextBlock _sbState = new() { FontSize = 12 };
    private readonly TextBlock _music = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _musicSub = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Fader _sysVol;
    private readonly TextBlock _sysVolText = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };

    public BroadcastPanel(Deck deck)
    {
        _deck = deck;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Padding = new Thickness(16, 4, 12, 16);
        var sp = new StackPanel();
        _mic = new MicModuleView(deck);
        sp.Children.Add(_mic);

        // Soundboard source
        var sb = new StackPanel();
        sb.Children.Add(SourceHeader("", "SOUNDBOARD · JINGLES & FX"));
        var sbVolHead = new DockPanel { Margin = new Thickness(0, 10, 0, 6) };
        var sbVolText = new TextBlock { Text = $"{deck.Config.Settings.SoundboardVolume} %", FontSize = 13, FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(sbVolText, Dock.Right);
        sbVolHead.Children.Add(sbVolText);
        sbVolHead.Children.Add(F.Caps("Soundboard level", 0));
        sb.Children.Add(sbVolHead);
        sb.Children.Add(new Fader(deck.Config.Settings.SoundboardVolume, 5, v =>
        {
            deck.Config.Settings.SoundboardVolume = v;
            deck.Soundboard.MasterVolume = v / 100f;
            sbVolText.Text = $"{v} %";
            deck.SaveSoon();
        }));
        _sbState.Margin = new Thickness(0, 8, 0, 8);
        _sbState.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        sb.Children.Add(_sbState);
        var stop = F.Button("■  STOP ALL SOUNDS", () => deck.Soundboard.StopAll(Math.Max(60, deck.Config.Settings.DefaultFadeMs)), "Btn.Danger");
        stop.MinHeight = 44;
        sb.Children.Add(stop);
        sp.Children.Add(Card(sb));

        // Music source (Windows media session)
        var mu = new StackPanel();
        mu.Children.Add(SourceHeader("", "MUSIC · MEDIA SESSION"));
        _music.Margin = new Thickness(0, 10, 0, 0);
        mu.Children.Add(_music);
        _musicSub.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        mu.Children.Add(_musicSub);
        var transport = new UniformGrid { Columns = 3, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (g, cmd, tip) in new[] { ("", MediaCmds.Previous, "Previous"), ("", MediaCmds.PlayPause, "Play / pause"), ("", MediaCmds.Next, "Next") })
        {
            var b = new Button { Content = g, FontFamily = IconVisual.Glyphs, FontSize = 16, MinHeight = 44, Margin = new Thickness(2, 0, 2, 0), ToolTip = tip };
            b.Click += (_, _) => _ = deck.RunAsync(ActionSpec.Of(ActionTypes.Media, cmd), new ActionContext(ActionSource.Grid, Window.GetWindow(this)));
            transport.Children.Add(b);
        }
        mu.Children.Add(transport);
        var svHead = new DockPanel { Margin = new Thickness(0, 14, 0, 6) };
        DockPanel.SetDock(_sysVolText, Dock.Right);
        svHead.Children.Add(_sysVolText);
        svHead.Children.Add(F.Caps("Master output (Windows)", 0));
        mu.Children.Add(svHead);
        _sysVol = new Fader(deck.Audio.Speaker.VolumePercent, deck.Config.Settings.VolumeStep, v =>
        {
            try { deck.Audio.SetSpeakerVolume(v); } catch (Exception ex) { UiHost.Instance.Toast(ex.Message, ToastKind.Error); }
        });
        mu.Children.Add(_sysVol);
        sp.Children.Add(Card(mu));
        Content = sp;

        deck.LiveChanged += Refresh;
        Refresh();
    }

    private static Border Card(UIElement child)
    {
        var c = F.Card(child, 16);
        c.Margin = new Thickness(0, 12, 0, 0);
        c.CornerRadius = new CornerRadius(14);
        return c;
    }

    private static UIElement SourceHeader(string glyph, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = glyph, FontFamily = IconVisual.Glyphs, FontSize = 15, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private void Refresh()
    {
        var sb = _deck.Soundboard;
        _sbState.Text = sb.AnyPlaying ? $"Playing on {sb.ActiveDeviceName}" : sb.LastError ?? $"Idle · output: {(_deck.Config.Settings.SoundboardDevice.Length == 0 ? "Windows default" : sb.ActiveDeviceName.Length > 0 ? sb.ActiveDeviceName : "selected device")}";
        var m = _deck.Media;
        if (!m.HasSession) { _music.Text = "No media playing"; _musicSub.Text = "Start music in any app that shows in the Windows volume flyout"; }
        else { _music.Text = m.Title.Length > 0 ? m.Title : "Unknown title"; _musicSub.Text = (m.Playing == true ? "▶ Playing" : "⏸ Paused") + (m.Artist.Length > 0 ? " · " + m.Artist : ""); }
        var spk = _deck.Audio.Speaker;
        _sysVol.IsEnabled = spk.Available;
        _sysVol.SetQuiet(spk.VolumePercent);
        _sysVolText.Text = !spk.Available ? "—" : spk.Muted ? "MUTED" : $"{spk.VolumePercent} %";
        _sysVol.ToolTip = spk.DeviceName;
    }

    public void Detach()
    {
        _mic.Detach();
        _deck.LiveChanged -= Refresh;
    }
}
