using NAudio.CoreAudioApi;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VirtualStreamDeck.Core;

namespace VirtualStreamDeck.Services;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>
/// AudioEngine for the soundboard: one shared WASAPI output per selected device, a float mixer,
/// and any number of simultaneous voices with per-voice volume, loop and fades.
///
/// Short sounds are decoded once and cached as PCM at the mixer rate so a press starts playback
/// within one buffer period. The device is only held open while something plays (plus a grace
/// period), so an idle soundboard costs no CPU and does not keep the audio endpoint awake.
/// </summary>
public sealed class SoundboardEngine : IDisposable
{
    private const int CacheMaxSeconds = 90;
    private const int LatencyMs = 40;
    private readonly object _lock = new();
    private readonly Dictionary<string, float[]> _cache = new();
    private readonly List<Voice> _voices = new();
    private WasapiOut? _out;
    private MixingSampleProvider? _mixer;
    private string _deviceId = "";
    private int _rate = 48000;
    private System.Threading.Timer? _idleTimer;
    private bool _disposed;

    /// <summary>Raised (on an arbitrary thread) whenever a voice starts or ends.</summary>
    public event Action? VoicesChanged;
    /// <summary>Raised (on an arbitrary thread) when the output device failed and playback fell back.</summary>
    public event Action<string>? DeviceProblem;

    public float MasterVolume { get; set; } = 0.8f;
    public string ActiveDeviceName { get; private set; } = "";
    public string? LastError { get; private set; }

    public static List<AudioDeviceInfo> OutputDevices()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var en = new MMDeviceEnumerator();
            string? def = null;
            try { def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                list.Add(new AudioDeviceInfo(d.ID, d.FriendlyName, d.ID == def));
                d.Dispose();
            }
        }
        catch (Exception ex) { Log.Error("Enumerating output devices failed", ex); }
        return list;
    }

    /// <summary>Selects the soundboard output ("" = follow the Windows default device).</summary>
    public void SetDevice(string deviceId)
    {
        lock (_lock)
        {
            if (_deviceId == deviceId && _out != null) return;
            _deviceId = deviceId ?? "";
            CloseOutput();
            if (_voices.Count > 0) EnsureOutput();
        }
    }

    public bool IsPlaying(string tag)
    {
        lock (_lock) return _voices.Any(v => v.Tag == tag && !v.Finished);
    }

    public bool AnyPlaying
    {
        get { lock (_lock) return _voices.Any(v => !v.Finished); }
    }

    /// <summary>Playback progress 0..1 of the most recent voice with this tag, or null.</summary>
    public double? Progress(string tag)
    {
        lock (_lock)
        {
            var v = _voices.LastOrDefault(x => x.Tag == tag && !x.Finished);
            return v?.Progress;
        }
    }

    /// <summary>Decodes a sound into the cache ahead of time (safe to call from any thread).</summary>
    public void Preload(SoundItem item)
    {
        try { GetPcm(item, _rate); } catch (Exception ex) { Log.Warn($"Preload of '{item.Name}' failed: {ex.Message}"); }
    }

    /// <summary>Starts a sound. Throws <see cref="FileNotFoundException"/> or <see cref="InvalidDataException"/> on bad input.</summary>
    public void Play(SoundItem item, string tag, float volume, bool loop, int fadeInMs, int fadeOutMs, string retrigger)
    {
        if (_disposed) return;
        lock (_lock)
        {
            switch (retrigger)
            {
                case "toggle" when _voices.Any(v => v.Tag == tag && !v.Finished && !v.Stopping):
                    foreach (var v in _voices.Where(v => v.Tag == tag)) v.StopWithFade(Math.Max(fadeOutMs, 30));
                    return;
                case "restart":
                    foreach (var v in _voices.Where(v => v.Tag == tag)) v.StopWithFade(15);
                    break;
            }
        }

        if (!item.IsBuiltIn && !File.Exists(ResolvePath(item)))
        {
            Invalidate(item.File); // never keep playing a file the user deleted from a stale cache
            throw new FileNotFoundException("The sound file was deleted or moved.", ResolvePath(item));
        }
        EnsureOutputSafe();
        var pcm = GetPcm(item, _rate);
        ISoundSource src = pcm != null ? new CachedSource(pcm) : new StreamSource(() => OpenDecoded(item, _rate));
        var voice = new Voice(src, tag, _rate, Math.Clamp(volume, 0, 1) * Math.Clamp(item.Volume / 100f, 0, 1), loop, fadeInMs, fadeOutMs, this);
        lock (_lock)
        {
            EnsureOutput();
            if (_mixer == null) throw new InvalidOperationException(LastError ?? "No audio output device is available.");
            _voices.Add(voice);
            _mixer.AddMixerInput(voice);
            _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        VoicesChanged?.Invoke();
    }

    public void Stop(string tag, int fadeMs)
    {
        lock (_lock) foreach (var v in _voices.Where(v => v.Tag == tag)) v.StopWithFade(fadeMs);
    }

    public void StopAll(int fadeMs = 120)
    {
        lock (_lock) foreach (var v in _voices) v.StopWithFade(fadeMs);
    }

    internal void OnVoiceFinished(Voice v)
    {
        lock (_lock)
        {
            _voices.Remove(v);
            if (_voices.Count == 0)
            {
                _idleTimer ??= new System.Threading.Timer(_ => { lock (_lock) if (_voices.Count == 0) CloseOutput(); });
                _idleTimer.Change(8000, Timeout.Infinite);
            }
        }
        VoicesChanged?.Invoke();
    }

    public void Invalidate(string file)
    {
        lock (_lock) foreach (var k in _cache.Keys.Where(k => k.StartsWith(file + "|", StringComparison.OrdinalIgnoreCase)).ToList()) _cache.Remove(k);
    }

    // ---- output ------------------------------------------------------------------------------

    private void EnsureOutputSafe()
    {
        lock (_lock) EnsureOutput();
    }

    private void EnsureOutput()
    {
        if (_out != null || _disposed) return;
        using var en = new MMDeviceEnumerator();
        MMDevice? dev = null;
        if (_deviceId.Length > 0)
        {
            try
            {
                dev = en.GetDevice(_deviceId);
                if (dev.State != DeviceState.Active) { dev.Dispose(); dev = null; }
            }
            catch { dev = null; }
            if (dev == null)
                DeviceProblem?.Invoke("The selected soundboard output device is not available - using the Windows default device.");
        }
        try { dev ??= en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
        catch (Exception ex)
        {
            LastError = "No audio output device is available.";
            Log.Error(LastError, ex);
            return;
        }

        try
        {
            var mix = dev.AudioClient.MixFormat;
            var rate = mix.SampleRate is >= 8000 and <= 384000 ? mix.SampleRate : 48000;
            if (rate != _rate) { _rate = rate; }
            _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(_rate, 2)) { ReadFully = true };
            var output = new WasapiOut(dev, AudioClientShareMode.Shared, true, LatencyMs);
            output.Init(new MasterVolumeProvider(_mixer, this));
            output.PlaybackStopped += OnPlaybackStopped;
            output.Play();
            _out = output;
            ActiveDeviceName = dev.FriendlyName;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = $"Could not open '{dev.FriendlyName}': {ex.Message}";
            Log.Error("Opening soundboard output failed", ex);
            _mixer = null;
            _out = null;
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception == null) return;
        Log.Error("Soundboard output stopped", e.Exception);
        lock (_lock)
        {
            foreach (var v in _voices) v.Kill();
            _voices.Clear();
            CloseOutput();
        }
        DeviceProblem?.Invoke("The audio device stopped responding or was removed. The next sound will use the available device.");
        VoicesChanged?.Invoke();
    }

    private void CloseOutput()
    {
        var o = _out;
        _out = null;
        _mixer = null;
        if (o == null) return;
        o.PlaybackStopped -= OnPlaybackStopped;
        // Disposing from inside a lock on the audio thread can deadlock; hand it off.
        ThreadPool.QueueUserWorkItem(_ => { try { o.Stop(); o.Dispose(); } catch { } });
    }

    // ---- decoding ----------------------------------------------------------------------------

    public static string ResolvePath(SoundItem item) => Path.Combine(AppPaths.Sounds, item.File);

    private float[]? GetPcm(SoundItem item, int rate)
    {
        var key = $"{item.File}|{rate}";
        lock (_lock) if (_cache.TryGetValue(key, out var hit)) return hit;
        // Decode outside the lock so a long file never stalls other presses.
        float[]? data;
        if (item.IsBuiltIn)
        {
            data = SoundSynth.Generate(item.File["builtin:".Length..], rate)
                   ?? throw new InvalidDataException($"Unknown built-in sound '{item.File}'.");
        }
        else
        {
            var path = ResolvePath(item);
            if (!File.Exists(path)) throw new FileNotFoundException("The sound file no longer exists.", path);
            using var src = OpenDecodedWithReader(item, rate, out var reader);
            using (reader)
            {
                if (reader.TotalTime.TotalSeconds > CacheMaxSeconds) return null; // stream it instead
                var list = new List<float>((int)Math.Min(int.MaxValue / 2, reader.TotalTime.TotalSeconds * rate * 2 + 4096));
                var buf = new float[rate];
                int n;
                while ((n = src.Provider.Read(buf, 0, buf.Length)) > 0)
                    for (var i = 0; i < n; i++) list.Add(buf[i]);
                data = list.ToArray();
            }
        }
        lock (_lock) _cache[key] = data;
        return data;
    }

    private sealed class Decoded : IDisposable
    {
        public required ISampleProvider Provider { get; init; }
        public required IDisposable Owner { get; init; }
        public void Dispose() => Owner.Dispose();
    }

    private static Decoded OpenDecoded(SoundItem item, int rate)
    {
        var d = OpenDecodedWithReader(item, rate, out _);
        return d;
    }

    private static Decoded OpenDecodedWithReader(SoundItem item, int rate, out WaveStream reader)
    {
        var path = ResolvePath(item);
        if (!File.Exists(path)) throw new FileNotFoundException("The sound file no longer exists.", path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        ISampleProvider sp;
        try
        {
            switch (ext)
            {
                case ".ogg":
                    var vr = new VorbisWaveReader(path);
                    reader = vr;
                    sp = vr;
                    break;
                case ".wav":
                    var wr = new WaveFileReader(path);
                    reader = wr;
                    sp = wr.ToSampleProvider(); // WAV may be PCM/float/ADPCM; non-PCM is converted below via MF on failure
                    break;
                case ".aif" or ".aiff":
                    var ar = new AiffFileReader(path);
                    reader = ar;
                    sp = ar.ToSampleProvider();
                    break;
                default:
                    var mf = new MediaFoundationReader(path);
                    reader = mf;
                    sp = mf.ToSampleProvider();
                    break;
            }
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' could not be decoded ({ex.Message}).", ex);
        }

        if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > 2)
        {
            var mux = new MultiplexingSampleProvider(new[] { sp }, 2);
            mux.ConnectInputToOutput(0, 0);
            mux.ConnectInputToOutput(1, 1);
            sp = mux;
        }
        if (sp.WaveFormat.SampleRate != rate) sp = new WdlResamplingSampleProvider(sp, rate);
        return new Decoded { Provider = sp, Owner = reader };
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_lock)
        {
            foreach (var v in _voices) v.Kill();
            _voices.Clear();
            var o = _out;
            _out = null;
            try { o?.Stop(); o?.Dispose(); } catch { }
            _idleTimer?.Dispose();
        }
    }

    // ---- voices ------------------------------------------------------------------------------

    internal interface ISoundSource : IDisposable
    {
        int Read(float[] buffer, int offset, int count);
        void Rewind();
        long Length { get; }
        long Position { get; }
    }

    private sealed class CachedSource : ISoundSource
    {
        private readonly float[] _data;
        private long _pos;
        public CachedSource(float[] data) => _data = data;
        public long Length => _data.Length;
        public long Position => _pos;
        public int Read(float[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, _data.Length - _pos);
            if (n <= 0) return 0;
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public void Rewind() => _pos = 0;
        public void Dispose() { }
    }

    private sealed class StreamSource : ISoundSource
    {
        private readonly Func<Decoded> _open;
        private Decoded _cur;
        private long _pos;
        public StreamSource(Func<Decoded> open) { _open = open; _cur = open(); }
        public long Length => -1;
        public long Position => _pos;
        public int Read(float[] buffer, int offset, int count)
        {
            var n = _cur.Provider.Read(buffer, offset, count);
            _pos += n;
            return n;
        }
        public void Rewind() { _cur.Dispose(); _cur = _open(); _pos = 0; }
        public void Dispose() => _cur.Dispose();
    }

    internal sealed class Voice : ISampleProvider
    {
        private readonly ISoundSource _src;
        private readonly SoundboardEngine _owner;
        private readonly bool _loop;
        private readonly float _volume;
        private readonly long _fadeInSamples;
        private readonly long _fadeOutSamples;
        private long _played;
        private long _stopStart = -1;
        private long _stopLen;
        private volatile bool _finished;
        private int _reported;

        public Voice(ISoundSource src, string tag, int rate, float volume, bool loop, int fadeInMs, int fadeOutMs, SoundboardEngine owner)
        {
            _src = src;
            Tag = tag;
            _owner = owner;
            _loop = loop;
            _volume = volume;
            _fadeInSamples = (long)(Math.Max(0, fadeInMs) / 1000.0 * rate) * 2;
            _fadeOutSamples = (long)(Math.Max(0, fadeOutMs) / 1000.0 * rate) * 2;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
            Rate = rate;
        }

        public string Tag { get; }
        public int Rate { get; }
        public WaveFormat WaveFormat { get; }
        public bool Finished => _finished;
        public bool Stopping => _stopStart >= 0;

        public double? Progress
        {
            get
            {
                var len = _src.Length;
                if (len <= 0) return null;
                return Math.Clamp((double)_src.Position / len, 0, 1);
            }
        }

        public void StopWithFade(int ms)
        {
            if (_stopStart >= 0) return;
            _stopLen = Math.Max(2, (long)(ms / 1000.0 * Rate) * 2);
            _stopStart = _played;
        }

        public void Kill() => Finish();

        public int Read(float[] buffer, int offset, int count)
        {
            if (_finished) return 0;
            var written = 0;
            var rewound = false;
            while (written < count)
            {
                var n = _src.Read(buffer, offset + written, count - written);
                if (n == 0)
                {
                    // Loop, but never spin on a source that yields nothing even from the start.
                    if (_loop && _stopStart < 0 && !rewound) { _src.Rewind(); rewound = true; continue; }
                    break;
                }
                rewound = false;
                written += n;
            }

            var len = _src.Length;
            for (var i = 0; i < written; i++)
            {
                var pos = _played + i;
                var g = _volume;
                if (_fadeInSamples > 0 && pos < _fadeInSamples) g *= (float)pos / _fadeInSamples;
                if (!_loop && _fadeOutSamples > 0 && len > 0)
                {
                    var remain = len - _src.Position + (written - i);
                    if (remain < _fadeOutSamples) g *= Math.Max(0, (float)remain / _fadeOutSamples);
                }
                if (_stopStart >= 0)
                {
                    var k = pos - _stopStart;
                    if (k >= _stopLen) { g = 0; }
                    else g *= 1 - (float)k / _stopLen;
                }
                buffer[offset + i] *= g;
            }
            _played += written;

            if (written == 0 || (_stopStart >= 0 && _played - _stopStart >= _stopLen))
            {
                Finish();
                return written; // mixer removes inputs that return fewer samples than requested
            }
            return written;
        }

        private void Finish()
        {
            if (Interlocked.Exchange(ref _reported, 1) == 1) { _finished = true; return; }
            _finished = true;
            try { _src.Dispose(); } catch { }
            ThreadPool.QueueUserWorkItem(_ => _owner.OnVoiceFinished(this));
        }
    }

    private sealed class MasterVolumeProvider : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private readonly SoundboardEngine _eng;
        public MasterVolumeProvider(ISampleProvider src, SoundboardEngine eng) { _src = src; _eng = eng; }
        public WaveFormat WaveFormat => _src.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            var n = _src.Read(buffer, offset, count);
            var g = Math.Clamp(_eng.MasterVolume, 0f, 1f);
            if (Math.Abs(g - 1f) > 0.0001f)
                for (var i = 0; i < n; i++) buffer[offset + i] *= g;
            return n;
        }
    }
}
