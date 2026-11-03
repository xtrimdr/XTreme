using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.Audio.Midi;

/// <summary>A note starting or ending live: the pitch played and the one it sounds at (the track's Transponer, whole semitones).</summary>
public struct LiveNote
{
    public bool On;
    public byte Pitch;
    public byte Sounds;
    public byte Velocity;
}

/// <summary>
/// A live arpeggiator's settings, swapped whole by the UI: an index into <see cref="Patterns"/>, beats per step, octaves
/// and how much of a step each note lasts.
/// </summary>
public sealed record LiveArp(int Pattern, double Step, int Octaves, double Gate)
{
    public static readonly string[] Patterns = ["up", "down", "updown", "random"];
}

/// <summary>
/// The keys played live on one track: what is held, the sustain pedal (a key let go while it is down keeps sounding
/// until it comes up, done here because the plugin bridge only carries notes) and, for the test tone, the voices.
/// Every change becomes a <see cref="LiveNote"/> in <see cref="Events"/> for this block, read by the instrument plugin
/// and by the recorder, so a take keeps the notes as they sounded. Audio thread only (<see cref="Transpose"/> is set
/// by the UI); no allocations.
/// </summary>
public sealed class LiveNotes(int sampleRate)
{
    readonly LiveNote[] events = new LiveNote[256];
    int count;
    readonly bool[] down = new bool[128];
    readonly bool[] sounding = new bool[128];
    readonly byte[] soundsAs = new byte[128];
    readonly byte[] velocityOf = new byte[128];
    bool pedal;

    /// <summary>The track's Transponer, in semitones (fractions reach the test tone only, like the clips' notes).</summary>
    public double Transpose { get; set; }

    /// <summary>What the keys did since <see cref="BeginBlock"/>, in order.</summary>
    public ReadOnlySpan<LiveNote> Events => events.AsSpan(0, count);
    public int EventCount => count;

    /// <summary>Notes held now, by the hand or by the pedal.</summary>
    public bool AnySounding { get; private set; }

    struct Voice
    {
        public bool Active;
        public byte Pitch;
        public double Hz;
        public float Gain;
        public long T;
        public long Held;
    }

    /// <summary>Sixteen test-tone notes at once; one more takes the place of the oldest.</summary>
    readonly Voice[] voices = new Voice[16];
    /// <summary>How long the chain keeps running after the last voice ends: a reverb slot's tail rings out.</summary>
    long tail;
    readonly long tailFrames = 3L * sampleRate;

    /// <summary>The track has something to play live, or a tail to let ring: run it while the song is stopped.</summary>
    public bool Active => AnySounding || tail > 0 || count > 0;

    public void BeginBlock() => count = 0;

    /// <summary>The keyboard goes to the nearest note of this scale; null = as played.</summary>
    public MusicScale? Snap { get; set; }
    /// <summary>The arpeggiator; null = the keys play themselves. Set by the UI; a reference, so it swaps whole.</summary>
    public LiveArp? Arp { get; set; }
    /// <summary>The song's tempo, what the arpeggiator counts by. Set by the UI.</summary>
    public double Tempo { get; set; } = 120;

    /// <summary>What each key sent: its note-off ends that, whatever changed since.</summary>
    readonly byte[] snappedTo = new byte[128];
    // the arpeggiator's keys, in the order they came, and where it is
    readonly bool[] arpHeld = new bool[128];
    readonly byte[] arpVelocity = new byte[128];
    readonly byte[] arpPitches = new byte[256];
    bool arpRunning, arpFreeRun;
    uint arpStep, arpRandom = 0x9E3779B9;
    int arpLast = -1;
    long arpOffAt = -1, arpFree;

    public void Handle(in MidiEvent e)
    {
        switch (e.Kind)
        {
            case MidiKind.NoteOn:
                int pitch = e.Pitch & 0x7F, to = Snap is { } scale ? Math.Clamp(scale.Nearest(pitch), 0, 127) : pitch;
                snappedTo[pitch] = (byte)to;
                if (Arp != null) { arpHeld[to] = true; arpVelocity[to] = e.Velocity; }
                else Press(to, e.Velocity);
                break;
            case MidiKind.NoteOff:
                int off = snappedTo[e.Pitch & 0x7F];
                arpHeld[off] = false;
                Release(off);
                break;
            case MidiKind.Sustain: Pedal(e.Velocity >= 64); break;
            case MidiKind.AllOff:
                Array.Clear(arpHeld);
                arpRunning = false;
                AllOff();
                break;
        }
    }

    /// <summary>
    /// Runs the arpeggiator for this block: <paramref name="song"/> is where the song is, or -1 stopped (then it counts on its
    /// own). Steps fall on the song's grid and go at the start of the block they land in. Call after the keys of the block.
    /// </summary>
    public void RunArp(long song, int n, double framesPerBeat)
    {
        if (Arp is not { } arp)
        {
            if (arpRunning) StopArp();
            return;
        }
        bool any = false;
        for (int p = 0; p < 128 && !any; p++) any = arpHeld[p];
        if (!any)
        {
            if (arpRunning) StopArp();
            return;
        }
        long stepFrames = Math.Max(1, (long)Math.Round(arp.Step * framesPerBeat));
        long now = song >= 0 ? song : arpFree;
        arpFree = song >= 0 ? song + n : arpFree + n;
        if (!arpRunning || arpFreeRun != song < 0)
        {
            arpRunning = true;
            arpFreeRun = song < 0;
            arpStep = 0;
            arpLast = -1;
            NextArp(arp, now, stepFrames);
        }
        if (arpLast >= 0 && arpOffAt >= 0 && now + n > arpOffAt)
        {
            Release(arpLast);
            arpOffAt = -1;
        }
        long next = (now / stepFrames + 1) * stepFrames;
        if (next < now + n) NextArp(arp, next, stepFrames);
    }

    void NextArp(LiveArp arp, long at, long stepFrames)
    {
        int count = 0;
        for (int o = 0; o < arp.Octaves; o++)
            for (int p = 0; p < 128; p++)
                if (arpHeld[p] && p + 12 * o < 128 && count < arpPitches.Length) arpPitches[count++] = (byte)(p + 12 * o);
        if (count == 0) return;
        if (arpLast >= 0) Release(arpLast);
        uint step = arpStep++;
        int index;
        switch (arp.Pattern)
        {
            case 1: index = count - 1 - (int)(step % count); break;
            case 2:
                int lap = Math.Max(1, 2 * count - 2), at2 = (int)(step % lap);
                index = at2 < count ? at2 : lap - at2;
                break;
            case 3:
                arpRandom ^= arpRandom << 13;
                arpRandom ^= arpRandom >> 17;
                arpRandom ^= arpRandom << 5;
                index = (int)(arpRandom % count);
                break;
            default: index = (int)(step % count); break;
        }
        int pitch = arpPitches[index], velocity = 0;
        // its velocity is the key's that makes it, octaves down
        for (int o = 0; o < arp.Octaves && velocity == 0; o++)
            if (pitch - 12 * o >= 0 && arpHeld[pitch - 12 * o]) velocity = arpVelocity[pitch - 12 * o];
        arpLast = pitch;
        Press(pitch, velocity == 0 ? 100 : velocity);
        arpOffAt = at + (long)(stepFrames * arp.Gate);
    }

    void StopArp()
    {
        arpRunning = false;
        if (arpLast >= 0) Release(arpLast);
        arpLast = -1;
    }

    public void Press(int pitch, int velocity)
    {
        pitch &= 0x7F;
        velocity = Math.Clamp(velocity, 1, 127);
        if (sounding[pitch]) End(pitch); // the same key again: the note starts over
        int sounds = Math.Clamp(pitch + (int)Math.Round(Transpose), 0, 127);
        down[pitch] = sounding[pitch] = true;
        soundsAs[pitch] = (byte)sounds;
        velocityOf[pitch] = (byte)velocity;
        Emit(true, pitch, sounds, velocity);
        StartVoice(pitch, velocity);
        AnySounding = true;
    }

    public void Release(int pitch)
    {
        pitch &= 0x7F;
        down[pitch] = false;
        if (sounding[pitch] && !pedal) End(pitch);
    }

    public void Pedal(bool isDown)
    {
        pedal = isDown;
        if (isDown) return;
        for (int p = 0; p < 128; p++)
            if (sounding[p] && !down[p]) End(p);
    }

    /// <summary>Everything off at once: the pedal up and every note ended (the track stopped listening, a panic).</summary>
    public void AllOff()
    {
        pedal = false;
        for (int p = 0; p < 128; p++)
        {
            down[p] = false;
            if (sounding[p]) End(p);
        }
    }

    void End(int pitch)
    {
        sounding[pitch] = false;
        Emit(false, pitch, soundsAs[pitch], 0);
        for (int i = 0; i < voices.Length; i++)
            if (voices[i].Active && voices[i].Pitch == pitch && voices[i].Held < 0) voices[i].Held = Math.Max(1, voices[i].T);
        bool any = false;
        for (int p = 0; p < 128 && !any; p++) any = sounding[p];
        AnySounding = any;
    }

    void Emit(bool on, int pitch, int sounds, int velocity)
    {
        if (count >= events.Length) return;
        events[count++] = new LiveNote { On = on, Pitch = (byte)pitch, Sounds = (byte)sounds, Velocity = (byte)velocity };
    }

    /// <summary>The notes sounding now as note-ons (a plugin that was told "all notes off" gets them back).</summary>
    public int Sounding(Span<LiveNote> into)
    {
        int k = 0;
        for (int p = 0; p < 128 && k < into.Length; p++)
            if (sounding[p]) into[k++] = new LiveNote { On = true, Pitch = (byte)p, Sounds = soundsAs[p], Velocity = velocityOf[p] };
        return k;
    }

    void StartVoice(int pitch, int velocity)
    {
        int at = -1;
        long oldest = -1;
        for (int i = 0; i < voices.Length; i++)
        {
            if (!voices[i].Active) { at = i; break; }
            if (voices[i].T > oldest) { oldest = voices[i].T; at = i; }
        }
        voices[at] = new Voice
        {
            Active = true, Pitch = (byte)pitch, Hz = NoteSynth.Hz(pitch + Transpose), Gain = NoteSynth.Gain(velocity), T = 0, Held = -1,
        };
    }

    /// <summary>The test tone of the keys for the next <paramref name="n"/> frames, added to <paramref name="l"/> / <paramref name="r"/>.</summary>
    public void Render(int n, Span<float> l, Span<float> r)
    {
        bool any = false;
        for (int i = 0; i < voices.Length; i++)
        {
            ref var v = ref voices[i];
            if (!v.Active) continue;
            v.Active = NoteSynth.RenderLive(v.Hz, v.Gain, v.T, v.Held, n, sampleRate, l, r);
            v.T += n;
            any |= v.Active;
        }
        Tick(n, any);
    }

    /// <summary>Time passes without the test tone (an instrument plugin plays the keys): the voices end, the tail counts down.</summary>
    public void Skip(int n)
    {
        bool any = false;
        for (int i = 0; i < voices.Length; i++) voices[i].Active = false;
        Tick(n, any || AnySounding);
    }

    void Tick(int n, bool any)
    {
        if (any) tail = tailFrames;
        else if (tail > 0) tail = Math.Max(0, tail - n);
    }
}

/// <summary>
/// The notes a track records while the take runs: each <see cref="LiveNote"/> with its timeline frame, written by the
/// audio thread into an array made before the take (no allocations there), copied to disk meanwhile by
/// <see cref="MidiTakeWriter"/> and read by the UI once it ends.
/// </summary>
public sealed class MidiTake
{
    /// <summary>65536 starts and ends: a long take played very busy fits; past that, notes are dropped (and counted).</summary>
    public const int Capacity = 1 << 16;
    readonly (long Frame, LiveNote Note)[] items = new (long, LiveNote)[Capacity];
    int count;
    public int Count => Volatile.Read(ref count);
    public int Dropped { get; private set; }

    /// <summary>An item below <see cref="Count"/>: never changes once written, so any thread can read it (<see cref="MidiTakeWriter"/>).</summary>
    public (long Frame, LiveNote Note) this[int i] => items[i];

    /// <summary>Audio thread.</summary>
    public void Add(long frame, in LiveNote note)
    {
        int c = count;
        if (c >= items.Length) { Dropped++; return; }
        items[c] = (frame, note);
        Volatile.Write(ref count, c + 1);
    }

    /// <summary>
    /// The notes as start, length (frames, from the timeline's 0), played pitch and velocity, in order of their start;
    /// a note still held ends at <paramref name="endFrame"/>. Nothing before <paramref name="fromFrame"/>: a note played
    /// during the count-in starts there.
    /// </summary>
    public List<(long Start, long Length, int Pitch, int Velocity)> Notes(long fromFrame, long endFrame)
    {
        var list = new List<(long, long, int, int)>();
        var open = new (long Frame, int Velocity)?[128];
        int c = Count;
        for (int i = 0; i < c; i++)
        {
            var (frame, note) = items[i];
            frame = Math.Max(fromFrame, frame);
            int p = note.Pitch;
            if (open[p] is { } was)
            {
                list.Add((was.Frame, Math.Max(1, frame - was.Frame), p, was.Velocity));
                open[p] = null;
            }
            if (note.On) open[p] = (frame, note.Velocity);
        }
        for (int p = 0; p < 128; p++)
            if (open[p] is { } was && was.Frame < endFrame) list.Add((was.Frame, endFrame - was.Frame, p, was.Velocity));
        list.Sort((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item3.CompareTo(b.Item3));
        return list;
    }
}
