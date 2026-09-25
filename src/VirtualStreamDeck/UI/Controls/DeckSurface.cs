using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Services;

namespace VirtualStreamDeck.UI.Controls;

/// <summary>
/// Hosts the button grid of the active page. Shared by the main window and the full-screen control
/// surface; handles mouse, touch and keyboard activation, edit-mode selection and drag &amp; drop.
/// </summary>
public sealed class DeckSurface : Border
{
    private const string DragFormat = "VirtualStreamDeck.Button";
    private readonly Deck _deck;
    private readonly DeckGridPanel _panel = new();
    private readonly Dictionary<string, DeckButtonView> _views = new();
    private DeckButtonView? _pressed;
    private Point _downPoint;
    private bool _dragStarted;
    private string? _selectedId;
    private readonly System.Windows.Threading.DispatcherTimer _meterTimer;

    public ActionSource Source { get; init; } = ActionSource.Grid;
    /// <summary>Larger gaps and touch-first behaviour for the control surface.</summary>
    public bool SurfaceMode { get; init; }

    public event Action<DeckButton>? ButtonSelected;
    public event Action<DeckButton>? EditRequested;
    public event Action? Rebuilt;

    public DeckSurface(Deck deck)
    {
        _deck = deck;
        Child = _panel;
        Background = System.Windows.Media.Brushes.Transparent;
        Focusable = false;
        Stylus.SetIsPressAndHoldEnabled(this, false);
        Stylus.SetIsFlicksEnabled(this, false);
        KeyboardNavigation.SetDirectionalNavigation(_panel, KeyboardNavigationMode.None);
        KeyboardNavigation.SetTabNavigation(_panel, KeyboardNavigationMode.Cycle);
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;

        deck.ProfileChanged += Rebuild;
        deck.LayoutChanged += Rebuild;
        deck.EditModeChanged += Rebuild;
        deck.LiveChanged += RefreshLive;
        deck.SoundProgress += RefreshProgress;
        deck.ButtonFeedback += OnFeedback;
        ThemeManager.Changed += Rebuild;
        _meterTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _meterTimer.Tick += (_, _) => TickMeters();
        IsVisibleChanged += (_, _) => UpdateMeterTimer();
        deck.SettingsApplied += UpdateMeterTimer;
        Rebuild();
    }

    /// <summary>Unhooks from the deck; call when the owning window closes.</summary>
    public void Detach()
    {
        _deck.ProfileChanged -= Rebuild;
        _deck.LayoutChanged -= Rebuild;
        _deck.EditModeChanged -= Rebuild;
        _deck.LiveChanged -= RefreshLive;
        _deck.SoundProgress -= RefreshProgress;
        _deck.ButtonFeedback -= OnFeedback;
        _deck.SettingsApplied -= UpdateMeterTimer;
        ThemeManager.Changed -= Rebuild;
        _meterTimer.Stop();
    }

    private static bool IsMicKey(DeckButton b) =>
        b.Action.Type == ActionTypes.System && b.Action.Command is SysCommands.MicToggle or SysCommands.MicMute or SysCommands.MicUnmute
            or SysCommands.MicVolumeUp or SysCommands.MicVolumeDown or SysCommands.MicVolumeSet;

    private void UpdateMeterTimer()
    {
        var want = IsVisible && _deck.Config.Settings.MeterOnButtons && _views.Values.Any(v => IsMicKey(v.Model));
        if (want) _meterTimer.Start();
        else
        {
            _meterTimer.Stop();
            foreach (var v in _views.Values) v.SetMeter(null, false);
        }
    }

    private void TickMeters()
    {
        var m = _deck.Meter;
        var mic = _deck.Audio.Microphone;
        float? db = mic.Available && !mic.Muted && m.HasSignal ? m.Levels().Max(l => l.PeakDb) : null;
        foreach (var v in _views.Values)
            if (IsMicKey(v.Model)) v.SetMeter(db ?? (mic.Available ? LevelMeter.Min : null), m.Clipping);
    }

    public DeckButton? SelectedButton => _selectedId != null ? _deck.FindButton(_selectedId) : null;

    public void Select(DeckButton? b)
    {
        _selectedId = b?.Id;
        foreach (var v in _views.Values) v.Selected = v.Model.Id == _selectedId;
    }

    public DeckButtonView? ViewFor(string id) => _views.TryGetValue(id, out var v) ? v : null;
    public IReadOnlyCollection<DeckButtonView> Views => _views.Values;

    public void Rebuild()
    {
        var s = _deck.Config.Settings;
        var profile = _deck.ActiveProfile;
        var page = _deck.ActivePage;
        var edit = _deck.EditMode && !SurfaceMode;
        var focusedId = (Keyboard.FocusedElement as DeckButtonView)?.Model.Id;

        _panel.Columns = profile.Columns;
        _panel.Rows = profile.Rows;
        _panel.Square = s.SquareButtons;
        _panel.Gap = (SurfaceMode ? 16 : 12) * (s.CompactMode ? 0.6 : 1);
        _panel.MaxCell = s.ButtonSize switch { "small" => 110, "medium" => 150, "large" => 210, _ => double.PositiveInfinity };
        _panel.Children.Clear();
        _views.Clear();

        var occupied = GridLayout.Occupancy(page, profile.Columns, profile.Rows);
        foreach (var b in page.Buttons)
        {
            var v = new DeckButtonView(b);
            v.Update(_deck, edit);
            v.Selected = edit && b.Id == _selectedId;
            DeckGridPanel.Place(v, b.Row, b.Col, b.RowSpan, b.ColSpan);
            Wire(v, edit);
            _panel.Children.Add(v);
            _views[b.Id] = v;
        }
        for (var r = 0; r < profile.Rows; r++)
        for (var c = 0; c < profile.Columns; c++)
        {
            if (occupied[r, c]) continue;
            var e = new EmptySlotView(r, c, edit);
            DeckGridPanel.Place(e, r, c);
            if (edit) WireEmpty(e);
            _panel.Children.Add(e);
        }
        _panel.InvalidateMeasure();
        UpdateMeterTimer();
        if (focusedId != null && _views.TryGetValue(focusedId, out var fv)) Dispatcher.BeginInvoke(() => fv.Focus());
        Rebuilt?.Invoke();
    }

    private void RefreshLive()
    {
        var edit = _deck.EditMode && !SurfaceMode;
        foreach (var v in _views.Values) v.Update(_deck, edit);
    }

    private void RefreshProgress()
    {
        foreach (var v in _views.Values)
        {
            if (v.Model.Action.Type != ActionTypes.Sound) continue;
            v.UpdateProgress(_deck.Soundboard.IsPlaying(v.Model.Id) ? _deck.Soundboard.Progress(v.Model.Id) ?? -1 : null);
        }
    }

    private void OnFeedback(string id, bool ok)
    {
        if (_views.TryGetValue(id, out var v)) v.Flash(ok);
    }

    private Window? OwnerWindow => Window.GetWindow(this);
    private ActionContext Ctx => new(Source, OwnerWindow);

    // ---- pointer input ---------------------------------------------------------------------

    private void Wire(DeckButtonView v, bool edit)
    {
        v.AllowDrop = edit;
        v.MouseLeftButtonDown += (_, e) =>
        {
            if (e.StylusDevice != null) return; // touch is handled by the Touch events below
            e.Handled = true;
            v.CaptureMouse();
            Down(v, e.GetPosition(this));
        };
        v.MouseMove += (_, e) =>
        {
            if (e.StylusDevice != null || _pressed != v || e.LeftButton != MouseButtonState.Pressed) return;
            MaybeDrag(v, e.GetPosition(this));
        };
        v.MouseLeftButtonUp += (_, e) =>
        {
            if (e.StylusDevice != null) return;
            e.Handled = true;
            var inside = v.IsMouseOver;
            v.ReleaseMouseCapture();
            Up(v, inside);
        };
        v.LostMouseCapture += (_, _) => { if (_pressed == v && !_dragStarted && Mouse.LeftButton != MouseButtonState.Pressed) Up(v, false); };
        v.TouchDown += (_, e) =>
        {
            e.Handled = true;
            v.CaptureTouch(e.TouchDevice);
            Down(v, e.GetTouchPoint(this).Position);
        };
        v.TouchMove += (_, e) =>
        {
            if (_pressed == v) MaybeDrag(v, e.GetTouchPoint(this).Position);
        };
        v.TouchUp += (_, e) =>
        {
            e.Handled = true;
            var pt = e.GetTouchPoint(v).Position;
            var inside = pt.X >= 0 && pt.Y >= 0 && pt.X <= v.ActualWidth && pt.Y <= v.ActualHeight;
            v.ReleaseTouchCapture(e.TouchDevice);
            Up(v, inside);
        };
        v.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowContextMenu(v);
        };
        v.DragEnter += (_, e) => { if (e.Data.GetDataPresent(DragFormat)) v.Selected = true; };
        v.DragLeave += (_, _) => v.Selected = v.Model.Id == _selectedId;
        v.Drop += (_, e) => DropOn(e, v.Model.Row, v.Model.Col);
    }

    private void WireEmpty(EmptySlotView e)
    {
        e.MouseLeftButtonUp += (_, _) => AddAt(e.Row, e.Col);
        e.KeyDown += (_, k) => { if (k.Key is Key.Enter or Key.Space) { k.Handled = true; AddAt(e.Row, e.Col); } };
        e.TouchUp += (_, t) => { t.Handled = true; AddAt(e.Row, e.Col); };
        e.DragEnter += (_, _) => e.DragHighlight(true);
        e.DragLeave += (_, _) => e.DragHighlight(false);
        e.Drop += (_, d) => DropOn(d, e.Row, e.Col);
    }

    private void AddAt(int row, int col)
    {
        var b = _deck.AddButton(row, col);
        Select(b);
        ButtonSelected?.Invoke(b);
    }

    private void Down(DeckButtonView v, Point p)
    {
        _pressed = v;
        _downPoint = p;
        _dragStarted = false;
        if (!(_deck.EditMode && !SurfaceMode)) v.Focus();
        v.PressVisual(true);
        if (_deck.EditMode && !SurfaceMode) return;
        if (!_deck.Config.Settings.TriggerOnRelease || v.Model.Mode == ButtonModes.Momentary)
            _ = _deck.PressAsync(v.Model, Ctx);
    }

    private void Up(DeckButtonView v, bool inside)
    {
        if (_pressed != v) return;
        _pressed = null;
        v.PressVisual(false);
        if (_dragStarted) return;
        if (_deck.EditMode && !SurfaceMode)
        {
            if (!inside) return;
            Select(v.Model);
            ButtonSelected?.Invoke(v.Model);
            return;
        }
        if (v.Model.Mode == ButtonModes.Momentary) { _ = _deck.ReleaseAsync(v.Model, Ctx); return; }
        if (_deck.Config.Settings.TriggerOnRelease && inside) _ = _deck.PressAsync(v.Model, Ctx);
    }

    private void MaybeDrag(DeckButtonView v, Point p)
    {
        if (!_deck.EditMode || SurfaceMode || _dragStarted) return;
        if (Math.Abs(p.X - _downPoint.X) < 8 && Math.Abs(p.Y - _downPoint.Y) < 8) return;
        _dragStarted = true;
        v.PressVisual(false);
        v.ReleaseMouseCapture();
        v.ReleaseAllTouchCaptures();
        _pressed = null;
        v.Opacity = 0.5;
        try { DragDrop.DoDragDrop(v, new DataObject(DragFormat, v.Model.Id), DragDropEffects.Move); }
        finally { v.Opacity = 1; }
    }

    private void DropOn(DragEventArgs e, int row, int col)
    {
        if (e.Data.GetData(SoundLibraryPanel.DragFormat) is string soundId)
        {
            e.Handled = true;
            var sound = _deck.Config.Sounds.FirstOrDefault(s => s.Id == soundId);
            if (sound == null) return;
            var target = GridLayout.ButtonAt(_deck.ActivePage, row, col) ?? _deck.AddButton(row, col);
            _deck.CommitButton(target, SoundLibraryPanel.SoundButton(target, sound));
            UiHost.Instance.Toast($"\"{sound.Name}\" assigned", ToastKind.Success);
            return;
        }
        if (e.Data.GetData(DragFormat) is not string id) return;
        e.Handled = true;
        var b = _deck.FindButton(id);
        if (b == null) return;
        var loc = _deck.Locate(b);
        if (loc != null && loc.Value.Page != _deck.ActivePage)
        {
            // Dropped from another page (via the page tabs): bring it to this page first.
            if (!GridLayout.Transfer(loc.Value.Page, _deck.ActivePage, _deck.ActiveProfile.Columns, _deck.ActiveProfile.Rows, b)) return;
        }
        _deck.MoveOrSwap(b, row, col);
    }

    public static string DragData => DragFormat;

    private void ShowContextMenu(DeckButtonView v)
    {
        if (SurfaceMode) return;
        var b = v.Model;
        var m = new ContextMenu();
        MenuItem Item(string header, Action a)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => a();
            m.Items.Add(mi);
            return mi;
        }
        Item("Edit button…", () => { _deck.SetEditMode(true); Select(b); EditRequested?.Invoke(b); });
        Item("Run now", () => _ = _deck.PressAsync(b, Ctx));
        m.Items.Add(new Separator());
        Item("Duplicate", () => { var c = _deck.DuplicateButton(b); if (c != null && _deck.EditMode) { Select(c); ButtonSelected?.Invoke(c); } });
        var move = new MenuItem { Header = "Move to profile" };
        foreach (var p in _deck.Config.Profiles.Where(p => p.Id != _deck.ActiveProfile.Id))
        {
            var mi = new MenuItem { Header = p.Name };
            var target = p;
            mi.Click += (_, _) => _deck.MoveToProfile(b, target);
            move.Items.Add(mi);
        }
        move.IsEnabled = move.Items.Count > 0;
        m.Items.Add(move);
        if (_deck.ActiveProfile.Pages.Count > 1)
        {
            var mp = new MenuItem { Header = "Move to page" };
            for (var i = 0; i < _deck.ActiveProfile.Pages.Count; i++)
            {
                if (i == _deck.PageIndex) continue;
                var idx = i;
                var mi = new MenuItem { Header = $"Page {i + 1}" };
                mi.Click += (_, _) => _deck.MoveToProfile(b, _deck.ActiveProfile, idx);
                mp.Items.Add(mi);
            }
            m.Items.Add(mp);
        }
        m.Items.Add(new Separator());
        Item("Delete", async () =>
        {
            if (await UiHost.Instance.ConfirmAsync(Ctx, $"Delete \"{(b.Title.Length > 0 ? b.Title : "this button")}\"?", "This cannot be undone.", "DELETE", true))
            {
                if (_selectedId == b.Id) Select(null);
                _deck.DeleteButton(b);
            }
        });
        m.PlacementTarget = v;
        m.IsOpen = true;
    }

    // ---- keyboard --------------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is not DeckButtonView v || !_views.ContainsValue(v)) return;
        switch (e.Key)
        {
            case Key.Left: e.Handled = MoveFocus(v, 0, -1); break;
            case Key.Right: e.Handled = MoveFocus(v, 0, 1); break;
            case Key.Up: e.Handled = MoveFocus(v, -1, 0); break;
            case Key.Down: e.Handled = MoveFocus(v, 1, 0); break;
            case Key.Enter:
            case Key.Space:
                e.Handled = true;
                if (e.IsRepeat) return;
                if (_deck.EditMode && !SurfaceMode) { Select(v.Model); ButtonSelected?.Invoke(v.Model); return; }
                v.PressVisual(true);
                _ = _deck.PressAsync(v.Model, Ctx);
                break;
            case Key.Delete when _deck.EditMode:
                e.Handled = true;
                ShowDeleteConfirm(v.Model);
                break;
        }
    }

    private async void ShowDeleteConfirm(DeckButton b)
    {
        if (await UiHost.Instance.ConfirmAsync(Ctx, $"Delete \"{(b.Title.Length > 0 ? b.Title : "this button")}\"?", "This cannot be undone.", "DELETE", true))
        {
            if (_selectedId == b.Id) Select(null);
            _deck.DeleteButton(b);
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || Keyboard.FocusedElement is not DeckButtonView v) return;
        v.PressVisual(false);
        if (!_deck.EditMode) _ = _deck.ReleaseAsync(v.Model, Ctx);
    }

    /// <summary>Spatial navigation: the nearest button in the arrow's direction.</summary>
    private bool MoveFocus(DeckButtonView from, int dr, int dc)
    {
        var b = from.Model;
        DeckButtonView? best = null;
        var bestScore = double.MaxValue;
        foreach (var v in _views.Values)
        {
            if (v == from) continue;
            var m = v.Model;
            var drow = m.Row - b.Row;
            var dcol = m.Col - b.Col;
            if (dr != 0 && Math.Sign(drow) != dr) continue;
            if (dc != 0 && Math.Sign(dcol) != dc) continue;
            var score = dr != 0 ? Math.Abs(drow) * 10 + Math.Abs(dcol) : Math.Abs(dcol) * 10 + Math.Abs(drow);
            if (score < bestScore) { bestScore = score; best = v; }
        }
        if (best == null) return false;
        best.Focus();
        return true;
    }

    public void FocusFirst()
    {
        var first = _views.Values.OrderBy(v => v.Model.Row).ThenBy(v => v.Model.Col).FirstOrDefault();
        first?.Focus();
    }
}
