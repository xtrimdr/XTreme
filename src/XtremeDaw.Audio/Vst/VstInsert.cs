using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.Audio.Vst;

/// <summary>
/// A VST3 effect slot's settings as the engine takes them: which plugin (and its saved state, for the export), whether
/// it is on, and either the running process the window keeps for it (<see cref="Live"/>; null while it loads or if it
/// couldn't open) or <see cref="Offline"/>, for the export, where the slot opens its own.
/// </summary>
public sealed record VstSlotSpec(VstInstance Plugin, bool On)
{
    public VstBridge? Live { get; init; }
    public bool Offline { get; init; }
    /// <summary>Its automated parameters; null = none.</summary>
    public VstParamCurves? Curves { get; init; }
}

/// <summary>
/// Sound on its way out with a fixed delay, kept by the frame of the stream it belongs to: a block goes to its place
/// whenever it arrives, what is read and never came is silence, and what comes after its frame was read is dropped, so a
/// late block can never push the rest back. Audio thread; never allocates.
/// </summary>
public sealed class FrameQueue
{
    readonly float[] l, r;
    readonly int mask;

    /// <param name="size">A power of two: more than the delay and the blocks under way put together.</param>
    public FrameQueue(int size)
    {
        l = new float[size];
        r = new float[size];
        mask = size - 1;
    }

    /// <summary>The frame the next <see cref="Read"/> starts at.</summary>
    public long ReadAt { get; private set; }

    /// <summary>Starts empty, reading from <paramref name="frame"/>.</summary>
    public void Start(long frame)
    {
        Array.Clear(l);
        Array.Clear(r);
        ReadAt = frame;
    }

    /// <summary>Puts a block in its place, the one that starts at <paramref name="frame"/>.</summary>
    public void Write(long frame, ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        long end = ReadAt + l.Length;
        for (int i = (int)Math.Clamp(ReadAt - frame, 0, left.Length); i < left.Length && frame + i < end; i++)
        {
            int at = (int)(frame + i) & mask;
            l[at] = left[i];
            r[at] = right[i];
        }
    }

    /// <summary>The next frames into the block, leaving their place clean.</summary>
    public void Read(Span<float> left, Span<float> right)
    {
        for (int i = 0; i < left.Length; i++)
        {
            int at = (int)(ReadAt + i) & mask;
            left[i] = l[at];
            right[i] = r[at];
            l[at] = r[at] = 0;
        }
        ReadAt += left.Length;
    }

    /// <summary>Goes <paramref name="frames"/> back, into what is already clean: everything still to come is heard that much later.</summary>
    public void Hold(int frames) => ReadAt -= frames;
}

/// <summary>
/// The delay of a live slot: the queue of what comes back (<see cref="FrameQueue"/>) and the extra block it falls behind
/// when the plugin is late (0 or 1). Steady, for a take, it starts with that block already given: a late block lands in
/// its place and nothing moves, so the mix's delay is the one the take was placed with from its first sample to its last.
/// Apart from <see cref="VstInsert"/> because it needs no plugin: <c>comprobar-dsp</c> runs it with a scripted one.
/// </summary>
public sealed class LiveLag(int queueSize)
{
    /// <summary>100 ms at 48 kHz: late blocks right after a start don't count (its Reset, or its first blocks, can take long).</summary>
    const int SettleFrames = 4800;

    int settle;
    volatile int behind;

    public FrameQueue Queue { get; } = new(queueSize);

    /// <summary>Extra blocks of delay since the start: 0 or 1. The UI reads it for the PDC.</summary>
    public int Behind => behind;

    /// <summary>The queue starts over, one block behind (two when <paramref name="steady"/>): from here on, the output is its input that much later.</summary>
    public void Start(long time, int block, bool steady)
    {
        behind = steady ? 1 : 0;
        Queue.Start(time - (1 + behind) * block);
        settle = SettleFrames;
    }

    /// <summary><paramref name="frames"/> went through.</summary>
    public void Passed(int frames)
    {
        if (settle > 0) settle -= frames;
    }

    /// <summary>The block due now isn't back: the slot falls one block behind if it still can.</summary>
    public void Miss(int block)
    {
        if (behind > 0 || settle > 0) return;
        behind = 1;
        Queue.Hold(block);
    }
}

/// <summary>
/// A VST3 effect as one more insert slot (<see cref="ITrackInsert"/>), mixed with the built-in ones in any order.
/// <para>
/// Live, the audio thread never waits for the plugin's process (<see cref="VstBridge"/>): each block it takes what the
/// plugin made of the block before and sends this one. What comes back waits in a queue (<see cref="FrameQueue"/>)
/// that starts one driver block (<see cref="LiveBlock"/>) behind, so the slot's output is always its input exactly one
/// block plus the plugin's own latency later, whatever the size of each call (the first block of a recording after its
/// count-in is shorter): that is its <see cref="LatencySamples"/>, which the rest of the mix waits for (<see cref="MixLatency"/>).
/// </para>
/// <para>
/// A block the plugin didn't finish in time is one block of silence (counted in <see cref="Misses"/>) and the slot
/// falls one block further behind until the transport starts again: the late block is still heard, in the place the
/// extra block left for it, and the delay compensation moves with it. Dropping it, a plugin that is always a little
/// late would never be heard at all; with the extra block it plays whole, and a plugin that only spikes now and then
/// plays on through its spikes. It never goes further than that: past it, a block that comes too late is lost.
/// </para>
/// <para>
/// During a take (<see cref="Steady"/>) that block is given from the start: a take has one offset, so the delay it was
/// placed with can't move under it (<see cref="LiveLag"/>). The take is placed that much later and the backing track
/// comes a block later than in plain playback, which the player can't tell from a slightly bigger buffer.
/// </para>
/// <para>
/// Off, while it loads or after it crashed, the sound goes through untouched (delayed as much as the plugin would, so
/// switching never jumps in time) with a <see cref="WetFade"/> crossfade; off, the plugin keeps getting the sound, so
/// turning it on again is seamless. In the export (<see cref="VstSlotSpec.Offline"/>) it opens its own process with the
/// saved state and waits for every block: no extra block, only the plugin's latency.
/// </para>
/// </summary>
public sealed class VstInsert : ITrackInsert, IDisposable
{
    /// <summary>10 ms at 48 kHz: switching between the plugin and the untouched sound.</summary>
    const int WetFade = 480;
    const int MaxBlock = VstBridge.MaxBlock;
    /// <summary>The queue of what came back: its two blocks of delay, plus the two blocks the plugin may have under way.</summary>
    const int QueueSize = 4 * MaxBlock;
    /// <summary>Blocks that can be under way at once: the bridge has two slots.</summary>
    const int MaxSent = 2;
    static volatile int steady;
    /// <summary>A take is being recorded: every live slot keeps a fixed delay, with the extra block (<see cref="LiveLag"/>). Set by the engine before the slots start over.</summary>
    public static bool Steady { get => steady != 0; set => steady = value ? 1 : 0; }

    static volatile int liveBlock = 512;
    /// <summary>The driver's block (frames), set by the engine when it opens: live, an effect is heard this much late.</summary>
    public static int LiveBlock { get => liveBlock; set => liveBlock = Math.Clamp(value, 1, MaxBlock); }

    readonly int sampleRate;
    volatile VstBridge? bridge;
    VstBridge? own;
    volatile bool on = true;
    double tempo = 120;
    volatile int reset;
    volatile VstParamCurves? curves;

    public VstInsert(int sampleRate) => this.sampleRate = sampleRate;

    /// <summary>The timeline frame of the next block: where its parameter lanes are read.</summary>
    public long Frame { get; set; }

    public string Kind => TrackInserts.Vst;

    /// <summary>The process it plays through: the window's, or its own in the export; null while there is none.</summary>
    public VstBridge? Bridge => own ?? bridge;

    /// <summary>Blocks the plugin didn't finish in time (live), for the diagnostics.</summary>
    public int Misses;

    public int LatencySamples => own is { } o ? o.Latency : bridge is { } b ? (1 + (Steady ? 1 : lag.Behind)) * LiveBlock + b.Latency : 0;

    /// <summary>
    /// Takes the slot's settings (UI thread; the export's thread there). Offline, the first call opens the plugin with its
    /// saved state and waits until it is up: it throws if it can't.
    /// </summary>
    public void Set(VstSlotSpec spec, double bpm)
    {
        on = spec.On;
        tempo = bpm;
        curves = spec.Curves;
        if (spec.Offline)
        {
            if (own != null) return;
            var p = spec.Plugin;
            VstBridge b;
            try
            {
                b = VstBridge.Start(p.Path, p.ClassId, p.Name, sampleRate, offline: true);
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                throw new InvalidOperationException(Texts.T("err.vst.export", "No se pudo abrir {0} para exportar: {1}", p.Name, e.Message), e);
            }
            if (p.State is { Length: > 0 } state && !b.SetState(state))
            {
                b.Dispose();
                throw new InvalidOperationException(Texts.T("err.vst.state", "{0} no aceptó su configuración guardada.", p.Name));
            }
            own = b;
            dry.Set(b.Latency);
            return;
        }
        if (spec.Live is { } live) dry.Reserve(2 * MaxBlock + live.Latency);
        bridge = spec.Live;
    }

    // live, audio thread only
    VstBridge? seen;
    readonly LiveLag lag = new(QueueSize);
    int primed;
    /// <summary>Blocks on their way, oldest first: their number, and where they go in the queue.</summary>
    readonly long[] sentBlock = new long[MaxSent], sentAt = new long[MaxSent];
    int sentCount;
    /// <summary>The last block counted as a miss, so one that keeps the queue waiting counts once.</summary>
    long missed;
    /// <summary>Frames the plugin still has to get before what it puts out is its own (after it starts or comes back).</summary>
    int warm;
    float wet;
    long time;
    readonly float[] dryL = new float[MaxBlock], dryR = new float[MaxBlock];
    readonly float[] takeL = new float[MaxBlock], takeR = new float[MaxBlock];
    readonly LatencyDelay dry = new();

    public void Process(Span<float> left, Span<float> right)
    {
        if (own != null) Render(left, right);
        else Live(left, right);
    }

    void Live(Span<float> left, Span<float> right)
    {
        int m = Math.Min(left.Length, MaxBlock);
        var b = bridge;
        int block = liveBlock;
        if (b != seen)
        {
            // another process (it loaded, crashed or opened again): its blocks count from zero
            seen = b;
            StartOver(block);
            warm = b == null ? 0 : (1 + lag.Behind) * block + b.Latency;
        }
        else if (block != primed || Interlocked.Exchange(ref reset, 0) != 0) StartOver(block);
        if (b != null)
        {
            TakeFinished(b);
            if (sentCount > 0 && sentAt[0] < lag.Queue.ReadAt + m) Missed(block);
            curves?.Push(b, Frame);
            long sent = sentCount < MaxSent ? b.TrySend(left[..m], right[..m], m, time, tempo, VstBridge.FlagPlaying | flags) : -1;
            if (sent > 0)
            {
                sentBlock[sentCount] = sent;
                sentAt[sentCount++] = time;
                flags = 0;
                if (warm > 0) warm -= m;
            }
        }
        lag.Passed(m);
        time += m;

        // the untouched sound, as late as the plugin's
        dry.Target = b == null ? 0 : (1 + lag.Behind) * block + b.Latency;
        var dl = dryL.AsSpan(0, m);
        var dr = dryR.AsSpan(0, m);
        dl.Clear();
        dr.Clear();
        dry.Add(left[..m], right[..m], 1f, dl, dr);

        float target = on && b != null && b.Ready && warm <= 0 ? 1 : 0;
        lag.Queue.Read(left[..m], right[..m]);
        if (b == null)
        {
            left[..m].Clear();
            right[..m].Clear();
        }
        float w = wet, step = 1f / WetFade;
        if (w == target && w == 0)
        {
            dl.CopyTo(left);
            dr.CopyTo(right);
            return;
        }
        for (int i = 0; i < m; i++)
        {
            if (w != target) w = target > w ? Math.Min(target, w + step) : Math.Max(target, w - step);
            left[i] = dl[i] + (left[i] - dl[i]) * w;
            right[i] = dr[i] + (right[i] - dr[i]) * w;
        }
        wet = w;
    }

    int flags;

    /// <summary>The queue starts over one block behind: from here on, the output is its input that much later.</summary>
    void StartOver(int block)
    {
        lag.Start(time, block, Steady);
        sentCount = 0;
        primed = block;
        flags |= VstBridge.FlagReset;
    }

    /// <summary>What the plugin has finished, in order, to its place in the queue; what is already past is dropped there.</summary>
    void TakeFinished(VstBridge b)
    {
        while (sentCount > 0)
        {
            int got = b.TryTake(sentBlock[0], takeL, takeR);
            if (got < 0) return;
            lag.Queue.Write(sentAt[0], takeL.AsSpan(0, got), takeR.AsSpan(0, got));
            sentBlock[0] = sentBlock[1];
            sentAt[0] = sentAt[1];
            sentCount--;
        }
    }

    /// <summary>The block due now isn't back: it counts as a miss, and the slot falls one block behind if it still can.</summary>
    void Missed(int block)
    {
        if (sentBlock[0] == missed) return;
        missed = sentBlock[0];
        Misses++;
        lag.Miss(block);
    }

    /// <summary>The export: each block through the plugin, waiting for it; off, the untouched sound as late as the plugin's.</summary>
    void Render(Span<float> left, Span<float> right)
    {
        var b = own!;
        if (!on)
        {
            int m = Math.Min(left.Length, MaxBlock);
            var dl = dryL.AsSpan(0, m);
            var dr = dryR.AsSpan(0, m);
            dl.Clear();
            dr.Clear();
            dry.Add(left[..m], right[..m], 1f, dl, dr);
            dl.CopyTo(left);
            dr.CopyTo(right);
            return;
        }
        for (int off = 0; off < left.Length; off += MaxBlock)
        {
            int len = Math.Min(MaxBlock, left.Length - off);
            var l = left.Slice(off, len);
            var r = right.Slice(off, len);
            curves?.Push(b, Frame + off);
            b.Render([], len, time, tempo, VstBridge.FlagPlaying, l, r, l, r);
            time += len;
        }
    }

    /// <summary>The transport starts: the plugin forgets its tails and the queue its old blocks (audio thread).</summary>
    public void Reset()
    {
        reset = 1;
        dry.Reset();
        curves?.Resend();
    }

    public double TakeGrDb() => 0;

    /// <summary>Closes its own process (the export's); the window's belongs to the window.</summary>
    public void Dispose()
    {
        own?.Dispose();
        own = null;
    }
}
