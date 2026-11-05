using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.Projects;

/// <summary>How a parameter's value reads: the window formats it, this says what it is.</summary>
public enum FxUnit { Percent, Db, Hz, Ms, Sec, Ratio, Level, Ceiling, Recorte, Rate, Count, Division, Signed }

/// <summary>
/// One knob of a native effect: where it lives in the slot (<see cref="Get"/>, <see cref="Set"/>), its range and curve
/// and what the window shows. <see cref="Label"/> and <see cref="Tip"/> are text keys. <see cref="OffBelow"/>: at or under
/// this value it reads "Off". <see cref="OffAtZero"/>: the bottom of the knob is 0 = off, under a range that starts at
/// <see cref="Min"/>. <see cref="Fine"/>: it goes in the fine controls. <see cref="Manual"/>: moving it takes a
/// compressor out of its preset. <see cref="TurnsOn"/>: moving it turns the effect on. <see cref="Steps"/>: a stepped knob,
/// like the switches of the hardware a compressor model follows; it holds the step's value, not its number.
/// </summary>
public sealed record FxParam(string Id, string Label, double Min, double Max, double Default, FxUnit Unit, double Step, Func<InsertSlot, double> Get, Action<InsertSlot, double> Set)
{
    public bool Log { get; init; }
    public bool OffAtZero { get; init; }
    public double OffBelow { get; init; } = double.NegativeInfinity;
    public bool Fine { get; init; }
    public bool Manual { get; init; }
    public bool TurnsOn { get; init; }
    public string? Tip { get; init; }
    /// <summary>The text key it reads at or under <see cref="OffBelow"/> (null = "Off").</summary>
    public string? OffText { get; init; }
    /// <summary>A switch, not a knob: a lane holds it until its next point (<see cref="FxDefs.Power"/>).</summary>
    public bool Toggle { get; init; }
    /// <summary>The values a stepped knob stops at, in order, and the text key of the ones a number doesn't say (null = the value).</summary>
    public double[]? Steps { get; init; }
    public string?[]? StepNames { get; init; }

    int StepOf(double v)
    {
        int best = 0;
        for (int i = 1; i < Steps!.Length; i++)
            if (Math.Abs(Steps[i] - v) < Math.Abs(Steps[best] - v)) best = i;
        return best;
    }

    /// <summary>The nearest step's value (a stepped knob), or the value as it is.</summary>
    public double Snap(double v) => Steps == null ? v : Steps[StepOf(v)];

    /// <summary>The text key of a step that has a name ("Auto", "Todos"), or null.</summary>
    public string? StepName(double v) => Steps == null || StepNames == null ? null : StepNames[StepOf(v)];

    /// <summary>The knob's position (0 to 1) for a value.</summary>
    public double ToKnob(double v)
    {
        if (Steps != null) return Steps.Length < 2 ? 0 : StepOf(v) / (double)(Steps.Length - 1);
        if (OffAtZero && v <= 0) return 0;
        double x = Math.Clamp(v, Min, Max);
        return Log ? Math.Log(x / Min) / Math.Log(Max / Min) : (x - Min) / (Max - Min);
    }

    /// <summary>The value for a knob's position, on its steps.</summary>
    public double FromKnob(double k)
    {
        k = Math.Clamp(k, 0, 1);
        if (Steps != null) return Steps[(int)Math.Round(k * (Steps.Length - 1))];
        if (OffAtZero && k < 0.02) return 0;
        double v = Log ? Min * Math.Pow(Max / Min, k) : Min + (Max - Min) * k;
        return Step > 0 ? Math.Clamp(Math.Round(v / Step) * Step, Min, Max) : v;
    }
}

/// <summary>
/// One inner panel of an effect's full view: its title (a text key), what goes in it (knob ids of <see cref="FxDefs.Params"/>
/// and <see cref="FxDefs.Common"/>, or <see cref="FxDefs.GraphPart"/> / <see cref="FxDefs.KeyPart"/> for the window's own
/// controls) and its share of the row's width.
/// </summary>
public sealed record FxGroup(string Label, string[] Ids, double Weight);

/// <summary>
/// How an effect shows in its two sizes (asked by the user, 2026-11-05, #140): <see cref="Compact"/> lists the main knobs,
/// <see cref="Rows"/> the inner panels of the full view, row by row. Every knob of the effect is in some panel.
/// </summary>
public sealed record FxLayout(string[] Compact, FxGroup[][] Rows);

/// <summary>
/// The list of every native effect's parameters, one place for the window's panel (the same in the inspector, the
/// mixer, the timeline and the master), <see cref="ChannelDsp"/> and xtlab. Each kind's <see cref="Common"/> knobs (Mix,
/// output) come from here too. Also what the perillas configurables, MIDI learn and automation will read.
/// </summary>
public static class FxDefs
{
    const double DecayMin = 0.2, DecayMax = 10, ToneMin = 1000, ToneMax = 18000, LowCutMin = 20, LowCutMax = 1000, DelayMin = 20, DelayMax = 2000, PreMax = 200;
    const double OutputMin = -24, OutputMax = 12;

    public static readonly FxParam Dry = new("dry", "fx.p.dry", 0, 1, 0, FxUnit.Percent, 0.01, s => s.Dry, (s, v) => s.Dry = v) { Tip = "fx.p.dry.tip" };
    public static readonly FxParam Wet = new("wet", "fx.p.wet", 0, 1, 1, FxUnit.Percent, 0.01, s => s.Wet, (s, v) => s.Wet = v) { Tip = "fx.p.wet.tip" };
    public static readonly FxParam Output = new("out", "fx.p.out", OutputMin, OutputMax, 0, FxUnit.Db, 0.5, s => s.OutputDb, (s, v) => s.OutputDb = v) { Tip = "fx.p.out.tip" };

    /// <summary>A master effect's value, read from the slot's own list.</summary>
    static FxParam Stage(string id, string label, double min, double max, double fallback, FxUnit unit, double step, string? tip) =>
        new(id, label, min, max, fallback, unit, step, s => s.Stage?.Get(id, fallback) ?? fallback, (s, v) => { if (s.Stage != null) s.Stage.Values[id] = v; }) { Tip = tip };

    static readonly FxParam[] SatList =
    [
        new("amount", "insp.sat.life", 0, 1, 0.3, FxUnit.Percent, 0.01, s => s.Sat!.Amount, (s, v) => s.Sat!.Amount = v) { TurnsOn = true, Tip = "mix.sat.knob.tip" },
    ];

    static readonly FxParam[] GateList =
    [
        new("threshold", "insp.gate.threshold", GateParams.MinThresholdDb, GateParams.MaxThresholdDb, GateParams.DefaultThresholdDb, FxUnit.Level, 0.5,
            s => s.Gate!.Threshold, (s, v) => s.Gate!.Threshold = v) { TurnsOn = true, Tip = "mix.gate.knob.tip" },
    ];

    static readonly FxParam[] CompList =
    [
        new("amount", "insp.comp.amount", 0, 1, 0.5, FxUnit.Percent, 0.001, s => s.Comp!.Amount, (s, v) => s.Comp!.Amount = v) { TurnsOn = true, Tip = "mix.comp.knob.tip" },
        new("threshold", "insp.comp.threshold", -60, 0, -18, FxUnit.Level, 0.5, s => s.Comp!.Threshold, (s, v) => s.Comp!.Threshold = v) { Fine = true, Manual = true },
        new("ratio", "insp.comp.ratio", 1, 20, 4, FxUnit.Ratio, 0.1, s => s.Comp!.Ratio, (s, v) => s.Comp!.Ratio = v) { Fine = true, Manual = true, Log = true },
        new("attack", "insp.comp.attack", 0.1, 100, 10, FxUnit.Ms, 0.1, s => s.Comp!.Attack * 1000, (s, v) => s.Comp!.Attack = v / 1000) { Fine = true, Manual = true, Log = true },
        new("release", "insp.comp.release", 10, 1000, 150, FxUnit.Ms, 5, s => s.Comp!.Release * 1000, (s, v) => s.Comp!.Release = v / 1000) { Fine = true, Manual = true, Log = true },
        new("knee", "insp.comp.knee", 0, 24, 6, FxUnit.Level, 0.5, s => s.Comp!.Knee, (s, v) => s.Comp!.Knee = v) { Fine = true, Manual = true },
        new("makeup", "insp.comp.makeup", -12, 30, 0, FxUnit.Db, 0.5, s => s.Comp!.Makeup, (s, v) => s.Comp!.Makeup = v) { Fine = true, Manual = true, Tip = "insp.comp.makeup.hint" },
        new("hpf", "fx.p.hpf", 10, 300, 10, FxUnit.Hz, 5, s => s.Comp!.Eq.HpfHz, (s, v) => s.Comp!.Eq.HpfHz = v) { Fine = true, Manual = true, Log = true, OffBelow = 10, Tip = "insp.comp.hpf" },
        new("mud", "fx.p.mud", -6, 6, 0, FxUnit.Db, 0.5, s => s.Comp!.Eq.MudDb, (s, v) => s.Comp!.Eq.MudDb = v) { Fine = true, Manual = true, Tip = "insp.comp.mud" },
        new("presence", "fx.p.presence", -6, 6, 0, FxUnit.Db, 0.5, s => s.Comp!.Eq.PresenceDb, (s, v) => s.Comp!.Eq.PresenceDb = v) { Fine = true, Manual = true, Tip = "insp.comp.presence.hint" },
        new("air", "fx.p.air", -6, 6, 0, FxUnit.Db, 0.5, s => s.Comp!.Eq.AirDb, (s, v) => s.Comp!.Eq.AirDb = v) { Fine = true, Manual = true, Tip = "insp.comp.air" },
    ];

    static readonly FxParam[] ReverbList =
    [
        new("decay", "mix.knob.decay", DecayMin, DecayMax, 1.8, FxUnit.Sec, 0.05, s => s.Reverb!.Decay, (s, v) => s.Reverb!.Decay = v) { Log = true, Tip = "mix.hint.reverb.decay" },
        new("pre", "mix.knob.pre", 0, PreMax, 20, FxUnit.Ms, 1, s => s.Reverb!.PreDelayMs, (s, v) => s.Reverb!.PreDelayMs = v) { Tip = "mix.hint.reverb.pre" },
        new("tone", "mix.knob.tone", ToneMin, ToneMax, 6000, FxUnit.Hz, 1, s => s.Reverb!.Tone, (s, v) => s.Reverb!.Tone = v) { Log = true, Tip = "mix.hint.reverb.tone" },
        new("lowCut", "mix.knob.lowcut", LowCutMin, LowCutMax, 150, FxUnit.Hz, 1, s => s.Reverb!.LowCut, (s, v) => s.Reverb!.LowCut = v) { Log = true, Tip = "mix.hint.reverb.lowcut" },
        new("width", "mix.knob.width", 0, 1, 1, FxUnit.Percent, 0.01, s => s.Reverb!.Width, (s, v) => s.Reverb!.Width = v) { Tip = "mix.hint.reverb.width" },
    ];

    static readonly FxParam[] DelayList =
    [
        new("time", "mix.knob.time", DelayMin, DelayMax, 375, FxUnit.Ms, 1, s => s.Delay!.TimeMs, (s, v) => s.Delay!.TimeMs = v) { Log = true, Tip = "mix.hint.delay.time" },
        new("repeats", "mix.knob.repeats", 0, 0.9, 0.35, FxUnit.Percent, 0.01, s => s.Delay!.Feedback, (s, v) => s.Delay!.Feedback = v) { Tip = "mix.hint.delay.repeats" },
        new("tone", "mix.knob.tone", ToneMin, ToneMax, 5000, FxUnit.Hz, 1, s => s.Delay!.Tone, (s, v) => s.Delay!.Tone = v) { Log = true, Tip = "mix.hint.delay.tone" },
        new("lowCut", "mix.knob.lowcut", LowCutMin, LowCutMax, 250, FxUnit.Hz, 1, s => s.Delay!.LowCut, (s, v) => s.Delay!.LowCut = v) { Log = true, Tip = "mix.hint.delay.lowcut" },
        new("width", "mix.knob.width", 0, 1, 1, FxUnit.Percent, 0.01, s => s.Delay!.Width, (s, v) => s.Delay!.Width = v) { Tip = "mix.hint.delay.width" },
        new("duck", "mix.knob.duck", 0, 1, 0.35, FxUnit.Percent, 0.01, s => s.Delay!.Duck, (s, v) => s.Delay!.Duck = v) { Tip = "mix.hint.delay.duck" },
    ];

    static readonly FxParam[] CleanList =
    [
        Stage("lowCut", "master.eq.lowcut", MasterEq.LowCutMin, MasterEq.LowCutMax, 0, FxUnit.Hz, 1, "master.eq.lowcut.tip") with { Log = true, OffAtZero = true, OffBelow = 0 },
        Stage("mud", "master.eq.mud", -MasterEq.GainMax, MasterEq.GainMax, 0, FxUnit.Db, 0.5, "master.eq.mud.tip"),
    ];

    static readonly FxParam[] BandsList =
    [
        Stage("low", "master.band.low", 0, 1, MasterBands.DefaultAmount, FxUnit.Percent, 0.01, "master.band.low.tip") with { OffBelow = 0 },
        Stage("mid", "master.band.mid", 0, 1, MasterBands.DefaultAmount, FxUnit.Percent, 0.01, "master.band.mid.tip") with { OffBelow = 0 },
        Stage("high", "master.band.high", 0, 1, MasterBands.DefaultAmount, FxUnit.Percent, 0.01, "master.band.high.tip") with { OffBelow = 0 },
        Stage("pick", "master.band.pick", 0, 1, 0, FxUnit.Percent, 0.01, "master.band.pick.tip") with { OffBelow = 0 },
        Stage("lowX", "fx.p.lowX", MasterBands.LowXMin, MasterBands.LowXMax, MasterBands.DefaultLowXHz, FxUnit.Hz, 1, "fx.p.cross.tip") with { Log = true },
        Stage("highX", "fx.p.highX", MasterBands.HighXMin, MasterBands.HighXMax, MasterBands.DefaultHighXHz, FxUnit.Hz, 100, "fx.p.cross.tip") with { Log = true },
    ];

    static readonly FxParam[] ColorList =
    [
        Stage("lows", "master.eq.lows", -MasterEq.GainMax, MasterEq.GainMax, 0, FxUnit.Db, 0.5, "master.eq.lows.tip"),
        Stage("presence", "master.eq.presence", -MasterEq.GainMax, MasterEq.GainMax, 0, FxUnit.Db, 0.5, "master.eq.presence.tip"),
        Stage("air", "master.eq.air", -MasterEq.GainMax, MasterEq.GainMax, 0, FxUnit.Db, 0.5, "master.eq.air.tip"),
        Stage("tilt", "master.eq.tilt", -MasterEq.TiltMax, MasterEq.TiltMax, 0, FxUnit.Db, 0.5, "master.eq.tilt.tip"),
    ];

    static readonly FxParam[] TapeList =
    [
        Stage("amount", "master.tape.amount", 0, 1, 0.3, FxUnit.Percent, 0.01, "master.tape.amount.tip") with { OffBelow = 0 },
    ];

    static readonly FxParam[] WidthList =
    [
        Stage("width", "master.width", 0, StereoWidth.MaxWidth, 1, FxUnit.Percent, 0.05, "master.width.tip"),
        Stage("monoBass", "master.monobass", 40, StereoWidth.MonoBassMaxHz, 0, FxUnit.Hz, 1, "master.monobass.tip") with { Log = true, OffAtZero = true, OffBelow = 0 },
    ];

    static readonly FxParam[] ClipList =
    [
        Stage("recorte", "master.clip.amount", 0, SoftClipper.MaxDb, 0, FxUnit.Recorte, 0.1, "master.clip.amount.tip"),
        Stage("ceiling", "master.lim.ceiling", MasterLimiter.CeilingMinDb, 0, MasterLimiter.CeilingDb, FxUnit.Ceiling, 0.1, "master.lim.ceiling.tip"),
    ];

    static readonly FxParam[] LimitList =
    [
        Stage("drive", "master.lim.drive", 0, MasterParams.DriveMaxDb, 0, FxUnit.Db, 0.5, "fx.p.drive.tip"),
        Stage("ceiling", "master.lim.ceiling", MasterLimiter.CeilingMinDb, 0, MasterLimiter.CeilingDb, FxUnit.Ceiling, 0.1, "master.lim.ceiling.tip"),
        Stage("release", "master.lim.release", MasterLimiter.ReleaseMinMs, MasterLimiter.ReleaseMaxMs, MasterLimiter.ReleaseMs, FxUnit.Ms, 1, "master.lim.release.tip") with { Log = true },
    ];

    static FxParam ModRate(double start) => Stage("rate", "fx.p.rate", ModParams.RateMin, ModParams.RateMax, start, FxUnit.Rate, 0.01, "fx.p.rate.tip") with { Log = true };
    static FxParam ModDepth(double start) => Stage("depth", "fx.p.depth", 0, 1, start, FxUnit.Percent, 0.01, "fx.p.depth.tip");
    static FxParam ModSpread(double start) => Stage("spread", "fx.p.spread", 0, 1, start, FxUnit.Percent, 0.01, "fx.p.spread.tip");
    static FxParam ModFeedback(double start) => Stage("feedback", "fx.p.feedback", -0.95, 0.95, start, FxUnit.Percent, 0.01, "fx.p.feedback.tip");

    static readonly FxParam[] ChorusList =
    [
        ModRate(0.8), ModDepth(0.35),
        Stage("delay", "fx.p.delay", 5, 30, 15, FxUnit.Ms, 0.5, "fx.p.delay.tip"),
        ModSpread(0.5),
    ];

    static readonly FxParam[] FlangerList =
    [
        ModRate(0.3), ModDepth(0.6),
        Stage("delay", "fx.p.delay", 0.2, 10, 1, FxUnit.Ms, 0.1, "fx.p.delay.tip") with { Log = true },
        ModFeedback(0.5), ModSpread(0.25),
    ];

    static readonly FxParam[] PhaserList =
    [
        ModRate(0.5), ModDepth(0.7), ModFeedback(0.4),
        Stage("stages", "fx.p.stages", 2, PhaserInsert.MaxStages, 6, FxUnit.Count, 2, "fx.p.stages.tip"),
        ModSpread(0.2),
    ];

    static readonly FxParam[] TremoloList =
    [
        ModRate(5), ModDepth(0.5),
        Stage("shape", "fx.p.shape", 0, 1, 0, FxUnit.Percent, 0.01, "fx.p.shape.tip"),
        ModSpread(0),
    ];

    static readonly FxParam[] PumpList =
    [
        Stage("depth", "fx.p.depth", 0, 1, PumpParams.Defaults.Depth, FxUnit.Percent, 0.01, "fx.pump.depth.tip"),
        Stage("division", "fx.pump.division", 0, PumpParams.Beats.Length - 1, PumpParams.Defaults.Division, FxUnit.Division, 1, "fx.pump.division.tip"),
        Stage("length", "fx.pump.length", PumpParams.LengthMin, 1, PumpParams.Defaults.Length, FxUnit.Percent, 0.01, "fx.pump.length.tip"),
        Stage("curve", "fx.pump.curve", -1, 1, PumpParams.Defaults.Curve, FxUnit.Signed, 0.01, "fx.pump.curve.tip"),
        Stage("attack", "fx.pump.attack", 0, PumpParams.AttackMaxMs, PumpParams.Defaults.AttackMs, FxUnit.Ms, 0.1, "fx.pump.attack.tip") with { Fine = true },
        Stage("offset", "fx.pump.offset", -0.5, 0.5, 0, FxUnit.Signed, 0.01, "fx.pump.offset.tip") with { Fine = true },
        Stage("threshold", "insp.comp.threshold", PumpParams.ThresholdAutoDb, 0, PumpParams.ThresholdAutoDb, FxUnit.Level, 0.5, "fx.pump.threshold.tip")
            with { OffBelow = PumpParams.ThresholdAutoDb, OffText = "fx.auto" },
        Stage("hold", "fx.pump.hold", 0, PumpParams.HoldMaxMs, 0, FxUnit.Ms, 1, "fx.pump.hold.tip"),
        Stage("keyMs", "insp.comp.release", PumpParams.KeyMinMs, PumpParams.KeyMaxMs, 250, FxUnit.Ms, 1, "fx.pump.keyMs.tip") with { Log = true },
    ];

    /// <summary>The pump as the engine reads it.</summary>
    public static PumpParams ToPump(InsertSlot s)
    {
        double V(string id) => PumpList.First(p => p.Id == id).Get(s);
        return new((int)Math.Round(V("division")), V("depth"), V("length"), V("curve"), V("attack"), V("offset"))
            { KeyMs = V("keyMs"), ThresholdDb = V("threshold"), HoldMs = V("hold") };
    }

    static FxParam Makeup(double max) => Stage("makeup", "insp.comp.makeup", 0, max, 0, FxUnit.Db, 0.5, "fx.model.makeup.tip");
    static FxParam Stepped(string id, string label, double[] steps, double start, FxUnit unit, string tip, string?[]? names = null) =>
        Stage(id, label, steps.Min(), steps.Max(), start, unit, 0, tip) with { Steps = steps, StepNames = names };

    static readonly FxParam[] FetList =
    [
        Stage("input", "fx.p.input", 0, 48, 6, FxUnit.Db, 0.5, "fx.fet.input.tip"),
        Stepped("ratio", "insp.comp.ratio", [4, 8, 12, 20, CompModelParams.AllButtons], 4, FxUnit.Ratio, "fx.fet.ratio.tip", [null, null, null, null, "fx.fet.all"]),
        Stage("attack", "insp.comp.attack", 0.02, 0.8, 0.2, FxUnit.Ms, 0.01, "fx.fet.attack.tip") with { Log = true },
        Stage("release", "insp.comp.release", 50, 1100, 400, FxUnit.Ms, 5, "fx.fet.release.tip") with { Log = true },
        Makeup(24),
    ];

    static readonly FxParam[] OptoList =
    [
        Stage("reduction", "fx.p.reduction", 0, 1, 0.4, FxUnit.Percent, 0.01, "fx.opto.reduction.tip"),
        Makeup(40),
        Stepped("mode", "fx.p.mode", [0, 1], 0, FxUnit.Count, "fx.opto.mode.tip", ["fx.opto.compress", "fx.opto.limit"]),
    ];

    static readonly FxParam[] BusCompList =
    [
        Stage("threshold", "insp.comp.threshold", -40, 0, -14, FxUnit.Level, 0.5, "fx.model.threshold.tip"),
        Stepped("ratio", "insp.comp.ratio", [2, 4, 10], 4, FxUnit.Ratio, "fx.bus.ratio.tip"),
        Stepped("attack", "insp.comp.attack", [0.1, 0.3, 1, 3, 10, 30], 30, FxUnit.Ms, "fx.bus.attack.tip"),
        Stepped("release", "insp.comp.release", [100, 300, 600, 1200, CompModelParams.AutoRelease], CompModelParams.AutoRelease, FxUnit.Ms, "fx.bus.release.tip",
            [null, null, null, null, "fx.auto"]),
        Makeup(20),
        Stage("scHpf", "fx.p.schpf", 20, 300, 20, FxUnit.Hz, 1, "fx.p.schpf.tip") with { Log = true, OffBelow = 20 },
    ];

    static readonly FxParam[] VcaList =
    [
        Stage("threshold", "insp.comp.threshold", -50, 0, -20, FxUnit.Level, 0.5, "fx.model.threshold.tip"),
        Stepped("ratio", "insp.comp.ratio", [1.5, 2, 3, 4, 6, 10, 20, CompModelParams.Infinite], 4, FxUnit.Ratio, "fx.vca.ratio.tip",
            [null, null, null, null, null, null, null, "fx.ratio.inf"]),
        Stepped("knee", "insp.comp.knee", [0, 1], 0, FxUnit.Count, "fx.vca.knee.tip", ["fx.knee.hard", "fx.knee.soft"]),
        Makeup(24),
    ];

    static readonly FxParam[] VariMuList =
    [
        Stage("input", "fx.p.input", 0, 24, 6, FxUnit.Db, 0.5, "fx.varimu.input.tip"),
        Stage("threshold", "insp.comp.threshold", -40, 0, -18, FxUnit.Level, 0.5, "fx.model.threshold.tip"),
        Stepped("time", "fx.p.time", [1, 2, 3, 4, 5, 6], 2, FxUnit.Count, "fx.varimu.time.tip",
            ["fx.varimu.t1", "fx.varimu.t2", "fx.varimu.t3", "fx.varimu.t4", "fx.varimu.t5", "fx.varimu.t6"]),
        Makeup(24),
    ];

    /// <summary>A compressor model as the engine reads it: the Óptico's Reducción becomes its threshold, and each model's switch its mode.</summary>
    public static CompModelParams ToCompModel(InsertSlot s)
    {
        var ps = Params(s.Type);
        double V(string id, double fallback) => Array.Find(ps, p => p.Id == id)?.Get(s) ?? fallback;
        var d = CompModelParams.Defaults;
        double threshold = s.Type == TrackInserts.Opto ? OptoCompInsert.ThresholdFor(V("reduction", 0.4)) : V("threshold", d.ThresholdDb);
        double mode = s.Type switch { TrackInserts.VariMu => V("time", 2), TrackInserts.Vca => V("knee", 0), _ => V("mode", 0) };
        return new(V("input", d.InputDb), threshold, V("ratio", d.Ratio), V("attack", d.AttackMs), V("release", d.ReleaseMs), V("makeup", 0), V("scHpf", 0), (int)Math.Round(mode));
    }

    /// <summary>A modulation effect as the engine reads it.</summary>
    public static ModParams ToMod(InsertSlot s)
    {
        double V(string id, double fallback) => Array.Find(Params(s.Type), p => p.Id == id)?.Get(s) ?? fallback;
        var d = ModParams.Defaults;
        return new(V("rate", d.RateHz), V("depth", d.Depth), V("feedback", d.Feedback), V("delay", d.DelayMs), V("spread", d.Spread), (int)V("stages", d.Stages), V("shape", d.Shape));
    }

    /// <summary>
    /// The Mix a new slot starts at: all of the effect, but a chorus, a flanger and a phaser only sound against the dry signal.
    /// </summary>
    public static (double Wet, double Dry) StartLevels(string kind) => kind is TrackInserts.Chorus or TrackInserts.Flanger or TrackInserts.Phaser ? (0.5, 0.5) : (1, 0);

    /// <summary>The effect's own knobs (an EQ's bands and a VST3's parameters have their own editors).</summary>
    public static FxParam[] Params(string kind) => kind switch
    {
        InsertSlot.SatKind => SatList,
        InsertSlot.GateKind => GateList,
        InsertSlot.CompKind => CompList,
        InsertSlot.ReverbKind => ReverbList,
        InsertSlot.DelayKind => DelayList,
        TrackInserts.Clean => CleanList,
        TrackInserts.Bands => BandsList,
        TrackInserts.Color => ColorList,
        TrackInserts.Tape => TapeList,
        TrackInserts.Width => WidthList,
        TrackInserts.Clip => ClipList,
        TrackInserts.Limit => LimitList,
        TrackInserts.Chorus => ChorusList,
        TrackInserts.Flanger => FlangerList,
        TrackInserts.Phaser => PhaserList,
        TrackInserts.Tremolo => TremoloList,
        TrackInserts.Fet => FetList,
        TrackInserts.Opto => OptoList,
        TrackInserts.BusComp => BusCompList,
        TrackInserts.Vca => VcaList,
        TrackInserts.VariMu => VariMuList,
        TrackInserts.Pump => PumpList,
        _ => [],
    };

    /// <summary>Parts of a full view that aren't knobs: a compressor's graph and what an effect listens to (its sidechain).</summary>
    public const string GraphPart = "@graph", KeyPart = "@key";

    static readonly FxLayout CompLayout = new(["amount", "makeup", "wet"],
    [
        [new("fx.panel.curve", [GraphPart], 4), new("fx.panel.dynamics", ["amount", "threshold", "ratio", "knee"], 4)],
        [new("fx.panel.times", ["attack", "release"], 2), new("fx.panel.output", ["makeup", "dry", "wet", "out"], 4)],
        [new("fx.panel.eq", ["hpf", "mud", "presence", "air"], 4), new("fx.panel.key", [KeyPart], 2)],
    ]);

    /// <summary>
    /// The effect's compact and full views, or null for one that has a single view (it shows as it always did). The effects
    /// get theirs one by one (asked by the user).
    /// </summary>
    public static FxLayout? Layout(string kind) => kind switch
    {
        InsertSlot.CompKind => CompLayout,
        _ => null,
    };

    /// <summary>The effect's power switch, for a lane: <see cref="InsertSlot.On"/> as a parameter.</summary>
    public static readonly FxParam Power = new("on", "auto.fx.on", 0, 1, 1, FxUnit.Percent, 1, s => s.On ? 1 : 0, (s, v) => s.On = v >= 0.5) { Toggle = true };

    /// <summary>The EQ's bands, each with its frequency, gain and Q: its editor has its own controls, a lane needs them as knobs.</summary>
    static readonly FxParam[] EqBandList = [.. Enumerable.Range(0, TrackEq.BandCount).SelectMany(EqBand)];

    static IEnumerable<FxParam> EqBand(int b)
    {
        EqBand? Of(InsertSlot s) => s.Eq is { } e && b < e.Bands.Count ? e.Bands[b] : null;
        void Put(InsertSlot s, Action<EqBand> set) { if (Of(s) is { } x) set(x); }
        yield return new($"b{b}.freq", "auto.eq.freq", 20, 20000, 1000, FxUnit.Hz, 1, s => Of(s)?.Freq ?? 1000, (s, v) => Put(s, x => x.Freq = v)) { Log = true };
        yield return new($"b{b}.gain", "auto.eq.gain", -24, 24, 0, FxUnit.Db, 0.1, s => Of(s)?.Gain ?? 0, (s, v) => Put(s, x => x.Gain = v));
        yield return new($"b{b}.q", "auto.eq.q", 0.1, 10, 1, FxUnit.Ratio, 0.01, s => Of(s)?.Q ?? 1, (s, v) => Put(s, x => x.Q = v)) { Log = true };
    }

    /// <summary>Everything of an effect a lane can drive: its switch, its knobs, what every effect adds and an EQ's bands.</summary>
    public static IEnumerable<FxParam> Automatable(string kind) =>
        kind == InsertSlot.VstKind ? [] : [Power, .. Params(kind), .. Common(kind), .. kind == InsertSlot.EqKind ? EqBandList : []];

    /// <summary>What every native effect adds: Dry, Wet and output (asked by the user). A VST3 has its own.</summary>
    public static FxParam[] Common(string kind) => kind == InsertSlot.VstKind ? [] : [Dry, Wet, Output];

    /// <summary>A master effect as the engine reads it: the master's own settings, with this slot's values.</summary>
    public static MasterParams ToMaster(InsertSlot s)
    {
        double V(string id) => Params(s.Type).First(p => p.Id == id).Get(s);
        return s.Type switch
        {
            TrackInserts.Clean => MasterParams.Off with { LowCutHz = V("lowCut"), MudDb = V("mud") },
            TrackInserts.Bands => MasterParams.Off with { Low = new(V("low")), Mid = new(V("mid")), High = new(V("high")), PickAmount = V("pick"), LowXHz = V("lowX"), HighXHz = V("highX") },
            TrackInserts.Color => MasterParams.Off with { LowsDb = V("lows"), PresenceDb = V("presence"), AirDb = V("air"), TiltDb = V("tilt") },
            TrackInserts.Tape => MasterParams.Off with { TapeAmount = V("amount") },
            TrackInserts.Width => MasterParams.Off with { Width = V("width"), MonoBassHz = V("monoBass") },
            TrackInserts.Clip => MasterParams.Off with { ClipDb = V("recorte"), LimCeilingDb = V("ceiling") },
            TrackInserts.Limit => MasterParams.Off with { LimDriveDb = V("drive"), LimCeilingDb = V("ceiling"), LimReleaseMs = V("release") },
            _ => MasterParams.Off,
        };
    }

    /// <summary>
    /// A look or a suggestion reaches the chain: the first slot of each stage takes its values, wherever it is in the
    /// list, and a stage that isn't there comes in at the end. The crossovers are not a look's.
    /// </summary>
    public static void SyncStages(MasterSettings m)
    {
        foreach (string kind in TrackInserts.MasterOrder)
        {
            var from = FromMaster(kind, m);
            if (m.Slots.Find(s => s.Type == kind) is not { Stage: { } stage })
            {
                from.On = true;
                m.Slots.Add(from);
                continue;
            }
            foreach (var (id, v) in from.Stage!.Values)
                if (id is not ("lowX" or "highX")) stage.Values[id] = v;
        }
    }

    /// <summary>A master stage's slot with the master's values, and that stage's ON.</summary>
    public static InsertSlot FromMaster(string kind, MasterSettings m)
    {
        var s = InsertSlot.New(kind);
        void V(string id, double v) => s.Stage!.Values[id] = v;
        switch (kind)
        {
            case TrackInserts.Clean: V("lowCut", m.LowCutHz); V("mud", m.MudDb); s.On = m.EqOn; break;
            case TrackInserts.Bands:
                V("low", m.Bands[0].Amount); V("mid", m.Bands[1].Amount); V("high", m.Bands[2].Amount);
                V("pick", m.PickAmount); V("lowX", m.LowXHz); V("highX", m.HighXHz); s.On = m.BandsOn;
                break;
            case TrackInserts.Color: V("lows", m.LowsDb); V("presence", m.PresenceDb); V("air", m.AirDb); V("tilt", m.TiltDb); s.On = m.ColorOn; break;
            case TrackInserts.Tape: V("amount", m.TapeAmount); s.On = m.TapeOn; break;
            case TrackInserts.Width: V("width", m.Width); V("monoBass", m.MonoBassHz); s.On = m.WidthOn; break;
            case TrackInserts.Clip: V("recorte", m.ClipDb); V("ceiling", m.LimCeilingDb); s.On = m.ClipOn; break;
        }
        return s;
    }
}
