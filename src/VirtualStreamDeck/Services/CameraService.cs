using System.Diagnostics;
using System.Management;
using Microsoft.Win32;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Interop;

namespace VirtualStreamDeck.Services;

public sealed record CameraDevice(string Name, string InstanceId, bool Enabled);

public sealed record CameraState(
    bool AnyCamera,
    bool InUse,
    IReadOnlyList<string> UsedBy,
    bool AccessAllowed,
    IReadOnlyList<CameraDevice> Devices)
{
    public static readonly CameraState Unknown = new(false, false, Array.Empty<string>(), true, Array.Empty<CameraDevice>());
    public bool AllDisabled => Devices.Count > 0 && Devices.All(d => !d.Enabled);
}

/// <summary>
/// Windows exposes no supported API to switch a webcam off, so this service does not pretend to.
/// What it does, all real:
///  • detects whether any app is using the camera right now (the CapabilityAccessManager consent
///    store Windows uses for the privacy indicator - watched with RegNotifyChangeKeyValue, no polling);
///  • reads whether camera access is blocked in Windows privacy settings;
///  • disables / enables the camera device through PnP, which genuinely cuts it off - this needs
///    administrator rights, so it asks for UAC each time and says so up front.
/// </summary>
public sealed class CameraService : IDisposable
{
    private const string ConsentPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
    private readonly CancellationTokenSource _cts = new();
    private int _refreshing;
    private List<CameraDevice> _devices = new();

    public event Action? Changed;
    public CameraState State { get; private set; } = CameraState.Unknown;

    public void Start()
    {
        RefreshDevices();
        Refresh();
        StartWatcher(Registry.CurrentUser);
        StartWatcher(Registry.LocalMachine);
    }

    private void StartWatcher(RegistryKey hive)
    {
        var t = new Thread(() =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using var key = hive.OpenSubKey(ConsentPath);
                    if (key == null) return;
                    using var evt = new AutoResetEvent(false);
                    var rc = Native.RegNotifyChangeKeyValue(key.Handle, true,
                        Native.REG_NOTIFY_CHANGE_LAST_SET | Native.REG_NOTIFY_CHANGE_NAME, evt.SafeWaitHandle, true);
                    if (rc != 0) return;
                    if (WaitHandle.WaitAny(new[] { evt, _cts.Token.WaitHandle }) == 1) return;
                    // Several values change together when an app opens the camera; let them settle.
                    Thread.Sleep(150);
                    Refresh();
                }
                catch (Exception ex)
                {
                    Log.Warn("Camera watcher stopped: " + ex.Message);
                    return;
                }
            }
        }) { IsBackground = true, Name = "camera-watch-" + hive.Name, Priority = ThreadPriority.BelowNormal };
        t.Start();
    }

    public void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            var users = new List<string>();
            ScanInUse(Registry.CurrentUser, users);
            ScanInUse(Registry.LocalMachine, users);
            var allowed = AccessAllowed(Registry.CurrentUser) && AccessAllowed(Registry.LocalMachine);
            var devices = _devices;
            State = new CameraState(devices.Count > 0, users.Count > 0, users.Distinct().ToList(), allowed, devices);
        }
        catch (Exception ex) { Log.Warn("Camera state read failed: " + ex.Message); }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
        Changed?.Invoke();
    }

    private static bool AccessAllowed(RegistryKey hive)
    {
        using var key = hive.OpenSubKey(ConsentPath);
        var v = key?.GetValue("Value") as string;
        return !string.Equals(v, "Deny", StringComparison.OrdinalIgnoreCase);
    }

    private static void ScanInUse(RegistryKey hive, List<string> users)
    {
        using var root = hive.OpenSubKey(ConsentPath);
        if (root == null) return;
        void Check(RegistryKey k, string name)
        {
            if (k.GetValue("LastUsedTimeStop") is long stop && stop == 0 && k.GetValue("LastUsedTimeStart") is long start && start != 0)
                users.Add(FriendlyAppName(name));
        }
        foreach (var sub in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(sub);
            if (k == null) continue;
            if (sub.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var app in k.GetSubKeyNames())
                {
                    using var ak = k.OpenSubKey(app);
                    if (ak != null) Check(ak, app);
                }
            }
            else Check(k, sub);
        }
    }

    private static string FriendlyAppName(string key)
    {
        // Non-packaged apps are keyed by their path with '#' for '\'; packaged ones by family name.
        var s = key.Replace('#', '\\');
        if (s.Contains('\\')) return Path.GetFileNameWithoutExtension(s);
        var us = s.IndexOf('_');
        var name = us > 0 ? s[..us] : s;
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    /// <summary>Enumerates camera devices via WMI (PnP class Camera or Image). Slow-ish; call off the UI thread.</summary>
    public void RefreshDevices()
    {
        var list = new List<CameraDevice>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, ConfigManagerErrorCode, PNPClass, Service FROM Win32_PnPEntity WHERE PNPClass = 'Camera' OR PNPClass = 'Image'");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var id = mo["PNPDeviceID"] as string ?? "";
                    var err = Convert.ToUInt32(mo["ConfigManagerErrorCode"] ?? 0u);
                    // Scanners are also class "Image"; keep only video capture devices (USB video class or MIPI).
                    var cls = mo["PNPClass"] as string;
                    if (cls == "Image" && !IsVideoDevice(mo["Service"] as string)) continue;
                    list.Add(new CameraDevice(mo["Name"] as string ?? "Camera", id, err != 22));
                }
            }
        }
        catch (Exception ex) { Log.Warn("Camera enumeration failed: " + ex.Message); }
        _devices = list;
    }

    private static bool IsVideoDevice(string? service) =>
        service != null && (service.Contains("video", StringComparison.OrdinalIgnoreCase) || service.Contains("avstream", StringComparison.OrdinalIgnoreCase));

    public static void OpenPrivacySettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:privacy-webcam") { UseShellExecute = true });

    /// <summary>
    /// Disables (or enables) every camera device. Runs PowerShell's PnP cmdlets elevated - Windows
    /// shows a UAC prompt. Returns false when the user declined the prompt.
    /// </summary>
    public async Task<bool> SetDevicesEnabledAsync(bool enable)
    {
        RefreshDevices();
        if (_devices.Count == 0) throw new InvalidOperationException("No camera device was found.");
        var ids = string.Join(",", _devices.Select(d => "'" + d.InstanceId.Replace("'", "''") + "'"));
        var verb = enable ? "Enable-PnpDevice" : "Disable-PnpDevice";
        var script = $"$ErrorActionPreference='Stop'; foreach($i in @({ids})) {{ {verb} -InstanceId $i -Confirm:$false }}";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process? p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; } // UAC cancelled
        if (p == null) return false;
        await p.WaitForExitAsync();
        var code = p.ExitCode;
        p.Dispose();
        await Task.Run(RefreshDevices);
        Refresh();
        if (code != 0) throw new InvalidOperationException($"Windows could not {(enable ? "enable" : "disable")} the camera (exit code {code}).");
        return true;
    }

    public void Dispose() => _cts.Cancel();
}
