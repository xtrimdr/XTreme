using XtremeDaw.Audio.Vst;

namespace XtremeDaw.Audio.Dsp;

/// <summary>
/// One effect in a track's insert slots (<see cref="TrackDspChain"/>). The chain only knows this shape, so a built-in
/// effect and a VST3 plugin (<see cref="VstInsert"/>) are both just a kind of slot. Runs on the audio thread: <see cref="Process"/> never
/// allocates, locks or throws. Its settings live in the project (<c>InsertSlot</c>); <see cref="TrackInserts.Apply"/>
/// hands them over from the UI thread as whole immutable values, like every other effect.
/// </summary>
public interface ITrackInsert
{
    /// <summary>The slot kind (<see cref="TrackInserts.Sat"/>…), as saved in the project.</summary>
    string Kind { get; }
    /// <summary>How late its output comes out (frames). Zero for the built-in effects but the limiter; it is compensated
    /// in the rest of the mix (<see cref="MixLatency"/>). UI thread.</summary>
    int LatencySamples { get; }
    void Process(Span<float> left, Span<float> right);
    void Reset();
    /// <summary>Its gain reduction since the last call (dB, &lt;= 0), for the strip's meter; 0 for effects that don't reduce.</summary>
    double TakeGrDb();
}

/// <summary>The saturator as a slot.</summary>
public sealed class SatInsert(int sampleRate) : ITrackInsert
{
    public Saturator Sat { get; } = new(sampleRate);
    public string Kind => TrackInserts.Sat;
    public int LatencySamples => 0;

    /// <summary>The web ran its parameters as 32-bit floats: rounded the same way, the native sounds the same.</summary>
    public void Set(SatParams p) => Sat.Set(p with
    {
        Drive = (float)p.Drive,
        Bias = (float)p.Bias,
        Tone = (float)p.Tone,
        ToneMix = (float)p.ToneMix,
        Bump = (float)p.Bump,
    });

    public void Process(Span<float> left, Span<float> right) => Sat.Process(left, right);
    public void Reset() => Sat.Reset();
    public double TakeGrDb() => 0;
}

/// <summary>The compressor with the small EQ its presets bring (before and after it) as one slot; it can listen to another track (<see cref="Key"/>).</summary>
public sealed class CompInsert(int sampleRate) : ITrackInsert, IKeyed, ICompGraph
{
    public KeyTap Key { get; } = new();
    public CompEqFilter Eq { get; } = new(sampleRate);
    public Compressor Comp { get; } = new(sampleRate) { Scope = new() };
    public CompScope Scope => Comp.Scope!;

    public double CurveOutDb(double inDb) => Comp.CurveOutDb(inDb);
    public string Kind => TrackInserts.Comp;
    public int LatencySamples => 0;

    static double F(double v) => (float)v;

    public void Set(CompState c)
    {
        Eq.Set(c.On, c.Eq);
        Comp.Bypass = !c.On;
        Comp.Threshold = F(c.Threshold);
        Comp.Ratio = F(c.Ratio);
        Comp.Knee = F(c.Knee);
        Comp.Attack = F(c.Attack);
        Comp.Release = F(c.Release);
        Comp.Makeup = F(c.Makeup);
        Comp.Limit = c.Limit;
        Comp.Ceiling = F(CompPresets.TrackLimitCeilingDb);
    }

    public void Process(Span<float> left, Span<float> right)
    {
        if (Key.Take(left.Length, out var kl, out var kr)) Eq.ProcessWith(Comp, left, right, kl, kr);
        else Eq.ProcessWith(Comp, left, right);
    }

    public void Reset()
    {
        Eq.Reset();
        Comp.Reset();
    }

    public double TakeGrDb() => Comp.TakeMeter();
}

/// <summary>The 6-band track EQ as a slot.</summary>
public sealed class EqInsert(int sampleRate) : ITrackInsert
{
    public TrackEq Eq { get; } = new(sampleRate);
    public string Kind => TrackInserts.Eq;
    public int LatencySamples => 0;

    public void Set(bool on, ReadOnlySpan<EqBandSpec> bands) => Eq.Set(on, bands);
    public void Process(Span<float> left, Span<float> right) => Eq.Process(left, right);
    public void Reset() => Eq.Reset();
    public double TakeGrDb() => 0;
}

/// <summary>The noise gate as a slot: its reduction feeds the strip's meter like the compressor's. It can open by another track (<see cref="Key"/>).</summary>
public sealed class GateInsert(int sampleRate) : ITrackInsert, IKeyed
{
    public KeyTap Key { get; } = new();
    public NoiseGate Gate { get; } = new(sampleRate);
    public string Kind => TrackInserts.Gate;
    public int LatencySamples => 0;

    public void Set(GateParams p) => Gate.Set(p);
    public void Process(Span<float> left, Span<float> right)
    {
        if (Key.Take(left.Length, out var kl, out var kr)) Gate.Process(left, right, kl, kr);
        else Gate.Process(left, right);
    }
    public void Reset() => Gate.Reset();
    public double TakeGrDb() => Gate.TakeGrDb();
}

/// <summary>
/// The reverb as a slot, for a return's chain (<see cref="ReturnDspChain"/>): the same <see cref="ReverbBus"/> as the fixed
/// Reverb return, so a return with only this slot sounds exactly like it. Off (<see cref="ReverbParams.Muted"/>) it is
/// bypassed, like every other slot that is off. The first <see cref="Set"/> builds its impulse at once (the export, a new
/// slot); later ones build it in the background, like the fixed return's knobs.
/// </summary>
public sealed class ReverbInsert(int sampleRate) : ITrackInsert
{
    public ReverbBus Bus { get; } = new(sampleRate);
    public string Kind => TrackInserts.Reverb;
    public int LatencySamples => 0;
    bool set;
    volatile bool bypass;

    /// <param name="wait">The export: the new impulse is ready before the next block, so every run sounds the same.</param>
    public void Set(ReverbParams p, bool wait = false)
    {
        bypass = p.Muted;
        var on = p with { Muted = false };
        if (set && !wait) Bus.SetLive(on);
        else Bus.Set(on);
        set = true;
    }

    public void Process(Span<float> left, Span<float> right)
    {
        if (!bypass) Bus.Process(left, right);
    }

    public void Reset() => Bus.Reset();
    public double TakeGrDb() => 0;
}

/// <summary>
/// The delay as a slot, for a return's chain: the same <see cref="DelayBus"/> as the fixed Delay return, following the
/// project's tempo (<see cref="InsertSpec.Bpm"/>). The first <see cref="Set"/> snaps to its settings, later ones glide,
/// like the fixed return. Off it is bypassed.
/// </summary>
public sealed class DelayInsert(int sampleRate) : ITrackInsert
{
    public DelayBus Bus { get; } = new(sampleRate);
    public string Kind => TrackInserts.Delay;
    public int LatencySamples => 0;
    bool set;
    volatile bool bypass;

    public void Set(DelayParams p, double bpm)
    {
        bypass = p.Muted;
        Bus.Set(p with { Muted = false }, bpm, glide: set);
        set = true;
    }

    public void Process(Span<float> left, Span<float> right)
    {
        if (!bypass) Bus.Process(left, right);
    }

    public void Reset() => Bus.Reset();
    public double TakeGrDb() => 0;
}

/// <summary>
/// One slot's settings as the engine takes them (the export builds a chain from these; the live engine updates its
/// inserts with them). <see cref="Id"/> ties it to its insert, so an edit or a reorder keeps the effect's state.
/// </summary>
public sealed record InsertSpec(string Id, string Kind)
{
    public SatParams Sat { get; init; } = SatParams.From(false, SatMode.Tube, 0.3);
    public CompState Comp { get; init; } = CompState.Defaults;
    public bool EqOn { get; init; }
    public EqBandSpec[] EqBands { get; init; } = TrackEq.DefaultBands;
    /// <summary>A reverb slot's settings; <see cref="ReverbParams.Muted"/> = the slot is off (bypassed).</summary>
    public ReverbParams Reverb { get; init; } = ReverbParams.Defaults;
    /// <summary>A delay slot's settings (<see cref="DelayParams.Muted"/> = off) and the tempo its note values follow.</summary>
    public DelayParams Delay { get; init; } = DelayParams.Defaults;
    public double Bpm { get; init; } = 120;
    /// <summary>A gate slot's settings (<see cref="GateParams.On"/> = the slot is on).</summary>
    public GateParams Gate { get; init; } = GateParams.Defaults;
    /// <summary>A VST3 effect slot's plugin, whether it is on and its process (<see cref="VstSlotSpec"/>).</summary>
    public VstSlotSpec? Vst { get; init; }
    /// <summary>A master effect's values (<see cref="TrackInserts.IsStage"/>), in the master's own shape, and whether it is on.</summary>
    public MasterParams Stage { get; init; } = MasterParams.Off;
    public bool StageOn { get; init; }
    /// <summary>A modulation effect's values (<see cref="TrackInserts.IsMod"/>); its ON is <see cref="StageOn"/>.</summary>
    public ModParams Mod { get; init; } = ModParams.Defaults;
    /// <summary>A compressor model's values (<see cref="TrackInserts.IsModel"/>); its ON is <see cref="StageOn"/>.</summary>
    public CompModelParams Model { get; init; } = CompModelParams.Defaults;
    /// <summary>A pump's values (<see cref="TrackInserts.Pump"/>), locked to <see cref="Bpm"/>; its ON is <see cref="StageOn"/>.</summary>
    public PumpParams Pump { get; init; } = PumpParams.Defaults;
    /// <summary>The effect's level, the dry signal's (each 0 to 1) and the output (dB): every native effect's (<see cref="FxShell"/>).</summary>
    public double Wet { get; init; } = 1;
    public double Dry { get; init; }
    public double OutputDb { get; init; }
    /// <summary>The track a slot listens to instead of its own input (a sidechain, <see cref="IKeyed"/>); null = its own.</summary>
    public string? Key { get; init; }
    /// <summary>An export's change on the way: what would be built in the background is built before going on.</summary>
    public bool Wait { get; init; }

    /// <summary>Whether the slot sounds, by its kind's own switch.</summary>
    public bool On => Kind switch
    {
        TrackInserts.Sat => !Sat.Bypass,
        TrackInserts.Comp => Comp.On,
        TrackInserts.Eq => EqOn,
        TrackInserts.Reverb => !Reverb.Muted,
        TrackInserts.Delay => !Delay.Muted,
        TrackInserts.Gate => Gate.On,
        _ => StageOn,
    };

    /// <summary>The same settings with its switch on: <see cref="FxShell"/> does the turning on and off.</summary>
    public InsertSpec Running => this with
    {
        Sat = Sat with { Bypass = false },
        Comp = Comp with { On = true },
        EqOn = true,
        Reverb = Reverb with { Muted = false },
        Delay = Delay with { Muted = false },
        Gate = Gate with { On = true },
        StageOn = true,
    };
}

/// <summary>
/// The kinds of insert slot and how a list of <see cref="InsertSpec"/> becomes the chain's inserts. A new channel gets
/// <see cref="DefaultOrder"/>, the order the chain was fixed in before slots existed, so an old project sounds the same.
/// Every native kind goes in any chain, in a <see cref="FxShell"/>, and the master's stages are kinds too
/// (<see cref="IsStage"/>). The limiter and a VST3 go without the shell: each has a latency of its own that a dry
/// signal would comb with, and its own way of turning off (the limiter delays its Dry to match); the tonal balance too,
/// as it never changes the sound.
/// </summary>
public static class TrackInserts
{
    public const string Sat = "sat";
    public const string Comp = "comp";
    public const string Eq = "eq";
    public const string Reverb = "reverb";
    public const string Delay = "delay";
    public const string Gate = "gate";
    /// <summary>A VST3 effect (<see cref="VstInsert"/>).</summary>
    public const string Vst = "vst";
    /// <summary>The master's stages as slots (<see cref="IsStage"/>): the same DSP as <see cref="MasterChain"/>.</summary>
    public const string Clean = "clean", Bands = "bands", Color = "color", Tape = "tape", Width = "width", Clip = "softclip", Limit = "limiter";
    public static readonly string[] DefaultOrder = [Sat, Comp, Eq];
    /// <summary>
    /// The master's stages in the order they used to sound: what "Cadena completa" adds. The soft clip and the limiter stay
    /// in Salida, after the Empuje or the Destino's gain, which they have to come after (they are slots too, for anywhere else).
    /// </summary>
    public static readonly string[] MasterOrder = [Clean, Bands, Color, Tape, Width];

    public static bool IsStage(string? kind) => kind is Clean or Bands or Color or Tape or Width or Clip or Limit;

    /// <summary>The modulation effects, kept like the stages: an ON and a list of values.</summary>
    public const string Chorus = "chorus", Flanger = "flanger", Phaser = "phaser", Tremolo = "tremolo";
    /// <summary>The pump (<see cref="PumpInsert"/>): a level curve on the beat, kept like the modulation effects.</summary>
    public const string Pump = "pump";

    public static bool IsMod(string? kind) => kind is Chorus or Flanger or Phaser or Tremolo or Pump;

    /// <summary>The compressor models (<see cref="CompModelParams"/>), kept like the stages: an ON and a list of values.</summary>
    public const string Fet = "fet", Opto = "opto", BusComp = "buscomp", Vca = "vca", VariMu = "varimu";

    public static bool IsModel(string? kind) => kind is Fet or Opto or BusComp or Vca or VariMu;

    /// <summary>
    /// The tonal balance (<see cref="TonalInsert"/>, #210): an analyzer kept like the stages (an ON and a list of values),
    /// that never touches the sound. It goes without the shell: nothing to mix, and not even its switch may fade.
    /// </summary>
    public const string Tonal = "tonal";

    /// <summary>The kinds that only look at the sound (no Dry, Wet, output or lanes).</summary>
    public static bool IsMeter(string? kind) => kind is Tonal;

    /// <summary>The kinds that can listen to another track (a sidechain, <see cref="IKeyed"/>).</summary>
    /// <summary>Whether the kind draws a compressor's graph (<see cref="ICompGraph"/>).</summary>
    public static bool HasCompGraph(string? kind) => kind == Comp || IsModel(kind);

    /// <summary>
    /// A compressor with <paramref name="spec"/>'s settings outside the engine, for its graph's curve while the slot isn't
    /// running: <paramref name="reuse"/> takes them when it is of the same kind, so an edit makes nothing new. UI thread.
    /// </summary>
    public static ICompGraph? CurveOf(InsertSpec spec, ICompGraph? reuse, int sampleRate)
    {
        if (!HasCompGraph(spec.Kind)) return null;
        var insert = reuse is ITrackInsert r && r.Kind == spec.Kind ? r : Native(spec.Kind, sampleRate);
        ApplyNative(insert, spec.Running);
        return insert as ICompGraph;
    }

    public static bool TakesKey(string? kind) => kind is Comp or Gate or Pump || IsModel(kind);

    public static bool IsKind(string? kind) => kind is Sat or Comp or Eq or Reverb or Delay or Gate or Vst || IsStage(kind) || IsMod(kind) || IsModel(kind) || IsMeter(kind);

    public static ITrackInsert Create(string kind, int sampleRate) => kind switch
    {
        Vst => new VstInsert(sampleRate),
        Limit => new LimitInsert(sampleRate),
        Tonal => new TonalInsert(sampleRate),
        _ => new FxShell(Native(kind, sampleRate), sampleRate),
    };

    static ITrackInsert Native(string kind, int sampleRate) => kind switch
    {
        Sat => new SatInsert(sampleRate),
        Comp => new CompInsert(sampleRate),
        Eq => new EqInsert(sampleRate),
        Reverb => new ReverbInsert(sampleRate),
        Delay => new DelayInsert(sampleRate),
        Gate => new GateInsert(sampleRate),
        Clean => new CleanInsert(sampleRate),
        Bands => new BandsInsert(sampleRate),
        Color => new ColorInsert(sampleRate),
        Tape => new TapeInsert(sampleRate),
        Width => new WidthInsert(sampleRate),
        Clip => new ClipInsert(sampleRate),
        Chorus => new ChorusInsert(sampleRate),
        Flanger => new FlangerInsert(sampleRate),
        Phaser => new PhaserInsert(sampleRate),
        Tremolo => new TremoloInsert(sampleRate),
        Fet => new FetCompInsert(sampleRate),
        Opto => new OptoCompInsert(sampleRate),
        BusComp => new BusCompInsert(sampleRate),
        Vca => new VcaCompInsert(sampleRate),
        VariMu => new VariMuInsert(sampleRate),
        Pump => new PumpInsert(sampleRate),
        _ => throw new ArgumentException($"unknown insert kind '{kind}'", nameof(kind)),
    };

    public static void Apply(ITrackInsert insert, InsertSpec spec)
    {
        switch (insert)
        {
            case FxShell f: f.Set(spec); break;
            case LimitInsert l: l.Set(spec.Stage, spec.StageOn, spec.Wet, spec.Dry, spec.OutputDb); break;
            case VstInsert v when spec.Vst != null: v.Set(spec.Vst, spec.Bpm); break;
            default: ApplyNative(insert, spec); break;
        }
    }

    /// <summary>Hands the settings to the effect itself, in or out of its <see cref="FxShell"/>.</summary>
    internal static void ApplyNative(ITrackInsert insert, InsertSpec spec)
    {
        switch (insert)
        {
            case SatInsert s: s.Set(spec.Sat); break;
            case CompInsert c: c.Set(spec.Comp); break;
            case EqInsert e: e.Set(spec.EqOn, spec.EqBands); break;
            case ReverbInsert r: r.Set(spec.Reverb, spec.Wait); break;
            case DelayInsert d: d.Set(spec.Delay, spec.Bpm); break;
            case GateInsert g: g.Set(spec.Gate); break;
            case CleanInsert c: c.Set(spec.Stage); break;
            case BandsInsert b: b.Set(spec.Stage); break;
            case ColorInsert c: c.Set(spec.Stage); break;
            case TapeInsert t: t.Set(spec.Stage); break;
            case WidthInsert w: w.Set(spec.Stage); break;
            case ClipInsert c: c.Set(spec.Stage); break;
            case ChorusInsert c: c.Set(spec.Mod); break;
            case FlangerInsert f: f.Set(spec.Mod); break;
            case PhaserInsert p: p.Set(spec.Mod); break;
            case TremoloInsert t: t.Set(spec.Mod); break;
            case FetCompInsert f: f.Set(spec.Model); break;
            case OptoCompInsert o: o.Set(spec.Model); break;
            case BusCompInsert b: b.Set(spec.Model); break;
            case VcaCompInsert v: v.Set(spec.Model); break;
            case VariMuInsert m: m.Set(spec.Model); break;
            case PumpInsert p: p.Set(spec.Pump, spec.Bpm); break;
            case TonalInsert t: t.Set(spec.On); break;
        }
    }

    /// <summary>
    /// The inserts for <paramref name="specs"/>, in their order: an insert of <paramref name="current"/> with the same id
    /// and kind is reused (its state carries on), the rest are new. Returns <paramref name="current"/> itself when nothing
    /// was added, removed or moved, so the caller knows the chain doesn't need swapping. UI thread. If one can't be set up
    /// (an export's plugin that doesn't open) it throws, after closing the ones it had made.
    /// </summary>
    public static ITrackInsert[] Build(IReadOnlyList<InsertSpec> specs, ITrackInsert[] current, string[] currentIds,
        int sampleRate, out string[] ids)
    {
        bool same = specs.Count == current.Length;
        var next = new ITrackInsert[specs.Count];
        ids = new string[specs.Count];
        for (int i = 0; i < specs.Count; i++)
        {
            var s = specs[i];
            int k = Array.IndexOf(currentIds, s.Id);
            var insert = k >= 0 && current[k].Kind == s.Kind ? current[k] : Create(s.Kind, sampleRate);
            try
            {
                Apply(insert, s);
            }
            catch
            {
                foreach (var made in next.Take(i).Append(insert))
                    if (Array.IndexOf(current, made) < 0) (made as IDisposable)?.Dispose();
                throw;
            }
            next[i] = insert;
            ids[i] = s.Id;
            same &= k == i && insert == current[i];
        }
        if (same)
        {
            ids = currentIds;
            return current;
        }
        return next;
    }

    /// <summary>The fixed chain from before slots (saturation → compressor → EQ), for specs that bring no slots.</summary>
    public static InsertSpec[] Legacy(SatParams sat, CompState comp, bool eqOn, EqBandSpec[] eqBands) =>
    [
        new("sat", Sat) { Sat = sat },
        new("comp", Comp) { Comp = comp },
        new("eq", Eq) { EqOn = eqOn, EqBands = eqBands },
    ];
}
