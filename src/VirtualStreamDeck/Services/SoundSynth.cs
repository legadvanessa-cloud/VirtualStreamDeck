namespace VirtualStreamDeck.Services;

/// <summary>
/// The built-in sounds are synthesised at runtime rather than shipped as recordings: nothing to
/// license, nothing to extract, and the exe stays free of bundled media files.
/// All generators return interleaved stereo float samples at the requested rate.
/// </summary>
public static class SoundSynth
{
    public static float[]? Generate(string key, int rate) => key switch
    {
        "airhorn" => Airhorn(rate),
        "applause" => Applause(rate),
        "laugh" => Laugh(rate),
        "error" => Error(rate),
        "notification" => Notification(rate),
        "tada" => Tada(rate),
        "rimshot" => Rimshot(rate),
        "alarm" => Alarm(rate),
        _ => null,
    };

    private static float[] Stereo(float[] mono, float width = 0f)
    {
        var o = new float[mono.Length * 2];
        for (var i = 0; i < mono.Length; i++) { o[2 * i] = mono[i]; o[2 * i + 1] = mono[i]; }
        return o;
    }

    private static void Normalize(float[] s, float peak = 0.85f)
    {
        var max = 0f;
        foreach (var v in s) max = Math.Max(max, Math.Abs(v));
        if (max < 1e-6f) return;
        var g = peak / max;
        for (var i = 0; i < s.Length; i++) s[i] *= g;
    }

    private static float Env(double t, double attack, double length, double release)
    {
        if (t < 0 || t > length) return 0;
        if (t < attack) return (float)(t / attack);
        if (t > length - release) return (float)Math.Max(0, (length - t) / release);
        return 1;
    }

    private static double Saw(double phase) => 2 * (phase - Math.Floor(phase + 0.5));
    private static double Square(double phase) => phase - Math.Floor(phase) < 0.5 ? 1 : -1;

    /// <summary>Simple resonant band-pass (RBJ biquad), used for formants and claps.</summary>
    private sealed class BandPass
    {
        private readonly double _b0, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;
        public BandPass(double freq, double q, int rate)
        {
            var w = 2 * Math.PI * freq / rate;
            var alpha = Math.Sin(w) / (2 * q);
            var a0 = 1 + alpha;
            _b0 = alpha / a0; _b2 = -alpha / a0;
            _a1 = -2 * Math.Cos(w) / a0; _a2 = (1 - alpha) / a0;
        }
        public double Process(double x)
        {
            var y = _b0 * x + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return y;
        }
    }

    private static float[] Airhorn(int rate)
    {
        // short - short - long, a detuned sawtooth stack with a slight upward bend and soft clip
        var blasts = new[] { (0.0, 0.22), (0.30, 0.22), (0.60, 1.05) };
        var len = (int)(1.75 * rate);
        var s = new float[len];
        var freqs = new[] { 466.0, 470.0, 587.0, 699.0 };
        var phases = new double[freqs.Length];
        for (var i = 0; i < len; i++)
        {
            var t = (double)i / rate;
            var amp = 0f;
            foreach (var (start, dur) in blasts) amp = Math.Max(amp, Env(t - start, 0.015, dur, 0.04));
            if (amp <= 0) continue;
            var bend = 1 + 0.02 * Math.Min(1, (t % 0.3) * 8);
            double v = 0;
            for (var k = 0; k < freqs.Length; k++)
            {
                phases[k] += freqs[k] * bend / rate;
                v += Saw(phases[k]) * (k == 0 ? 1 : 0.7);
            }
            s[i] = (float)Math.Tanh(v * 0.9) * amp;
        }
        Normalize(s);
        return Stereo(s);
    }

    private static float[] Applause(int rate)
    {
        var rnd = new Random(7);
        var len = (int)(3.2 * rate);
        var l = new float[len];
        var r = new float[len];
        var clapLen = (int)(0.045 * rate);
        for (var n = 0; n < 150; n++)
        {
            var start = (int)(rnd.NextDouble() * (len - clapLen));
            var bp = new BandPass(900 + rnd.NextDouble() * 1800, 1.2, rate);
            var pan = (float)rnd.NextDouble();
            var gain = 0.4f + (float)rnd.NextDouble() * 0.6f;
            for (var i = 0; i < clapLen; i++)
            {
                var e = (float)Math.Exp(-i / (0.008 * rate));
                var v = (float)bp.Process(rnd.NextDouble() * 2 - 1) * e * gain;
                l[start + i] += v * (1 - pan);
                r[start + i] += v * pan;
            }
        }
        var o = new float[len * 2];
        for (var i = 0; i < len; i++)
        {
            var t = (double)i / rate;
            var env = Env(t, 0.35, 3.2, 1.1);
            o[2 * i] = l[i] * env;
            o[2 * i + 1] = r[i] * env;
        }
        Normalize(o, 0.8f);
        return o;
    }

    private static float[] Laugh(int rate)
    {
        // "ha ha ha ha": a glottal pulse train through two vowel formants, with a breathy onset.
        var rnd = new Random(3);
        var syllables = 6;
        var sylLen = 0.13;
        var gap = 0.075;
        var len = (int)((syllables * (sylLen + gap) + 0.1) * rate);
        var s = new float[len];
        var f1 = new BandPass(820, 6, rate);
        var f2 = new BandPass(1250, 7, rate);
        var f3 = new BandPass(2600, 8, rate);
        double phase = 0;
        for (var i = 0; i < len; i++)
        {
            var t = (double)i / rate;
            var idx = (int)(t / (sylLen + gap));
            var local = t - idx * (sylLen + gap);
            if (idx >= syllables || local > sylLen) { f1.Process(0); f2.Process(0); f3.Process(0); continue; }
            var f0 = 250 - idx * 9 - local * 120;
            phase += f0 / rate;
            var pulse = phase - Math.Floor(phase) < 0.08 ? 1.0 : 0.0;
            var breath = local < 0.03 ? (rnd.NextDouble() * 2 - 1) * 0.6 : (rnd.NextDouble() * 2 - 1) * 0.05;
            var src = pulse + breath;
            var v = f1.Process(src) * 1.0 + f2.Process(src) * 0.6 + f3.Process(src) * 0.25;
            s[i] = (float)v * Env(local, 0.012, sylLen, 0.05) * (1 - idx * 0.07f);
        }
        Normalize(s);
        return Stereo(s);
    }

    private static float[] Tones(int rate, IEnumerable<(double Start, double Dur, double Freq, Func<double, double> Wave, double Gain)> notes, double total, double release = 0.03)
    {
        var len = (int)(total * rate);
        var s = new float[len];
        foreach (var n in notes)
        {
            var start = (int)(n.Start * rate);
            var dur = (int)(n.Dur * rate);
            double ph = 0;
            for (var i = 0; i < dur && start + i < len; i++)
            {
                ph += n.Freq / rate;
                s[start + i] += (float)(n.Wave(ph) * n.Gain) * Env((double)i / rate, 0.005, n.Dur, release);
            }
        }
        Normalize(s);
        return s;
    }

    private static float[] Error(int rate) => Stereo(Tones(rate, new (double, double, double, Func<double, double>, double)[]
    {
        (0.0, 0.16, 392, p => Square(p) * 0.5 + Math.Sin(2 * Math.PI * p) * 0.5, 1),
        (0.19, 0.30, 262, p => Square(p) * 0.5 + Math.Sin(2 * Math.PI * p) * 0.5, 1),
    }, 0.55, 0.06));

    private static float[] Notification(int rate)
    {
        var len = (int)(1.0 * rate);
        var s = new float[len];
        foreach (var (start, f) in new[] { (0.0, 880.0), (0.12, 1318.5) })
        {
            var st = (int)(start * rate);
            for (var i = 0; st + i < len; i++)
            {
                var t = (double)i / rate;
                var e = Math.Exp(-t * 5.5) * Math.Min(1, t / 0.004);
                s[st + i] += (float)((Math.Sin(2 * Math.PI * f * t) + 0.3 * Math.Sin(2 * Math.PI * f * 2.01 * t)) * e);
            }
        }
        Normalize(s, 0.7f);
        return Stereo(s);
    }

    private static float[] Tada(int rate)
    {
        double Brass(double p) => Saw(p) * 0.6 + Math.Sin(2 * Math.PI * p) * 0.4;
        var notes = new List<(double, double, double, Func<double, double>, double)>
        {
            (0.00, 0.12, 523.25, Brass, 0.8), (0.12, 0.12, 659.25, Brass, 0.8), (0.24, 0.12, 783.99, Brass, 0.8),
        };
        foreach (var f in new[] { 523.25, 659.25, 783.99, 1046.5 }) notes.Add((0.38, 1.1, f, Brass, 0.55));
        var s = Tones(rate, notes, 1.6, 0.5);
        return Stereo(s);
    }

    private static float[] Rimshot(int rate)
    {
        var rnd = new Random(11);
        var len = (int)(1.6 * rate);
        var s = new float[len];
        void Tom(double start, double f0)
        {
            var st = (int)(start * rate);
            double ph = 0;
            for (var i = 0; i < (int)(0.35 * rate) && st + i < len; i++)
            {
                var t = (double)i / rate;
                var f = f0 * (1 + 0.8 * Math.Exp(-t * 30));
                ph += f / rate;
                s[st + i] += (float)(Math.Sin(2 * Math.PI * ph) * Math.Exp(-t * 9) + (rnd.NextDouble() * 2 - 1) * Math.Exp(-t * 80) * 0.3);
            }
        }
        Tom(0.0, 190);
        Tom(0.22, 120);
        var cst = (int)(0.48 * rate);
        double prev = 0;
        for (var i = 0; cst + i < len; i++)
        {
            var t = (double)i / rate;
            var n = rnd.NextDouble() * 2 - 1;
            var hp = n - prev; prev = n; // crude high-pass for a cymbal wash
            s[cst + i] += (float)(hp * 0.45 * Math.Exp(-t * 3.2));
        }
        Normalize(s);
        return Stereo(s);
    }

    private static float[] Alarm(int rate)
    {
        var notes = new List<(double, double, double, Func<double, double>, double)>();
        for (var i = 0; i < 8; i++) notes.Add((i * 0.19, 0.16, i % 2 == 0 ? 960 : 760, p => Square(p) * 0.6 + Math.Sin(2 * Math.PI * p) * 0.4, 0.8));
        return Stereo(Tones(rate, notes, 1.6, 0.02));
    }
}
