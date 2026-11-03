using XtremeDaw.Audio.Dsp;
using XtremeDaw.Audio.Vst;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// The plugin delay compensation's rows of comprobar-dsp: <see cref="LatencyDelay"/> fed in blocks like the audio thread
/// (an exact delay, a change of delay that lands exact after its crossfade, a ring that grows without losing its place),
/// <see cref="MixLatency"/> on a small mix (a latent track, a latent bus, a send), and a VST3 slot with no process yet,
/// which has to let the sound through untouched. Then what is shown against what is heard (<see cref="HeardClock"/>, on
/// the transport's moves) and a take with a plugin that turns late (<see cref="LiveLag"/>, with a scripted plugin). The
/// plugin itself is tried with <c>vst efecto</c>.
/// </summary>
static class PdcChecks
{
    const int Block = 128;

    public static bool Check()
    {
        bool allOk = true;
        void Row(string name, string got, string want, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"{name,-36} {got,12} {want,10}  {(ok ? "igual" : "DISTINTO")}");
        }

        // 1. a ramp through 1000 frames of delay: every sample comes out exactly that much later, from the first one
        int n = 20000;
        var ramp = new float[n];
        for (int i = 0; i < n; i++) ramp[i] = i + 1;
        var d = new LatencyDelay();
        d.Set(1000);
        var got = Run(d, ramp, _ => { });
        double err = 0;
        for (int i = 0; i < n; i++) err = Math.Max(err, Math.Abs(got[i] - (i >= 1000 ? ramp[i - 1000] : 0)));
        Row("PDC: demora exacta (1000 muestras)", $"{err:0.##}", "0", err == 0);

        // 2. from 1000 to 300 while it runs: once its crossfade is over, exact again
        var d2 = new LatencyDelay();
        d2.Set(1000);
        int at = 8192;
        var moved = Run(d2, ramp, i => { if (i == at) d2.Set(300); });
        double err2 = 0;
        for (int i = at + Block + LatencyDelay.Fade; i < n; i++) err2 = Math.Max(err2, Math.Abs(moved[i] - ramp[i - 300]));
        Row("PDC: cambio de demora con fundido", $"{err2:0.##}", "0", err2 == 0);

        // 3. a demand past its ring (16384): a bigger one takes over with its history, and the new delay is exact
        int m = 90000;
        var longRamp = new float[m];
        for (int i = 0; i < m; i++) longRamp[i] = i + 1;
        var d3 = new LatencyDelay();
        d3.Set(100);
        int grow = 10240;
        var grown = Run(d3, longRamp, i => { if (i == grow) d3.Set(20000); });
        double err3 = 0;
        for (int i = grow + Block + LatencyDelay.Fade; i < m; i++)
            err3 = Math.Max(err3, Math.Abs(grown[i] - (i >= 20000 ? longRamp[i - 20000] : 0)));
        Row("PDC: el anillo crece sin perderse", $"{err3:0.##}", "0", err3 == 0);

        // 4. the plan: A (300 late) and C straight to the master, B into a bus that is 100 late, C sends to that bus too
        var plan = MixLatency.Compute(
            [new(300, -1, []), new(0, 0, []), new(0, -1, [0])],
            [new(100, -1, [])]);
        bool planOk = plan.Master == 300 && plan.TrackOut is [0, 0, 300] && plan.TrackSends[2] is [0] && plan.NodeOut is [200];
        Row("PDC: plan (pista, bus y envío)", $"{plan.Master} / {plan.TrackOut[2]} / {plan.NodeOut[0]}", "300/300/200", planOk);

        // 5. a VST3 slot whose plugin isn't up: the sound goes through untouched, and it adds no latency
        var vst = new VstInsert(48000);
        vst.Set(new VstSlotSpec(new VstInstance("x.vst3", "0", "Nada", null), true), 120);
        var l = new float[Block];
        var r = new float[Block];
        double err5 = 0;
        for (int b = 0; b < 20; b++)
        {
            for (int i = 0; i < Block; i++) { l[i] = MathF.Sin(b * Block + i); r[i] = -l[i]; }
            vst.Process(l, r);
            for (int i = 0; i < Block; i++) err5 = Math.Max(err5, Math.Abs(l[i] - MathF.Sin(b * Block + i)) + Math.Abs(r[i] + MathF.Sin(b * Block + i)));
        }
        Row("VST3 sin abrir: pasa el sonido", $"{err5:0.0e0}", "0", err5 == 0 && vst.LatencySamples == 0);

        // 6. a late block goes in its place in the queue; one too late is lost and the rest stay where they were
        var queue = new FrameQueue(1 << 12);
        float[] One(int block) => [.. Enumerable.Range(0, Block).Select(i => block + i / 1000f)];
        float[] gotL = new float[Block], gotR = new float[Block];
        queue.Start(-Block);
        var heardBlocks = new List<float[]>();
        void Next()
        {
            queue.Read(gotL, gotR);
            heardBlocks.Add((float[])gotL.Clone());
        }
        queue.Write(0, One(1), One(1));
        Next(); // the delay's block
        Next(); // block 1, on time
        queue.Hold(Block);
        Next(); // block 2 didn't come: the extra block
        queue.Write(Block, One(2), One(2));
        queue.Write(2 * Block, One(3), One(3));
        Next(); // block 2, late
        Next(); // block 3, one block later too
        Next(); // block 4 never comes in time
        queue.Write(3 * Block, One(4), One(4));
        queue.Write(4 * Block, One(5), One(5));
        Next(); // block 5, still where it was
        int[] want = [0, 1, 0, 2, 3, 0, 5];
        bool inPlace = heardBlocks.Count == want.Length;
        for (int k = 0; inPlace && k < want.Length; k++) inPlace = want[k] == 0 ? heardBlocks[k].All(x => x == 0) : heardBlocks[k].SequenceEqual(One(want[k]));
        Row("VST3: un bloque tarde entra en su lugar, uno perdido no corre el resto", inPlace ? "igual" : "distinto", "igual", inPlace);

        // 7. the model: a VST3 slot is saved, read back, and turned into the engine's spec offline (the export's)
        var ch = new Channel();
        var slot = ChannelInserts.AddVst(ch.Slots, new PluginRef { Path = @"C:\x\Amp.vst3", ClassId = "ABC", Name = "Amp", Vendor = "Yo", State = "no" });
        slot!.Vst!.Plugin.State = "estado";
        var back = System.Text.Json.JsonSerializer.Deserialize<Channel>(System.Text.Json.JsonSerializer.Serialize(ch, ProjectStore.Json), ProjectStore.Json)!;
        var spec = ChannelDsp.Specs(back, -24, 90).SingleOrDefault();
        bool modelOk = spec is { Kind: TrackInserts.Vst, Vst: { Offline: true, On: true, Plugin: { Name: "Amp", State: "estado" } }, Bpm: 90 }
            && ChannelInserts.Add(ch.Slots, InsertSlot.VstKind) == null
            && !new InsertSlot { Type = InsertSlot.VstKind }.Known;
        Row("VST3: slot guardado y leído", modelOk ? "bien" : "mal", "bien", modelOk);

        // 8. what is shown against what the master puts out: the sound itself, a ramp of timeline frames through the delay
        var run = new Transport(1000);
        run.Blocks(50);
        bool heldStart = run.Worst == 0 && run.Shown == run.Heard;
        Row("PDC: el cabezal espera el sonido al arrancar", $"{run.Worst}", "0", heldStart);

        var lap = new Transport(1000) { LoopA = 4800, LoopB = 12000 };
        lap.Seek(4800);
        lap.Blocks(400);
        bool lapOk = lap.Worst == 0 && lap.Turns >= 5 && lap.ShownMin >= 4800 && lap.ShownMax < 12000;
        Row("PDC: el cabezal termina la vuelta del bucle", $"{lap.Worst} ({lap.Turns})", "0", lapOk);

        var jump = new Transport(1000);
        jump.Blocks(100);
        jump.Seek(40000);
        int stepsBack = 0;
        long before = 40000;
        for (int b = 0; b < 50; b++)
        {
            jump.Blocks(1);
            if (jump.Shown < before) stepsBack++;
            before = jump.Shown;
        }
        bool jumpOk = stepsBack == 0 && jump.Worst == 0 && jump.Shown == jump.Heard;
        Row("PDC: un salto no retrocede", $"{stepsBack} / {jump.Worst}", "0 / 0", jumpOk);

        var pause = new Transport(1000);
        pause.Blocks(100);
        long lastHeard = pause.Heard, stopped = pause.Stop();
        pause.Play();
        pause.Blocks(1);
        bool stopOk = stopped == lastHeard && pause.Read == stopped + Block;
        Row("PDC: parado queda en lo último oído", $"{stopped - lastHeard}", "0", stopOk);

        var clock = new HeardClock();
        clock.Start(4800);
        bool plain = clock.At(4800, 0) == 4800 && clock.At(9000, 0) == 9000;
        Row("PDC: sin plugins el cabezal no cambia", plain ? "igual" : "distinto", "igual", plain);

        // 9. a take can't have its delay move: a plugin that turns late in the middle of one
        var (freeDelays, _) = LatePlugin(false, 1);
        var (takeDelays, takeLost) = LatePlugin(true, 1);
        bool fellBehind = freeDelays is [(1 * Block), (2 * Block)];
        Row("VST: fuera de una toma se atrasa un bloque", string.Join("/", freeDelays), $"{Block}/{2 * Block}", fellBehind);
        bool fixedDelay = takeDelays is [(2 * Block)] && takeLost == 0;
        Row("VST: en una toma la demora no se mueve", $"{string.Join("/", takeDelays)} ({takeLost})", $"{2 * Block} (0)", fixedDelay);
        var (worseDelays, worseLost) = LatePlugin(true, 2);
        Row("VST: más tarde se pierde, sin correrse", $"{string.Join("/", worseDelays)}", $"{2 * Block}", worseDelays is [(2 * Block)] && worseLost > 0);

        // 10. the plugin went away during the take: the rest of the mix keeps waiting as if it were there
        var kept = MixLatency.Compute([new(0, -1, []), new(0, 0, [])], [new(0, -1, [])], 600);
        bool keptOk = kept.Master == 600 && kept.TrackOut is [600, 0] && kept.NodeOut is [600];
        Row("PDC: la toma conserva su demora", $"{kept.Master}", "600", keptOk);
        return allOk;
    }

    /// <summary>
    /// <see cref="LiveLag"/> driven the way <see cref="VstInsert"/> does, with a scripted plugin: from block 200 on, each
    /// block comes back <paramref name="late"/> callbacks late. Returns every delay the output had (from the frames it
    /// carries, a ramp) and the blocks of it that were lost.
    /// </summary>
    static (int[] Delays, int Lost) LatePlugin(bool steady, int late)
    {
        const int Blocks = 400;
        var lag = new LiveLag(1 << 12);
        lag.Start(0, Block, steady);
        var left = new float[Block];
        var right = new float[Block];
        var due = new List<(int Callback, long At)>();
        var delays = new List<int>();
        int lost = 0;
        for (int k = 0; k < Blocks; k++)
        {
            long now = k * (long)Block;
            foreach (var (_, at) in due.Where(d => d.Callback <= k).ToList())
            {
                var ramp = Enumerable.Range(0, Block).Select(i => (float)(at + i + 1)).ToArray();
                lag.Queue.Write(at, ramp, ramp);
            }
            due.RemoveAll(d => d.Callback <= k);
            if (due.Count > 0 && due[0].At < lag.Queue.ReadAt + Block) lag.Miss(Block);
            due.Add((k + 1 + (k >= 200 ? late : 0), now));
            lag.Passed(Block);
            lag.Queue.Read(left, right);
            // the first blocks are the delay itself; past them, silence is a lost block
            if (left[0] == 0)
            {
                if (k > 10) lost++;
                continue;
            }
            int delay = (int)(now + 1 - left[0]);
            if (!delays.Contains(delay)) delays.Add(delay);
        }
        return ([.. delays], lost);
    }

    /// <summary>
    /// The transport's reading and its <see cref="HeardClock"/>, as the engine moves them: each block's timeline frames go
    /// through a <see cref="LatencyDelay"/> of the PDC, so what comes out says what is being heard, and <see cref="Worst"/>
    /// is how far the shown frame ever was from it. Before a jump's sound arrives what is shown holds at the target.
    /// </summary>
    sealed class Transport
    {
        readonly int pdc;
        readonly LatencyDelay delay = new();
        readonly HeardClock clock = new();
        readonly float[] ramp = new float[Block], outL = new float[Block], outR = new float[Block];
        long seekedTo = -1;

        public Transport(int pdc)
        {
            this.pdc = pdc;
            delay.Set(pdc);
            clock.Start(0);
        }

        public long LoopA, LoopB;
        public long Read { get; private set; }
        public long Heard { get; private set; }
        public long Shown { get; private set; }
        public long ShownMin { get; private set; } = long.MaxValue;
        public long ShownMax { get; private set; }
        public long Worst { get; private set; }
        public int Turns { get; private set; }

        public void Seek(long frame)
        {
            clock.Start(frame);
            Read = frame;
            seekedTo = frame;
        }

        /// <summary>The master fades out here: where the engine leaves the transport.</summary>
        public long Stop()
        {
            long at = clock.At(Read, pdc);
            clock.Start(at);
            Read = at;
            return at;
        }

        public void Play()
        {
            clock.Start(Read);
            delay.Reset();
        }

        public void Blocks(int count)
        {
            for (int k = 0; k < count; k++)
            {
                long lap = LoopB - LoopA;
                for (int i = 0; i < Block; i++)
                {
                    long f = Read + i;
                    ramp[i] = (LoopB > 0 && f >= LoopB ? f - lap : f) + 1;
                }
                Array.Clear(outL);
                Array.Clear(outR);
                delay.Add(ramp, ramp, 1f, outL, outR);
                Read += Block;
                if (LoopB > 0 && Read >= LoopB)
                {
                    clock.Turn(LoopA, lap);
                    Read -= lap;
                    Turns++;
                }
                Shown = clock.At(Read, pdc);
                ShownMin = Math.Min(ShownMin, Shown);
                ShownMax = Math.Max(ShownMax, Shown);
                Heard = (long)outL[Block - 1];
                if (Heard == 0) continue; // the delay still holds nothing: the start's own wait
                if (seekedTo >= 0 && Heard < seekedTo) continue; // the old stretch on its way out: held at the target
                seekedTo = -1;
                Worst = Math.Max(Worst, Math.Abs(Shown - Heard));
            }
        }
    }

    /// <summary>The signal through the delay in blocks, with <paramref name="before"/> called at each block's start (UI edits).</summary>
    static float[] Run(LatencyDelay d, float[] x, Action<int> before)
    {
        var outL = new float[x.Length];
        var outR = new float[x.Length];
        for (int at = 0; at < x.Length; at += Block)
        {
            int len = Math.Min(Block, x.Length - at);
            before(at);
            d.Add(x.AsSpan(at, len), x.AsSpan(at, len), 1f, outL.AsSpan(at, len), outR.AsSpan(at, len));
        }
        return outL;
    }
}
