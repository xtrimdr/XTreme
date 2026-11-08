using System.Globalization;

namespace XtremeDaw.Audio.Dsp;

/// <summary>
/// Port of <c>EqBand</c> in the web app's <c>eq.ts</c>.
/// </summary>
public readonly record struct EqBandSpec(
    bool On,
    BiquadType Type,
    double Freq,
    double Gain = 0,
    double Q = 1,
    int Slope = 12,
    bool Dyn = false,
    double DynThreshold = EqDynamics.DefaultThresholdDb,
    double DynRange = EqDynamics.DefaultRangeDb,
    double DynAttackMs = EqDynamics.DefaultAttackMs,
    double DynReleaseMs = EqDynamics.DefaultReleaseMs)
{
    public static EqBandSpec FromStrings(bool on, string? type, double freq, double gain, double q, int slope, BiquadType fallbackType = BiquadType.Peaking) =>
        new(
            On: on,
            Type: BiquadSpec.ParseType(type, fallbackType),
            Freq: Math.Clamp(double.IsFinite(freq) ? freq : 1000, TrackEq.MinHz, TrackEq.MaxHz),
            Gain: Math.Clamp(double.IsFinite(gain) ? gain : 0, -TrackEq.GainMax, TrackEq.GainMax),
            Q: Math.Clamp(double.IsFinite(q) ? q : 1, TrackEq.QMin, TrackEq.QMax),
            Slope: slope == 24 ? 24 : 12);
}

public sealed record EqPresetDef(string Id, string Label, string Hint, EqBandSpec[] Bands);

/// <summary>
/// Port of the web app's <c>eq.ts</c>: per-track 7-band parametric EQ (low cut, 5 bells, high cut),
/// 2 biquad stages per band (14 Biquads in series, zero latency, zero allocations in <see cref="Process"/>). A bell or
/// shelf can also move with the signal (<see cref="EqDynamics"/>); those run after the static curve.
/// </summary>
public sealed class TrackEq
{
    public const double MinHz = 20;
    public const double MaxHz = 20000;
    public const double GainMax = 18;
    public const double QMin = 0.3;
    public const double QMax = 12;
    public const int BandCount = 7;
    public const int StagesPerBand = 2;
    public const int TotalStages = BandCount * StagesPerBand;

    static readonly double Butter12 = 20.0 * Math.Log10(Math.Sqrt(0.5));
    static readonly double Butter24A = 20.0 * Math.Log10(0.5412);
    static readonly double Butter24B = 20.0 * Math.Log10(1.3066);

    public static readonly EqBandSpec[] DefaultBands =
    [
        new(false, BiquadType.Highpass, 80, 0, 0.7, 12),
        new(false, BiquadType.Peaking, 250, 0, 1, 12),
        new(false, BiquadType.Peaking, 800, 0, 1, 12),
        new(false, BiquadType.Peaking, 2000, 0, 1, 12),
        new(false, BiquadType.Peaking, 4500, 0, 1, 12),
        new(false, BiquadType.Peaking, 8000, 0, 1, 12),
        new(false, BiquadType.Lowpass, 12000, 0, 0.7, 12),
    ];

    public static readonly EqPresetDef[] Presets =
    [
        new("flat", "Plano", "Todas las bandas apagadas", [.. DefaultBands]),
        new("rhythm", "Rítmica", "Saca graves que embarran y un poco de barro en 250 Hz",
        [
            DefaultBands[0] with { On = true, Freq = 100, Slope = 24 },
            DefaultBands[1] with { On = true, Freq = 250, Gain = -2.5, Q = 1.2 },
            DefaultBands[2],
            DefaultBands[3],
            DefaultBands[4],
            DefaultBands[5],
            DefaultBands[6],
        ]),
        new("lead", "Punteo", "Doma lo chirriante (3–4 kHz) y corta el fizz de arriba",
        [
            DefaultBands[0] with { On = true, Freq = 120 },
            DefaultBands[1],
            DefaultBands[2],
            DefaultBands[3],
            DefaultBands[4] with { On = true, Freq = 3500, Gain = -3, Q = 1.4 },
            DefaultBands[5],
            DefaultBands[6] with { On = true, Freq = 9000 },
        ]),
        new("acoustic", "Acústica", "Menos retumbe de caja, un poco de brillo",
        [
            DefaultBands[0] with { On = true, Freq = 80, Slope = 24 },
            DefaultBands[1] with { On = true, Freq = 200, Gain = -3, Q = 1 },
            DefaultBands[2],
            DefaultBands[3],
            DefaultBands[4] with { On = true, Type = BiquadType.Highshelf, Freq = 8000, Gain = 2 },
            DefaultBands[5],
            DefaultBands[6],
        ]),
        new("front", "Al frente", "Más presencia para que la guitarra se escuche adelante",
        [
            DefaultBands[0] with { On = true, Freq = 90 },
            DefaultBands[1],
            DefaultBands[2],
            DefaultBands[3] with { On = true, Freq = 2200, Gain = 2.5, Q = 0.9 },
            DefaultBands[4],
            DefaultBands[5],
            DefaultBands[6],
        ]),
    ];

    sealed record EqConfig(bool Active, Biquad.Coeffs[] StageCoeffs);

    readonly double fs;
    readonly Biquad[] stages = new Biquad[TotalStages];
    readonly EqDynamics[] dynamics = new EqDynamics[BandCount];
    volatile EqConfig config;

    public TrackEq(double sampleRate)
    {
        fs = sampleRate;
        var flat = new Biquad.Coeffs[TotalStages];
        for (int i = 0; i < TotalStages; i++)
        {
            stages[i] = new Biquad(sampleRate);
            flat[i] = Biquad.FlatCoeffs;
        }
        config = new EqConfig(false, flat);
        for (int i = 0; i < BandCount; i++) dynamics[i] = new EqDynamics(sampleRate);
    }

    /// <summary>What a band's dynamics are doing now (dB), for the graph.</summary>
    public double DynamicDb(int band) => dynamics[band].NowDb;

    /// <summary>How loud a band's own stretch of the spectrum is now (dBFS, what its threshold is compared with), for the graph.</summary>
    public double DynamicLevelDb(int band) => dynamics[band].LevelDb;

    public static bool IsCut(BiquadType t) => t is BiquadType.Highpass or BiquadType.Lowpass;

    public static bool HasGain(BiquadType t) => !IsCut(t);

    public static string TypeLabel(BiquadType t) => t switch
    {
        BiquadType.Highpass => "Corte de graves",
        BiquadType.Lowshelf => "Estante grave",
        BiquadType.Peaking => "Campana",
        BiquadType.Highshelf => "Estante agudo",
        BiquadType.Lowpass => "Corte de agudos",
        _ => "Campana",
    };

    static double F(double v) => (float)v;

    /// <summary>Port of <c>bandSpecs</c> in <c>eq.ts</c>.</summary>
    public static (BiquadSpec Stage0, BiquadSpec Stage1) BandSpecs(EqBandSpec b, bool eqOn = true)
    {
        if (!eqOn || !b.On) return (BiquadSpec.Flat, BiquadSpec.Flat);
        double freq = F(Math.Clamp(b.Freq, MinHz, MaxHz));
        if (IsCut(b.Type))
        {
            if (b.Slope == 24)
            {
                return (
                    new BiquadSpec(b.Type, freq, F(Butter24A), 0),
                    new BiquadSpec(b.Type, freq, F(Butter24B), 0));
            }
            return (
                new BiquadSpec(b.Type, freq, F(Butter12), 0),
                BiquadSpec.Flat);
        }
        return (
            new BiquadSpec(b.Type, freq, F(Math.Clamp(b.Q, QMin, QMax)), F(Math.Clamp(b.Gain, -GainMax, GainMax))),
            BiquadSpec.Flat);
    }

    /// <summary>Port of <c>eqSpecs</c> in <c>eq.ts</c>.</summary>
    public static void GetSpecs(bool eqOn, ReadOnlySpan<EqBandSpec> bands, Span<BiquadSpec> dst)
    {
        for (int i = 0; i < BandCount; i++)
        {
            var b = i < bands.Length ? bands[i] : DefaultBands[i];
            var (s0, s1) = BandSpecs(b, eqOn);
            dst[i * 2] = s0;
            dst[i * 2 + 1] = s1;
        }
    }

    /// <summary>Port of <c>eqActive</c> in <c>eq.ts</c>.</summary>
    public static bool IsActive(bool eqOn, ReadOnlySpan<EqBandSpec> bands)
    {
        if (!eqOn) return false;
        for (int i = 0; i < bands.Length; i++)
        {
            var b = bands[i];
            if (b.On && (IsCut(b.Type) || Math.Abs(b.Gain) >= 0.05)) return true;
        }
        return false;
    }

    public static EqBandSpec[] ApplyPreset(string id)
    {
        foreach (var p in Presets)
            if (p.Id == id) return [.. p.Bands];
        return [.. Presets[0].Bands];
    }

    /// <summary>Port of <c>eqPresetOf</c> in <c>eq.ts</c>.</summary>
    public static string PresetOf(ReadOnlySpan<EqBandSpec> bands)
    {
        if (bands.Length < BandCount) return "";
        foreach (var p in Presets)
        {
            bool match = true;
            for (int i = 0; i < BandCount; i++)
            {
                if (!SameBand(p.Bands[i], bands[i])) { match = false; break; }
            }
            if (match) return p.Id;
        }
        return "";
    }

    static bool SameBand(EqBandSpec a, EqBandSpec b) =>
        a.On == b.On &&
        (!a.On ||
            (a.Type == b.Type &&
             Math.Abs(a.Freq - b.Freq) < 0.5 &&
             Math.Abs(a.Gain - b.Gain) < 0.05 &&
             Math.Abs(a.Q - b.Q) < 0.01 &&
             a.Slope == b.Slope));

    public static string FmtHz(double v) =>
        v >= 1000
            ? $"{(v / 1000.0).ToString(v >= 10000 ? "0" : "0.0", CultureInfo.InvariantCulture)}k"
            : $"{Math.Round(v).ToString("0", CultureInfo.InvariantCulture)}";

    public void Set(bool eqOn, ReadOnlySpan<EqBandSpec> bands)
    {
        Span<BiquadSpec> specs = stackalloc BiquadSpec[TotalStages];
        GetSpecs(eqOn, bands, specs);
        var stageCoeffs = new Biquad.Coeffs[TotalStages];
        bool any = false;
        for (int i = 0; i < TotalStages; i++)
        {
            stageCoeffs[i] = Biquad.Compute(fs, specs[i]);
            if (!specs[i].IsFlat) any = true;
        }
        config = new EqConfig(any, stageCoeffs);
        for (int i = 0; i < BandCount; i++) dynamics[i].Set(i < bands.Length && eqOn ? bands[i] : DefaultBands[i]);
    }

    public void Reset()
    {
        for (int i = 0; i < TotalStages; i++) stages[i].Reset();
        for (int i = 0; i < BandCount; i++) dynamics[i].Reset();
    }

    public void Process(Span<float> left, Span<float> right)
    {
        var cfg = config;
        if (cfg.Active)
            for (int i = 0; i < TotalStages; i++)
                stages[i].Process(left, right, cfg.StageCoeffs[i]);
        for (int i = 0; i < BandCount; i++) dynamics[i].Process(left, right);
    }
}

/// <summary>
/// A band that moves with the signal: it listens to its own stretch of the spectrum and, as that goes over
/// <see cref="EqBandSpec.DynThreshold"/>, adds up to <see cref="EqBandSpec.DynRange"/> dB there (usually a cut: it tames a
/// resonance or a pick only when it jumps out). Reached <see cref="SpanDb"/> over the threshold. A band filter is both the
/// detector and what is added back: out = in + k·band, with k from the gain, so k = 0 is the input bit for bit and no
/// coefficient is ever worked out on the audio thread. A bell uses a band pass at its frequency and Q, a shelf a low or
/// high pass. Zero latency, nothing allocated.
/// </summary>
public sealed class EqDynamics
{
    public const double DefaultThresholdDb = -24, DefaultRangeDb = -6, DefaultAttackMs = 5, DefaultReleaseMs = 120;
    public const double ThresholdMinDb = -60, RangeMaxDb = 18, AttackMinMs = 0.5, AttackMaxMs = 100, ReleaseMinMs = 10, ReleaseMaxMs = 1000;
    /// <summary>How far over the threshold the band reaches its whole range.</summary>
    const double SpanDb = 12;
    /// <summary>Under this (dB) the band counts as at rest.</summary>
    const double RestDb = 1e-3;
    /// <summary>A shelf's detector, Butterworth (in dB, as <see cref="Biquad"/> reads Q for a low or high pass).</summary>
    static readonly double ButterQDb = 20 * Math.Log10(Math.Sqrt(0.5));

    sealed record Shape(bool On, Biquad.Coeffs Filter, double Threshold, double Range, double Attack, double Release);

    readonly double fs;
    readonly Biquad filter;
    volatile Shape shape;
    double env, gain;
    volatile float now, level = SilentDb;
    bool idle = true;
    /// <summary>The band level reported while nothing comes in.</summary>
    const float SilentDb = -120;

    public EqDynamics(double sampleRate)
    {
        fs = sampleRate;
        filter = new Biquad(sampleRate);
        shape = new Shape(false, Biquad.FlatCoeffs, DefaultThresholdDb, 0, 0, 0);
    }

    public double NowDb => now;

    /// <summary>The detector's level at the end of the last block (dBFS).</summary>
    public double LevelDb => level;

    public void Set(EqBandSpec b)
    {
        bool on = b.On && b.Dyn && !TrackEq.IsCut(b.Type) && Math.Abs(b.DynRange) >= 0.05;
        if (!on)
        {
            shape = shape with { On = false };
            return;
        }
        double hz = Math.Clamp(b.Freq, TrackEq.MinHz, Math.Min(TrackEq.MaxHz, fs * 0.45));
        Biquad.Coeffs c;
        if (b.Type == BiquadType.Lowshelf) c = Biquad.Compute(fs, new BiquadSpec(BiquadType.Lowpass, hz, ButterQDb));
        else if (b.Type == BiquadType.Highshelf) c = Biquad.Compute(fs, new BiquadSpec(BiquadType.Highpass, hz, ButterQDb));
        else
        {
            // RBJ band pass with 0 dB at the centre
            double w0 = 2 * Math.PI * hz / fs, q = Math.Clamp(b.Q, TrackEq.QMin, TrackEq.QMax), alpha = Math.Sin(w0) / (2 * q), a0 = 1 + alpha;
            c = new Biquad.Coeffs(new BiquadSpec(BiquadType.Peaking, hz, q), alpha / a0, 0, -alpha / a0, -2 * Math.Cos(w0) / a0, (1 - alpha) / a0);
        }
        double attack = Math.Clamp(b.DynAttackMs, AttackMinMs, AttackMaxMs) / 1000, release = Math.Clamp(b.DynReleaseMs, ReleaseMinMs, ReleaseMaxMs) / 1000;
        shape = new Shape(true, c, b.DynThreshold, Math.Clamp(b.DynRange, -RangeMaxDb, RangeMaxDb), Math.Exp(-1 / (attack * fs)), Math.Exp(-1 / (release * fs)));
    }

    public void Reset()
    {
        filter.Reset();
        env = gain = 0;
        now = 0;
        level = SilentDb;
        idle = true;
    }

    public void Process(Span<float> left, Span<float> right)
    {
        var s = shape;
        // off and let go, nothing to do; the filter starts clean when it comes back
        if (!s.On && Math.Abs(gain) < RestDb)
        {
            gain = 0;
            now = 0;
            level = SilentDb;
            idle = true;
            return;
        }
        if (idle)
        {
            filter.Reset();
            env = 0;
            idle = false;
        }
        var c = s.Filter;
        double e = env, g = gain;
        double aR = s.On ? s.Release : Math.Exp(-1 / (ReleaseMinMs / 1000 * fs));
        for (int i = 0; i < left.Length; i++)
        {
            double l = left[i], r = right[i];
            double bl = filter.ProcessSample(l, 0, c), br = filter.ProcessSample(r, 1, c);
            double x = Math.Max(Math.Abs(bl), Math.Abs(br));
            e = x > e ? s.Attack * e + (1 - s.Attack) * x : aR * e + (1 - aR) * x;
            double target = 0;
            if (s.On && e > 1e-6)
                target = s.Range * Math.Clamp((20 * Math.Log10(e) - s.Threshold) / SpanDb, 0, 1);
            g = s.On ? target : aR * g;
            if (Math.Abs(g) < RestDb) continue;
            double k = Math.Pow(10, g / 20) - 1;
            left[i] = (float)(l + k * bl);
            right[i] = (float)(r + k * br);
        }
        env = e;
        gain = g;
        now = (float)g;
        level = e > 1e-6 ? (float)(20 * Math.Log10(e)) : SilentDb;
    }
}
