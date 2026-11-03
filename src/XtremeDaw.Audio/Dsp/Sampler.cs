using XtremeDaw.Audio.Midi;

namespace XtremeDaw.Audio.Dsp;

public enum SamplerFilter { Off, Low12, Low24, High12, High24, Band, Notch }
public enum SamplerLfoShape { Sine, Triangle, Saw, Square, Random }

/// <summary>An envelope: attack, decay and release in seconds, the sustain as a level 0..1.</summary>
public readonly record struct SamplerEnvelope(double Attack, double Decay, double Sustain, double Release);

/// <summary>
/// What a sampler plays with, all of it but the sample (<see cref="SamplerSound"/>). Immutable: every edit swaps a new
/// one in whole and the voices read it again at their next control step, so a knob moves what is already sounding.
/// </summary>
public sealed record SamplerPatch
{
    /// <summary>Where a note starts and ends, 0..1 of the sound.</summary>
    public double Start { get; init; }
    public double End { get; init; } = 1;
    /// <summary>Plays to its end whatever the note does; the loop doesn't apply.</summary>
    public bool OneShot { get; init; }
    public int Root { get; init; } = 60;
    /// <summary>Semitones (fractions are cents) added to every note.</summary>
    public double Tune { get; init; }
    /// <summary>False: every key plays it at the same pitch.</summary>
    public bool KeyTrack { get; init; } = true;
    /// <summary>Notes at once; 1 = mono.</summary>
    public int Voices { get; init; } = 16;
    /// <summary>Mono: a note played over another bends it, without starting the sample again.</summary>
    public bool Legato { get; init; }
    /// <summary>Mono: how long a note takes to reach its pitch from the last one.</summary>
    public double GlideSec { get; init; }
    public double GainDb { get; init; }
    public double Pan { get; init; }
    /// <summary>How much the velocity changes the level: 0 = none, 1 = all of it.</summary>
    public double VelAmp { get; init; } = 0.7;
    public SamplerEnvelope Amp { get; init; } = new(0, 0.1, 1, 0.15);
    public SamplerFilter Filter { get; init; }
    public double Cutoff { get; init; } = 20000;
    /// <summary>0..1.</summary>
    public double Resonance { get; init; }
    /// <summary>0..1: how hard the sound hits the filter.</summary>
    public double Drive { get; init; }
    /// <summary>0..1: how much the cutoff follows the key, 1 = a full octave per octave.</summary>
    public double FilterKey { get; init; }
    /// <summary>0..1: octaves the velocity opens the filter, at most <see cref="VelOctaves"/>.</summary>
    public double FilterVel { get; init; }
    /// <summary>-1..1: how far the filter envelope moves the cutoff, at most <see cref="EnvOctaves"/>.</summary>
    public double FilterEnv { get; init; }
    public SamplerEnvelope FilterShape { get; init; } = new(0, 0.3, 0, 0.2);
    public SamplerLfoShape LfoShape { get; init; }
    public double LfoHz { get; init; } = 5;
    /// <summary>Seconds until the LFO reaches its full depth, from the start of the note.</summary>
    public double LfoDelay { get; init; }
    /// <summary>Semitones of vibrato.</summary>
    public double LfoPitch { get; init; }
    /// <summary>Octaves of wah.</summary>
    public double LfoFilter { get; init; }
    /// <summary>0..1 of tremolo.</summary>
    public double LfoAmp { get; init; }
    /// <summary>Semitones the note starts away from its pitch.</summary>
    public double PitchEnv { get; init; }
    /// <summary>Seconds for that to come down by 60 dB.</summary>
    public double PitchDecay { get; init; } = 0.05;
    /// <summary>The gain of Normalizar (1 = off), worked out from the sound.</summary>
    public double Scale { get; init; } = 1;

    public const double EnvOctaves = 7, VelOctaves = 4;
}

/// <summary>The knobs of a sampler a lane can drive: what can move under a sounding note without rewriting its past.</summary>
public enum SamplerKnob { Cutoff, Resonance, Drive, FilterEnv, Gain, Pan, LfoPitch, LfoFilter, LfoAmp }

/// <summary>
/// What a knob's position (0..1, what a lane draws) means, the same travel the sampler's window gives it. Here because
/// the audio thread reads it; the project's limits come from these.
/// </summary>
public static class SamplerKnobs
{
    public const double MinCutoff = 20, MaxCutoff = 20000, MinGainDb = -36, MaxGainDb = 12, MaxLfoPitch = 12, MaxLfoFilter = 4;
    /// <summary>What a lane's target calls each knob, in the order of <see cref="SamplerKnob"/>: saved in the project.</summary>
    public static readonly string[] Ids = ["cutoff", "resonance", "drive", "filterEnv", "gain", "pan", "lfoPitch", "lfoFilter", "lfoAmp"];

    public static double Value(SamplerKnob knob, double k) => knob switch
    {
        SamplerKnob.Cutoff => MinCutoff * Math.Pow(MaxCutoff / MinCutoff, k),
        SamplerKnob.FilterEnv or SamplerKnob.Pan => 2 * k - 1,
        SamplerKnob.Gain => MinGainDb + k * (MaxGainDb - MinGainDb),
        SamplerKnob.LfoPitch => k * k * MaxLfoPitch,
        SamplerKnob.LfoFilter => k * MaxLfoFilter,
        _ => k,
    };

    public static double Position(SamplerKnob knob, double v) => Math.Clamp(knob switch
    {
        SamplerKnob.Cutoff => Math.Log(v / MinCutoff) / Math.Log(MaxCutoff / MinCutoff),
        SamplerKnob.FilterEnv or SamplerKnob.Pan => (v + 1) / 2,
        SamplerKnob.Gain => (v - MinGainDb) / (MaxGainDb - MinGainDb),
        SamplerKnob.LfoPitch => Math.Sqrt(v / MaxLfoPitch),
        SamplerKnob.LfoFilter => v / MaxLfoFilter,
        _ => v,
    }, 0, 1);
}

/// <summary>
/// A sampler's lanes, one curve per <see cref="SamplerKnob"/> (null = the patch's value). Immutable and swapped whole. A
/// voice reads them at its own control steps, at the frame of the song it has reached, so a knob moves the same way
/// live and in the export, in any block size. Allocates nothing.
/// </summary>
public sealed class SamplerCurves(AutoCurve?[] byKnob)
{
    public double At(SamplerKnob knob, double sec, double plain) =>
        byKnob[(int)knob] is { } curve ? SamplerKnobs.Value(knob, curve.PositionAt(sec)) : plain;
}

/// <summary>A sampler as the export needs it: what it plays with and the sound, null for a sampler with no sample.</summary>
public sealed record SamplerRig(SamplerPatch Patch, SamplerSound? Sound)
{
    /// <summary>Its lanes; null = none.</summary>
    public SamplerCurves? Curves { get; init; }

    /// <summary>
    /// How far past the last note it can ring: a release, or a one shot played to its end at the lowest note of
    /// <paramref name="notes"/>, however long that is.
    /// </summary>
    public double TailSec(NoteSequence? notes, int sampleRate)
    {
        if (Sound == null) return 0;
        double tail = Patch.Amp.Release;
        if (!Patch.OneShot || notes == null) return tail;
        double lowest = 1;
        if (Patch.KeyTrack)
            foreach (var n in notes.Notes) lowest = Math.Min(lowest, Math.Pow(2, (n.Pitch + (n.Bend?.Lowest ?? 0) - Patch.Root + Patch.Tune) / 12));
        return Math.Max(tail, Sound.Frames * Math.Max(0, Patch.End - Patch.Start) / sampleRate / lowest);
    }
}

/// <summary>
/// The native instrument of a MIDI track: one sample played across the keyboard, each note a voice with its own
/// envelopes, filter and LFO. The same class plays live and in the export (<see cref="MixRenderer"/>), and a voice only
/// depends on the time since its note began and, through the lanes on its knobs (<see cref="Curves"/>), on the frame of
/// the song: the same notes give the same floats in any block size. Each note is its own: a slide, a porta (a
/// <see cref="NoteBend"/>) and a pan are that voice's, not the channel's. Nothing here waits: unlike a plugin it has no
/// lateness, so a note starts on its exact sample.
/// <para>
/// A jump lets the old notes go in 5 ms and picks up the notes the playhead landed in, from where they would be. A loop
/// is told where it turns (<see cref="Play"/>), so the notes at its start come in on their exact sample, whole.
/// </para>
/// <para>
/// Audio thread only, except what says otherwise: every voice exists from the start and nothing allocates. <see
/// cref="Set"/>, <see cref="Sound"/> and <see cref="Notes"/> swap immutable objects from the UI thread, and the window's
/// keyboard goes through a lock-free ring.
/// </para>
/// </summary>
public sealed class Sampler
{
    /// <summary>The most notes at once, what Voces can ask for.</summary>
    public const int MaxVoices = 32;
    /// <summary>Room for the notes fading out behind those: a voice taken away still needs its 5 ms.</summary>
    const int Pool = 2 * MaxVoices;
    /// <summary>A voice recomputes pitch, filter and LFO every this many frames (0.33 ms at 48 kHz); its level moves every frame.</summary>
    const int ControlFrames = 16;
    /// <summary>A voice taken away, or the notes of the song after a jump, fade over 5 ms.</summary>
    const int FadeFrames = 240;
    /// <summary>The soft edge of a cut in the middle of the sound, in place of a click.</summary>
    const int EdgeFrames = 64;
    /// <summary>A note picked up in its middle comes in over 5 ms.</summary>
    const int ChaseFrames = 240;
    /// <summary>Below this (-80 dB) an envelope is over.</summary>
    const float Silent = 1e-4f;
    /// <summary>How long a track keeps running stopped after its last voice: a reverb slot's tail rings out.</summary>
    const int TailSec = 3;
    /// <summary>The cutoff stays this far from Nyquist, where the filter's frequency warp runs away.</summary>
    const double TopCutoff = 0.45, LowCutoff = 16;
    /// <summary>The filter's damping at full resonance: +20 dB at the cutoff, loud but never a runaway.</summary>
    const double MinDamping = 0.1;
    /// <summary>The second stage of the 24 dB types: only the first one resonates, or their peaks would multiply.</summary>
    const float SecondDamping = 1.2f;

    enum Stage : byte { Attack, Decay, Sustain, Release, Done }

    /// <summary>An envelope's numbers for one rate: per frame for the level, per control step for the filter.</summary>
    readonly struct Rates
    {
        public readonly float AttackStep, DecayCoef, Sustain, ReleaseCoef;
        public readonly double AttackFrames, DecayFrames, ReleaseFrames;

        public Rates(SamplerEnvelope e, double framesPerSec)
        {
            AttackFrames = e.Attack * framesPerSec;
            DecayFrames = Math.Max(1, e.Decay * framesPerSec);
            ReleaseFrames = Math.Max(1, e.Release * framesPerSec);
            AttackStep = AttackFrames < 1 ? 1 : (float)(1 / AttackFrames);
            DecayCoef = (float)Math.Exp(-SixtyDb / DecayFrames);
            ReleaseCoef = (float)Math.Exp(-SixtyDb / ReleaseFrames);
            Sustain = (float)e.Sustain;
        }

        /// <summary>The level a held note has after <paramref name="t"/> steps, and its stage.</summary>
        public (float Level, Stage Stage) At(double t)
        {
            if (t < AttackFrames) return ((float)(t / AttackFrames), Stage.Attack);
            float level = (float)(Sustain + (1 - Sustain) * Math.Exp(-SixtyDb * (t - AttackFrames) / DecayFrames));
            return (level, Math.Abs(level - Sustain) < Silent ? Stage.Sustain : Stage.Decay);
        }
    }

    /// <summary>ln(1000): an exponential "over" in T takes T / this as its time constant.</summary>
    const double SixtyDb = 6.907755;

    /// <summary>A patch worked out for the audio thread, so that thread only reads numbers.</summary>
    sealed class Compiled
    {
        public readonly SamplerPatch P;
        public readonly Rates Amp, Filter;
        public readonly float Drive, DriveMakeup, PitchCoef, Damping;
        public readonly double Gain, Cutoff, LfoStep;
        public readonly bool Mono;
        public readonly int GlideSteps;

        public Compiled(SamplerPatch p, int rate)
        {
            P = p;
            Amp = new Rates(p.Amp, rate);
            Filter = new Rates(p.FilterShape, rate / (double)ControlFrames);
            Gain = GainOf(p.GainDb, p);
            Drive = DriveOf(p.Drive);
            DriveMakeup = MakeupOf(p.Drive);
            PitchCoef = (float)Math.Exp(-SixtyDb * ControlFrames / Math.Max(1, p.PitchDecay * rate));
            Damping = DampingOf(p.Resonance);
            Cutoff = Math.Clamp(p.Cutoff, LowCutoff, TopCutoff * rate);
            LfoStep = p.LfoHz * ControlFrames / rate;
            Mono = p.Voices <= 1;
            GlideSteps = (int)Math.Round(p.GlideSec * rate / ControlFrames);
        }
    }

    static double GainOf(double db, SamplerPatch p) => Math.Pow(10, db / 20) * p.Scale;
    static float DriveOf(double drive) => (float)(1 + 7 * drive);
    static float MakeupOf(double drive) => (float)(1 / (1 + 1.5 * drive));
    static float DampingOf(double resonance) => (float)Math.Max(MinDamping, 2 * (1 - resonance));

    /// <summary>What a voice belongs to: the song's notes go when the song stops or jumps, the others stay.</summary>
    const byte FromSong = 0, FromKeys = 1, FromWindow = 2;

    /// <summary>One note. A class so that a voice can be handed around by reference; all of them exist from the start.</summary>
    sealed class Voice
    {
        public bool Active, Fading;
        public byte Source;
        public int Key;
        public long Order;
        /// <summary>Frames until its note ends; -1 = until told.</summary>
        public long OffIn;
        public double Pos, Step;
        public double Note, GlideStep, GlideTo;
        public int GlideLeft;
        /// <summary>The note's slides and porta, from <see cref="BendZero"/> on in <see cref="Time"/>; null = none.</summary>
        public NoteBend? Bend;
        public long BendZero;
        /// <summary>The note's own pan, added to the patch's.</summary>
        public float Pan;
        public Stage AmpStage, FilterStage;
        public float AmpLevel, FilterLevel, PitchLevel;
        public double LfoPhase, Random;
        public uint Seed;
        public long Time;
        public float Velocity, VelGain, FadeGain = 1;
        public int Edge, EdgeFrames;
        public int Ctrl;
        public int Direction = 1;
        public int Level;
        public double Scale = 1, LastSemis = double.NaN;
        public SamplerSound Sound = null!;
        public double End;
        public SamplerLoop Loop;
        public float GainL, GainR, TargetL, TargetR, StepL, StepR;
        public float A1, A2, A3, Damp, B1, B2, B3, Damp2;
        public bool Filtering;
        public float Drive, Makeup;
        public float S1L, S2L, S3L, S4L, S1R, S2R, S3R, S4R;
    }

    readonly int rate;
    readonly Voice[] voices = new Voice[Pool];
    readonly int tailFrames;
    volatile Compiled compiled;
    volatile SamplerSound? sound;
    volatile NoteSequence? notes;
    volatile SamplerCurves? curves;
    /// <summary><see cref="curves"/> for this block.</summary>
    SamplerCurves? lanes;
    /// <summary>The frame of the song where the next voices start and <see cref="Render"/> goes on from.</summary>
    long now;
    NoteSequence? lastNotes;
    /// <summary>The next note of the song to start; -1 = it has to be looked for.</summary>
    int next = -1;
    long expected = -1;
    bool playing;
    long order;
    double lastNote = double.NaN;
    int ring;
    int panic;

    // the window's keyboard and the piano roll's keys: a lock-free ring from the UI thread
    readonly (int Pitch, int Velocity, int Frames)[] inbox = new (int, int, int)[64];
    int inHead, inTail;
    /// <summary>A key held until <see cref="Lift"/>, in <see cref="inbox"/>; a lift has velocity 0.</summary>
    const int Held = -1;

    public Sampler(int sampleRate)
    {
        rate = sampleRate;
        tailFrames = TailSec * sampleRate;
        compiled = new Compiled(new SamplerPatch(), sampleRate);
        for (int i = 0; i < Pool; i++) voices[i] = new Voice();
    }

    /// <summary>The new patch, from the next control step on. UI thread.</summary>
    public void Set(SamplerPatch patch) => compiled = new Compiled(patch, rate);

    /// <summary>The sound new notes play; the ones sounding finish with the one they began with. Null = silence.</summary>
    public SamplerSound? Sound { get => sound; set => sound = value; }

    /// <summary>The track's notes in frames, swapped whole by the UI.</summary>
    public NoteSequence? Notes { get => notes; set => notes = value; }

    /// <summary>The lanes on its knobs, swapped whole by the UI. Null = none.</summary>
    public SamplerCurves? Curves { get => curves; set => curves = value; }

    /// <summary>Every note goes in 5 ms, whoever played it. Any thread.</summary>
    public void Panic() => Volatile.Write(ref panic, 1);

    /// <summary>A key on its own for <paramref name="frames"/> (the piano roll). UI thread.</summary>
    public void Audition(int pitch, int velocity, int frames) => Post(pitch, velocity, Math.Max(1, frames));

    /// <summary>A key held until <see cref="Lift"/> (the window's keyboard). UI thread.</summary>
    public void Press(int pitch, int velocity) => Post(pitch, velocity, Held);

    public void Lift(int pitch) => Post(pitch, 0, Held);

    void Post(int pitch, int velocity, int frames)
    {
        int head = inHead, after = (head + 1) % inbox.Length;
        if (after == Volatile.Read(ref inTail)) return; // full: dropped
        inbox[head] = (Math.Clamp(pitch, 0, 127), Math.Clamp(velocity, 0, 127), frames);
        Volatile.Write(ref inHead, after);
    }

    /// <summary>Where the voices are now, 0..1 of the sound, for the window to draw; returns how many. Any thread: a stale value only moves a line.</summary>
    public int Heads(Span<float> into)
    {
        int k = 0;
        for (int i = 0; i < Pool && k < into.Length; i++)
        {
            var v = voices[i];
            if (!v.Active || v.Fading) continue;
            var s = v.Sound;
            if (s != null && s.Frames > 0) into[k++] = (float)(v.Pos / s.Frames);
        }
        return k;
    }

    /// <summary>
    /// While playing: adds frames <c>[pos .. pos + n)</c> to <paramref name="l"/> / <paramref name="r"/>. A loop that
    /// turns inside the block says where: <paramref name="turnAt"/> frames in, the song goes on from <paramref name="turnTo"/>.
    /// </summary>
    public void Play(long pos, int n, Span<float> l, Span<float> r, LiveNotes? keys = null, int turnAt = -1, long turnTo = 0)
    {
        var seq = notes;
        if (seq != lastNotes)
        {
            lastNotes = seq;
            next = -1;
        }
        var c = compiled;
        lanes = curves;
        now = pos;
        if (Interlocked.Exchange(ref panic, 0) != 0) FadeAll(true);
        if (pos != expected || !playing)
        {
            FadeAll(false);
            next = Find(seq, pos);
            Chase(c, seq, pos);
        }
        else if (next < 0) next = Find(seq, pos);
        playing = true;
        Inputs(c, keys);
        int done = 0;
        if (turnAt >= 0 && turnAt <= n)
        {
            Run(c, seq, pos, 0, turnAt, l, r);
            FadeAll(false);
            next = Find(seq, turnTo);
            // what sounds at the loop's start began before it: a long chord would stay silent from the second lap on
            now = turnTo;
            Chase(c, seq, turnTo);
            pos = turnTo - turnAt;
            done = turnAt;
        }
        Run(c, seq, pos, done, n, l, r);
        expected = pos + n;
        ring = tailFrames;
    }

    /// <summary>
    /// While stopped: the keyboard and the piano roll's keys. The song's notes fade at the stop (the transport already
    /// faded it out). False when there is nothing to hear and no tail to let ring. <paramref name="frame"/> is where the
    /// playhead rests, what the lanes read.
    /// </summary>
    public bool Idle(int n, Span<float> l, Span<float> r, LiveNotes? keys = null, long frame = 0)
    {
        var c = compiled;
        lanes = curves;
        now = frame;
        if (Interlocked.Exchange(ref panic, 0) != 0) FadeAll(true);
        if (playing)
        {
            playing = false;
            expected = -1;
            FadeAll(false);
        }
        Inputs(c, keys);
        l.Clear();
        r.Clear();
        bool any = false;
        for (int i = 0; i < Pool; i++) any |= voices[i].Active;
        if (any)
        {
            RenderVoices(c, 0, n, l, r);
            ring = tailFrames;
            return true;
        }
        ring = Math.Max(0, ring - n);
        return ring > 0;
    }

    /// <summary>
    /// The transport finished its fade-out: what the song was playing is already silent, and no gain is left to keep it
    /// so, so its voices and the track's tail must not come back at full level in <see cref="Idle"/>. Audio thread.
    /// </summary>
    public void FadedOut()
    {
        for (int i = 0; i < Pool; i++)
        {
            var v = voices[i];
            if (v.Active && v.Source == FromSong) v.Active = v.Fading = false;
        }
        ring = 0;
    }

    /// <summary>The first note at or after <paramref name="pos"/>.</summary>
    static int Find(NoteSequence? seq, long pos)
    {
        if (seq == null) return 0;
        var all = seq.Notes;
        int lo = 0, hi = all.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (all[mid].Start < pos) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Starts the notes the playhead landed in the middle of, from where they would be by now.</summary>
    void Chase(Compiled c, NoteSequence? seq, long pos)
    {
        if (seq == null || sound is not { } s) return;
        var all = seq.Notes;
        for (int k = 0; k < all.Length && all[k].Start < pos; k++)
        {
            ref readonly var e = ref all[k];
            long t = pos - e.Start, left = e.Length - t;
            if (left <= 0 && !c.P.OneShot) continue;
            Begin(c, s, FromSong, e.Pitch, PitchOf(e.Hz), e.Velocity, left > 0 ? left : -1, t, e.Bend, e.Pan);
        }
    }

    static double PitchOf(double hz) => 69 + 12 * Math.Log2(hz / 440);

    /// <summary>Renders <c>[from .. to)</c> of the block, stopping at each note so that it starts on its exact frame.</summary>
    void Run(Compiled c, NoteSequence? seq, long pos, int from, int to, Span<float> l, Span<float> r)
    {
        int i = from;
        while (i < to)
        {
            int stop = to;
            if (seq != null)
            {
                var all = seq.Notes;
                var s = sound;
                now = pos + i;
                while (next < all.Length && all[next].Start <= pos + i)
                {
                    ref readonly var e = ref all[next];
                    if (s != null) Begin(c, s, FromSong, e.Pitch, PitchOf(e.Hz), e.Velocity, e.Length, 0, e.Bend, e.Pan);
                    next++;
                }
                if (next < all.Length) stop = (int)Math.Min(to, all[next].Start - pos);
            }
            now = pos + i;
            RenderVoices(c, i, stop - i, l, r);
            i = stop;
        }
    }

    /// <summary>The keys of a MIDI keyboard and what the UI asked for, at the start of the block.</summary>
    void Inputs(Compiled c, LiveNotes? keys)
    {
        var s = sound;
        if (keys != null)
        {
            double cents = keys.Transpose - Math.Round(keys.Transpose);
            foreach (ref readonly var e in keys.Events)
            {
                if (e.On) { if (s != null) Begin(c, s, FromKeys, e.Sounds, e.Sounds + cents, e.Velocity, -1, 0); }
                else End(FromKeys, e.Sounds);
            }
        }
        int tail = inTail;
        while (tail != Volatile.Read(ref inHead))
        {
            var (pitch, velocity, frames) = inbox[tail];
            if (velocity == 0) End(FromWindow, pitch);
            else if (s != null)
            {
                End(FromWindow, pitch);
                Begin(c, s, FromWindow, pitch, pitch, velocity, frames == Held ? -1 : frames, 0);
            }
            tail = (tail + 1) % inbox.Length;
        }
        Volatile.Write(ref inTail, tail);
    }

    void End(byte source, int key)
    {
        for (int i = 0; i < Pool; i++)
        {
            var v = voices[i];
            if (v.Active && v.Source == source && v.Key == key && v.OffIn < 0) Release(v);
        }
    }

    void Release(Voice v)
    {
        v.OffIn = -1;
        if (compiled.P.OneShot) return;
        v.AmpStage = Stage.Release;
        if (v.FilterStage != Stage.Done) v.FilterStage = Stage.Release;
    }

    /// <summary>The song's notes, or everyone's, go in 5 ms.</summary>
    void FadeAll(bool everyone)
    {
        for (int i = 0; i < Pool; i++)
        {
            var v = voices[i];
            if (v.Active && (everyone || v.Source == FromSong)) v.Fading = true;
        }
    }

    /// <summary>A note starts: on a free voice, over the oldest one if there are too many, or as a bend of the one playing (Legato).</summary>
    /// <param name="key">What ends it from the keyboard.</param>
    /// <param name="note">The pitch it sounds at; fractions are the Transponer's cents.</param>
    /// <param name="off">Frames until it ends, -1 = until told.</param>
    /// <param name="t">Frames it would have been sounding by now.</param>
    /// <param name="bend">Its slides and porta.</param>
    /// <param name="pan">Its own pan.</param>
    void Begin(Compiled c, SamplerSound s, byte source, int key, double note, int velocity, long off, long t, NoteBend? bend = null, float pan = 0)
    {
        if (s.Frames == 0) return;
        var p = c.P;
        double from = double.NaN;
        if (c.Mono)
        {
            Voice? held = null;
            for (int i = 0; i < Pool; i++)
            {
                var v = voices[i];
                if (!v.Active || v.Fading) continue;
                if (p.Legato && v.AmpStage != Stage.Release && t == 0) held = v;
                else v.Fading = true;
            }
            if (held != null)
            {
                held.Source = source;
                held.Key = key;
                held.OffIn = off;
                held.Velocity = velocity / 127f;
                held.Bend = bend;
                held.BendZero = held.Time;
                held.Pan = pan;
                Glide(c, held, note);
                lastNote = note;
                return;
            }
            if (c.GlideSteps > 0) from = lastNote;
        }
        else Limit(p.Voices - 1);
        lastNote = note;
        var free = Free();
        Start(c, free, s, source, key, note, velocity, off, t, from, bend, pan);
    }

    void Glide(Compiled c, Voice v, double note)
    {
        if (c.GlideSteps <= 0)
        {
            v.Note = note;
            v.GlideLeft = 0;
            return;
        }
        v.GlideTo = note;
        v.GlideLeft = c.GlideSteps;
        v.GlideStep = (note - v.Note) / c.GlideSteps;
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

    void Start(Compiled c, Voice v, SamplerSound s, byte source, int key, double note, int velocity, long off, long t, double glideFrom, NoteBend? bend, float pan)
    {
        var p = c.P;
        double start = Math.Clamp(p.Start, 0, 1) * s.Frames, end = Math.Clamp(p.End, 0, 1) * s.Frames;
        if (end <= start) return;
        var loop = p.OneShot ? SamplerLoop.Off : s.Loop;
        if (loop != SamplerLoop.Off && start >= s.LoopEnd) start = s.LoopStart;
        v.Sound = s;
        v.Source = source;
        v.Key = key;
        v.Order = ++order;
        v.OffIn = off;
        v.Loop = loop;
        v.End = end;
        v.Pos = start;
        v.Direction = 1;
        v.Note = double.IsNaN(glideFrom) ? note : glideFrom;
        v.GlideLeft = 0;
        if (!double.IsNaN(glideFrom)) Glide(c, v, note);
        v.Bend = bend;
        v.BendZero = 0;
        v.Pan = pan;
        v.Velocity = velocity / 127f;
        v.VelGain = (float)(1 - p.VelAmp + p.VelAmp * v.Velocity * v.Velocity);
        v.Fading = false;
        v.FadeGain = 1;
        v.Time = 0;
        v.Ctrl = 0;
        v.Level = 0;
        v.Scale = 1;
        v.LastSemis = double.NaN;
        v.LfoPhase = 0;
        v.Seed = (uint)(v.Order * 2654435761L) | 1;
        v.Random = 0;
        v.S1L = v.S2L = v.S3L = v.S4L = v.S1R = v.S2R = v.S3R = v.S4R = 0;
        v.Edge = 0;
        v.EdgeFrames = t > 0 ? ChaseFrames : start > 0 ? EdgeFrames : 0;
        v.AmpStage = c.Amp.AttackFrames < 1 && p.Amp.Sustain >= 1 ? Stage.Sustain : Stage.Attack;
        v.AmpLevel = c.Amp.AttackFrames < 1 ? 1 : 0;
        if (v.AmpStage == Stage.Attack && c.Amp.AttackFrames < 1) v.AmpStage = Stage.Decay;
        v.FilterStage = Stage.Attack;
        v.FilterLevel = 0;
        v.PitchLevel = 1;
        if (t > 0) Forward(c, v, t);
        if (v.Pos >= v.End && v.Loop == SamplerLoop.Off) return;
        v.GainL = v.TargetL = 0;
        v.GainR = v.TargetR = 0;
        Control(c, v, now);
        v.GainL = v.TargetL;
        v.GainR = v.TargetR;
        v.StepL = v.StepR = 0;
        v.Ctrl = ControlFrames;
        v.Active = true;
    }

    /// <summary>Moves a voice <paramref name="t"/> frames on, as if it had been playing: a note picked up in its middle.</summary>
    static void Forward(Compiled c, Voice v, long t)
    {
        var p = c.P;
        double ratio = Math.Pow(2, ((p.KeyTrack ? v.Note - p.Root : 0) + p.Tune) / 12);
        double pos = v.Pos + ratio * (p.KeyTrack && v.Bend != null ? v.Bend.Frames(t) : t);
        if (v.Loop != SamplerLoop.Off && pos >= v.Sound.LoopEnd)
        {
            double len = v.Sound.LoopEnd - v.Sound.LoopStart, past = pos - v.Sound.LoopStart;
            if (v.Loop == SamplerLoop.Forward) pos = v.Sound.LoopStart + past % len;
            else
            {
                double fold = past % (2 * len);
                v.Direction = fold < len ? 1 : -1;
                pos = v.Sound.LoopStart + (fold < len ? fold : 2 * len - fold);
            }
        }
        v.Pos = pos;
        v.Time = t;
        (v.AmpLevel, v.AmpStage) = c.Amp.At(t);
        (v.FilterLevel, v.FilterStage) = c.Filter.At(t / (double)ControlFrames);
        v.PitchLevel = MathF.Pow(c.PitchCoef, t / (float)ControlFrames);
        v.LfoPhase = c.LfoStep * t / ControlFrames % 1;
    }

    /// <summary>Everything a voice works out every <see cref="ControlFrames"/>, at <paramref name="frame"/> of the song: pitch, the level it heads to, the filter.</summary>
    void Control(Compiled c, Voice v, long frame)
    {
        var p = c.P;
        double cutoff = c.Cutoff, gain = c.Gain, pan = p.Pan, filterEnv = p.FilterEnv, lfoPitch = p.LfoPitch, lfoFilter = p.LfoFilter, lfoAmp = p.LfoAmp;
        float damping = c.Damping;
        v.Drive = c.Drive;
        v.Makeup = c.DriveMakeup;
        if (lanes is { } auto)
        {
            double sec = frame / (double)rate;
            cutoff = Math.Clamp(auto.At(SamplerKnob.Cutoff, sec, cutoff), LowCutoff, TopCutoff * rate);
            damping = DampingOf(auto.At(SamplerKnob.Resonance, sec, p.Resonance));
            double drive = auto.At(SamplerKnob.Drive, sec, p.Drive);
            v.Drive = DriveOf(drive);
            v.Makeup = MakeupOf(drive);
            filterEnv = auto.At(SamplerKnob.FilterEnv, sec, filterEnv);
            gain = GainOf(auto.At(SamplerKnob.Gain, sec, p.GainDb), p);
            pan = auto.At(SamplerKnob.Pan, sec, pan);
            lfoPitch = auto.At(SamplerKnob.LfoPitch, sec, lfoPitch);
            lfoFilter = auto.At(SamplerKnob.LfoFilter, sec, lfoFilter);
            lfoAmp = auto.At(SamplerKnob.LfoAmp, sec, lfoAmp);
        }
        if (v.GlideLeft > 0)
        {
            v.Note = --v.GlideLeft == 0 ? v.GlideTo : v.Note + v.GlideStep;
        }
        // the LFO: sine, triangle, saw and square from its phase, the random one a new value each lap
        double ph = v.LfoPhase;
        double lfo = p.LfoShape switch
        {
            SamplerLfoShape.Triangle => ph < 0.5 ? 4 * ph - 1 : 3 - 4 * ph,
            SamplerLfoShape.Saw => 2 * ph - 1,
            SamplerLfoShape.Square => ph < 0.5 ? 1 : -1,
            SamplerLfoShape.Random => v.Random,
            _ => Math.Sin(2 * Math.PI * ph),
        };
        ph += c.LfoStep;
        if (ph >= 1)
        {
            ph -= Math.Floor(ph);
            uint x = v.Seed;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            v.Seed = x;
            v.Random = x / 2147483648.0 - 1;
        }
        v.LfoPhase = ph;
        if (p.LfoDelay > 0) lfo *= Math.Min(1, v.Time / (p.LfoDelay * rate));

        double fromRoot = v.Note + (v.Bend?.At(v.Time - v.BendZero) ?? 0) - p.Root;
        double semis = (p.KeyTrack ? fromRoot : 0) + p.Tune + p.PitchEnv * v.PitchLevel + lfoPitch * lfo;
        v.PitchLevel *= c.PitchCoef;
        if (semis != v.LastSemis)
        {
            v.LastSemis = semis;
            v.Step = Math.Pow(2, semis / 12);
            // a level up from a little above the root, not from the root: a tone or two up keeps its top end
            v.Level = Math.Clamp((int)Math.Ceiling(semis / 12 - 0.5), 0, SamplerSound.Levels - 1);
            v.Scale = 1.0 / (1 << v.Level);
        }

        float amp = (float)(lfoAmp > 0 ? 1 - lfoAmp * (0.5 + 0.5 * lfo) : 1) * v.VelGain;
        pan = Math.Clamp(pan + v.Pan, -1, 1);
        v.TargetL = (float)(gain * Math.Min(1, 1 - pan)) * amp;
        v.TargetR = (float)(gain * Math.Min(1, 1 + pan)) * amp;
        v.StepL = (v.TargetL - v.GainL) / ControlFrames;
        v.StepR = (v.TargetR - v.GainR) / ControlFrames;

        v.Filtering = p.Filter != SamplerFilter.Off;
        v.FilterLevel = Next(ref v.FilterStage, v.FilterLevel, c.Filter, p.OneShot);
        if (!v.Filtering) return;
        double octaves = p.FilterKey * fromRoot / 12 + p.FilterVel * SamplerPatch.VelOctaves * (v.Velocity - 1)
            + filterEnv * SamplerPatch.EnvOctaves * v.FilterLevel + lfoFilter * lfo;
        double hz = Math.Clamp(cutoff * Math.Pow(2, octaves), LowCutoff, TopCutoff * rate);
        float g = (float)Math.Tan(Math.PI * hz / rate), k = damping;
        v.A1 = 1 / (1 + g * (g + k));
        v.A2 = g * v.A1;
        v.A3 = g * v.A2;
        v.Damp = k;
        v.Damp2 = Math.Max(k, SecondDamping);
        v.B1 = 1 / (1 + g * (g + v.Damp2));
        v.B2 = g * v.B1;
        v.B3 = g * v.B2;
        // the state of a silent filter must not sink into denormals
        if (Math.Abs(v.S1L) + Math.Abs(v.S2L) + Math.Abs(v.S1R) + Math.Abs(v.S2R) < 1e-20f) v.S1L = v.S2L = v.S1R = v.S2R = 0;
    }

    /// <summary>One step of an envelope. A one shot never releases: its note has no end, so its sustain stays until the sound does.</summary>
    static float Next(ref Stage stage, float level, in Rates e, bool oneShot)
    {
        switch (stage)
        {
            case Stage.Attack:
                level += e.AttackStep;
                if (level >= 1)
                {
                    level = 1;
                    stage = Stage.Decay;
                }
                break;
            case Stage.Decay:
                level = e.Sustain + (level - e.Sustain) * e.DecayCoef;
                if (Math.Abs(level - e.Sustain) < Silent)
                {
                    level = e.Sustain;
                    stage = Stage.Sustain;
                }
                break;
            case Stage.Sustain:
                level = e.Sustain;
                if (oneShot && level < Silent) stage = Stage.Done;
                break;
            case Stage.Release:
                level *= e.ReleaseCoef;
                if (level < Silent)
                {
                    level = 0;
                    stage = Stage.Done;
                }
                break;
        }
        return level;
    }

    void RenderVoices(Compiled c, int at, int n, Span<float> l, Span<float> r)
    {
        if (n <= 0) return;
        var ol = l.Slice(at, n);
        var or = r.Slice(at, n);
        for (int i = 0; i < Pool; i++)
            if (voices[i].Active) Render(c, voices[i], ol, or, n);
    }

    /// <summary>A voice's next frames, added to the output. Its state moves frame by frame, so block sizes don't matter.</summary>
    void Render(Compiled c, Voice v, Span<float> ol, Span<float> or, int n)
    {
        var s = v.Sound;
        var table = SamplerKernel.Table;
        var filter = c.P.Filter;
        bool oneShot = c.P.OneShot;
        long frame = now;
        const int Taps = SamplerKernel.Taps, Phases = SamplerKernel.Phases, Before = SamplerKernel.Before;
        for (int i = 0; i < n; i++)
        {
            if (v.Ctrl <= 0)
            {
                Control(c, v, frame + i);
                v.Ctrl = ControlFrames;
            }
            v.Ctrl--;
            if (v.OffIn >= 0 && v.OffIn-- == 0) Release(v);

            var ll = s.L[v.Level];
            var rr = s.R[v.Level];
            double q = v.Pos * v.Scale;
            int ix = (int)q;
            double frac = q - ix;
            int read = ix + SamplerSound.Pad - Before;
            if (read < 0 || read + Taps > ll.Length)
            {
                v.Active = false;
                return;
            }
            float a, b;
            if (frac == 0)
            {
                a = ll[read + Before];
                b = rr[read + Before];
            }
            else
            {
                double place = frac * Phases;
                int phase = (int)place;
                float mix = (float)(place - phase);
                int k0 = phase * Taps, k1 = k0 + Taps;
                a = 0;
                b = 0;
                for (int j = 0; j < Taps; j++)
                {
                    float w = table[k0 + j] + mix * (table[k1 + j] - table[k0 + j]);
                    a += w * ll[read + j];
                    b += w * rr[read + j];
                }
            }

            v.Pos += v.Direction * v.Step;
            float edge = 1;
            switch (v.Loop)
            {
                case SamplerLoop.Off:
                    double left = (v.End - v.Pos) / v.Step;
                    if (left <= 0)
                    {
                        v.Active = false;
                        return;
                    }
                    if (left < EdgeFrames) edge = (float)(left / EdgeFrames);
                    break;
                case SamplerLoop.Forward:
                    if (v.Pos >= s.LoopEnd) v.Pos -= s.LoopEnd - s.LoopStart;
                    break;
                default:
                    if (v.Direction > 0 && v.Pos >= s.LoopEnd)
                    {
                        v.Pos = 2.0 * s.LoopEnd - v.Pos;
                        v.Direction = -1;
                    }
                    else if (v.Direction < 0 && v.Pos <= s.LoopStart)
                    {
                        v.Pos = 2.0 * s.LoopStart - v.Pos;
                        v.Direction = 1;
                    }
                    break;
            }

            if (v.Filtering)
            {
                if (v.Drive > 1)
                {
                    a = Clip(a * v.Drive) * v.Makeup;
                    b = Clip(b * v.Drive) * v.Makeup;
                }
                a = Filter(v, filter, a, ref v.S1L, ref v.S2L, ref v.S3L, ref v.S4L);
                b = Filter(v, filter, b, ref v.S1R, ref v.S2R, ref v.S3R, ref v.S4R);
            }

            if (v.EdgeFrames > 0 && v.Edge < v.EdgeFrames) edge *= ++v.Edge / (float)v.EdgeFrames;
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

            v.AmpLevel = Next(ref v.AmpStage, v.AmpLevel, c.Amp, oneShot);
            if (v.AmpStage == Stage.Done)
            {
                v.Active = false;
                return;
            }
            v.Time++;
        }
    }

    /// <summary>A soft ceiling for the drive, smooth and cheap: tanh's shape up to where it stops at 1.</summary>
    static float Clip(float x)
    {
        if (x >= 3) return 1;
        if (x <= -3) return -1;
        return x * (27 + x * x) / (27 + 9 * x * x);
    }

    /// <summary>A state-variable filter (trapezoidal, so a fast sweep stays stable); the 24 dB types are two in a row.</summary>
    static float Filter(Voice v, SamplerFilter type, float x, ref float s1, ref float s2, ref float s3, ref float s4)
    {
        float a1 = v.A1, a2 = v.A2, a3 = v.A3, k = v.Damp;
        float v3 = x - s2, v1 = a1 * s1 + a2 * v3, v2 = s2 + a2 * s1 + a3 * v3;
        s1 = 2 * v1 - s1;
        s2 = 2 * v2 - s2;
        float low = v2, band = v1, high = x - k * v1 - v2;
        switch (type)
        {
            case SamplerFilter.Low12: return low;
            case SamplerFilter.High12: return high;
            case SamplerFilter.Band: return band;
            case SamplerFilter.Notch: return x - k * v1;
        }
        float y = type == SamplerFilter.Low24 ? low : high;
        v3 = y - s4;
        v1 = v.B1 * s3 + v.B2 * v3;
        v2 = s4 + v.B2 * s3 + v.B3 * v3;
        s3 = 2 * v1 - s3;
        s4 = 2 * v2 - s4;
        return type == SamplerFilter.Low24 ? v2 : y - v.Damp2 * v1 - v2;
    }
}
