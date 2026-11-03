using XtremeDaw.Audio.Dsp;
using XtremeDaw.Audio.Midi;

namespace XtremeDaw.Audio.Vst;

/// <summary>
/// A MIDI track's notes played by an instrument plugin (<see cref="VstBridge"/>), live. The plugin is heard one block
/// late plus its own latency (<see cref="VstBridge.Exchange"/>), so the notes go that much early: when the playhead is
/// at <c>pos</c>, the notes sent are the ones from <c>pos + block + latency</c>, and they sound right on the timeline
/// with nothing else delayed. A jump (a seek) or a stop turns every note off first; the notes between where it lands
/// and that lookahead (the first beat of the song, when it starts from 0) go right away, at the start of the first block,
/// late by the lookahead instead of lost; so do the ones that began less than a block before it and the ones the playhead
/// lands in the middle of, which start over from there (a long chord would stay silent until the next note otherwise).
/// <para>
/// A loop is not a jump: the lookahead knows it is coming and gets to B before the playhead does, so the end of the lap
/// and the start of the next one go in the same block, each on its frame, and the first beat of every lap sounds on its
/// sample. At the turn (<see cref="Turn"/>) only the notes that cross B end, and the keyboard's stay held. It needs a
/// lap longer than the lookahead and its block; a shorter one jumps. If the playhead doesn't turn after all (the loop
/// went off, it moved), its position gives it away and that counts as a jump.
/// </para>
/// <para>
/// While stopped (<see cref="Idle"/>) it keeps running, silent, so the piano roll's keys (<see cref="Audition"/>) and
/// what the plugin plays on its own (the keys in its window) sound through the instrument: its output opens with the
/// first one and closes at the next stop. Audio thread only, except
/// <see cref="Audition"/>, <see cref="Notes"/> and <see cref="Tempo"/>; no allocations, no locks.
/// </para>
/// <para>
/// The keys of a MIDI keyboard (<see cref="LiveNotes"/>) go at the start of the block they arrive in, playing or not;
/// after an "all notes off" (a jump, the song starting) the ones still held are sent again, so a chord held while the
/// song starts keeps sounding.
/// </para>
/// <para>
/// The notes' controllers (<see cref="NoteSequence.Controls"/>) go the same way, in their block; a jump first puts the
/// bend at the centre and then sends each controller's last value before where it lands, and a stop leaves the bend
/// centred for what is played next.
/// </para>
/// </summary>
public sealed class InstrumentPlayer(VstBridge bridge)
{
    public VstBridge Bridge { get; } = bridge;

    volatile NoteSequence? notes;
    /// <summary>The track's notes in frames, swapped whole by the UI.</summary>
    public NoteSequence? Notes { get => notes; set => notes = value; }
    /// <summary>The project's tempo, for the plugin's transport (arpeggiators, synced LFOs).</summary>
    public double Tempo { get; set; } = 120;

    volatile VstParamCurves? curves;
    /// <summary>The instrument's automated parameters, swapped whole by the UI. They go as early as the notes.</summary>
    public VstParamCurves? Curves { get => curves; set => curves = value; }

    readonly VstNote[] buf = new VstNote[VstBridge.MaxEvents];
    int count;
    /// <summary>The block that could not be sent: its notes go at the start of the next one.</summary>
    bool carried;
    /// <summary>Where the next block should start if nothing jumps; -1 = after a stop.</summary>
    long expected = -1;
    bool idle = true;
    /// <summary>The output while stopped: 0 (shut after a stop) to 1 (open since an audition).</summary>
    float gate;
    const float GateStep = 1f / 240; // 5 ms at 48 kHz
    /// <summary>What it plays after a stop is still the song's tail, until it goes quiet. It starts that way: what a
    /// just opened plugin plays is what <see cref="InstrumentWarmup"/>'s key left, not someone playing it.</summary>
    bool tail = true;
    /// <summary>Below this (-80 dB), it is not playing.</summary>
    const float Quiet = 1e-4f;
    int quietFor;
    /// <summary>How long it has to stay quiet for the tail to be over (500 ms at 48 kHz): a release can dip and come back.</summary>
    const int TailGap = 24000;

    // the piano roll's notes: a lock-free ring from the UI thread, and how long each key still has to sound
    readonly (int Pitch, int Velocity, int Frames)[] ring = new (int, int, int)[64];
    int ringHead, ringTail;
    readonly int[] offIn = new int[128];

    readonly NoteControl[] chased = new NoteControl[NoteControl.Count];
    /// <summary>The plugin may have the bend off centre: what the notes sent last.</summary>
    bool bent;

    int panic;
    /// <summary>Every note it holds goes off on the next block, whoever played it. Any thread.</summary>
    public void Panic() => Volatile.Write(ref panic, 1);

    /// <summary>Plays a key on its own for <paramref name="frames"/> (the piano roll). UI thread.</summary>
    public void Audition(int pitch, int velocity, int frames)
    {
        int head = ringHead, next = (head + 1) % ring.Length;
        if (next == Volatile.Read(ref ringTail)) return; // full: dropped
        ring[head] = (Math.Clamp(pitch, 0, 127), Math.Clamp(velocity, 1, 127), Math.Max(1, frames));
        Volatile.Write(ref ringHead, next);
    }

    /// <summary>
    /// While playing: adds the instrument's sound for frames <c>[pos .. pos + n)</c> to <paramref name="l"/> /
    /// <paramref name="r"/> and sends the notes that start or end a block and a latency later.
    /// <paramref name="loopEnd"/> above <paramref name="loopStart"/>: the loop the engine is turning on (0 = none).
    /// </summary>
    public void Play(long pos, int n, Span<float> l, Span<float> r, LiveNotes? keys = null, long loopStart = 0, long loopEnd = 0)
    {
        int flags = VstBridge.FlagPlaying;
        bool jumped = pos != expected || idle || Interlocked.Exchange(ref panic, 0) != 0;
        if (jumped)
        {
            flags |= VstBridge.FlagAllNotesOff;
            if (!carried) count = 0;
        }
        idle = false;
        long ahead = pos + n + Bridge.Latency, lap = loopEnd - loopStart;
        bool laps = pos < loopEnd && lap > ahead - pos + n;
        expected = laps && LoopSplice.Turned(pos, pos + n, loopStart, loopEnd, out long turned) ? turned : pos + n;
        BeginBlock();
        // past B the lookahead is already in the next lap; the block that reaches B has both
        long from = laps && ahead > loopEnd ? ahead - lap : ahead;
        int turn = laps && ahead <= loopEnd && ahead + n > loopEnd ? (int)(loopEnd - ahead) : -1;
        if (notes is { } seq)
        {
            if (jumped)
            {
                ChaseControls(seq, from, 0);
                // a seek to the very end of a lap lands with the lookahead already past B
                CatchUp(seq, from < pos ? loopStart : pos - n, from, n);
            }
            if (turn < 0)
            {
                AddControls(seq, from, from + n, 0);
                AddNotes(seq, from, from + n, 0);
            }
            else
            {
                AddControls(seq, from, loopEnd, 0);
                AddNotes(seq, from, loopEnd, 0);
                Turn(seq, loopStart, loopEnd, turn);
                AddControls(seq, loopStart, loopStart + n - turn, turn);
                AddNotes(seq, loopStart, loopStart + n - turn, turn);
            }
        }
        else if (jumped) Recentre(0);
        Auditions(n);
        Keys(keys, flags);
        if (curves is { } c)
        {
            if (jumped) c.Resend();
            c.Push(Bridge, from);
        }
        Send(n, from, flags, l, r);
    }

    readonly LiveNote[] held = new LiveNote[128];

    /// <summary>The keys played since the last block; after "all notes off", every key still sounding.</summary>
    bool Keys(LiveNotes? keys, int flags)
    {
        if (keys == null) return false;
        if ((flags & VstBridge.FlagAllNotesOff) != 0)
        {
            int k = keys.Sounding(held);
            for (int i = 0; i < k; i++) Add(0, VstNote.On, held[i].Sounds, held[i].Velocity);
        }
        else
        {
            var events = keys.Events;
            for (int i = 0; i < events.Length; i++)
                Add(0, events[i].On ? VstNote.On : VstNote.Off, events[i].Sounds, events[i].Velocity);
        }
        return keys.EventCount > 0 || keys.AnySounding;
    }

    /// <summary>
    /// After a jump: the notes that start between <paramref name="since"/> and the lookahead (they can't be early any
    /// more) and the ones still sounding past it (the playhead landed in their middle) go at the start of the block, with
    /// their own length when they also end before the lookahead; the end of the others comes in its own block. It looks
    /// a block back from the playhead: a loop too short to go ahead of lands a little past its start, and the notes right
    /// at the start would be lost.
    /// </summary>
    void CatchUp(NoteSequence seq, long since, long from, int n)
    {
        var all = seq.Notes;
        int lo = 0, hi = all.Length;
        long earliest = since - seq.MaxSpan;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (all[mid].Start < earliest) lo = mid + 1; else hi = mid;
        }
        for (int k = lo; k < all.Length && all[k].Start < from; k++)
        {
            var e = all[k];
            long end = e.Start + e.Length;
            if (e.Start < since && end < from) continue; // over already
            Add(0, VstNote.On, e.Pitch, e.Velocity);
            // a block can be a single frame (the end of a count-in): the host brings the offset back inside it
            if (end < from) Add((int)Math.Clamp(e.Length, 1, Math.Max(1, n - 1)), VstNote.Off, e.Pitch, e.Release);
        }
    }

    /// <summary>
    /// The lap turns at <paramref name="offset"/> of the block: what sounds at B ends, what sounds at A (it began before
    /// it) starts over, and the controllers go where they were at A. Nothing else stops, so nothing else is cut.
    /// </summary>
    void Turn(NoteSequence seq, long start, long end, int offset)
    {
        var all = seq.Notes;
        for (int k = FirstAround(seq, end); k < all.Length && all[k].Start < end; k++)
            if (all[k].Start + all[k].Length >= end) Add(offset, VstNote.Off, all[k].Pitch, all[k].Release);
        ChaseControls(seq, start, offset);
        for (int k = FirstAround(seq, start); k < all.Length && all[k].Start < start; k++)
            if (all[k].Start + all[k].Length > start) Add(offset, VstNote.On, all[k].Pitch, all[k].Velocity);
    }

    /// <summary>The first note that can still be sounding at <paramref name="frame"/>.</summary>
    static int FirstAround(NoteSequence seq, long frame)
    {
        var all = seq.Notes;
        int lo = 0, hi = all.Length;
        long earliest = frame - seq.MaxSpan;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (all[mid].Start < earliest) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>A jump: the bend goes back to the centre, then every controller to where it was just before <paramref name="landing"/>.</summary>
    void ChaseControls(NoteSequence seq, long landing, int offset)
    {
        if (seq.Controls.Length > 0) bent = true;
        Recentre(offset);
        int k = seq.LastValues(landing, chased);
        for (int i = 0; i < k; i++) AddControl(offset, chased[i]);
    }

    void Recentre(int offset)
    {
        if (bent) AddControl(offset, new NoteControl(0, NoteControl.Bend, NoteControl.Centre));
    }

    void AddControl(int offset, in NoteControl c)
    {
        if (count >= buf.Length) return;
        buf[count++] = new VstNote { Offset = offset, Type = VstNote.Control, Pitch = c.Controller, Velocity = c.Value };
        if (c.Controller == NoteControl.Bend) bent = c.Value != NoteControl.Centre;
    }

    /// <summary>The controls in <c>[from .. to)</c>, with <c>from</c> at <paramref name="at"/> of the block.</summary>
    void AddControls(NoteSequence seq, long from, long to, int at)
    {
        var all = seq.Controls;
        for (int k = seq.ControlAt(from); k < all.Length && all[k].Frame < to; k++) AddControl((int)(all[k].Frame - from) + at, all[k]);
    }

    /// <summary>
    /// While stopped: auditions, the keyboard, and what the plugin plays on its own (the keys in its window, a preset's
    /// preview). Puts the output in <paramref name="l"/> / <paramref name="r"/> while the gate is open; false when there
    /// is nothing to hear.
    /// </summary>
    public bool Idle(int n, Span<float> l, Span<float> r, LiveNotes? keys = null)
    {
        int flags = Interlocked.Exchange(ref panic, 0) != 0 ? VstBridge.FlagAllNotesOff : 0;
        if (!idle)
        {
            // just stopped: everything off, and the output shuts (the transport already faded it out)
            idle = true;
            gate = 0;
            tail = true;
            quietFor = 0;
            flags |= VstBridge.FlagAllNotesOff;
            if (!carried) count = 0;
        }
        expected = -1;
        BeginBlock();
        if ((flags & VstBridge.FlagAllNotesOff) != 0) Recentre(0);
        bool auditioned = Auditions(n);
        auditioned |= Keys(keys, flags);
        l.Clear();
        r.Clear();
        Send(n, 0, flags, l, r);
        if (!auditioned && gate <= 0)
        {
            // it keeps running so its tails and envelopes stay current, but the song's tail is not heard: once it has
            // gone quiet, the next sound can only be the plugin's own
            bool quiet = true;
            for (int i = 0; i < n && quiet; i++) quiet = Math.Abs(l[i]) < Quiet && Math.Abs(r[i]) < Quiet;
            quietFor = quiet ? quietFor + n : 0;
            if (quietFor >= TailGap) tail = false;
            if (quiet || tail) return false;
        }
        float g = gate;
        for (int i = 0; i < n; i++)
        {
            if (g < 1) g = Math.Min(1, g + GateStep);
            l[i] *= g;
            r[i] *= g;
        }
        gate = g;
        return true;
    }

    void BeginBlock()
    {
        // a block that could not go: its notes stay, at the start of this one
        if (!carried) count = 0;
        else for (int i = 0; i < count; i++) buf[i].Offset = 0;
    }

    void Send(int n, long time, int flags, Span<float> l, Span<float> r)
    {
        VstNote.Sort(buf.AsSpan(0, count));
        Span<float> none = default;
        carried = !Bridge.Exchange(buf.AsSpan(0, count), n, time, Tempo, flags, l.IsEmpty ? none : l, r.IsEmpty ? none : r);
        if (!carried) count = 0;
    }

    void Add(int offset, int type, int pitch, int velocity)
    {
        if (count >= buf.Length) return;
        buf[count++] = new VstNote { Offset = offset, Type = type, Pitch = pitch, Velocity = velocity / 127f };
    }

    /// <summary>Notes that start or end in <c>[from .. to)</c>, with <c>from</c> at <paramref name="at"/> of the block.</summary>
    void AddNotes(NoteSequence seq, long from, long to, int at)
    {
        var all = seq.Notes;
        for (int k = FirstAround(seq, from); k < all.Length && all[k].Start < to; k++)
        {
            var e = all[k];
            if (e.Start >= from) Add((int)(e.Start - from) + at, VstNote.On, e.Pitch, e.Velocity);
            long end = e.Start + e.Length;
            if (end >= from && end < to) Add((int)(end - from) + at, VstNote.Off, e.Pitch, e.Release);
        }
    }

    /// <summary>The piano roll's keys: new ones start now, held ones end when their time runs out. True if any sounds.</summary>
    bool Auditions(int n)
    {
        bool any = false;
        for (int p = 0; p < 128; p++)
        {
            if (offIn[p] <= 0) continue;
            any = true;
            if (offIn[p] <= n) { Add(offIn[p] - 1, VstNote.Off, p, 0); offIn[p] = 0; }
            else offIn[p] -= n;
        }
        int tail = ringTail;
        while (tail != Volatile.Read(ref ringHead))
        {
            var (pitch, velocity, frames) = ring[tail];
            if (offIn[pitch] > 0) Add(0, VstNote.Off, pitch, 0);
            Add(0, VstNote.On, pitch, velocity);
            offIn[pitch] = frames;
            tail = (tail + 1) % ring.Length;
            any = true;
        }
        Volatile.Write(ref ringTail, tail);
        return any;
    }
}

/// <summary>
/// Waits for an instrument to really sound. Some (sample libraries) finish opening with their samples still loading in
/// the background, some load them only while they are being processed, and until then they drop the notes they get, so
/// a held chord stays silent, or a whole export. This plays a key of the track at the speed of real time until the
/// plugin answers and clears what the key left. Before the audio thread or the export use the bridge: nobody hears it.
/// </summary>
public static class InstrumentWarmup
{
    /// <summary>The key to try on a track without notes.</summary>
    public const int DefaultPitch = 60;
    const int Block = 1024;
    /// <summary>Blocks between tries (about 200 ms): a key sent while it was loading is gone, so it goes again.</summary>
    const int RepeatBlocks = 9;
    /// <summary>What counts as sound: -80 dB.</summary>
    const float Heard = 1e-4f;
    /// <summary>What it runs on silence afterwards, for the release of the key.</summary>
    const double TailSec = 1;

    /// <summary>The key of <paramref name="notes"/> to try: the first one the track plays, so it is one that means something on this instrument.</summary>
    public static int Pitch(NoteSequence? notes) => notes is { Notes.Length: > 0 } seq ? seq.Notes[0].Pitch : DefaultPitch;

    /// <summary>True when it sounded before <paramref name="seconds"/> ran out. Blocks; throws if the plugin crashes.</summary>
    public static bool Run(VstBridge bridge, int pitch, double tempo, int sampleRate, double seconds)
    {
        var l = new float[Block];
        var r = new float[Block];
        var key = new VstNote[1];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long time = -1_000_000;
        bool heard = false;
        int blocks = (int)(seconds * sampleRate / Block);
        for (int k = 0; k < blocks && !heard; k++, time += Block)
        {
            int phase = k % RepeatBlocks, count = phase == 0 || phase == RepeatBlocks - 1 ? 1 : 0;
            key[0] = new VstNote { Type = phase == 0 ? VstNote.On : VstNote.Off, Pitch = pitch, Velocity = phase == 0 ? 0.8f : 0 };
            bridge.Render(key.AsSpan(0, count), Block, time, tempo, 0, l, r);
            for (int i = 0; i < Block && !heard; i++) heard = Math.Abs(l[i]) > Heard || Math.Abs(r[i]) > Heard;
            double ahead = (k + 1) * Block / (double)sampleRate - sw.Elapsed.TotalSeconds;
            if (ahead > 0 && !heard) Thread.Sleep(TimeSpan.FromSeconds(ahead));
        }
        bridge.Render([], Block, time, tempo, VstBridge.FlagAllNotesOff | VstBridge.FlagReset, l, r);
        for (int k = (int)(TailSec * sampleRate / Block); k > 0; k--) bridge.Render([], Block, time, tempo, 0, l, r);
        return heard;
    }
}

/// <summary>
/// The export's side of an instrument plugin: a fresh process with the track's saved state, rendering offline and
/// waiting for every block (<see cref="VstBridge.Render"/>), so it plays exactly the notes at their time. The plugin's
/// own latency is taken out by sending the notes that much early, as live.
/// </summary>
public sealed class InstrumentRender : IDisposable
{
    readonly VstBridge bridge;
    readonly NoteSequence? notes;
    readonly double tempo;
    readonly VstNote[] buf = new VstNote[VstBridge.MaxEvents];
    readonly NoteControl[] chased = new NoteControl[NoteControl.Count];
    readonly int sampleRate;
    bool started;

    /// <summary>The instrument's automated parameters.</summary>
    public VstParamCurves? Curves { get; init; }
    /// <summary>Its block with <see cref="Curves"/> (10.7 ms at 48 kHz, about how often they move live).</summary>
    const int AutoBlock = 512;

    /// <summary>How long the export waits for an instrument to sound (<see cref="InstrumentWarmup"/>).</summary>
    public const double WarmSec = 10;

    /// <param name="offline">Tell the plugin it renders offline (it may use its best quality); false = as live.</param>
    public InstrumentRender(VstInstance instrument, NoteSequence? notes, double tempo, int sampleRate, bool offline = true)
    {
        bridge = VstBridge.Start(instrument.Path, instrument.ClassId, instrument.Name, sampleRate, offline);
        if (instrument.State is { Length: > 0 } state && !bridge.SetState(state))
        {
            string name = instrument.Name;
            bridge.Dispose();
            throw new InvalidOperationException(Texts.T("err.vst.state", "{0} no aceptó su configuración guardada.", name));
        }
        this.notes = notes;
        this.tempo = tempo;
        this.sampleRate = sampleRate;
    }

    /// <summary>Waits for it to sound, for up to <paramref name="seconds"/> (<see cref="InstrumentWarmup"/>), before <see cref="Render"/>.</summary>
    public bool Warm(double seconds = WarmSec) => InstrumentWarmup.Run(bridge, InstrumentWarmup.Pitch(notes), tempo, sampleRate, seconds);

    /// <summary>Renders frames <c>[pos .. pos + n)</c> into <paramref name="l"/> / <paramref name="r"/>, in blocks of at most <see cref="VstBridge.MaxBlock"/>.</summary>
    public void Render(long pos, int n, Span<float> l, Span<float> r)
    {
        // lanes move between blocks: with any, the blocks are short enough for them to glide
        int block = Curves != null ? AutoBlock : VstBridge.MaxBlock;
        for (int off = 0; off < n; off += block)
        {
            int len = Math.Min(block, n - off);
            long from = pos + off + bridge.Latency;
            int count = 0;
            if (notes is { } seq)
            {
                var all = seq.Notes;
                // the first block also takes what starts before its plugin latency (the first beat, with a latent plugin)
                long first = started ? from : Math.Min(from, pos + off);
                // the render may start in the middle of the song: its controllers land where they were by then
                if (!started)
                {
                    int was = seq.LastValues(from, chased);
                    for (int k = 0; k < was && count < buf.Length; k++) buf[count++] = Control(0, chased[k]);
                }
                for (int k = seq.ControlAt(from); k < seq.Controls.Length && seq.Controls[k].Frame < from + len && count < buf.Length; k++)
                    buf[count++] = Control((int)(seq.Controls[k].Frame - from), seq.Controls[k]);
                for (int k = 0; k < all.Length && all[k].Start < from + len; k++)
                {
                    var e = all[k];
                    if (e.Start < from && e.Start >= first && count < buf.Length)
                        buf[count++] = new VstNote { Offset = 0, Type = VstNote.On, Pitch = e.Pitch, Velocity = e.Velocity / 127f };
                    if (e.Start >= from && count < buf.Length) buf[count++] = new VstNote { Offset = (int)(e.Start - from), Type = VstNote.On, Pitch = e.Pitch, Velocity = e.Velocity / 127f };
                    long end = e.Start + e.Length;
                    if (end >= from && end < from + len && count < buf.Length) buf[count++] = new VstNote { Offset = (int)(end - from), Type = VstNote.Off, Pitch = e.Pitch, Velocity = e.Release / 127f };
                }
                VstNote.Sort(buf.AsSpan(0, count));
            }
            int flags = VstBridge.FlagPlaying | (started ? 0 : VstBridge.FlagAllNotesOff);
            started = true;
            Curves?.Push(bridge, from);
            bridge.Render(buf.AsSpan(0, count), len, from, tempo, flags, l.Slice(off, len), r.Slice(off, len));
        }
    }

    static VstNote Control(int offset, in NoteControl c) => new() { Offset = offset, Type = VstNote.Control, Pitch = c.Controller, Velocity = c.Value };

    public void Dispose() => bridge.Dispose();
}

/// <summary>An instrument as the export needs it: which plugin (file and class) and its saved state (base64).</summary>
public sealed record VstInstance(string Path, string ClassId, string Name, string? State);
