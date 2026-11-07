using System.Text.Json;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// The pump, rows of comprobar-dsp: it reaches its bottom on the beat and is back by its length, it follows the song frame
/// (a run that starts mid-song lands where one from the start does), any block size gives the same floats, it runs on by
/// itself while the song stands still, and a slot keeps its values and the tempo through the project. Also the PDC a node
/// on the beat goes by (<see cref="MixLatency.Plan.NodeArrive"/>).
/// </summary>
static class PumpChecks
{
    const int Sr = 48000;
    const double Bpm = 120;

    /// <summary>Prints its rows; true when all of them pass.</summary>
    public static bool Check()
    {
        bool allOk = true;
        void Row(string name, string got, string want, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"{name,-36} {got,12} {want,10}  {(ok ? "igual" : "DISTINTO")}");
        }

        var p = PumpParams.Defaults with { Division = 2, Depth = 0.8, Length = 0.5, Curve = 0, AttackMs = 2, Offset = 0 };
        int beat = (int)(Sr * 60 / Bpm);

        // 1. on a steady level: down to 1 - depth just after the beat, back up once its length has gone by
        var flat = Run(p, 0, 4 * beat, 128);
        int bottom = beat + (int)(0.002 * Sr) + 1;
        Row("Bombeo: abajo en el tiempo", $"{flat[bottom]:0.000}", "0.200", Math.Abs(flat[bottom] - 0.2) < 0.01);
        int back = beat + (int)(0.002 * Sr) + beat / 2 + 8;
        Row("Bombeo: vuelve en su largo", $"{flat[back]:0.000}", "1.000", flat[back] == 1);
        Row("Bombeo: arriba antes del tiempo", $"{flat[beat - 1]:0.000}", "1.000", flat[beat - 1] == 1);

        // 2. starting mid-song it lands where a run from the start is, and any block size is the same
        int from = 3 * beat / 2 + 17;
        var mid = Run(p, from, 2 * beat, 128);
        double moved = 0;
        for (int i = 0; i < mid.Length; i++) moved = Math.Max(moved, Math.Abs(mid[i] - flat[from + i]));
        Row("Bombeo: sigue a la canción", moved == 0 ? "exacto" : $"{moved:0.0e0}", "exacto", moved == 0);
        var small = Run(p, 0, 4 * beat, 32);
        var big = Run(p, 0, 4 * beat, 1000);
        double blocks = 0;
        for (int i = 0; i < flat.Length; i++) blocks = Math.Max(blocks, Math.Max(Math.Abs(small[i] - flat[i]), Math.Abs(big[i] - flat[i])));
        Row("Bombeo: igual con otro bloque", blocks == 0 ? "exacto" : $"{blocks:0.0e0}", "exacto", blocks == 0);

        // 3. the song stands still (an instrument played while stopped): it keeps pumping
        var still = Run(p, 5000, 2 * beat, 128, standStill: true);
        float low = still.Min(), high = still.Max();
        Row("Bombeo: corre solo con la canción quieta", $"{low:0.00}–{high:0.00}", "0.20–1.00", low < 0.25 && high == 1);

        // 4. the slot through the project: its values and the tempo reach the engine
        var slots = new List<InsertSlot>();
        var slot = ChannelInserts.Add(slots, TrackInserts.Pump)!;
        slot.Stage!.Values["division"] = 3;
        var read = JsonSerializer.Deserialize<InsertSlot>(JsonSerializer.Serialize(slot, ProjectStore.Json), ProjectStore.Json)!;
        var spec = ChannelDsp.Specs([read], -24, 140)[0];
        bool saved = spec.On && spec.Wet == 1 && spec.Dry == 0 && spec.Pump.Division == 3 && spec.Bpm == 140 && spec.Pump.Depth == PumpParams.Defaults.Depth
            && TrackInserts.Create(spec.Kind, Sr) is FxShell { Inner: PumpInsert };
        Row("Bombeo: guarda y sigue el tempo", saved ? "sí" : "no", "sí", saved);

        // 5. a node fed by a plugin 300 frames late goes by that: its beat lines up with the tracks'
        var plan = MixLatency.Compute([new(300, 0, []), new(0, 0, [])], [new(0, -1, [])]);
        Row("Bombeo en un bus: su latencia", $"{plan.NodeArrive[0]}", "300", plan.NodeArrive[0] == 300);

        // 6. after a slot that delays the sound (the limiter) it ducks on the beat it hears, not the block's (#256)
        var pumpSlots = new List<InsertSlot>();
        ChannelInserts.Add(pumpSlots, TrackInserts.Pump);
        var limSlots = new List<InsertSlot>();
        ChannelInserts.Add(limSlots, TrackInserts.Limit);
        var pumpSpec = ChannelDsp.Specs(pumpSlots, -24, Bpm)[0] with { Pump = p };
        var limSpec = ChannelDsp.Specs(limSlots, -24, Bpm)[0];
        var lim = RackRun([limSpec], 0, 4 * beat, 128, -1, 0, out int late);
        var both = RackRun([limSpec, pumpSpec], 0, 4 * beat, 128, -1, 0, out _);
        var alone = RackRun([pumpSpec], 0, 4 * beat, 128, -1, 0, out _);
        double off = 0;
        for (int i = beat / 2; i < 4 * beat - late; i++)
            if (lim[i + late] > 1e-3) off = Math.Max(off, Math.Abs(both[i + late] / lim[i + late] - alone[i]));
        Row("Bombeo: tras un efecto con latencia", late > 0 && off < 1e-4 ? "en el tiempo" : $"{late} · {off:0.000}", "en el tiempo", late > 0 && off < 1e-4);

        // 7. a loop that turns inside a block: from the turn on it follows the loop's start (#256)
        int lapStart = beat / 3, lapEnd = 2 * beat + 101, turnAt = 37, block = 128;
        long turnFrame = lapEnd - turnAt;
        var turned = RackRun([pumpSpec], turnFrame, block, block, turnAt, lapEnd - lapStart, out _);
        double turnOff = 0;
        for (int i = 0; i < block; i++)
        {
            long f = i < turnAt ? turnFrame + i : lapStart + i - turnAt;
            turnOff = Math.Max(turnOff, Math.Abs(turned[i] - flat[f]));
        }
        Row("Bombeo: el bucle gira dentro del bloque", turnOff == 0 ? "exacto" : $"{turnOff:0.0e0}", "exacto", turnOff == 0);
        return allOk;
    }

    /// <summary>
    /// What a rack of <paramref name="specs"/> gives a steady 1 over <paramref name="frames"/> from song frame
    /// <paramref name="start"/>, in blocks; the first block turns a loop at <paramref name="turnAt"/> (−1 = no),
    /// <paramref name="back"/> frames back. <paramref name="late"/>: the rack's latency.
    /// </summary>
    static float[] RackRun(InsertSpec[] specs, long start, int frames, int block, int turnAt, long back, out int late)
    {
        var rack = new InsertRack(Sr);
        rack.Set(specs, fade: false);
        rack.Reset();
        late = rack.LatencySamples;
        var outL = new float[frames];
        var outR = new float[frames];
        Array.Fill(outL, 1f);
        Array.Fill(outR, 1f);
        for (int at = 0; at < frames; at += block)
        {
            int n = Math.Min(block, frames - at);
            rack.Frame = start + at;
            if (at == 0 && turnAt >= 0) rack.Turn(turnAt, back);
            rack.Process(outL.AsSpan(at, n), outR.AsSpan(at, n));
        }
        return outL;
    }

    /// <summary>The pump's gain over <paramref name="frames"/> from song frame <paramref name="start"/>, run in blocks of <paramref name="block"/>.</summary>
    static float[] Run(PumpParams p, long start, int frames, int block, bool standStill = false)
    {
        var insert = TrackInserts.Create(TrackInserts.Pump, Sr);
        TrackInserts.Apply(insert, new InsertSpec("p", TrackInserts.Pump) { Pump = p, Bpm = Bpm, StageOn = true });
        insert.Reset();
        var pump = (PumpInsert)((FxShell)insert).Inner;
        var outL = new float[frames];
        var outR = new float[frames];
        Array.Fill(outL, 1f);
        Array.Fill(outR, 1f);
        for (int at = 0; at < frames; at += block)
        {
            int n = Math.Min(block, frames - at);
            pump.Frame = standStill ? start : start + at;
            insert.Process(outL.AsSpan(at, n), outR.AsSpan(at, n));
        }
        return outL;
    }
}
