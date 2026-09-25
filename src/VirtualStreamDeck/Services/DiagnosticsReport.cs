using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using VirtualStreamDeck.Core;
using Windows.Devices.Radios;

namespace VirtualStreamDeck.Services;

/// <summary>Plain-text troubleshooting report ("Copy Diagnostics"). Contains no secrets.</summary>
public static class DiagnosticsReport
{
    public static string WindowsVersion()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var name = k?.GetValue("ProductName") as string ?? "Windows";
            var build = k?.GetValue("CurrentBuildNumber") as string ?? "";
            var ubr = k?.GetValue("UBR")?.ToString() ?? "";
            var disp = k?.GetValue("DisplayVersion") as string ?? "";
            if (int.TryParse(build, out var b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
            return $"{name} {disp} (build {build}.{ubr})".Replace("  ", " ");
        }
        catch { return RuntimeInformation.OSDescription; }
    }

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static string Build(Deck deck)
    {
        var sb = new StringBuilder();
        void H(string t) => sb.AppendLine().AppendLine($"== {t} ==");
        void L(string k, object? v) => sb.AppendLine($"{k,-22} {v}");

        sb.AppendLine($"{AppInfo.FullName} diagnostics — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        H("Application");
        L("Version", AppInfo.Version);
        L("Executable", AppInfo.ExePath);
        L("Runtime", $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture}, self-contained)");
        L("Data folder", AppPaths.Root);
        L("Elevated", IsElevated() ? "yes (not required)" : "no (normal)");
        L("Logging to file", deck.Config.Settings.Logging ? "on" : "off");

        H("Windows");
        L("Version", WindowsVersion());
        L("Machine", $"{Environment.ProcessorCount} logical CPUs");

        H("Displays");
        var displays = Displays.All();
        L("Display count", displays.Count);
        foreach (var d in displays) L($"  Display {d.Number}", $"{d.DeviceName} {d.Bounds.Width}×{d.Bounds.Height} @ {d.Dpi} dpi{(d.IsPrimary ? " primary" : "")} {d.FriendlyName}");
        L("Surface display", string.IsNullOrEmpty(deck.Config.Settings.SurfaceDisplay) ? "(auto)" : deck.Config.Settings.SurfaceDisplay);
        L("Surface open", deck.SurfaceOpen);

        H("Audio devices");
        foreach (var d in deck.Audio.Devices(DataFlow.Render)) L("  Output", d.Name + (d.IsDefault ? "  [default]" : ""));
        foreach (var d in deck.Audio.Devices(DataFlow.Capture)) L("  Input", d.Name + (d.IsDefault ? "  [default]" : ""));
        L("Soundboard output", deck.Soundboard.ActiveDeviceName.Length > 0 ? deck.Soundboard.ActiveDeviceName : (deck.Config.Settings.SoundboardDevice.Length == 0 ? "Windows default (not opened yet)" : "selected device (not opened yet)"));
        if (deck.Soundboard.LastError != null) L("Soundboard error", deck.Soundboard.LastError);

        H("Microphone");
        var mic = deck.Audio.Microphone;
        L("Device", mic.Available ? mic.DeviceName : "none connected");
        if (mic.Available)
        {
            L("State", mic.Muted ? "MUTED" : "ON AIR");
            L("Volume", $"{mic.VolumePercent}%" + (mic.HasDbRange ? $" ({mic.Db:0.0} dB, range {mic.DbMin:0}..{mic.DbMax:0} dB)" : ""));
            L("Pinned device", deck.Config.Settings.MicrophoneDevice.Length > 0 ? "yes" : "no (follows Windows default)");
            L("Meter stream", deck.Meter.Running ? $"open, {deck.Meter.Channels} channel(s)" : "closed");
            L("Monitoring", deck.Meter.Monitoring ? "on → " + deck.Meter.MonitorDeviceName : "off");
            if (deck.Meter.Error != null) L("Meter error", deck.Meter.Error);
        }

        H("Camera");
        var cam = deck.Camera.State;
        L("Devices", cam.Devices.Count == 0 ? "none detected" : string.Join("; ", cam.Devices.Select(d => d.Name + (d.Enabled ? "" : " (disabled)"))));
        L("In use", cam.InUse ? "yes — " + string.Join(", ", cam.UsedBy) : "no");
        L("Privacy access", cam.AccessAllowed ? "allowed" : "blocked in Windows settings");

        H("Radios & media");
        L("Wi-Fi", deck.Radios.IsOn(RadioKind.WiFi) is { } w ? (w ? "on" : "off") : "no adapter / no access");
        L("Bluetooth", deck.Radios.IsOn(RadioKind.Bluetooth) is { } bt ? (bt ? "on" : "off") : "no radio / no access");
        if (deck.Radios.AccessProblem != null) L("Radio access", deck.Radios.AccessProblem);
        L("Media session", deck.Media.HasSession ? $"{deck.Media.Title} ({(deck.Media.Playing == true ? "playing" : "paused")})" : "none");

        H("Integrations");
        L("OBS", deck.Obs.StatusText);
        L("Tablet remote", deck.Config.Settings.RemoteEnabled ? $"enabled on port {deck.Config.Settings.RemotePort}" : "off");

        H("Profiles");
        L("Profiles", deck.Config.Profiles.Count);
        foreach (var p in deck.Config.Profiles) L("  " + p.Name, $"{p.Columns}×{p.Rows}, {p.Pages.Count} page(s), {p.Pages.Sum(x => x.Buttons.Count)} button(s)");
        L("Active profile", deck.ActiveProfile.Name);
        L("Sounds", $"{deck.Config.Sounds.Count} ({deck.Config.Sounds.Count(s => !s.IsBuiltIn)} imported)");
        L("Auto profile", deck.Config.Settings.AutoProfile ? $"on, {deck.Config.AutoProfileRules.Count} rule(s)" : "off");
        L("Foreground watch", deck.Foreground.Active ? "active" : "unavailable");

        H("Registered hotkeys");
        if (deck.Hotkeys.Registrations.Count == 0) sb.AppendLine("(none)");
        foreach (var r in deck.Hotkeys.Registrations) L("  " + r.Chord, r.Ok ? r.Label : $"FAILED — {r.Label}: {r.Error}");

        var errors = Log.Recent.Where(e => e.Level != LogLevel.Info).TakeLast(15).ToList();
        H("Recent warnings");
        if (errors.Count == 0) sb.AppendLine("(none)");
        foreach (var e in errors) sb.AppendLine($"{e.Time:HH:mm:ss} {e.Level}: {e.Message}");
        return sb.ToString();
    }
}
