using System.Globalization;

namespace XtremeDaw.Audio.Dsp;

/// <summary>
/// Port of <c>CompEq</c> in the web app's <c>types.ts</c>: low-cut and mud cut before the compressor,
/// presence bell and air high-shelf after it.
/// </summary>
public readonly record struct CompEqParams(
    double HpfHz,
    double MudDb,
    double PresenceDb,
    double PresenceHz,
    double AirDb)
{
    public static readonly CompEqParams Flat = new(10, 0, 0, 2500, 0);
}

/// <summary>
/// Port of <c>CompSettings</c> in the web app's <c>types.ts</c> for DSP calculations.
/// </summary>
public readonly record struct CompState(
    bool On,
    string Preset,
    double Amount,
    bool Limit,
    double Threshold,
    double Ratio,
    double Attack,
    double Release,
    double Knee,
    double Makeup,
    CompEqParams Eq,
    double? EstGrDb = null,
    double ThresholdTrim = 0,
    double MakeupTrim = 0)
{
    public static readonly CompState Defaults = new(
        On: false,
        Preset: "rhythm",
        Amount: 0.5,
        Limit: true,
        Threshold: -18,
        Ratio: 4,
        Attack: 0.01,
        Release: 0.15,
        Knee: 6,
        Makeup: 0,
        Eq: CompEqParams.Flat);
}

public sealed record CompPresetDef(
    string Group,
    string Id,
    string Label,
    string Hint,
    (double Min, double Max) Gr,
    (double Min, double Max) Ratio,
    (double Min, double Max) AttackMs,
    (double Min, double Max) ReleaseMs,
    (double Min, double Max) Knee,
    double TargetRmsDb,
    (double Min, double Max) Hpf,
    (double Min, double Max) Mud,
    (double Min, double Max) Presence,
    double PresenceHz,
    (double Min, double Max) Air);

/// <summary>
/// Simple-mode compressor presets (<see cref="CompPresetTable"/>). A preset goes straight to fixed values
/// (<see cref="Apply"/>, decision of the user: no reading of the take), worked out for a take at <see cref="RefPeakDb"/>.
/// Only when asked, <see cref="UpdateComp(CompState, TakeEnvelope)"/> (port of <c>compPresets.ts</c>) simulates the
/// compressor on the take's 2 ms envelope to find the threshold and make-up, and keeps how far they are from the fixed
/// ones as trims, so the amount and other presets stay fixed-value sums on top of what was measured.
/// </summary>
public static class CompPresets
{
    public const double TrackLimitCeilingDb = -3;
    public const double EqMudHz = 280;
    public const double EqAirHz = 9000;
    /// <summary>The take the fixed values are for: its typical peak and level, as a recording usually sits (dBFS).</summary>
    public const double RefPeakDb = -12, RefRmsDb = -24;

    const double BlockSec = 0.002;
    const int WinBlocks = 25; // 50 ms windows

    /// <summary>A preset's <see cref="CompPresetDef.TargetRmsDb"/> when the make-up only gives back what it took off.</summary>
    public const double SameLevel = double.NaN;

    public static CompPresetGroup[] Groups => CompPresetTable.Groups;

    public static CompPresetDef[] All => CompPresetTable.All;

    public static string PresetLabel(string? id)
    {
        if (id == "custom") return "Manual";
        foreach (var p in All)
            if (p.Id == id) return p.Label;
        return id ?? "Rasgueo";
    }

    public static string NormalizePreset(string? id, string fallback = "rhythm")
    {
        if (string.IsNullOrEmpty(id)) return fallback;
        if (id == "custom") return "custom";
        foreach (var p in All)
            if (p.Id == id) return id;
        return id switch
        {
            "clean" => "arpeggio",
            "acoustic" => "rhythm",
            "level" => "rhythm",
            _ => fallback,
        };
    }

    public static string AmountLabel(double a) =>
        a < 0.2 ? "Muy suave" : a < 0.45 ? "Suave" : a < 0.7 ? "Media" : a < 0.9 ? "Fuerte" : "Muy fuerte";

    public static string Summary(CompState c, CultureInfo? culture = null)
    {
        var fmt = culture ?? CultureInfo.CurrentCulture;
        double gr = c.EstGrDb ?? Math.Max(0, -c.Threshold / 4.0);
        var parts = new List<string>(2) { $"comprime ~{gr.ToString("0", fmt)} dB" };
        if (c.Eq.PresenceDb >= 1)
            parts.Add($"presencia +{c.Eq.PresenceDb.ToString("0.0", fmt)} dB");
        else if (c.Eq.PresenceDb <= -1)
            parts.Add($"presencia {c.Eq.PresenceDb.ToString("0.0", fmt)} dB");
        return string.Join(" · ", parts);
    }

    public sealed class TakeEnvelope
    {
        public required float[] PeakDb { get; init; }
        public required float[] Pow { get; init; }
        public required int[] Active { get; init; }
        public required double RmsDb { get; init; }
        public required double PeakRefDb { get; init; }
    }

    static double ToDb(double x) => x > 1e-12 ? 10 * Math.Log10(x) : -120;

    static double Lerp((double Min, double Max) r, double t) => r.Min + (r.Max - r.Min) * t;

    static double JsRound(double x) => Math.Floor(x + 0.5);

    static double RoundStep(double v, double step) => JsRound(v / step) / (1.0 / step);

    static double Pct(List<double> arr, double p, double fallback)
    {
        if (arr.Count == 0) return fallback;
        var s = arr.ToArray();
        Array.Sort(s);
        return s[Math.Min(s.Length - 1, (int)Math.Floor(s.Length * p))];
    }

    public static TakeEnvelope Analyze(float[][] chans, int sampleRate)
    {
        int bs = Math.Max(1, (int)JsRound(sampleRate * BlockSec));
        // no channels: a track without audio
        int nb = chans.Length == 0 ? 0 : chans[0].Length / bs;
        var peakDb = new float[nb];
        var pow = new float[nb];
        for (int b = 0; b < nb; b++)
        {
            double sum = 0;
            double pk = 0;
            foreach (var ch in chans)
            {
                for (int i = b * bs, end = i + bs; i < end; i++)
                {
                    double v = ch[i];
                    sum += v * v;
                    double a = v < 0 ? -v : v;
                    if (a > pk) pk = a;
                }
            }
            pow[b] = (float)(sum / (bs * chans.Length));
            peakDb[b] = (float)ToDb(pk * pk);
        }
        var winRms = new List<double>();
        var winPeak = new List<double>();
        var winStart = new List<int>();
        for (int s = 0; s + WinBlocks <= nb; s += WinBlocks)
        {
            double sum = 0;
            double pk = -120;
            for (int b = s; b < s + WinBlocks; b++)
            {
                sum += pow[b];
                if (peakDb[b] > pk) pk = peakDb[b];
            }
            winRms.Add(ToDb(sum / WinBlocks));
            winPeak.Add(pk);
            winStart.Add(s);
        }
        double loud = Pct(winRms, 0.95, -30);
        double floor = Math.Max(-55, loud - 40);
        var active = new List<int>();
        var keepRms = new List<double>();
        var keepPeak = new List<double>();
        for (int i = 0; i < winRms.Count; i++)
        {
            if (winRms[i] <= floor) continue;
            active.Add(winStart[i]);
            keepRms.Add(winRms[i]);
            keepPeak.Add(winPeak[i]);
        }
        return new TakeEnvelope
        {
            PeakDb = peakDb,
            Pow = pow,
            Active = active.ToArray(),
            RmsDb = Pct(keepRms, 0.8, -30),
            PeakRefDb = Pct(keepPeak, 0.8, -18),
        };
    }

    readonly record struct Curve(double Threshold, double Ratio, double Knee, double Attack, double Release);

    static (double Gr, double OutRmsDb) Simulate(TakeEnvelope a, Curve c, float[] scratch, bool wantOut = false)
    {
        double T = c.Threshold;
        double W = c.Knee;
        double slope = 1.0 / Math.Max(1.0, c.Ratio) - 1.0;
        double aA = Math.Exp(-BlockSec / Math.Max(0.0001, c.Attack));
        double aR = Math.Exp(-BlockSec / Math.Max(0.005, c.Release));
        int n = a.PeakDb.Length;
        double y = 0;
        for (int b = 0; b < n; b++)
        {
            double over = a.PeakDb[b] - T;
            double target;
            if (2 * over <= -W) target = 0;
            else if (W > 0 && 2 * Math.Abs(over) < W) target = slope * (over + W / 2) * (over + W / 2) / (2 * W);
            else target = slope * over;
            y = target < y ? aA * y + (1 - aA) * target : aR * y + (1 - aR) * target;
            scratch[b] = (float)y;
        }
        var winGr = new List<double>(a.Active.Length);
        var winOut = wantOut ? new List<double>(a.Active.Length) : null;
        foreach (int s in a.Active)
        {
            double g = 0;
            double p = 0;
            for (int b = s; b < s + WinBlocks; b++)
            {
                g += scratch[b];
                if (wantOut) p += a.Pow[b] * Math.Pow(10, scratch[b] / 10.0);
            }
            winGr.Add(-g / WinBlocks);
            winOut?.Add(ToDb(p / WinBlocks));
        }
        return (Pct(winGr, 0.8, 0), wantOut && winOut != null ? Pct(winOut, 0.8, a.RmsDb) : a.RmsDb);
    }

    static double FindThreshold(TakeEnvelope a, Curve shape, double targetGr, float[] scratch)
    {
        double lo = a.PeakRefDb - 50;
        double hi = a.PeakRefDb + 6;
        for (int i = 0; i < 14; i++)
        {
            double mid = (lo + hi) / 2.0;
            var (gr, _) = Simulate(a, shape with { Threshold = mid }, scratch);
            if (gr > targetGr) lo = mid;
            else hi = mid;
        }
        return (lo + hi) / 2.0;
    }

    public static CompState UpdateComp(CompState comp, float[][] chans, int sampleRate) =>
        UpdateComp(comp, Analyze(chans, sampleRate));

    static CompPresetDef? Find(string id) => Array.Find(All, p => p.Id == id);

    static Curve Shape(CompPresetDef p, double t) => new(
        Threshold: 0,
        Ratio: Lerp(p.Ratio, t),
        Knee: Lerp(p.Knee, t),
        Attack: Lerp(p.AttackMs, t) / 1000.0,
        Release: Lerp(p.ReleaseMs, t) / 1000.0);

    /// <summary>
    /// The threshold that takes <paramref name="gr"/> dB off a peak at <see cref="RefPeakDb"/>, and the make-up that
    /// brings a take at <see cref="RefRmsDb"/> to the preset's level (with <see cref="SameLevel"/>, what it took off).
    /// </summary>
    static (double Threshold, double Makeup) Fixed(CompPresetDef p, Curve shape, double gr)
    {
        double over = gr / (1 - 1 / Math.Max(1.01, shape.Ratio));
        double makeup = double.IsNaN(p.TargetRmsDb) ? gr : p.TargetRmsDb - (RefRmsDb - gr);
        return (RefPeakDb - over, makeup);
    }

    /// <summary>A preset's values at its amount, without reading any audio: the trims of the last analysis stay.</summary>
    public static CompState Apply(CompState next)
    {
        if (Find(next.Preset) is not { } preset) return next;
        double t = Math.Clamp(next.Amount, 0, 1);
        var shape = Shape(preset, t);
        double gr = Lerp(preset.Gr, t);
        var (threshold, makeup) = Fixed(preset, shape, gr);
        return Values(next, preset, t, shape, threshold + next.ThresholdTrim, makeup + next.MakeupTrim, gr);
    }

    /// <summary>The analysis: a preset's values measured on the take, which stay as trims over the fixed ones.</summary>
    public static CompState UpdateComp(CompState next, TakeEnvelope a)
    {
        if (Find(next.Preset) is not { } preset) return next;
        double t = Math.Clamp(next.Amount, 0, 1);
        var shape = Shape(preset, t);
        var scratch = new float[a.PeakDb.Length];
        double threshold = Math.Clamp(FindThreshold(a, shape, Lerp(preset.Gr, t), scratch), -60, 0);
        var sim = Simulate(a, shape with { Threshold = threshold }, scratch, wantOut: true);
        double target = double.IsNaN(preset.TargetRmsDb) ? a.RmsDb : preset.TargetRmsDb;
        double makeup = Math.Clamp(target - sim.OutRmsDb, -12, 30);
        var (fixedThreshold, fixedMakeup) = Fixed(preset, shape, Lerp(preset.Gr, t));
        return Values(next with { ThresholdTrim = threshold - fixedThreshold, MakeupTrim = makeup - fixedMakeup }, preset, t, shape, threshold, makeup, sim.Gr);
    }

    static CompState Values(CompState next, CompPresetDef preset, double t, Curve shape, double threshold, double makeup, double gr)
    {
        threshold = Math.Clamp(threshold, -60, 0);
        makeup = Math.Clamp(makeup, -12, 30);
        var eq = new CompEqParams(
            HpfHz: JsRound(Lerp(preset.Hpf, t)),
            MudDb: RoundStep(Lerp(preset.Mud, t), 0.5),
            PresenceDb: RoundStep(Lerp(preset.Presence, t), 0.5),
            PresenceHz: preset.PresenceHz,
            AirDb: RoundStep(Lerp(preset.Air, t), 0.5));
        return next with
        {
            Ratio = RoundStep(shape.Ratio, 0.1),
            Threshold = RoundStep(threshold, 0.5),
            Attack = RoundStep(shape.Attack * 1000.0, 0.5) / 1000.0,
            Release = JsRound(shape.Release * 1000.0) / 1000.0,
            Knee = RoundStep(shape.Knee, 0.5),
            Makeup = RoundStep(makeup, 0.5),
            Eq = eq,
            EstGrDb = RoundStep(gr, 0.5),
        };
    }
}

/// <summary>
/// Port of the 4 biquads around the compressor in the web app's <c>mixGraph.ts</c>:
/// <c>eq[0]</c> (highpass) and <c>eq[1]</c> (mud peaking at 280 Hz) before the compressor;
/// <c>eq[2]</c> (presence peaking) and <c>eq[3]</c> (air highshelf at 9000 Hz) after it.
/// Updates via <see cref="Set"/> replace a single immutable configuration reference so live UI edits
/// never tear coefficients on the audio thread.
/// </summary>
public sealed class CompEqFilter
{
    sealed record FilterConfig(Biquad.Coeffs Hpf, Biquad.Coeffs Mud, Biquad.Coeffs Presence, Biquad.Coeffs Air);

    readonly double fs;
    readonly Biquad hpf;
    readonly Biquad mud;
    readonly Biquad presence;
    readonly Biquad air;
    volatile FilterConfig config;

    public CompEqFilter(double sampleRate)
    {
        fs = sampleRate;
        hpf = new Biquad(sampleRate);
        mud = new Biquad(sampleRate);
        presence = new Biquad(sampleRate);
        air = new Biquad(sampleRate);
        config = BuildConfig(false, CompEqParams.Flat);
    }

    static double F(double v) => (float)v;

    FilterConfig BuildConfig(bool compOn, CompEqParams eq)
    {
        var e = compOn ? eq : CompEqParams.Flat;
        return new FilterConfig(
            Biquad.Compute(fs, new BiquadSpec(BiquadType.Highpass, F(Math.Clamp(e.HpfHz, 10, 1000)), F(0.707), 0)),
            Biquad.Compute(fs, new BiquadSpec(BiquadType.Peaking, F(CompPresets.EqMudHz), F(1), F(Math.Clamp(e.MudDb, -12, 12)))),
            Biquad.Compute(fs, new BiquadSpec(BiquadType.Peaking, F(Math.Clamp(e.PresenceHz, 500, 8000)), F(0.9), F(Math.Clamp(e.PresenceDb, -12, 12)))),
            Biquad.Compute(fs, new BiquadSpec(BiquadType.Highshelf, F(CompPresets.EqAirHz), F(0.707), F(Math.Clamp(e.AirDb, -12, 12)))));
    }

    public void Set(bool compOn, CompEqParams eq) => config = BuildConfig(compOn, eq);

    public void Reset()
    {
        hpf.Reset();
        mud.Reset();
        presence.Reset();
        air.Reset();
    }

    public void ProcessPre(Span<float> left, Span<float> right)
    {
        var cfg = config;
        hpf.Process(left, right, cfg.Hpf);
        mud.Process(left, right, cfg.Mud);
    }

    public void ProcessPost(Span<float> left, Span<float> right)
    {
        var cfg = config;
        presence.Process(left, right, cfg.Presence);
        air.Process(left, right, cfg.Air);
    }

    public void ProcessWith(Compressor comp, Span<float> left, Span<float> right) => ProcessWith(comp, left, right, default, default);

    /// <summary>The same with the compressor listening to a key (its sidechain), which these filters don't touch.</summary>
    public void ProcessWith(Compressor comp, Span<float> left, Span<float> right, ReadOnlySpan<float> keyL, ReadOnlySpan<float> keyR)
    {
        var cfg = config;
        hpf.Process(left, right, cfg.Hpf);
        mud.Process(left, right, cfg.Mud);
        comp.Process(left, right, keyL, keyR);
        presence.Process(left, right, cfg.Presence);
        air.Process(left, right, cfg.Air);
    }
}
