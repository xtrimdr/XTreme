using System.Text.Json;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// The sidechain, rows of comprobar-dsp: a compressor and a gate react to the key, not to their input; the pump ducks on
/// the key's hits; in the export a source listed after its listener and muted still ducks it, the same in a stem; the run
/// order puts each source first and survives a loop; and a slot keeps its source through the project.
/// </summary>
static class SidechainChecks
{
    const int Sr = 48000;

    /// <summary>Prints its rows; true when all of them pass.</summary>
    public static bool Check()
    {
        bool allOk = true;
        void Row(string name, string got, string want, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"{name,-36} {got,12} {want,10}  {(ok ? "igual" : "DISTINTO")}");
        }

        // 1. a quiet input under the threshold: alone it passes, with a loud key it comes down
        int n = 128 * 200;
        var quiet = Fill(n, i => 0.01f * MathF.Sin(2 * MathF.PI * 440 * i / Sr));
        var loud = Fill(n, i => 0.5f * MathF.Sin(2 * MathF.PI * 60 * i / Sr));
        var silent = new float[n];
        double Comp(float[] key)
        {
            var c = new Compressor(Sr) { Bypass = false, Threshold = -30, Ratio = 4, Knee = 0, Attack = 0.005, Release = 0.1 };
            var l = (float[])quiet.Clone();
            var r = (float[])quiet.Clone();
            for (int at = 0; at < n; at += 128) c.Process(l.AsSpan(at, 128), r.AsSpan(at, 128), key.AsSpan(at, 128), key.AsSpan(at, 128));
            return Db(Rms(l, n / 2, n) / Rms(quiet, n / 2, n));
        }
        double alone = Comp(quiet), keyed = Comp(loud);
        Row("Sidechain: compresor sin key fuerte", $"{alone:0.0} dB", "0 dB", Math.Abs(alone) < 0.05);
        Row("Sidechain: compresor baja con la key", $"{keyed:0.0} dB", "< -15 dB", keyed < -15);

        // 2. a gate on a loud input closes when its key is silent and opens with it
        double Gate(float[] key)
        {
            var g = new NoiseGate(Sr);
            g.Set(new GateParams(true, GateParams.Normal, -40));
            var l = (float[])loud.Clone();
            var r = (float[])loud.Clone();
            for (int at = 0; at < n; at += 128) g.Process(l.AsSpan(at, 128), r.AsSpan(at, 128), key.AsSpan(at, 128), key.AsSpan(at, 128));
            return Db(Rms(l, n / 2, n) / Rms(loud, n / 2, n));
        }
        double shut = Gate(silent), open = Gate(loud);
        Row("Sidechain: puerta cierra sin key", $"{shut:0.0} dB", "< -10 dB", shut < -10);
        Row("Sidechain: puerta abre con la key", $"{open:0.0} dB", "0 dB", Math.Abs(open) < 0.05);

        // 3. the export: a pad listed before a muted kick, with a pump listening to the kick
        double sec = 2;
        int frames = (int)(sec * Sr);
        var pad = Fill(frames, _ => 0.25f);
        int hit = Sr / 2, gap = Sr / 2;
        var kick = Fill(frames, i => i >= hit && i % gap < 480 ? 0.9f : 0f);
        var pump = new InsertSpec("p", TrackInserts.Pump)
        {
            Pump = PumpParams.Defaults with { Division = 3, Depth = 0.8, Length = 0.5, Curve = 0 }, Bpm = 120, StageOn = true, Key = "kick",
        };
        MixTrackSpec[] tracks =
        [
            new() { Id = "pad", Name = "Pad", Clips = [new MixClipSpec { Left = pad, Right = pad, DurationSec = sec }], Inserts = [pump] },
            new() { Id = "kick", Name = "Kick", Muted = true, Clips = [new MixClipSpec { Left = kick, Right = kick, DurationSec = sec }], Inserts = [] },
        ];
        var bus = new MixBusSpec();
        var (mix, _) = MixRenderer.Render(tracks, bus, sec, Sr, limit: false);
        int after = hit + (int)(0.004 * Sr), before = hit - 100;
        Row("Sidechain: bombeo en la exportación", $"{mix[after] / mix[before]:0.00}", "< 0.30", mix[after] / mix[before] < 0.3);
        // the automatic threshold fades with the key's peak, so the way back ends a little after the release
        int back = hit + (int)((pump.Pump.KeyMs + 30) / 1000 * Sr);
        Row("Sidechain: bombeo vuelve en su largo", $"{mix[back]:0.000}", "0.250", Math.Abs(mix[back] - 0.25f) < 1e-4);
        Row("Sidechain: antes del golpe intacto", $"{mix[before]:0.000}", "0.250", Math.Abs(mix[before] - 0.25f) < 1e-4);
        var (stem, _) = MixRenderer.Render(tracks, bus, sec, Sr, only: 0, limit: false);
        double stemDiff = 0;
        for (int i = 0; i < frames; i++) stemDiff = Math.Max(stemDiff, Math.Abs(stem[i] - mix[i]));
        Row("Sidechain: igual en el stem", stemDiff < 1e-6 ? "exacto" : $"{stemDiff:0.0e0}", "exacto", stemDiff < 1e-6);
        var unkeyed = new[] { tracks[0], new MixTrackSpec { Id = "otra", Name = "Otra", Clips = tracks[1].Clips, Muted = true } };
        var (gone, _) = MixRenderer.Render(unkeyed, bus, sec, Sr, limit: false);
        var beat = new[] { new MixTrackSpec { Id = "pad", Name = "Pad", Clips = tracks[0].Clips, Inserts = [pump with { Key = null }] } };
        var (plain, _) = MixRenderer.Render(beat, bus, sec, Sr, limit: false);
        double goneDiff = 0;
        for (int i = 0; i < frames; i++) goneDiff = Math.Max(goneDiff, Math.Abs(gone[i] - plain[i]));
        Row("Sidechain: sin fuente, vuelve al tempo", goneDiff == 0 ? "exacto" : $"{goneDiff:0.0e0}", "exacto", goneDiff == 0);

        // the pump as a compressor: a threshold over the kick never ducks, one well under it ducks all the way, a softer
        // kick ducks less, and the hold keeps it down
        float[] Keyed(PumpParams p, float level = 1)
        {
            var key = level == 1 ? tracks[1] : new MixTrackSpec
            {
                Id = "kick", Name = "Kick", Muted = true,
                Clips = [new MixClipSpec { Left = kick.Select(v => v * level).ToArray(), Right = kick.Select(v => v * level).ToArray(), DurationSec = sec }], Inserts = [],
            };
            var t = new[] { new MixTrackSpec { Id = "pad", Name = "Pad", Clips = tracks[0].Clips, Inserts = [pump with { Pump = p }] }, key };
            return MixRenderer.Render(t, bus, sec, Sr, limit: false).Item1;
        }
        var over = Keyed(pump.Pump with { ThresholdDb = 0 });
        var under = Keyed(pump.Pump with { ThresholdDb = -30, HoldMs = 100 });
        var soft = Keyed(pump.Pump with { ThresholdDb = -30 }, 0.25f);
        Row("Sidechain: bombeo, umbral arriba", $"{over[after]:0.000}", "0.250", Math.Abs(over[after] - 0.25f) < 1e-4);
        Row("Sidechain: bombeo, umbral abajo", $"{under[after] / mix[before]:0.00}", "< 0.30", under[after] / mix[before] < 0.3);
        float softDuck = soft[after] / mix[before];
        Row("Sidechain: bombeo sigue el nivel", $"{softDuck:0.00}", "0.30–0.90", softDuck > 0.3 && softDuck < 0.9);
        int held = hit + (int)(0.09 * Sr);
        Row("Sidechain: bombeo, hold abajo", $"{under[held]:0.000}", "0.050", Math.Abs(under[held] - 0.05f) < 1e-3);

        // 4. the run order: a source first, its listeners after; a loop doesn't hang
        int[][] deps = [[2], [], [1]];
        var order = Sidechains.Levels(3, k => deps[k]).SelectMany(g => g).ToArray();
        Row("Sidechain: orden de las pistas", string.Join(",", order), "1,2,0", order.SequenceEqual([1, 2, 0]));
        var loop = Sidechains.Levels(2, k => [1 - k]).SelectMany(g => g).ToArray();
        Row("Sidechain: un lazo no se cuelga", string.Join(",", loop), "2 pistas", loop.Length == 2);

        // 5. the slot through the project: a compressor keeps its source, an EQ never has one
        var slots = new List<InsertSlot>();
        var comp = ChannelInserts.Add(slots, InsertSlot.CompKind)!;
        comp.Sidechain = "trk-kick";
        var eq = ChannelInserts.Add(slots, InsertSlot.EqKind)!;
        eq.Sidechain = "trk-kick";
        var read = slots.Select(s => JsonSerializer.Deserialize<InsertSlot>(JsonSerializer.Serialize(s, ProjectStore.Json), ProjectStore.Json)!).ToList();
        var specs = ChannelDsp.Specs(read, -24);
        bool kept = specs[0].Key == "trk-kick" && specs[1].Key == null;
        Row("Sidechain: guarda la fuente", kept ? "sí" : "no", "sí", kept);
        return allOk;
    }

    static float[] Fill(int n, Func<int, float> f)
    {
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = f(i);
        return x;
    }

    static double Rms(float[] x, int from, int to)
    {
        double s = 0;
        for (int i = from; i < to; i++) s += x[i] * (double)x[i];
        return Math.Sqrt(s / Math.Max(1, to - from));
    }

    static double Db(double ratio) => 20 * Math.Log10(Math.Max(ratio, 1e-9));
}
