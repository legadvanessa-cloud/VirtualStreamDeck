using System.Diagnostics;
using System.Runtime.InteropServices;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Interop;
using static VirtualStreamDeck.Interop.Native;

namespace VirtualStreamDeck.Services;

/// <summary>Where synthesized input goes. The real sink is SendInput; tests record instead.</summary>
internal interface IInputSink
{
    void Send(INPUT[] inputs);
}

internal sealed class SendInputSink : IInputSink
{
    public void Send(INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            throw new InvalidOperationException(
                "Windows blocked the simulated input (the target window may be running as administrator, or a secure desktop is active).");
    }
}

/// <summary>Builds INPUT sequences for key chords, text and mouse events. Pure; unit tested.</summary>
internal static class InputBuilder
{
    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RETURN = 0x0D;

    public static Func<int, ushort> ScanCode = vk => (ushort)MapVirtualKey((uint)vk, 0);

    public static INPUT Key(int vk, bool up)
    {
        var flags = up ? KEYEVENTF_KEYUP : 0;
        if (KeyChord.IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = ScanCode(vk), dwFlags = flags } } };
    }

    private static IEnumerable<int> ModifierKeys(ChordModifiers m)
    {
        if (m.HasFlag(ChordModifiers.Ctrl)) yield return VK_CONTROL;
        if (m.HasFlag(ChordModifiers.Alt)) yield return VK_MENU;
        if (m.HasFlag(ChordModifiers.Shift)) yield return VK_SHIFT;
        if (m.HasFlag(ChordModifiers.Win)) yield return VK_LWIN;
    }

    public static List<INPUT> ChordDown(KeyChord c)
    {
        var list = ModifierKeys(c.Modifiers).Select(vk => Key(vk, false)).ToList();
        if (c.HasKey) list.Add(Key(c.VirtualKey, false));
        return list;
    }

    public static List<INPUT> ChordUp(KeyChord c)
    {
        var list = new List<INPUT>();
        if (c.HasKey) list.Add(Key(c.VirtualKey, true));
        list.AddRange(ModifierKeys(c.Modifiers).Reverse().Select(vk => Key(vk, true)));
        return list;
    }

    public static List<INPUT> Tap(KeyChord c)
    {
        var l = ChordDown(c);
        l.AddRange(ChordUp(c));
        return l;
    }

    public static List<INPUT> Text(string text)
    {
        var list = new List<INPUT>(text.Length * 2);
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\n') { list.Add(Key(VK_RETURN, false)); list.Add(Key(VK_RETURN, true)); continue; }
            if (ch == '\t') { list.Add(Key(0x09, false)); list.Add(Key(0x09, true)); continue; }
            foreach (var up in new[] { false, true })
                list.Add(new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } },
                });
        }
        return list;
    }

    public static INPUT Mouse(uint flags, int dx = 0, int dy = 0) =>
        new() { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags } } };

    public static (uint Down, uint Up) ButtonFlags(string button) => button.ToLowerInvariant() switch
    {
        "right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
        "middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
        _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
    };
}

public sealed class InputSender
{
    private readonly IInputSink _sink;
    internal InputSender(IInputSink sink) => _sink = sink;
    public InputSender() : this(new SendInputSink()) { }

    public void Tap(KeyChord c) => _sink.Send(InputBuilder.Tap(c).ToArray());
    public void Down(KeyChord c) => _sink.Send(InputBuilder.ChordDown(c).ToArray());
    public void Up(KeyChord c) => _sink.Send(InputBuilder.ChordUp(c).ToArray());

    public void Type(string text)
    {
        var all = InputBuilder.Text(text);
        // Chunk long text so a single SendInput call never floods the input queue.
        for (var i = 0; i < all.Count; i += 200) _sink.Send(all.Skip(i).Take(200).ToArray());
    }

    /// <summary>Moves the cursor, clamped to the virtual desktop so it can never land off-screen.</summary>
    public void MoveTo(int x, int y)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN)), vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        x = Math.Clamp(x, vx, vx + vw - 1);
        y = Math.Clamp(y, vy, vy + vh - 1);
        var nx = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1, vw - 1));
        var ny = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1, vh - 1));
        _sink.Send(new[] { InputBuilder.Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny) });
    }

    public void Click(string button)
    {
        var (d, u) = InputBuilder.ButtonFlags(button);
        _sink.Send(new[] { InputBuilder.Mouse(d), InputBuilder.Mouse(u) });
    }

    /// <summary>
    /// When an action is fired by a global hotkey the user is still physically holding its
    /// modifiers; injecting CTRL+SHIFT+M while ALT is down would produce CTRL+ALT+SHIFT+M.
    /// </summary>
    public static async Task WaitForModifiersReleasedAsync(int timeoutMs = 1200)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var held = new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);
            if (!held) return;
            await Task.Delay(25);
        }
    }
}

/// <summary>Brings other applications' windows to the front.</summary>
public static class WindowFocus
{
    public static IntPtr FindMainWindow(string processName)
    {
        var name = Path.GetFileNameWithoutExtension(processName);
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName(name))
        {
            pids.Add((uint)p.Id);
            p.Dispose();
        }
        if (pids.Count == 0) return IntPtr.Zero;
        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) && !IsIconic(h)) return true;
            if (GetWindowTextLength(h) == 0) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid) || GetAncestor(h, GA_ROOTOWNER) != h) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// SetForegroundWindow is refused unless the caller "owns" the last input. Tapping ALT first
    /// is the documented-behaviour workaround Windows itself honours for accessibility tools.
    /// </summary>
    public static bool Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        if (SetForegroundWindow(hwnd)) return true;
        try
        {
            new SendInputSink().Send(new[] { InputBuilder.Key(InputBuilder.VK_MENU, false), InputBuilder.Key(InputBuilder.VK_MENU, true) });
        }
        catch { /* best effort */ }
        return SetForegroundWindow(hwnd);
    }

    public static string? ProcessPathOfWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return ProcessPath(pid);
    }

    public static string? ProcessPath(uint pid)
    {
        if (pid == 0) return null;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }
}
