using XtremeDaw.Audio.Midi;

namespace XtremeDaw.Audio.Dsp;

/// <summary>An envelope: attack, decay and release in seconds, the sustain as a level 0..1.</summary>
public readonly record struct SamplerEnvelope(double Attack, double Decay, double Sustain, double Release);

/// <summary>
/// What every native instrument of a MIDI track does with its notes, apart from how a voice sounds (<see cref="Sampler"/>,
/// <see cref="Granular"/>): the song's notes started on their exact frame, the ones the playhead lands in the middle of
/// picked up, a loop's turn, a jump's 5 ms fade, the MIDI keyboard and the keys of the app's windows, and the tail that
/// keeps the track running stopped. One copy, so the two instruments behave the same everywhere they play: live and in
/// the export (<see cref="MixRenderer"/>).
/// <para>
/// Audio thread only, except what says otherwise: nothing allocates. <see cref="Notes"/> is swapped whole from the UI
/// thread, and the window's keyboard goes through a lock-free ring.
/// </para>
/// </summary>
public abstract class NoteInstrument
{
    /// <summary>A voice taken away, or the notes of the song after a jump, fade over 5 ms.</summary>
    protected const int FadeFrames = 240;
    /// <summary>A note picked up in its middle comes in over 5 ms.</summary>
    protected const int ChaseFrames = 240;
    /// <summary>Below this (-80 dB) an envelope is over.</summary>
    protected const float Silent = 1e-4f;
    /// <summary>How long a track keeps running stopped after its last voice: a reverb slot's tail rings out.</summary>
    const int TailSec = 3;
    /// <summary>ln(1000): an exponential "over" in T takes T / this as its time constant.</summary>
    protected const double SixtyDb = 6.907755;

    /// <summary>What a voice belongs to: the song's notes go when the song stops or jumps, the others stay.</summary>
    protected const byte FromSong = 0, FromKeys = 1, FromWindow = 2;

    protected enum Stage : byte { Attack, Decay, Sustain, Release, Done }

    /// <summary>An envelope's numbers for one rate: per frame for the level, per control step for a filter.</summary>
    protected readonly struct Rates
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

    /// <summary>One step of an envelope. A one shot never releases: its note has no end, so its sustain stays until the sound does.</summary>
    protected static float Next(ref Stage stage, float level, in Rates e, bool oneShot)
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

    protected readonly int rate;
    readonly int tailFrames;
    volatile NoteSequence? notes;
    /// <summary>The frame of the song where the next voices start and the voices render on from.</summary>
    protected long now;
    NoteSequence? lastNotes;
    /// <summary>The next note of the song to start; -1 = it has to be looked for.</summary>
    int next = -1;
    long expected = -1;
    bool playing;
    int ring;
    int panic;

    // the window's keyboard and the piano roll's keys: a lock-free ring from the UI thread
    readonly (int Pitch, int Velocity, int Frames)[] inbox = new (int, int, int)[64];
    int inHead, inTail;
    /// <summary>A key held until <see cref="Lift"/>, in <see cref="inbox"/>; a lift has velocity 0.</summary>
    const int Held = -1;

    protected NoteInstrument(int sampleRate)
    {
        rate = sampleRate;
        tailFrames = TailSec * sampleRate;
    }

    /// <summary>The track's notes in frames, swapped whole by the UI.</summary>
    public NoteSequence? Notes { get => notes; set => notes = value; }

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

    /// <summary>Takes what the UI swapped in for this block (the patch, the lanes): the voices read only that until the next one.</summary>
    protected abstract void Prepare();
    /// <summary>True when a note can start: there is a sound.</summary>
    protected abstract bool CanPlay { get; }
    /// <summary>True when a note plays on past its end (a one shot): a jump picks it up even then.</summary>
    protected abstract bool Lasts { get; }
    /// <summary>A note starts.</summary>
    /// <param name="key">What ends it from the keyboard.</param>
    /// <param name="note">The pitch it sounds at; fractions are the Transponer's cents.</param>
    /// <param name="off">Frames until it ends, -1 = until told.</param>
    /// <param name="t">Frames it would have been sounding by now.</param>
    /// <param name="bend">Its slides and porta.</param>
    /// <param name="pan">Its own pan.</param>
    protected abstract void Begin(byte source, int key, double note, int velocity, long off, long t, NoteBend? bend = null, float pan = 0);
    /// <summary>The keyboard let go of <paramref name="key"/>: its voices from <paramref name="source"/> release.</summary>
    protected abstract void End(byte source, int key);
    /// <summary>The song's notes, or everyone's, go in 5 ms.</summary>
    protected abstract void FadeAll(bool everyone);
    /// <summary>The song's voices stop where they are, with no fade (<see cref="FadedOut"/>).</summary>
    protected abstract void DropSong();
    /// <summary>True while any voice sounds.</summary>
    protected abstract bool Sounding { get; }
    /// <summary>Adds the voices' next <paramref name="n"/> frames to the block from <paramref name="at"/>, starting at <see cref="now"/> of the song.</summary>
    protected abstract void RenderVoices(int at, int n, Span<float> l, Span<float> r);

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
        Prepare();
        now = pos;
        if (Interlocked.Exchange(ref panic, 0) != 0) FadeAll(true);
        if (pos != expected || !playing)
        {
            FadeAll(false);
            next = Find(seq, pos);
            Chase(seq, pos);
        }
        else if (next < 0) next = Find(seq, pos);
        playing = true;
        Inputs(keys);
        int done = 0;
        if (turnAt >= 0 && turnAt <= n)
        {
            Run(seq, pos, 0, turnAt, l, r);
            FadeAll(false);
            next = Find(seq, turnTo);
            // what sounds at the loop's start began before it: a long chord would stay silent from the second lap on
            now = turnTo;
            Chase(seq, turnTo);
            pos = turnTo - turnAt;
            done = turnAt;
        }
        Run(seq, pos, done, n, l, r);
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
        Prepare();
        now = frame;
        if (Interlocked.Exchange(ref panic, 0) != 0) FadeAll(true);
        if (playing)
        {
            playing = false;
            expected = -1;
            FadeAll(false);
        }
        Inputs(keys);
        l.Clear();
        r.Clear();
        if (Sounding)
        {
            RenderVoices(0, n, l, r);
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
        DropSong();
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
    void Chase(NoteSequence? seq, long pos)
    {
        if (seq == null || !CanPlay) return;
        var all = seq.Notes;
        for (int k = 0; k < all.Length && all[k].Start < pos; k++)
        {
            ref readonly var e = ref all[k];
            long t = pos - e.Start, left = e.Length - t;
            if (left <= 0 && !Lasts) continue;
            Begin(FromSong, e.Pitch, PitchOf(e.Hz), e.Velocity, left > 0 ? left : -1, t, e.Bend, e.Pan);
        }
    }

    protected static double PitchOf(double hz) => 69 + 12 * Math.Log2(hz / 440);

    /// <summary>Renders <c>[from .. to)</c> of the block, stopping at each note so that it starts on its exact frame.</summary>
    void Run(NoteSequence? seq, long pos, int from, int to, Span<float> l, Span<float> r)
    {
        int i = from;
        while (i < to)
        {
            int stop = to;
            if (seq != null)
            {
                var all = seq.Notes;
                bool can = CanPlay;
                now = pos + i;
                while (next < all.Length && all[next].Start <= pos + i)
                {
                    ref readonly var e = ref all[next];
                    if (can) Begin(FromSong, e.Pitch, PitchOf(e.Hz), e.Velocity, e.Length, 0, e.Bend, e.Pan);
                    next++;
                }
                if (next < all.Length) stop = (int)Math.Min(to, all[next].Start - pos);
            }
            now = pos + i;
            RenderVoices(i, stop - i, l, r);
            i = stop;
        }
    }

    /// <summary>The keys of a MIDI keyboard and what the UI asked for, at the start of the block.</summary>
    void Inputs(LiveNotes? keys)
    {
        bool can = CanPlay;
        if (keys != null)
        {
            double cents = keys.Transpose - Math.Round(keys.Transpose);
            foreach (ref readonly var e in keys.Events)
            {
                if (e.On) { if (can) Begin(FromKeys, e.Sounds, e.Sounds + cents, e.Velocity, -1, 0); }
                else End(FromKeys, e.Sounds);
            }
        }
        int tail = inTail;
        while (tail != Volatile.Read(ref inHead))
        {
            var (pitch, velocity, frames) = inbox[tail];
            if (velocity == 0) End(FromWindow, pitch);
            else if (can)
            {
                End(FromWindow, pitch);
                Begin(FromWindow, pitch, pitch, velocity, frames == Held ? -1 : frames, 0);
            }
            tail = (tail + 1) % inbox.Length;
        }
        Volatile.Write(ref inTail, tail);
    }
}
