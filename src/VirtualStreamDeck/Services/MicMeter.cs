using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VirtualStreamDeck.Core;

namespace VirtualStreamDeck.Services;

/// <summary>
/// Broadcast metering for the microphone: opens a shared-mode WASAPI capture stream on the
/// controlled microphone and measures the real samples (per-channel peak and RMS, in dBFS). The
/// same stream can feed a monitor output so the DJ hears themselves - the equivalent of Windows'
/// "Listen to this device", implemented with a low-latency WASAPI render stream.
///
/// The stream is only open while something needs it (reference counted), because an open capture
/// stream makes Windows show the microphone-in-use indicator - which is correct and honest.
/// </summary>
public sealed class MicMeter : IDisposable
{
    public const float FloorDb = -60f;
    private readonly object _lock = new();
    private WasapiCapture? _capture;
    private WasapiOut? _monitorOut;
    private BufferedWaveProvider? _monitorBuffer;
    private string _deviceId = "";
    private int _users;
    private float[] _peak = new float[1];
    private float[] _rms = new float[1];
    private float[] _hold = new float[1];
    private long _clipUntil;
    private long _lastData;
    private bool _monitor;
    private string _monitorDevice = "";

    public float MonitorVolume { get; set; } = 0.8f;
    public int Channels { get; private set; } = 1;
    public string? Error { get; private set; }
    public bool Running => _capture != null;
    public bool Monitoring => _monitorOut != null;
    /// <summary>Monitoring was requested (it may be waiting for the device to come back).</summary>
    public bool MonitorEnabled => _monitor;
    public string MonitorDeviceName { get; private set; } = "";
    public event Action? StateChanged;

    public static float ToDb(float linear) => linear <= 0.000001f ? FloorDb : Math.Max(FloorDb, 20f * MathF.Log10(linear));

    /// <summary>Current level per channel: decaying peak (dBFS), RMS (dBFS), peak hold (dBFS).</summary>
    public (float PeakDb, float RmsDb, float HoldDb)[] Levels()
    {
        lock (_lock)
        {
            var stale = Environment.TickCount64 - _lastData > 400; // stream stalled or device gone
            var r = new (float, float, float)[_peak.Length];
            for (var i = 0; i < r.Length; i++)
                r[i] = stale ? (FloorDb, FloorDb, FloorDb) : (ToDb(_peak[i]), ToDb(_rms[i]), ToDb(_hold[i]));
            return r;
        }
    }

    public bool Clipping => Environment.TickCount64 < Interlocked.Read(ref _clipUntil);

    /// <summary>True once real samples have arrived recently.</summary>
    public bool HasSignal => Running && Environment.TickCount64 - Interlocked.Read(ref _lastData) < 400;

    public void Acquire()
    {
        lock (_lock)
        {
            _users++;
            if (_capture == null) StartCapture();
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            _users = Math.Max(0, _users - 1);
            if (_users == 0 && !_monitor) StopCapture();
        }
        StateChanged?.Invoke();
    }

    /// <summary>Called when the controlled microphone changed (or reconnected).</summary>
    public void SetDevice(string id)
    {
        lock (_lock)
        {
            if (id == _deviceId && (_capture != null || (_users == 0 && !_monitor))) return;
            _deviceId = id ?? "";
            var wasMonitoring = _monitor;
            StopCapture();
            if (_users > 0 || wasMonitoring) StartCapture();
        }
        StateChanged?.Invoke();
    }

    private void StartCapture()
    {
        if (string.IsNullOrEmpty(_deviceId)) { Error = "No microphone is connected."; return; }
        MMDevice? dev = null;
        try
        {
            using var en = new MMDeviceEnumerator();
            dev = en.GetDevice(_deviceId);
            if (dev.State != DeviceState.Active) { Error = "The microphone is disconnected."; dev.Dispose(); return; }
            var cap = new WasapiCapture(dev, true, 20);
            Channels = Math.Max(1, cap.WaveFormat.Channels);
            _peak = new float[Channels];
            _rms = new float[Channels];
            _hold = new float[Channels];
            cap.DataAvailable += OnData;
            cap.RecordingStopped += OnStopped;
            cap.StartRecording();
            _capture = cap;
            Error = null;
            if (_monitor) StartMonitorOut(cap.WaveFormat);
        }
        catch (Exception ex)
        {
            Error = "Could not open the microphone: " + ex.Message;
            Log.Warn(Error);
            _capture = null;
            dev?.Dispose();
        }
    }

    private void StopCapture()
    {
        var c = _capture;
        _capture = null;
        StopMonitorOut();
        if (c == null) return;
        c.DataAvailable -= OnData;
        c.RecordingStopped -= OnStopped;
        ThreadPool.QueueUserWorkItem(_ => { try { c.StopRecording(); c.Dispose(); } catch { } });
    }

    private void OnStopped(object? s, StoppedEventArgs e)
    {
        if (e.Exception == null) return;
        lock (_lock)
        {
            Error = "The microphone stopped delivering audio (disconnected?).";
            _capture = null;
            StopMonitorOut();
        }
        StateChanged?.Invoke();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var cap = sender as WasapiCapture;
        if (cap == null || e.BytesRecorded == 0) return;
        var fmt = cap.WaveFormat;
        var ch = Math.Max(1, fmt.Channels);
        var peak = new float[ch];
        var sum = new double[ch];
        var frames = Measure(e.Buffer, e.BytesRecorded, fmt, peak, sum);
        if (frames == 0) return;
        lock (_lock)
        {
            if (_peak.Length != ch) return;
            for (var i = 0; i < ch; i++)
            {
                var rms = (float)Math.Sqrt(sum[i] / frames);
                // Meter ballistics: instant attack, ~20 dB/s release; peak hold decays slowly.
                _peak[i] = Math.Max(peak[i], _peak[i] * 0.80f);
                _rms[i] = Math.Max(rms, _rms[i] * 0.85f);
                _hold[i] = Math.Max(peak[i], _hold[i] * 0.97f);
                if (peak[i] >= 0.989f) Interlocked.Exchange(ref _clipUntil, Environment.TickCount64 + 1500);
            }
            _lastData = Environment.TickCount64;
        }
        _monitorBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    /// <summary>Per-channel peak and sum of squares of an interleaved buffer. Returns frame count.</summary>
    private static readonly Guid KsFloat = new("00000003-0000-0010-8000-00aa00389b71"); // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT

    internal static int Measure(byte[] buf, int bytes, WaveFormat fmt, float[] peak, double[] sum)
    {
        var ch = peak.Length;
        var isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                      || fmt is WaveFormatExtensible wfe && wfe.SubFormat == KsFloat;
        var bps = fmt.BitsPerSample / 8;
        if (bps == 0) return 0;
        var frames = bytes / (bps * ch);
        for (var f = 0; f < frames; f++)
        {
            for (var c = 0; c < ch; c++)
            {
                var o = (f * ch + c) * bps;
                float v = isFloat && bps == 4 ? BitConverter.ToSingle(buf, o)
                    : bps switch
                    {
                        2 => BitConverter.ToInt16(buf, o) / 32768f,
                        3 => ((buf[o] | buf[o + 1] << 8 | (sbyte)buf[o + 2] << 16)) / 8388608f,
                        4 => BitConverter.ToInt32(buf, o) / 2147483648f,
                        _ => 0,
                    };
                var a = Math.Abs(v);
                if (a > peak[c]) peak[c] = a;
                sum[c] += v * v;
            }
        }
        return frames;
    }

    // ---- monitoring ----------------------------------------------------------------------------

    public void SetMonitor(bool on, string outputDeviceId)
    {
        lock (_lock)
        {
            _monitorDevice = outputDeviceId ?? "";
            _monitor = on;
            StopMonitorOut();
            if (on)
            {
                if (_capture == null) StartCapture();
                else StartMonitorOut(_capture.WaveFormat);
            }
            else if (_users == 0) StopCapture();
        }
        StateChanged?.Invoke();
    }

    private void StartMonitorOut(WaveFormat captureFormat)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            MMDevice dev;
            try { dev = _monitorDevice.Length > 0 ? en.GetDevice(_monitorDevice) : en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications); }
            catch { dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications); }
            var outRate = dev.AudioClient.MixFormat.SampleRate;
            _monitorBuffer = new BufferedWaveProvider(captureFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromMilliseconds(250), ReadFully = true };
            ISampleProvider sp = _monitorBuffer.ToSampleProvider();
            if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
            else if (sp.WaveFormat.Channels > 2)
            {
                var mux = new MultiplexingSampleProvider(new[] { sp }, 2);
                mux.ConnectInputToOutput(0, 0);
                mux.ConnectInputToOutput(1, 1);
                sp = mux;
            }
            if (sp.WaveFormat.SampleRate != outRate) sp = new WdlResamplingSampleProvider(sp, outRate);
            var vol = new VolumeSampleProvider(sp);
            var gain = new GainProvider(vol, this);
            var o = new WasapiOut(dev, AudioClientShareMode.Shared, true, 30);
            o.Init(gain);
            o.Play();
            _monitorOut = o;
            MonitorDeviceName = dev.FriendlyName;
        }
        catch (Exception ex)
        {
            Error = "Monitoring could not start: " + ex.Message;
            Log.Warn(Error);
            _monitorOut = null;
            _monitorBuffer = null;
            _monitor = false;
        }
    }

    private void StopMonitorOut()
    {
        var o = _monitorOut;
        _monitorOut = null;
        _monitorBuffer = null;
        MonitorDeviceName = "";
        if (o != null) ThreadPool.QueueUserWorkItem(_ => { try { o.Stop(); o.Dispose(); } catch { } });
    }

    private sealed class GainProvider : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private readonly MicMeter _m;
        public GainProvider(ISampleProvider src, MicMeter m) { _src = src; _m = m; }
        public WaveFormat WaveFormat => _src.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            var n = _src.Read(buffer, offset, count);
            var g = Math.Clamp(_m.MonitorVolume, 0f, 1f);
            for (var i = 0; i < n; i++) buffer[offset + i] *= g;
            return n;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _users = 0;
            _monitor = false;
            StopCapture();
        }
    }
}
