using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Interop;

namespace VirtualStreamDeck.Services;

/// <summary>
/// One hidden top-level window that receives WM_HOTKEY, tray callbacks and the TaskbarCreated
/// broadcast (message-only windows do not get broadcasts, so it is a real but invisible window).
/// </summary>
public sealed class MessageWindow : IDisposable
{
    private readonly HwndSource _src;
    public IntPtr Handle => _src.Handle;
    public event Func<int, IntPtr, IntPtr, bool>? Message;

    public MessageWindow()
    {
        var p = new HwndSourceParameters("VirtualStreamDeck.MessageWindow")
        {
            Width = 0, Height = 0, PositionX = -32000, PositionY = -32000,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            ExtendedWindowStyle = (int)Native.WS_EX_TOOLWINDOW,
        };
        _src = new HwndSource(p);
        _src.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (Message != null)
            foreach (Func<int, IntPtr, IntPtr, bool> h in Message.GetInvocationList())
                if (h(msg, w, l)) { handled = true; break; }
        return IntPtr.Zero;
    }

    public void Dispose() => _src.Dispose();
}

public sealed record HotkeyRegistration(string Chord, string Label, bool Ok, string? Error);

/// <summary>
/// HotkeyManager: system-wide shortcuts via RegisterHotKey. A chord another application already
/// owns fails to register - that is reported per binding (never thrown), so one conflict can't
/// stop the rest from working.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly MessageWindow _win;
    private readonly Dictionary<int, Action> _handlers = new();
    private readonly Dictionary<string, int> _chordIds = new();
    private readonly List<HotkeyRegistration> _registrations = new();
    private int _nextId = 1;

    public IReadOnlyList<HotkeyRegistration> Registrations => _registrations;

    public HotkeyManager(MessageWindow win)
    {
        _win = win;
        _win.Message += OnMessage;
    }

    private bool OnMessage(int msg, IntPtr w, IntPtr l)
    {
        if (msg != Native.WM_HOTKEY) return false;
        if (_handlers.TryGetValue(w.ToInt32(), out var h))
        {
            try { h(); } catch (Exception ex) { Log.Error("Hotkey handler failed", ex); }
        }
        return true;
    }

    public HotkeyRegistration Register(string chordText, string label, Action handler)
    {
        HotkeyRegistration reg;
        if (!KeyChord.TryParse(chordText, out var chord) || !chord.HasKey)
        {
            reg = new HotkeyRegistration(chordText, label, false, "Not a valid key combination.");
        }
        else if (chord.Modifiers == ChordModifiers.None && !(chord.VirtualKey is >= 0x7C and <= 0x87)) // F13-F24 alone are fine
        {
            reg = new HotkeyRegistration(chord.ToString(), label, false, "Global shortcuts need CTRL, ALT, SHIFT or WIN (except F13–F24).");
        }
        else if (_registrations.Any(r => r.Ok && r.Chord == chord.ToString()))
        {
            reg = new HotkeyRegistration(chord.ToString(), label, false,
                $"Already used by \"{_registrations.First(r => r.Ok && r.Chord == chord.ToString()).Label}\".");
        }
        else
        {
            var id = _nextId++;
            if (Native.RegisterHotKey(_win.Handle, id, (uint)chord.Modifiers | Native.MOD_NOREPEAT, (uint)chord.VirtualKey))
            {
                _handlers[id] = handler;
                _chordIds[chord.ToString()] = id;
                reg = new HotkeyRegistration(chord.ToString(), label, true, null);
            }
            else
            {
                var err = Marshal.GetLastWin32Error();
                reg = new HotkeyRegistration(chord.ToString(), label, false,
                    err == 1409 ? "Another application already uses this shortcut." : new System.ComponentModel.Win32Exception(err).Message);
            }
        }
        _registrations.Add(reg);
        if (!reg.Ok) Log.Warn($"Hotkey {reg.Chord} ({label}) not registered: {reg.Error}");
        return reg;
    }

    public void UnregisterAll()
    {
        foreach (var id in _handlers.Keys) Native.UnregisterHotKey(_win.Handle, id);
        _handlers.Clear();
        _chordIds.Clear();
        _registrations.Clear();
    }

    /// <summary>Test hook: delivers WM_HOTKEY for a registered chord through the real message path.</summary>
    internal bool Simulate(string chord) =>
        _chordIds.TryGetValue(KeyChord.Normalize(chord), out var id) &&
        Native.PostMessage(_win.Handle, Native.WM_HOTKEY, new IntPtr(id), IntPtr.Zero);

    public void Dispose()
    {
        UnregisterAll();
        _win.Message -= OnMessage;
    }
}

/// <summary>
/// Event-driven foreground tracking (SetWinEventHook - no polling). Remembers the last window that
/// was not ours, so shortcuts pressed on the deck can be delivered to the app the user was using.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private readonly Native.WinEventDelegate _cb; // held so the GC can't collect the native callback
    private IntPtr _hook;
    private readonly uint _ownPid = (uint)Environment.ProcessId;

    public IntPtr LastExternalWindow { get; private set; }
    public string LastExternalProcess { get; private set; } = "";
    public event Action<string>? ForegroundProcessChanged;

    public ForegroundWatcher()
    {
        _cb = OnEvent;
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _cb, 0, 0,
            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        Capture(Native.GetForegroundWindow());
    }

    public bool Active => _hook != IntPtr.Zero;

    private void OnEvent(IntPtr hook, uint type, IntPtr hwnd, int obj, int child, uint thread, uint time) => Capture(hwnd);

    private void Capture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _ownPid || pid == 0) return;
        LastExternalWindow = hwnd;
        var path = WindowFocus.ProcessPath(pid);
        var name = path != null ? Path.GetFileName(path) : "";
        if (name.Length == 0) return;
        if (!name.Equals(LastExternalProcess, StringComparison.OrdinalIgnoreCase))
        {
            LastExternalProcess = name;
            ForegroundProcessChanged?.Invoke(name);
        }
    }

    /// <summary>If one of our windows is in front, hands focus back to the app the user was in.</summary>
    public async Task RestoreExternalFocusAsync()
    {
        var fg = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(fg, out var pid);
        if (pid != _ownPid) return;
        var target = LastExternalWindow;
        if (target == IntPtr.Zero || !Native.IsWindow(target)) return;
        WindowFocus.Activate(target);
        await Task.Delay(60);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }
}

/// <summary>TrayManager: a Shell_NotifyIcon icon owned by the message window.</summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 77; // WM_APP + n
    private readonly MessageWindow _win;
    private readonly int _taskbarCreated;
    private IntPtr _hIcon;
    private bool _added;
    private string _tip;

    public event Action? LeftClick;
    public event Action? RightClick;
    public bool IsVisible => _added;

    public TrayIcon(MessageWindow win, string tip)
    {
        _win = win;
        _tip = tip;
        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        _hIcon = LoadIcon();
        _win.Message += OnMessage;
    }

    private static IntPtr LoadIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (info == null) return IntPtr.Zero;
            using var ms = new MemoryStream();
            info.Stream.CopyTo(ms);
            var bytes = ms.ToArray();
            // ICO directory: pick the entry closest to the small-icon size for crisp rendering.
            int count = BitConverter.ToUInt16(bytes, 4), best = -1, bestScore = int.MaxValue;
            var want = Native.GetSystemMetrics(49); // SM_CXSMICON
            for (var i = 0; i < count; i++)
            {
                var e = 6 + i * 16;
                var w = bytes[e] == 0 ? 256 : bytes[e];
                var score = w >= want ? w - want : (want - w) * 4;
                if (score < bestScore) { bestScore = score; best = i; }
            }
            if (best < 0) return IntPtr.Zero;
            var entry = 6 + best * 16;
            var size = BitConverter.ToInt32(bytes, entry + 8);
            var offset = BitConverter.ToInt32(bytes, entry + 12);
            var img = new byte[size];
            Array.Copy(bytes, offset, img, 0, size);
            return Native.CreateIconFromResourceEx(img, (uint)size, true, 0x00030000, want, want, 0);
        }
        catch (Exception ex)
        {
            Log.Warn("Tray icon image failed: " + ex.Message);
            return IntPtr.Zero;
        }
    }

    private Native.NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
        hWnd = _win.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _hIcon,
        szTip = _tip.Length > 127 ? _tip[..127] : _tip,
        szInfo = "", szInfoTitle = "",
    };

    public bool Show()
    {
        if (_added) return true;
        var d = Data(Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP);
        _added = Native.Shell_NotifyIcon(Native.NIM_ADD, ref d);
        if (_added)
        {
            d.uVersion = 4;
            Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref d);
        }
        else Log.Warn("Could not add the tray icon (Explorer's notification area is unavailable).");
        return _added;
    }

    public void SetTip(string tip)
    {
        _tip = tip;
        if (!_added) return;
        var d = Data(Native.NIF_TIP | Native.NIF_SHOWTIP);
        Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref d);
    }

    public void Hide()
    {
        if (!_added) return;
        var d = Data(0);
        Native.Shell_NotifyIcon(Native.NIM_DELETE, ref d);
        _added = false;
    }

    private bool OnMessage(int msg, IntPtr w, IntPtr l)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            _added = false; // Explorer restarted - the old icon is gone
            Show();
            return false;
        }
        if (msg != CallbackMessage) return false;
        var evt = (int)(l.ToInt64() & 0xFFFF); // NOTIFYICON_VERSION_4: event in LOWORD(lParam)
        switch (evt)
        {
            case Native.WM_LBUTTONUP: LeftClick?.Invoke(); break;
            case Native.WM_CONTEXTMENU: RightClick?.Invoke(); break; // v4 sends this after WM_RBUTTONUP
        }
        return true;
    }

    public void Dispose()
    {
        Hide();
        _win.Message -= OnMessage;
        if (_hIcon != IntPtr.Zero) Native.DestroyIcon(_hIcon);
        _hIcon = IntPtr.Zero;
    }
}
