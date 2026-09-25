using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VirtualStreamDeck.Interop;

namespace VirtualStreamDeck.Services;

public sealed record DisplayInfo(
    int Number,
    string DeviceName,
    string FriendlyName,
    bool IsPrimary,
    Native.RECT Bounds,
    Native.RECT WorkArea,
    int Dpi)
{
    public string Label => $"Display {Number}{(IsPrimary ? " (main)" : "")} — {Bounds.Width}×{Bounds.Height}" +
                           (string.IsNullOrWhiteSpace(FriendlyName) ? "" : $" · {FriendlyName}");
    public double Scale => Dpi / 96.0;
}

/// <summary>DisplayManager: monitor enumeration and moving windows onto a specific monitor.</summary>
public static class Displays
{
    public static List<IntPtr> MonitorHandles()
    {
        var list = new List<IntPtr>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT _, IntPtr _) => { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    public static List<DisplayInfo> All()
    {
        var result = new List<DisplayInfo>();
        foreach (var h in MonitorHandles())
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (!Native.GetMonitorInfo(h, ref mi)) continue;
            var dd = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
            var friendly = Native.EnumDisplayDevices(mi.szDevice, 0, ref dd, 0) ? dd.DeviceString : "";
            uint dpi = 96;
            try { Native.GetDpiForMonitor(h, 0, out dpi, out _); } catch { }
            var num = ParseNumber(mi.szDevice);
            result.Add(new DisplayInfo(num, mi.szDevice, friendly, (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0, mi.rcMonitor, mi.rcWork, (int)dpi));
        }
        // Stable, human numbering: Windows' own DISPLAYn index, renumbered 1..N.
        result = result.OrderBy(d => d.Number).ToList();
        for (var i = 0; i < result.Count; i++) result[i] = result[i] with { Number = i + 1 };
        return result;
    }

    private static int ParseNumber(string device)
    {
        var digits = new string(device.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out var n) ? n : 99;
    }

    /// <summary>
    /// Picks the display for the control surface: the remembered one if still connected,
    /// otherwise the first non-primary display, otherwise the primary.
    /// </summary>
    public static DisplayInfo Choose(string rememberedDevice, out bool fellBack)
    {
        var all = All();
        fellBack = false;
        var hit = all.FirstOrDefault(d => d.DeviceName.Equals(rememberedDevice, StringComparison.OrdinalIgnoreCase));
        if (hit != null) return hit;
        fellBack = !string.IsNullOrEmpty(rememberedDevice);
        return all.FirstOrDefault(d => !d.IsPrimary) ?? all.FirstOrDefault(d => d.IsPrimary) ?? all.First();
    }

    public static DisplayInfo? ForWindow(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return null;
        var mon = Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return null;
        return All().FirstOrDefault(d => d.DeviceName == mi.szDevice);
    }

    /// <summary>Moves a window (by physical pixels, so it is DPI-correct) onto a display's work area.</summary>
    public static void MoveTo(Window w, DisplayInfo d, bool fill)
    {
        var h = new WindowInteropHelper(w).EnsureHandle();
        var r = fill ? d.Bounds : d.WorkArea;
        if (fill)
        {
            Native.SetWindowPos(h, Native.HWND_TOP, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        }
        else
        {
            var width = Math.Min(r.Width, (int)(1280 * d.Scale));
            var height = Math.Min(r.Height, (int)(800 * d.Scale));
            Native.SetWindowPos(h, Native.HWND_TOP, r.Left + (r.Width - width) / 2, r.Top + (r.Height - height) / 2, width, height, Native.SWP_NOACTIVATE);
        }
    }
}
