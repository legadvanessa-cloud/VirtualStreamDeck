using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using VirtualStreamDeck.Core;

namespace VirtualStreamDeck.Services;

public sealed record EndpointState(bool Available, string DeviceName, bool Muted, int VolumePercent,
    string Id = "", float Db = 0, float DbMin = 0, float DbMax = 0)
{
    public static readonly EndpointState None = new(false, "", false, 0);
    /// <summary>The endpoint reports a real dB range (most microphones do).</summary>
    public bool HasDbRange => DbMax > DbMin;
}

/// <summary>
/// SystemControl / audio: the Windows default speaker and microphone endpoints via Core Audio.
/// State is event driven - IAudioEndpointVolume and IMMNotificationClient callbacks - so the
/// mic button reflects a mute made in Windows, Discord or a headset button without any polling.
/// </summary>
public sealed class SystemAudio : IDisposable
{
    private readonly MMDeviceEnumerator _enum = new();
    private readonly NotificationClient _notify;
    private readonly object _lock = new();
    private MMDevice? _speaker;
    private MMDevice? _mic;
    private string _micDeviceSetting = "";
    private int _refreshQueued;

    /// <summary>Raised on a background thread when any endpoint state changed.</summary>
    public event Action? Changed;
    /// <summary>Microphone connected / disconnected / switched: (previous name, new name). Background thread.</summary>
    public event Action<string, string>? MicrophoneSwitched;

    public EndpointState Speaker { get; private set; } = EndpointState.None;
    public EndpointState Microphone { get; private set; } = EndpointState.None;

    public SystemAudio()
    {
        _notify = new NotificationClient(this);
        try { _enum.RegisterEndpointNotificationCallback(_notify); }
        catch (Exception ex) { Log.Error("Audio device notifications unavailable", ex); }
        Rebind();
    }

    public void SetMicrophoneDevice(string id)
    {
        if ((id ?? "") == _micDeviceSetting && _mic != null) return;
        _micDeviceSetting = id ?? "";
        Rebind();
    }

    private bool _initialised;

    private void Rebind()
    {
        (string, string)? switched = null;
        lock (_lock)
        {
            Unhook(_speaker);
            Unhook(_mic);
            _speaker = TryDefault(DataFlow.Render);
            _mic = null;
            if (_micDeviceSetting.Length > 0)
            {
                try
                {
                    var d = _enum.GetDevice(_micDeviceSetting);
                    if (d.State == DeviceState.Active) _mic = d; else d.Dispose();
                }
                catch { /* configured mic is gone: fall back to the default */ }
            }
            _mic ??= TryDefault(DataFlow.Capture);
            Hook(_speaker);
            Hook(_mic);
            var before = Microphone;
            Speaker = Read(_speaker);
            Microphone = Read(_mic);
            if (before.Id != Microphone.Id && _initialised) switched = (before.DeviceName, Microphone.DeviceName);
            _initialised = true;
        }
        if (switched != null) MicrophoneSwitched?.Invoke(switched.Value.Item1, switched.Value.Item2);
        Changed?.Invoke();
    }

    private MMDevice? TryDefault(DataFlow flow)
    {
        try { return _enum.GetDefaultAudioEndpoint(flow, Role.Console); }
        catch { return null; } // no device of this kind
    }

    private void Hook(MMDevice? d)
    {
        if (d == null) return;
        try { d.AudioEndpointVolume.OnVolumeNotification += OnVolume; } catch { }
    }

    private void Unhook(MMDevice? d)
    {
        if (d == null) return;
        try { d.AudioEndpointVolume.OnVolumeNotification -= OnVolume; } catch { }
        try { d.Dispose(); } catch { }
    }

    private void OnVolume(AudioVolumeNotificationData data)
    {
        lock (_lock)
        {
            Speaker = Read(_speaker);
            Microphone = Read(_mic);
        }
        Changed?.Invoke();
    }

    private static EndpointState Read(MMDevice? d)
    {
        if (d == null) return EndpointState.None;
        try
        {
            var v = d.AudioEndpointVolume;
            float db = 0, min = 0, max = 0;
            try
            {
                db = v.MasterVolumeLevel;
                min = v.VolumeRange.MinDecibels;
                max = v.VolumeRange.MaxDecibels;
            }
            catch { /* some drivers expose only the scalar */ }
            return new EndpointState(true, d.FriendlyName, v.Mute, (int)Math.Round(v.MasterVolumeLevelScalar * 100), d.ID, db, min, max);
        }
        catch { return EndpointState.None; }
    }

    private void QueueRebind()
    {
        // Device notifications arrive in bursts (a headset plug fires 4-6); coalesce them.
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        Task.Delay(250).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            try { Rebind(); } catch (Exception ex) { Log.Error("Audio rebind failed", ex); }
        });
    }

    // ---- commands ----------------------------------------------------------------------------

    private MMDevice Require(MMDevice? d, string what) =>
        d ?? throw new InvalidOperationException($"No {what} is connected or enabled in Windows.");

    public bool ToggleMicMute()
    {
        lock (_lock)
        {
            var v = Require(_mic, "microphone").AudioEndpointVolume;
            v.Mute = !v.Mute;
            return v.Mute;
        }
    }

    public void SetMicMute(bool mute)
    {
        lock (_lock) Require(_mic, "microphone").AudioEndpointVolume.Mute = mute;
    }

    public int AdjustMicVolume(int delta) => Adjust(() => _mic, "microphone", delta, null);
    public int SetMicVolume(int percent) => Adjust(() => _mic, "microphone", 0, percent);

    public bool ToggleSpeakerMute()
    {
        lock (_lock)
        {
            var v = Require(_speaker, "playback device").AudioEndpointVolume;
            v.Mute = !v.Mute;
            return v.Mute;
        }
    }

    public int AdjustSpeakerVolume(int delta) => Adjust(() => _speaker, "playback device", delta, null);
    public int SetSpeakerVolume(int percent) => Adjust(() => _speaker, "playback device", 0, percent);

    private int Adjust(Func<MMDevice?> dev, string what, int delta, int? absolute)
    {
        lock (_lock)
        {
            var v = Require(dev(), what).AudioEndpointVolume;
            var cur = (int)Math.Round(v.MasterVolumeLevelScalar * 100);
            var next = Math.Clamp(absolute ?? cur + delta, 0, 100);
            v.MasterVolumeLevelScalar = next / 100f;
            if (absolute == 0) v.Mute = true;
            else if (next > 0 && v.Mute && (absolute != null || delta > 0)) v.Mute = false;
            return next;
        }
    }

    public List<AudioDeviceInfo> Devices(DataFlow flow)
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            string? def = null;
            try { def = _enum.GetDefaultAudioEndpoint(flow, Role.Console).ID; } catch { }
            foreach (var d in _enum.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                list.Add(new AudioDeviceInfo(d.ID, d.FriendlyName, d.ID == def));
                d.Dispose();
            }
        }
        catch (Exception ex) { Log.Error("Enumerating audio devices failed", ex); }
        return list;
    }

    /// <summary>
    /// Sets the Windows default device for all roles. Windows has no documented API for this; the
    /// IPolicyConfig interface used here is the one the Sound control panel itself uses and has
    /// been stable since Windows 7. Failures are reported, never hidden.
    /// </summary>
    public string SetDefaultDevice(string id)
    {
        var dev = Devices(DataFlow.Render).Concat(Devices(DataFlow.Capture)).FirstOrDefault(d => d.Id == id)
                  ?? throw new InvalidOperationException("The selected audio device is not connected.");
        PolicyConfig.SetDefault(id);
        QueueRebind();
        return dev.Name;
    }

    public string CycleOutput()
    {
        var devs = Devices(DataFlow.Render);
        if (devs.Count == 0) throw new InvalidOperationException("No playback devices are available.");
        if (devs.Count == 1) return devs[0].Name;
        var i = devs.FindIndex(d => d.IsDefault);
        var next = devs[(i + 1) % devs.Count];
        PolicyConfig.SetDefault(next.Id);
        QueueRebind();
        return next.Name;
    }

    /// <summary>Makes the next active recording device the Windows default (all roles).</summary>
    public string CycleInput()
    {
        var devs = Devices(DataFlow.Capture);
        if (devs.Count == 0) throw new InvalidOperationException("No microphones are connected.");
        if (_micDeviceSetting.Length > 0)
            throw new InvalidOperationException("A specific microphone is pinned in Settings › Audio; choose \"Windows default\" there to switch devices from the deck.");
        if (devs.Count == 1) return devs[0].Name;
        var i = devs.FindIndex(d => d.IsDefault);
        var next = devs[(i + 1) % devs.Count];
        PolicyConfig.SetDefault(next.Id);
        QueueRebind();
        return next.Name;
    }

    public void Dispose()
    {
        try { _enum.UnregisterEndpointNotificationCallback(_notify); } catch { }
        lock (_lock)
        {
            Unhook(_speaker);
            Unhook(_mic);
            _speaker = _mic = null;
        }
        _enum.Dispose();
    }

    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly SystemAudio _o;
        public NotificationClient(SystemAudio o) => _o = o;
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _o.QueueRebind();
        public void OnDeviceAdded(string pwstrDeviceId) => _o.QueueRebind();
        public void OnDeviceRemoved(string deviceId) => _o.QueueRebind();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _o.QueueRebind();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}

internal static class PolicyConfig
{
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class CPolicyConfigClient { }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat(string id, bool isDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(string id);
        [PreserveSig] int SetDeviceFormat(string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string id, bool isDefault, IntPtr defaultPeriod, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod(string id, IntPtr period);
        [PreserveSig] int GetShareMode(string id, IntPtr mode);
        [PreserveSig] int SetShareMode(string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility(string id, bool visible);
    }

    public static void SetDefault(string id)
    {
        var obj = new CPolicyConfigClient();
        try
        {
            var pc = (IPolicyConfig)obj;
            for (var role = 0; role <= 2; role++) // eConsole, eMultimedia, eCommunications
            {
                var hr = pc.SetDefaultEndpoint(id, role);
                if (hr != 0) throw new COMException("Windows refused to change the default audio device.", hr);
            }
        }
        finally { Marshal.ReleaseComObject(obj); }
    }
}
