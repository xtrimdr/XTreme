namespace XtremeDaw.Audio.Dsp;

/// <summary>
/// A note as the engine plays it: from frame <see cref="Start"/>, held <see cref="Length"/> frames, at <see cref="Hz"/>,
/// with its velocity's gain (the test tone), and as MIDI (<see cref="Pitch"/>, <see cref="Velocity"/>, the note-off's
/// <see cref="Release"/>, 0 = none) for an instrument plugin. The properties below are the test tone's expression, and
/// <see cref="Pan"/> and <see cref="Bend"/> the sampler's; all zero, it is a plain note.
/// </summary>
public readonly record struct NoteEvent(long Start, long Length, double Hz, float Gain, int Pitch = 60, int Velocity = 100, int Release = 0)
{
    /// <summary>-1 (left) to 1 (right), equal gain: left × (1 - pan), right × (1 + pan).</summary>
    public float Pan { get; init; }
    /// <summary>It starts this many semitones away from <see cref="Hz"/> and arrives there, straight in pitch, after <see cref="BendFrames"/>.</summary>
    public double BendSemis { get; init; }
    public long BendFrames { get; init; }
    /// <summary>The fundamental's phase at <see cref="Start"/>, in radians: a slide goes on from the note before.</summary>
    public double Phase { get; init; }
    /// <summary>How far into its envelope it starts: a slide has no attack of its own.</summary>
    public long EnvFrames { get; init; }
    /// <summary>Another event goes on from its last frame (<see cref="NoteSynth.Glide"/>): it has no release.</summary>
    public bool Tied { get; init; }
    /// <summary>Its slides and porta for what has a voice per note (<see cref="Sampler"/>); null = none. The test tone has its own events.</summary>
    public NoteBend? Bend { get; init; }
}

/// <summary>
/// What a note's pitch does over its life: semitones away from it in straight lines between points, holding the last.
/// Immutable and a function of the time since the note began, so a note picked up in its middle is where it would be.
/// Allocates nothing.
/// </summary>
public sealed class NoteBend
{
    /// <summary>ln(2) / 12: a semitone as an exponent.</summary>
    const double PerSemitone = 0.05776226504666211;
    readonly long[] at;
    readonly double[] semis;
    /// <summary>The furthest down it goes, 0 at most.</summary>
    public double Lowest { get; }

    /// <param name="at">Frames since the note began, in order, the first one 0.</param>
    /// <param name="semis">Semitones away at each of them.</param>
    public NoteBend(long[] at, double[] semis)
    {
        this.at = at;
        this.semis = semis;
        foreach (double s in semis) Lowest = Math.Min(Lowest, s);
    }

    /// <summary>Semitones away, <paramref name="t"/> frames into the note.</summary>
    public double At(long t)
    {
        int lo = 0, hi = at.Length;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (at[mid] <= t) lo = mid;
            else hi = mid;
        }
        if (lo + 1 >= at.Length) return semis[lo];
        return semis[lo] + (semis[lo + 1] - semis[lo]) * (t - at[lo]) / (at[lo + 1] - at[lo]);
    }

    /// <summary>
    /// How many frames of the note's plain speed it has run through after <paramref name="t"/>: the integral of
    /// 2^(semitones / 12), so a note picked up in its middle reads the sample where it would have got to.
    /// </summary>
    public double Frames(long t)
    {
        double run = 0;
        for (int k = 0; k < at.Length && at[k] < t; k++)
        {
            long end = k + 1 < at.Length ? Math.Min(t, at[k + 1]) : t;
            double from = semis[k], to = At(end), len = end - at[k];
            run += Math.Abs(to - from) < 1e-9 ? len * Math.Exp(PerSemitone * from)
                : len * (Math.Exp(PerSemitone * to) - Math.Exp(PerSemitone * from)) / (PerSemitone * (to - from));
        }
        return run;
    }
}

/// <summary>
/// A MIDI controller's new value at a frame of the song, for a plugin. <see cref="Controller"/> is 0..127 for a CC
/// (<see cref="Value"/> 0..1 of its 0..127) or <see cref="Bend"/> (0..1, <see cref="Centre"/> in the middle).
/// </summary>
public readonly record struct NoteControl(long Frame, int Controller, float Value)
{
    public const int ModX = 1, Pan = 10, ModY = 74, Bend = 128;
    /// <summary>How many controllers there can be: every CC and the bend.</summary>
    public const int Count = Bend + 1;
    public const float Centre = 0.5f;
    /// <summary>The semitones a full bend reaches: nothing tells a host how a plugin is set, and 2 is what they come with. Past it, a bend stops there.</summary>
    public const int BendRange = 2;
}

/// <summary>
/// A MIDI track's notes in frames, sorted by start (<c>ChannelDsp.Notes</c> builds it from the project). Never changed
/// once built: the UI swaps a new one in whole and the audio thread reads the reference once per block.
/// <see cref="Notes"/> is what plugins and the sampler play, <see cref="Controls"/> goes along with them to a plugin, and
/// <see cref="Tone"/> is what <see cref="NoteSynth"/> renders: the same notes unless a slide, which for a plugin is a
/// held note and a bend, for the sampler that held note's <see cref="NoteEvent.Bend"/> and for the test tone a chain of
/// tied events.
/// </summary>
public sealed class NoteSequence
{
    public readonly NoteEvent[] Notes;
    /// <summary>What the test tone plays, sorted by start.</summary>
    public readonly NoteEvent[] Tone;
    /// <summary>Sorted by frame; at one frame, in the order they came.</summary>
    public readonly NoteControl[] Controls;
    /// <summary>How many different controllers <see cref="Controls"/> has.</summary>
    public readonly int Controllers;
    /// <summary>The longest a note sounds, release included: how far back a block looks for notes still ringing.</summary>
    public readonly long MaxSpan;
    /// <summary>The frame after the last note's release.</summary>
    public readonly long End;

    public NoteSequence(NoteEvent[] notes, int sampleRate, NoteEvent[]? tone = null, NoteControl[]? controls = null)
    {
        Array.Sort(notes, (a, b) => a.Start.CompareTo(b.Start));
        Notes = notes;
        if (tone != null) Array.Sort(tone, (a, b) => a.Start.CompareTo(b.Start));
        Tone = tone ?? notes;
        Controls = controls is { Length: > 0 } ? [.. controls.OrderBy(c => c.Frame)] : []; // stable, unlike Array.Sort
        Controllers = Controls.Select(c => c.Controller).Distinct().Count();
        long release = NoteSynth.ReleaseFrames(sampleRate);
        foreach (var list in (NoteEvent[][])[notes, Tone])
            foreach (var n in list)
            {
                MaxSpan = Math.Max(MaxSpan, n.Length + release);
                End = Math.Max(End, n.Start + n.Length + release);
            }
    }

    /// <summary>The first control at or after <paramref name="frame"/>.</summary>
    public int ControlAt(long frame)
    {
        var all = Controls;
        int lo = 0, hi = all.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (all[mid].Frame < frame) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// The last value of each controller before <paramref name="frame"/>, where a jump lands: what a plugin should be
    /// left at. Fills <paramref name="into"/> (at least <see cref="NoteControl.Count"/> long) and says how many. Allocates nothing.
    /// </summary>
    public int LastValues(long frame, Span<NoteControl> into)
    {
        Span<bool> seen = stackalloc bool[NoteControl.Count];
        int found = 0;
        for (int k = ControlAt(frame) - 1; k >= 0 && found < Controllers; k--)
        {
            var c = Controls[k];
            if (seen[c.Controller]) continue;
            seen[c.Controller] = true;
            into[found++] = c;
        }
        return found;
    }
}

/// <summary>
/// The test tone a MIDI track plays until it has an instrument (stage 1 of the MIDI editor; the sampler and VST3
/// come next): a soft electric-piano-like tone, three harmonics with a short attack, a decay to a held level and a
/// release after the note ends. Every sample is a function of the time since its note began, with no state, so the
/// live engine (<c>LiveTrack</c>) and the export (<see cref="MixRenderer"/>) render it in any block size and from any
/// position (a seek, a loop, a crossfade) and get the same floats. That goes for a glide too: its phase is the closed
/// form of the frequency's integral, and a slid note carries the phase and the envelope of the one it goes on from
/// (<see cref="Glide"/>), so the join has no attack and no step.
/// </summary>
public static class NoteSynth
{
    /// <summary>Attack and release, short enough to feel immediate, long enough not to click.</summary>
    public const double AttackSec = 0.004, ReleaseSec = 0.12;
    /// <summary>How fast the tone falls from the attack to <see cref="Sustain"/> (seconds to fall by e).</summary>
    const double DecaySec = 0.45;
    /// <summary>The level a held note settles at, relative to its attack.</summary>
    const double Sustain = 0.35;
    /// <summary>The level of a note at full velocity (−12 dB): a chord of four stays under 0 dBFS.</summary>
    const double Level = 0.25;
    /// <summary>The second and third harmonics, relative to the fundamental: a little body without buzz.</summary>
    const double H2 = 0.3, H3 = 0.12;
    const double Norm = 1 / (1 + H2 + H3);

    public static long ReleaseFrames(int sampleRate) => (long)Math.Round(ReleaseSec * sampleRate);

    /// <summary>The frequency of a MIDI pitch (69 = A4 = 440 Hz); fractions are cents (a track's Transponer).</summary>
    public static double Hz(double pitch) => 440 * Math.Pow(2, (pitch - 69) / 12.0);

    /// <summary>The gain of a velocity (1..127): squared, as most instruments respond.</summary>
    public static float Gain(int velocity) => (float)(Level * Math.Pow(Math.Clamp(velocity, 1, 127) / 127.0, 2));

    /// <summary>
    /// Adds the notes sounding in frames <c>[pos .. pos + n)</c> to <paramref name="l"/> and <paramref name="r"/>
    /// (times <paramref name="weight"/> when given: a seek's crossfade). Zero allocations; safe on the audio thread.
    /// </summary>
    public static void Render(NoteSequence seq, long pos, int n, int sampleRate, Span<float> l, Span<float> r, ReadOnlySpan<float> weight = default)
    {
        var notes = seq.Tone;
        if (notes.Length == 0 || pos >= seq.End) return;
        long fullRelease = ReleaseFrames(sampleRate);
        // the first note that may still ring: notes start in order, none rings longer than MaxSpan
        int lo = 0, hi = notes.Length;
        long from = pos - seq.MaxSpan;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (notes[mid].Start < from) lo = mid + 1; else hi = mid;
        }
        double invSr = 1.0 / sampleRate;
        for (int k = lo; k < notes.Length; k++)
        {
            var note = notes[k];
            if (note.Start >= pos + n) break;
            long release = note.Tied ? 0 : fullRelease;
            long stop = note.Start + note.Length;
            if (stop + release <= pos) continue;
            int i0 = (int)Math.Max(0, note.Start - pos);
            int i1 = (int)Math.Min(n, stop + release - pos);
            double w = 2 * Math.PI * note.Hz * invSr;
            // the level the note had when it was let go, so the release starts from there
            double held = Envelope((note.Length + note.EnvFrames) * invSr);
            bool bent = Bent(note);
            float gl = 1 - note.Pan, gr = 1 + note.Pan;
            for (int i = i0; i < i1; i++)
            {
                long t = pos + i - note.Start;
                double env = t < note.Length ? Envelope((t + note.EnvFrames) * invSr) : held * (1 - (t - note.Length) / (double)release);
                double ph = note.Phase + w * (bent ? BentFrames(note, t) : t);
                double s = (Math.Sin(ph) + H2 * Math.Sin(2 * ph) + H3 * Math.Sin(3 * ph)) * Norm * env * note.Gain;
                float v = (float)s;
                if (!weight.IsEmpty) v *= weight[i];
                l[i] += v * gl;
                r[i] += v * gr;
            }
        }
    }

    /// <summary>Below this many semitones a glide is nothing: rounding, and <see cref="BentFrames"/> would divide by it.</summary>
    const double NoBend = 1e-6;

    static bool Bent(in NoteEvent n) => n.BendFrames > 0 && n.BendSemis != 0;

    /// <summary>
    /// How many frames of its plain <see cref="NoteEvent.Hz"/> a bent note has run through after <paramref name="t"/>:
    /// the integral of 2^(semitones left / 12), which falls straight to 0 over <see cref="NoteEvent.BendFrames"/>.
    /// </summary>
    static double BentFrames(in NoteEvent n, long t)
    {
        double f = n.BendFrames, a = n.BendSemis * Math.Log(2) / 12, part = Math.Min(t, n.BendFrames);
        return f / a * (Math.Exp(a) - Math.Exp(a * (1 - part / f))) + (t - part);
    }

    /// <summary>The pitch of a note <paramref name="t"/> frames after it starts, bend included.</summary>
    public static double PitchAt(in NoteEvent n, long t) =>
        69 + 12 * Math.Log2(n.Hz / 440) + (Bent(n) && t < n.BendFrames ? n.BendSemis * (1 - t / (double)n.BendFrames) : 0);

    /// <summary>
    /// What goes on from <paramref name="n"/> <paramref name="t"/> frames into it, with no new attack: same level, same
    /// pan, the phase and the envelope where it left them, and <paramref name="glide"/> frames to reach <paramref name="pitch"/>.
    /// <paramref name="n"/> has to be cut there and <see cref="NoteEvent.Tied"/>.
    /// </summary>
    public static NoteEvent Glide(in NoteEvent n, long t, long length, double pitch, long glide, int sampleRate)
    {
        double semis = PitchAt(n, t) - pitch;
        return n with
        {
            Start = n.Start + t, Length = length, Hz = Hz(pitch), Tied = false,
            BendSemis = Math.Abs(semis) < NoBend ? 0 : semis, BendFrames = glide,
            Phase = n.Phase + 2 * Math.PI * n.Hz / sampleRate * (Bent(n) ? BentFrames(n, t) : t),
            EnvFrames = n.EnvFrames + t,
        };
    }

    /// <summary>
    /// One note played live (a key held, then let go: <c>LiveNotes</c>), frames <c>[t .. t + n)</c> since it began, added
    /// to <paramref name="l"/> / <paramref name="r"/>. <paramref name="heldFrames"/> is how long the key was held, or -1
    /// while it still is. The same samples <see cref="Render"/> gives a note of that length. False once its release ended.
    /// </summary>
    public static bool RenderLive(double hz, float gain, long t, long heldFrames, int n, int sampleRate, Span<float> l, Span<float> r)
    {
        long release = ReleaseFrames(sampleRate);
        double invSr = 1.0 / sampleRate;
        double w = 2 * Math.PI * hz * invSr;
        double held = heldFrames < 0 ? 0 : Envelope(heldFrames * invSr);
        for (int i = 0; i < n; i++)
        {
            long tt = t + i;
            double env;
            if (heldFrames < 0 || tt < heldFrames) env = Envelope(tt * invSr);
            else if (tt < heldFrames + release) env = held * (1 - (tt - heldFrames) / (double)release);
            else return false;
            double ph = w * tt;
            float v = (float)((Math.Sin(ph) + H2 * Math.Sin(2 * ph) + H3 * Math.Sin(3 * ph)) * Norm * env * gain);
            l[i] += v;
            r[i] += v;
        }
        return heldFrames < 0 || t + n < heldFrames + release;
    }

    /// <summary>The level while the note is held: a linear attack, then an exponential fall to <see cref="Sustain"/>.</summary>
    static double Envelope(double t) => t < AttackSec ? t / AttackSec : Sustain + (1 - Sustain) * Math.Exp(-(t - AttackSec) / DecaySec);

    /// <summary>One note on its own (the piano roll's keys and new notes, <see cref="NotePreview"/>), mono: what <see cref="Render"/> plays for it. Fractions of <paramref name="pitch"/> are cents.</summary>
    public static float[] Audition(double pitch, int velocity, int sampleRate, double heldSec = 0.35)
    {
        long len = (long)Math.Round(heldSec * sampleRate);
        var seq = new NoteSequence([new NoteEvent(0, len, Hz(pitch), Gain(velocity))], sampleRate);
        var l = new float[seq.End];
        var r = new float[seq.End];
        Render(seq, 0, l.Length, sampleRate, l, r);
        return l;
    }
}
