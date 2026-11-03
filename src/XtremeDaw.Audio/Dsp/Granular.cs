using XtremeDaw.Audio.Midi;

namespace XtremeDaw.Audio.Dsp;

/// <summary>How the granular's playhead moves: through the sound at its own speed, or standing where Posición says.</summary>
public enum GranularMode { Stretch, Scrub }

/// <summary>
/// What a granular plays with, all of it but the sample (a <see cref="SamplerSound"/>, the same band-limited one the
/// sampler reads). Immutable: every edit swaps a new one in whole, and the voices read it again at their next control
/// step and their next grain, so a knob moves what is already sounding.
/// </summary>
public sealed record GranularPatch
{
    public GranularMode Mode { get; init; }
    /// <summary>The part of the sound the playhead goes over, 0..1.</summary>
    public double Start { get; init; }
    public double End { get; init; } = 1;
    /// <summary>0..1 of that part: where the playhead stands (Recorrer) or starts (Estirar).</summary>
    public double Position { get; init; }
    /// <summary>Estirar: how fast the playhead goes, 1 = the sound's own speed, whatever the pitch.</summary>
    public double Speed { get; init; } = 1;
    /// <summary>What <see cref="Speed"/> is multiplied by to follow the song's tempo (Sync); 1 = free.</summary>
    public double SpeedScale { get; init; } = 1;
    /// <summary>Estirar: the playhead goes backwards.</summary>
    public bool Reverse { get; init; }
    /// <summary>Grains per second.</summary>
    public double Density { get; init; } = 20;
    /// <summary>The density comes from the tempo (a note value): its lane doesn't apply.</summary>
    public bool DensitySynced { get; init; }
    public double SizeSec { get; init; } = 0.08;
    /// <summary>The window of a grain, 0..1: flat with soft edges up to a bell.</summary>
    public double Shape { get; init; } = 0.5;
    /// <summary>-1..1: the window's peak early (a hit) or late (a swell).</summary>
    public double Skew { get; init; }
    /// <summary>0..1: how far each grain's start wanders from the steady rhythm.</summary>
    public double Jitter { get; init; }
    /// <summary>Seconds a grain's reading place wanders around the playhead, either way.</summary>
    public double SpraySec { get; init; }
    /// <summary>0..1: how far each grain lands from the center, at random.</summary>
    public double Spread { get; init; }
    public int Root { get; init; } = 60;
    /// <summary>Semitones (fractions are cents) added to every note.</summary>
    public double Tune { get; init; }
    /// <summary>False: every key plays it at the same pitch.</summary>
    public bool KeyTrack { get; init; } = true;
    /// <summary>Notes at once; 1 = mono.</summary>
    public int Voices { get; init; } = 8;
    public double GainDb { get; init; }
    public double Pan { get; init; }
    /// <summary>How much the velocity changes the level: 0 = none, 1 = all of it.</summary>
    public double VelAmp { get; init; } = 0.7;
    public SamplerEnvelope Amp { get; init; } = new(0.01, 0.1, 1, 0.3);
    /// <summary>The gain of Normalizar (1 = off), worked out from the sound.</summary>
    public double Scale { get; init; } = 1;
}

/// <summary>The knobs of a granular a lane can drive: what is read again at every grain or control step.</summary>
public enum GranularKnob { Position, Speed, Density, Size, Jitter, Spray, Spread, Gain, Pan }

/// <summary>
/// What a knob's position (0..1, what a lane draws) means, the same travel the granular's window gives it. Here because
/// the audio thread reads it; the project's limits come from these.
/// </summary>
public static class GranularKnobs
{
    public const double MaxSpeed = 4, MinDensity = 1, MaxDensity = 200, MinSizeSec = 0.005, MaxSizeSec = 1, MaxSpraySec = 1;
    public const double MinGainDb = SamplerKnobs.MinGainDb, MaxGainDb = SamplerKnobs.MaxGainDb;
    /// <summary>What a lane's target calls each knob, in the order of <see cref="GranularKnob"/>: saved in the project.</summary>
    public static readonly string[] Ids = ["position", "speed", "density", "size", "jitter", "spray", "spread", "gain", "pan"];

    public static double Value(GranularKnob knob, double k) => knob switch
    {
        GranularKnob.Speed => k * k * MaxSpeed,
        GranularKnob.Density => MinDensity * Math.Pow(MaxDensity / MinDensity, k),
        GranularKnob.Size => MinSizeSec * Math.Pow(MaxSizeSec / MinSizeSec, k),
        GranularKnob.Spray => k * k * MaxSpraySec,
        GranularKnob.Gain => MinGainDb + k * (MaxGainDb - MinGainDb),
        GranularKnob.Pan => 2 * k - 1,
        _ => k,
    };

    public static double Position(GranularKnob knob, double v) => Math.Clamp(knob switch
    {
        GranularKnob.Speed => Math.Sqrt(Math.Max(0, v) / MaxSpeed),
        GranularKnob.Density => Math.Log(v / MinDensity) / Math.Log(MaxDensity / MinDensity),
        GranularKnob.Size => Math.Log(v / MinSizeSec) / Math.Log(MaxSizeSec / MinSizeSec),
        GranularKnob.Spray => Math.Sqrt(Math.Max(0, v) / MaxSpraySec),
        GranularKnob.Gain => (v - MinGainDb) / (MaxGainDb - MinGainDb),
        GranularKnob.Pan => (v + 1) / 2,
        _ => v,
    }, 0, 1);
}

/// <summary>
/// A granular's lanes, one curve per <see cref="GranularKnob"/> (null = the patch's value). Immutable and swapped whole.
/// Read at the frame of the song a grain starts on or a control step falls on, so a knob moves the same way live and in
/// the export, in any block size. Allocates nothing.
/// </summary>
public sealed class GranularCurves(AutoCurve?[] byKnob)
{
    public double At(GranularKnob knob, double sec, double plain) =>
        byKnob[(int)knob] is { } curve ? GranularKnobs.Value(knob, curve.PositionAt(sec)) : plain;
}

/// <summary>A granular as the export needs it: what it plays with and the sound, null for one with no sample.</summary>
public sealed record GranularRig(GranularPatch Patch, SamplerSound? Sound)
{
    /// <summary>Its lanes; null = none.</summary>
    public GranularCurves? Curves { get; init; }

    /// <summary>How far past the last note it rings: its release and the last grain.</summary>
    public double TailSec => Sound == null ? 0 : Patch.Amp.Release + GranularKnobs.MaxSizeSec;
}

/// <summary>
/// The granular instrument of a MIDI track (asked by the user, 2026-11-03: a granular sampler of its own, next to the
/// sampler): each note is a voice that fires short grains of the sample, read around a playhead that either runs through
/// the sound at a speed of its own (Estirar: the length changes, the pitch doesn't) or stands still where Posición says
/// (Recorrer). The pitch of a grain is the note's, so tempo and pitch never pull on each other.
/// <para>
/// Every random number a voice draws (Jitter, Spray, Ancho) comes from its own seed, made from the frame and the key its
/// note started on: the same notes give the same floats live and in the export, in any block size. Grains are read
/// from the sampler's band-limited levels (<see cref="SamplerSound"/>) with a 4-point interpolator, so a grain far above
/// its root doesn't alias, at a fraction of the sampler's cost per read; a grain that would not fit in its voice is
/// skipped, never allocated. What it does with the notes (jumps, loops, the keyboards) is <see cref="NoteInstrument"/>'s.
/// </para>
/// </summary>
public sealed class Granular : NoteInstrument
{
    /// <summary>The most notes at once, what Voces can ask for: each one can carry <see cref="MaxGrains"/> grains.</summary>
    public const int MaxVoices = 16;
    /// <summary>Grains one voice holds at once; more would only pile up the same sound at a higher cost.</summary>
    public const int MaxGrains = 32;
    /// <summary>Room for the notes fading out behind those: a voice taken away still needs its 5 ms.</summary>
    const int Pool = 2 * MaxVoices;
    /// <summary>A voice recomputes its level and pan every this many frames; the level moves every frame.</summary>
    const int ControlFrames = 16;
    /// <summary>Points of the grain window's table.</summary>
    public const int WindowPoints = 1024;
    /// <summary>The shortest edge of the flattest window, as a share of the grain: shorter would click.</summary>
    const double MinTaper = 0.04;
    /// <summary>How far Jitter moves a grain at most, as a share of the time between grains.</summary>
    const double JitterReach = 0.9;

    /// <summary>A patch worked out for the audio thread, so that thread only reads numbers.</summary>
    sealed class Compiled
    {
        public readonly GranularPatch P;
        public readonly Rates Amp;
        public readonly double Gain;
        /// <summary>The grain window, <see cref="WindowPoints"/> + 1 points.</summary>
        public readonly float[] Window;
        /// <summary>The window's average: how much of a grain's length it counts for in the sum.</summary>
        public readonly double WindowMean;
        public readonly bool Mono;

        public Compiled(GranularPatch p, int rate)
        {
            P = p;
            Amp = new Rates(p.Amp, rate);
            Gain = Math.Pow(10, p.GainDb / 20) * p.Scale;
            Window = MakeWindow(p.Shape, p.Skew);
            double sum = 0;
            for (int i = 0; i < WindowPoints; i++) sum += Window[i];
            WindowMean = sum / WindowPoints;
            Mono = p.Voices <= 1;
        }
    }

    /// <summary>
    /// A tapered window: the shape says how much of the grain its two cosine edges take (a bell at 1), the skew moves its
    /// peak, so the rise and the fall have their own lengths.
    /// </summary>
    public static float[] MakeWindow(double shape, double skew)
    {
        var w = new float[WindowPoints + 1];
        double taper = Math.Clamp(Math.Max(MinTaper, shape), 0, 1);
        double peak = 0.5 + 0.45 * Math.Clamp(skew, -1, 1);
        for (int i = 0; i <= WindowPoints; i++)
        {
            double x = i / (double)WindowPoints;
            // the skew bends the time axis so the middle of the window falls on its peak
            double u = x < peak ? 0.5 * x / peak : 0.5 + 0.5 * (x - peak) / (1 - peak);
            double edge = Math.Min(u, 1 - u), half = taper / 2;
            w[i] = (float)(edge >= half ? 1 : 0.5 - 0.5 * Math.Cos(Math.PI * edge / half));
        }
        return w;
    }

    struct Grain
    {
        public bool On;
        /// <summary>Where it reads, in frames of level 0; <see cref="Step"/> per frame.</summary>
        public double Pos, Step;
        /// <summary>Where it is in the window's table, and how far it goes per frame.</summary>
        public double Win, WinStep;
        public int Level;
        public double Scale;
        public float L, R;
    }

    /// <summary>One note. A class so that a voice can be handed around by reference; all of them exist from the start.</summary>
    sealed class Voice
    {
        public bool Active, Fading;
        public byte Source;
        public int Key;
        public long Order;
        /// <summary>Frames until its note ends; -1 = until told.</summary>
        public long OffIn;
        public double Note;
        public NoteBend? Bend;
        public long BendZero;
        public float Pan;
        public Stage AmpStage;
        public float AmpLevel;
        public float Velocity, VelGain, FadeGain = 1;
        public int Edge, EdgeFrames, Ctrl;
        public long Time;
        /// <summary>The playhead, in frames of level 0.</summary>
        public double Head;
        /// <summary>Frames until the next grain.</summary>
        public double Wait;
        /// <summary>How far the playhead goes per frame, from the last control step.</summary>
        public double Speed;
        public uint Seed;
        public SamplerSound Sound = null!;
        public float GainL, GainR, StepL, StepR;
        public readonly Grain[] Grains = new Grain[MaxGrains];
    }

    readonly Voice[] voices = new Voice[Pool];
    volatile Compiled compiled;
    volatile SamplerSound? sound;
    volatile GranularCurves? curves;
    /// <summary><see cref="compiled"/> for this block.</summary>
    Compiled block;
    /// <summary><see cref="curves"/> for this block.</summary>
    GranularCurves? lanes;
    long order;

    public Granular(int sampleRate) : base(sampleRate)
    {
        block = compiled = new Compiled(new GranularPatch(), sampleRate);
        for (int i = 0; i < Pool; i++) voices[i] = new Voice();
    }

    /// <summary>The new patch, from the next control step and grain on. UI thread.</summary>
    public void Set(GranularPatch patch) => compiled = new Compiled(patch, rate);

    /// <summary>The sound new notes play; the ones sounding finish with the one they began with. Null = silence.</summary>
    public SamplerSound? Sound { get => sound; set => sound = value; }

    /// <summary>The lanes on its knobs, swapped whole by the UI. Null = none.</summary>
    public GranularCurves? Curves { get => curves; set => curves = value; }

    /// <summary>Where the voices' playheads are now, 0..1 of the sound, for the window to draw; returns how many. Any thread: a stale value only moves a line.</summary>
    public int Heads(Span<float> into)
    {
        int k = 0;
        for (int i = 0; i < Pool && k < into.Length; i++)
        {
            var v = voices[i];
            if (!v.Active || v.Fading) continue;
            var s = v.Sound;
            if (s != null && s.Frames > 0) into[k++] = (float)(v.Head / s.Frames);
        }
        return k;
    }

    protected override void Prepare()
    {
        block = compiled;
        lanes = curves;
    }

    protected override bool CanPlay => sound != null;

    protected override bool Lasts => false;

    protected override bool Sounding
    {
        get
        {
            for (int i = 0; i < Pool; i++)
                if (voices[i].Active) return true;
            return false;
        }
    }

    protected override void DropSong()
    {
        for (int i = 0; i < Pool; i++)
        {
            var v = voices[i];
            if (v.Active && v.Source == FromSong) v.Active = v.Fading = false;
        }
    }

    protected override void End(byte source, int key)
    {
        for (int i = 0; i < Pool; i++)
        {
            var v = voices[i];
            if (v.Active && v.Source == source && v.Key == key && v.OffIn < 0) v.AmpStage = Stage.Release;
        }
    }

    protected override void FadeAll(bool everyone)
    {
        for (int i = 0; i < Pool; i++)
        {
            var v = voices[i];
            if (v.Active && (everyone || v.Source == FromSong)) v.Fading = true;
        }
    }

    /// <summary>A note starts on a free voice, over the oldest one if there are too many.</summary>
    protected override void Begin(byte source, int key, double note, int velocity, long off, long t, NoteBend? bend = null, float pan = 0)
    {
        var c = block;
        if (sound is not { Frames: > 0 } s) return;
        var p = c.P;
        Limit(c.Mono ? 0 : p.Voices - 1);
        var v = Free();
        v.Sound = s;
        v.Source = source;
        v.Key = key;
        v.Order = ++order;
        v.OffIn = off;
        v.Note = note;
        v.Bend = bend;
        v.BendZero = 0;
        v.Pan = pan;
        v.Velocity = velocity / 127f;
        v.VelGain = (float)(1 - p.VelAmp + p.VelAmp * v.Velocity * v.Velocity);
        v.Fading = false;
        v.FadeGain = 1;
        v.Time = 0;
        v.Ctrl = 0;
        v.Edge = 0;
        v.EdgeFrames = t > 0 ? ChaseFrames : 0;
        // the frame and key it started on: the same note draws the same grains wherever it is heard from
        long started = now - t;
        v.Seed = (uint)(started * 2654435761L ^ (key + 1) * 40503L) | 1;
        for (int g = 0; g < MaxGrains; g++) v.Grains[g].On = false;
        var (from, to) = Region(p, s);
        v.Head = p.Reverse && p.Mode == GranularMode.Stretch && p.Position <= 0 ? to : from + Math.Clamp(p.Position, 0, 1) * (to - from);
        v.Wait = 0;
        v.AmpStage = c.Amp.AttackFrames < 1 ? (p.Amp.Sustain >= 1 ? Stage.Sustain : Stage.Decay) : Stage.Attack;
        v.AmpLevel = c.Amp.AttackFrames < 1 ? 1 : 0;
        if (t > 0) Forward(c, v, t);
        v.GainL = v.GainR = 0;
        Control(c, v, now);
        v.GainL += v.StepL * ControlFrames;
        v.GainR += v.StepR * ControlFrames;
        v.StepL = v.StepR = 0;
        v.Ctrl = ControlFrames;
        v.Active = true;
    }

    /// <summary>The part of the sound the playhead goes over, in frames; never empty.</summary>
    static (double From, double To) Region(GranularPatch p, SamplerSound s)
    {
        double from = Math.Clamp(p.Start, 0, 1) * s.Frames, to = Math.Clamp(p.End, 0, 1) * s.Frames;
        return to - from >= 1 ? (from, to) : (0, s.Frames);
    }

    /// <summary>
    /// Moves a voice <paramref name="t"/> frames on, as if it had been playing: a note picked up in its middle. The
    /// playhead lands where it would be; the grains still sounding there start again from their places, a fresh draw.
    /// </summary>
    void Forward(Compiled c, Voice v, long t)
    {
        var p = c.P;
        v.Time = t;
        (v.AmpLevel, v.AmpStage) = c.Amp.At(t);
        if (p.Mode == GranularMode.Stretch)
        {
            var (from, to) = Region(p, v.Sound);
            double len = to - from, moved = (p.Reverse ? -1 : 1) * p.Speed * p.SpeedScale * t;
            double at = (v.Head - from + moved) % len;
            v.Head = from + (at < 0 ? at + len : at);
        }
        double gap = rate / Math.Max(GranularKnobs.MinDensity, p.Density);
        int size = Math.Max(1, (int)(p.SizeSec * rate));
        // the grains that would still be sounding: those started within one grain's length
        for (double back = Math.Min(t, size - 1); back >= 0; back -= gap) Spawn(c, v, now - (long)back, (int)back);
        v.Wait = gap - Math.Min(t, size - 1) % gap;
    }

    /// <summary>Leaves at most <paramref name="keep"/> notes sounding: the oldest ones go.</summary>
    void Limit(int keep)
    {
        while (true)
        {
            int count = 0;
            Voice? oldest = null;
            for (int i = 0; i < Pool; i++)
            {
                var v = voices[i];
                if (!v.Active || v.Fading) continue;
                count++;
                if (oldest == null || v.Order < oldest.Order) oldest = v;
            }
            if (count <= keep || oldest == null) return;
            oldest.Fading = true;
        }
    }

    Voice Free()
    {
        Voice? oldest = null;
        for (int i = 0; i < Pool; i++)
        {
            var v = voices[i];
            if (!v.Active) return v;
            if (oldest == null || v.Order < oldest.Order) oldest = v;
        }
        return oldest!;
    }

    /// <summary>A number in [0, 1) from the voice's own sequence.</summary>
    static double Draw(Voice v)
    {
        uint x = v.Seed;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        v.Seed = x;
        return x / 4294967296.0;
    }

    /// <summary>
    /// A grain starts at <paramref name="frame"/> of the song, <paramref name="age"/> frames ago. Always the same
    /// draws, whether it fits or not, so a full voice doesn't change what the next grains do.
    /// </summary>
    void Spawn(Compiled c, Voice v, long frame, int age)
    {
        var p = c.P;
        var s = v.Sound;
        double spray = p.SpraySec, size = p.SizeSec, spread = p.Spread, place = p.Position;
        if (lanes is { } auto)
        {
            double sec = frame / (double)rate;
            spray = auto.At(GranularKnob.Spray, sec, spray);
            size = auto.At(GranularKnob.Size, sec, size);
            spread = auto.At(GranularKnob.Spread, sec, spread);
            place = auto.At(GranularKnob.Position, sec, place);
        }
        double r1 = Draw(v), r2 = Draw(v);
        int length = Math.Max(2, (int)(Math.Clamp(size, GranularKnobs.MinSizeSec, GranularKnobs.MaxSizeSec) * rate));
        if (age >= length) return;
        int slot = -1;
        for (int g = 0; g < MaxGrains; g++)
            if (!v.Grains[g].On)
            {
                slot = g;
                break;
            }
        if (slot < 0) return;

        double head = v.Head;
        if (p.Mode == GranularMode.Scrub)
        {
            var (from, to) = Region(p, s);
            head = from + Math.Clamp(place, 0, 1) * (to - from);
        }
        double semis = (p.KeyTrack ? v.Note + (v.Bend?.At(v.Time - v.BendZero) ?? 0) - p.Root : 0) + p.Tune;
        double step = Math.Pow(2, semis / 12);
        // the grain is centered on the playhead: what it reads before and after it
        double pos = head + spray * rate * (2 * r1 - 1) - step * length / 2;
        pos = Math.Clamp(pos, 0, Math.Max(0, s.Frames - step * length));
        int level = Math.Clamp((int)Math.Ceiling(semis / 12 - 0.5), 0, SamplerSound.Levels - 1);
        double pan = Math.Clamp(spread, 0, 1) * (2 * r2 - 1);
        ref var gr = ref v.Grains[slot];
        gr.On = true;
        gr.Step = step;
        gr.Pos = pos + step * age;
        gr.WinStep = WindowPoints / (double)length;
        gr.Win = gr.WinStep * age;
        gr.Level = level;
        gr.Scale = 1.0 / (1 << level);
        gr.L = (float)Math.Min(1, 1 - pan);
        gr.R = (float)Math.Min(1, 1 + pan);
    }

    /// <summary>The time to the next grain: the density at <paramref name="frame"/>, moved by Jitter.</summary>
    double Gap(Compiled c, Voice v, long frame)
    {
        var p = c.P;
        double density = p.Density, jitter = p.Jitter;
        if (lanes is { } auto)
        {
            double sec = frame / (double)rate;
            if (!p.DensitySynced) density = auto.At(GranularKnob.Density, sec, density);
            jitter = auto.At(GranularKnob.Jitter, sec, jitter);
        }
        double gap = rate / Math.Clamp(density, GranularKnobs.MinDensity, GranularKnobs.MaxDensity);
        return gap * (1 + Math.Clamp(jitter, 0, 1) * JitterReach * (2 * Draw(v) - 1));
    }

    /// <summary>Every <see cref="ControlFrames"/>, at <paramref name="frame"/> of the song: the level the voice heads to, and its pan.</summary>
    void Control(Compiled c, Voice v, long frame)
    {
        var p = c.P;
        double gain = c.Gain, pan = p.Pan, density = p.Density, size = p.SizeSec, place = p.Position;
        v.Speed = p.Speed;
        if (lanes is { } auto)
        {
            double sec = frame / (double)rate;
            v.Speed = auto.At(GranularKnob.Speed, sec, p.Speed);
            if (!p.DensitySynced) density = auto.At(GranularKnob.Density, sec, density);
            gain = Math.Pow(10, auto.At(GranularKnob.Gain, sec, p.GainDb) / 20) * p.Scale;
            pan = auto.At(GranularKnob.Pan, sec, pan);
            size = auto.At(GranularKnob.Size, sec, size);
            place = auto.At(GranularKnob.Position, sec, place);
        }
        v.Speed *= p.SpeedScale;
        if (p.Mode == GranularMode.Scrub)
        {
            // standing still: the playhead is only where the window draws it
            var (from, to) = Region(p, v.Sound);
            v.Head = from + Math.Clamp(place, 0, 1) * (to - from);
        }
        // the grains overlap: their sum is kept at the level of one grain, so density and size don't change the volume
        double overlap = Math.Min(density * size, MaxGrains) * c.WindowMean;
        gain /= Math.Max(1, overlap);
        pan = Math.Clamp(pan + v.Pan, -1, 1);
        float targetL = (float)(gain * Math.Min(1, 1 - pan)) * v.VelGain;
        float targetR = (float)(gain * Math.Min(1, 1 + pan)) * v.VelGain;
        v.StepL = (targetL - v.GainL) / ControlFrames;
        v.StepR = (targetR - v.GainR) / ControlFrames;
    }

    protected override void RenderVoices(int at, int n, Span<float> l, Span<float> r)
    {
        if (n <= 0) return;
        var c = block;
        var ol = l.Slice(at, n);
        var or = r.Slice(at, n);
        for (int i = 0; i < Pool; i++)
            if (voices[i].Active) Render(c, voices[i], ol, or, n);
    }

    /// <summary>A voice's next frames, added to the output. Its state moves frame by frame, so block sizes don't matter.</summary>
    void Render(Compiled c, Voice v, Span<float> ol, Span<float> or, int n)
    {
        var p = c.P;
        var s = v.Sound;
        var window = c.Window;
        long frame = now;
        bool stretch = p.Mode == GranularMode.Stretch;
        var (from, to) = Region(p, s);
        double len = to - from;
        int reach = SamplerSound.Pad - 2;
        for (int i = 0; i < n; i++)
        {
            if (v.Ctrl <= 0)
            {
                Control(c, v, frame + i);
                v.Ctrl = ControlFrames;
            }
            v.Ctrl--;
            if (v.OffIn >= 0 && v.OffIn-- == 0) v.AmpStage = Stage.Release;

            if (--v.Wait <= 0)
            {
                Spawn(c, v, frame + i, 0);
                v.Wait += Math.Max(1, Gap(c, v, frame + i));
            }

            float a = 0, b = 0;
            for (int g = 0; g < MaxGrains; g++)
            {
                ref var gr = ref v.Grains[g];
                if (!gr.On) continue;
                int wi = (int)gr.Win;
                if (wi >= WindowPoints)
                {
                    gr.On = false;
                    continue;
                }
                float w = window[wi] + (float)(gr.Win - wi) * (window[wi + 1] - window[wi]);
                var ll = s.L[gr.Level];
                var rr = s.R[gr.Level];
                double q = gr.Pos * gr.Scale;
                int ix = (int)q;
                int read = ix + SamplerSound.Pad;
                if (ix >= -reach && read + 2 < ll.Length)
                {
                    float x = (float)(q - ix);
                    a += w * gr.L * Hermite(ll, read, x);
                    b += w * gr.R * Hermite(rr, read, x);
                }
                gr.Pos += gr.Step;
                gr.Win += gr.WinStep;
            }

            if (stretch)
            {
                v.Head += p.Reverse ? -v.Speed : v.Speed;
                if (v.Head >= to) v.Head -= len * Math.Ceiling((v.Head - to + 1e-9) / len);
                else if (v.Head < from) v.Head += len * Math.Ceiling((from - v.Head) / len);
            }

            float edge = 1;
            if (v.EdgeFrames > 0 && v.Edge < v.EdgeFrames) edge = ++v.Edge / (float)v.EdgeFrames;
            if (v.Fading)
            {
                v.FadeGain -= 1f / FadeFrames;
                if (v.FadeGain <= 0)
                {
                    v.Active = false;
                    v.Fading = false;
                    return;
                }
                edge *= v.FadeGain;
            }
            float level = v.AmpLevel * edge;
            v.GainL += v.StepL;
            v.GainR += v.StepR;
            ol[i] += a * level * v.GainL;
            or[i] += b * level * v.GainR;

            v.AmpLevel = Next(ref v.AmpStage, v.AmpLevel, c.Amp, false);
            if (v.AmpStage == Stage.Done)
            {
                v.Active = false;
                return;
            }
            v.Time++;
        }
    }

    /// <summary>A 4-point Hermite read between <paramref name="i"/> and the next frame: enough for grains, a quarter of the sampler's kernel.</summary>
    static float Hermite(float[] y, int i, float x)
    {
        float y0 = y[i - 1], y1 = y[i], y2 = y[i + 1], y3 = y[i + 2];
        float c1 = 0.5f * (y2 - y0);
        float c2 = y0 - 2.5f * y1 + 2 * y2 - 0.5f * y3;
        float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
        return ((c3 * x + c2) * x + c1) * x + y1;
    }
}
