using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Interop;
using Windows.Devices.Radios;
using Windows.Media.Control;

namespace VirtualStreamDeck.Services;

// ============================================================================================
// Brightness: WMI for built-in panels, DDC/CI for external monitors. Both are real, supported
// interfaces; a display that answers neither is reported as unsupported.
// ============================================================================================
public static class Brightness
{
    public sealed record Result(bool Supported, int Percent, int Changed, string Detail);

    public static Result Adjust(int delta, int? absolute = null)
    {
        int changed = 0, last = -1;
        var details = new List<string>();

        // Laptop / tablet panel
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
            var cur = -1;
            foreach (ManagementObject mo in s.Get()) using (mo) cur = Convert.ToInt32(mo["CurrentBrightness"]);
            if (cur >= 0)
            {
                var next = Math.Clamp(absolute ?? cur + delta, 0, 100);
                using var m = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE");
                foreach (ManagementObject mo in m.Get())
                    using (mo) mo.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)next });
                changed++; last = next; details.Add("built-in display");
            }
        }
        catch (ManagementException) { /* no internal panel: normal on desktops */ }
        catch (Exception ex) { Log.Warn("WMI brightness failed: " + ex.Message); }

        // External monitors via DDC/CI
        foreach (var hmon in Displays.MonitorHandles())
        {
            if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out var count) || count == 0) continue;
            var arr = new Native.PHYSICAL_MONITOR[count];
            if (!Native.GetPhysicalMonitorsFromHMONITOR(hmon, count, arr)) continue;
            try
            {
                foreach (var pm in arr)
                {
                    if (!Native.GetMonitorBrightness(pm.hPhysicalMonitor, out var min, out var cur, out var max) || max <= min) continue;
                    var curPct = (int)Math.Round((cur - min) * 100.0 / (max - min));
                    var nextPct = Math.Clamp(absolute ?? curPct + delta, 0, 100);
                    var raw = (uint)Math.Round(min + (max - min) * nextPct / 100.0);
                    if (Native.SetMonitorBrightness(pm.hPhysicalMonitor, raw)) { changed++; last = nextPct; details.Add(pm.szPhysicalMonitorDescription); }
                }
            }
            finally { Native.DestroyPhysicalMonitors(count, arr); }
        }

        return changed == 0
            ? new Result(false, -1, 0, "No display accepted a brightness change. Built-in panels need WMI support; external monitors need DDC/CI enabled in their on-screen menu.")
            : new Result(true, last, changed, string.Join(", ", details));
    }
}

// ============================================================================================
// Wi-Fi / Bluetooth radios through Windows.Devices.Radios - the same switch as Quick Settings.
// ============================================================================================
public sealed class RadioService
{
    private readonly Dictionary<RadioKind, Radio> _radios = new();
    public event Action? Changed;
    public bool Initialised { get; private set; }
    public string? AccessProblem { get; private set; }

    public async Task InitAsync()
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
                AccessProblem = access == RadioAccessStatus.DeniedByUser
                    ? "Radio control is turned off in Windows privacy settings (Radios)."
                    : $"Windows denied radio access ({access}).";
            foreach (var r in await Radio.GetRadiosAsync())
            {
                if (r.Kind is not (RadioKind.WiFi or RadioKind.Bluetooth) || _radios.ContainsKey(r.Kind)) continue;
                _radios[r.Kind] = r;
                r.StateChanged += (_, _) => Changed?.Invoke();
            }
        }
        catch (Exception ex) { AccessProblem = "Radio API unavailable: " + ex.Message; Log.Warn(AccessProblem); }
        Initialised = true;
        Changed?.Invoke();
    }

    /// <summary>null = no such radio on this PC.</summary>
    public bool? IsOn(RadioKind kind) => _radios.TryGetValue(kind, out var r) ? r.State == RadioState.On : null;
    public string? NameOf(RadioKind kind) => _radios.TryGetValue(kind, out var r) ? r.Name : null;

    public async Task<bool> ToggleAsync(RadioKind kind)
    {
        if (!Initialised) await InitAsync();
        if (!_radios.TryGetValue(kind, out var r))
            throw new InvalidOperationException(kind == RadioKind.WiFi ? "This PC has no Wi-Fi adapter." : "This PC has no Bluetooth radio.");
        if (r.State == RadioState.Disabled)
            throw new InvalidOperationException($"{r.Name} is disabled in Device Manager or by a hardware switch.");
        var target = r.State == RadioState.On ? RadioState.Off : RadioState.On;
        var result = await r.SetStateAsync(target);
        if (result != RadioAccessStatus.Allowed)
            throw new InvalidOperationException(AccessProblem ?? $"Windows refused the change ({result}). Check Settings > Privacy > Radios.");
        return target == RadioState.On;
    }
}

// ============================================================================================
// Media: the Windows media session (GSMTC) - the same one the volume flyout controls - with a
// virtual media-key fallback when no app has registered a session.
// ============================================================================================
public sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _session;
    public event Action? Changed;

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public bool? Playing { get; private set; }
    public bool HasSession => _session != null;

    public async Task InitAsync()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (_, _) => Rebind();
            Rebind();
        }
        catch (Exception ex) { Log.Warn("Media session manager unavailable: " + ex.Message); }
    }

    private void Rebind()
    {
        try
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnProps;
                _session.PlaybackInfoChanged -= OnPlayback;
            }
            _session = _mgr?.GetCurrentSession();
            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnProps;
                _session.PlaybackInfoChanged += OnPlayback;
            }
            _ = RefreshAsync();
        }
        catch (Exception ex) { Log.Warn("Media rebind failed: " + ex.Message); }
    }

    private void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = RefreshAsync();
    private void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        var s = _session;
        if (s == null) { Title = Artist = ""; Playing = null; Changed?.Invoke(); return; }
        try
        {
            var p = await s.TryGetMediaPropertiesAsync();
            Title = p?.Title ?? "";
            Artist = p?.Artist ?? "";
            Playing = s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch { Title = Artist = ""; Playing = null; }
        Changed?.Invoke();
    }

    /// <summary>Returns true when handled by the media session, false when the key fallback was used.</summary>
    public async Task<bool> SendAsync(string command, InputSender input)
    {
        var s = _session;
        if (s != null)
        {
            try
            {
                var ok = command switch
                {
                    MediaCmds.PlayPause => await s.TryTogglePlayPauseAsync(),
                    MediaCmds.Next => await s.TrySkipNextAsync(),
                    MediaCmds.Previous => await s.TrySkipPreviousAsync(),
                    MediaCmds.Stop => await s.TryStopAsync(),
                    _ => false,
                };
                if (ok) return true;
            }
            catch (Exception ex) { Log.Warn("Media session command failed, using media key: " + ex.Message); }
        }
        var vk = command switch
        {
            MediaCmds.PlayPause => 0xB3, MediaCmds.Next => 0xB0, MediaCmds.Previous => 0xB1, MediaCmds.Stop => 0xB2,
            _ => 0,
        };
        if (vk == 0) throw new InvalidOperationException("Unknown media command.");
        input.Tap(new KeyChord(ChordModifiers.None, vk));
        return false;
    }
}

// ============================================================================================
// Power
// ============================================================================================
public static class PowerControl
{
    private static void EnableShutdownPrivilege()
    {
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TOKEN_ADJUST_PRIVILEGES | Native.TOKEN_QUERY, out var token)) return;
        try
        {
            if (!Native.LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) return;
            var tp = new Native.TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = Native.SE_PRIVILEGE_ENABLED };
            Native.AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { Native.CloseHandle(token); }
    }

    public static void Execute(string command)
    {
        bool ok;
        switch (command)
        {
            case PowerCommands.Lock:
                ok = Native.LockWorkStation();
                break;
            case PowerCommands.Sleep:
            case PowerCommands.Hibernate:
                EnableShutdownPrivilege();
                ok = Native.SetSuspendState(command == PowerCommands.Hibernate, false, false);
                break;
            case PowerCommands.Restart:
                EnableShutdownPrivilege();
                ok = Native.ExitWindowsEx(Native.EWX_REBOOT, Native.SHTDN_REASON_FLAG_PLANNED);
                break;
            case PowerCommands.Shutdown:
                EnableShutdownPrivilege();
                ok = Native.ExitWindowsEx(Native.EWX_POWEROFF | Native.EWX_SHUTDOWN, Native.SHTDN_REASON_FLAG_PLANNED);
                break;
            case PowerCommands.SignOut:
                ok = Native.ExitWindowsEx(Native.EWX_LOGOFF, 0);
                break;
            default:
                throw new InvalidOperationException($"Unknown power command '{command}'.");
        }
        if (!ok)
        {
            var err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(command == PowerCommands.Hibernate && err == 50
                ? "Hibernation is not enabled on this PC."
                : $"Windows refused the request ({new System.ComponentModel.Win32Exception(err).Message}).");
        }
    }
}

// ============================================================================================
// Status bar metrics. Cheap native calls only; the caller's timer runs only while a window is visible.
// ============================================================================================
public sealed class SystemMonitor
{
    private ulong _lastIdle, _lastTotal;
    private DateTime _lastNet = DateTime.MinValue;
    private volatile bool _netDirty = true;

    public int CpuPercent { get; private set; }
    public int RamPercent { get; private set; }
    public string Network { get; private set; } = "—";

    public SystemMonitor()
    {
        NetworkChange.NetworkAddressChanged += (_, _) => _netDirty = true;
        NetworkChange.NetworkAvailabilityChanged += (_, _) => _netDirty = true;
        Sample();
    }

    public void Sample()
    {
        if (Native.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var total = kernel.Value + user.Value; // kernel time includes idle
            var dTotal = total - _lastTotal;
            var dIdle = idle.Value - _lastIdle;
            if (_lastTotal != 0 && dTotal > 0) CpuPercent = (int)Math.Round(100.0 * (dTotal - dIdle) / dTotal);
            _lastTotal = total;
            _lastIdle = idle.Value;
        }
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref m)) RamPercent = (int)m.dwMemoryLoad;
        if (_netDirty || DateTime.UtcNow - _lastNet > TimeSpan.FromMinutes(1))
        {
            _netDirty = false;
            _lastNet = DateTime.UtcNow;
            Network = ReadNetwork();
        }
    }

    private static string ReadNetwork()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(n => (n, props: n.GetIPProperties()))
                .Where(x => x.props.GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any) && !g.Address.Equals(System.Net.IPAddress.IPv6Any)))
                .Select(x => x.n)
                .OrderByDescending(n => n.Speed)
                .FirstOrDefault();
            if (nic == null) return "OFFLINE";
            var sp = nic.Speed;
            var speed = sp >= 1_000_000_000 ? $"{sp / 1e9:0.#} Gbps" : sp >= 1_000_000 ? $"{sp / 1e6:0} Mbps" : "UP";
            var kind = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "WI-FI" : "LAN";
            return $"{kind} {speed}";
        }
        catch { return "—"; }
    }
}

// ============================================================================================
// Start with Windows (HKCU Run key - per-user, no admin)
// ============================================================================================
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VirtualStreamDeck";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) is string s && s.Contains(Path.GetFileName(AppInfo.ExePath), StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enable)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enable) k.SetValue(ValueName, $"\"{AppInfo.ExePath}\" --autostart");
        else if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
    }
}

/// <summary>Keeps the display awake while the control surface is up.</summary>
public static class SleepBlocker
{
    private static bool _active;
    public static void Set(bool block)
    {
        if (block == _active) return;
        _active = block;
        Native.SetThreadExecutionState(block
            ? Native.ExecutionState.Continuous | Native.ExecutionState.DisplayRequired | Native.ExecutionState.SystemRequired
            : Native.ExecutionState.Continuous);
    }
}

public static class Shell
{
    public static void Open(string target, string args = "", string workingDir = "")
    {
        var psi = new ProcessStartInfo(target) { UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(args)) psi.Arguments = args;
        if (!string.IsNullOrWhiteSpace(workingDir)) psi.WorkingDirectory = workingDir;
        Process.Start(psi)?.Dispose();
    }
}
