using System.Text.Json;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// The modulation effects and the EQ's seventh band and dynamics, rows of comprobar-dsp: each effect does what it says on a
/// test tone, stays stable at its limits and gives the same result twice, and an EQ from before the seventh band sounds
/// as it did. A dynamic band leaves alone what stays under its threshold and comes down by its range over it.
/// </summary>
static class ModChecks
{
    const int Sr = 48000, Block = 128;

    /// <summary>Prints its rows; true when all of them pass.</summary>
    public static bool Check()
    {
        bool allOk = true;
        void Row(string name, string got, string want, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"{name,-36} {got,12} {want,10}  {(ok ? "igual" : "DISTINTO")}");
        }
        static string Diff(double d) => d == 0 ? "exacto" : $"{d:0.0e0}";

        var tone = Signal(2 * Sr, i => 0.5 * Math.Sin(2 * Math.PI * 440 * i / Sr));
        var noise = Noise(2 * Sr, 0.3);

        // 1. every effect: bounded and silent after silence at its most extreme, and the same twice
        foreach (string kind in new[] { TrackInserts.Chorus, TrackInserts.Flanger, TrackInserts.Phaser, TrackInserts.Tremolo })
        {
            var hard = new ModParams(ModParams.RateMax, 1, 2, 30, 1, 99, 1);
            var a = Run(Make(kind, hard), noise);
            var b = Run(Make(kind, hard), noise);
            double peak = a.Max(Math.Abs);
            bool finite = a.All(float.IsFinite);
            Row($"{kind}: estable al máximo", $"{peak:0.00}", "< 40", finite && peak < 40);
            Row($"{kind}: igual dos veces", Diff(MaxDiff(a, b)), "exacto", MaxDiff(a, b) == 0);
        }

        // 2. the chorus and the flanger move: the wet signal isn't a plain delay of the input
        var still = Run(Make(TrackInserts.Chorus, ModParams.Defaults with { Depth = 0 }), tone);
        var moving = Run(Make(TrackInserts.Chorus, ModParams.Defaults with { Depth = 0.5 }), tone);
        Row("Chorus: la modulación cambia el sonido", $"{MaxDiff(still, moving):0.00}", "> 0.05", MaxDiff(still, moving) > 0.05);

        // 3. a phaser against its dry signal digs a notch that travels: the level of a steady tone moves
        var phased = Run(Make(TrackInserts.Phaser, new ModParams(1, 1, 0, 0, 0, 6, 0)), tone);
        double loud = 0, soft = 9;
        for (int at = Sr / 2; at + 480 < tone.Length; at += 480)
        {
            double rms = 0;
            for (int i = at; i < at + 480; i++) rms += Math.Pow(0.5 * (phased[i] + tone[i]), 2);
            rms = Math.Sqrt(rms / 480);
            loud = Math.Max(loud, rms);
            soft = Math.Min(soft, rms);
        }
        double swing = 20 * Math.Log10(loud / Math.Max(soft, 1e-9));
        Row("Phaser: barrido contra lo directo", $"{swing:0.0} dB", "> 10 dB", swing > 10);

        // 4. the tremolo reaches silence at full depth; opposite sides stay together in level (auto-pan)
        var trem = Make(TrackInserts.Tremolo, new ModParams(2, 1, 0, 0, 1, 0, 0));
        var l = new float[Block];
        var r = new float[Block];
        double low = 1, sumErr = 0;
        for (int at = 0; at + Block <= Sr; at += Block)
        {
            Array.Fill(l, 1f);
            Array.Fill(r, 1f);
            trem.Process(l, r);
            low = Math.Min(low, l[0]);
            sumErr = Math.Max(sumErr, Math.Abs(l[0] + r[0] - 1));
        }
        Row("Tremolo al 100 % llega a silencio", $"{low:0.0000}", "< 0.001", low < 1e-3);
        Row("Paneo automático: suma constante", $"{sumErr:0.0e0}", "< 1e-5", sumErr < 1e-5);

        // 5. a new slot starts where it sounds, and the slot's trip through the project
        var slots = new List<InsertSlot>();
        var chorus = ChannelInserts.Add(slots, TrackInserts.Chorus)!;
        var read = JsonSerializer.Deserialize<InsertSlot>(JsonSerializer.Serialize(chorus, ProjectStore.Json), ProjectStore.Json)!;
        var spec = ChannelDsp.Specs([read], -24)[0];
        bool saved = spec.On && spec.Wet == 0.5 && spec.Dry == 0.5 && spec.Mod.RateHz == 0.8 && spec.Mod.Depth == 0.35 && TrackInserts.Create(spec.Kind, Sr) is FxShell;
        Row("Chorus: guarda y arranca al 50 %", saved ? "sí" : "no", "sí", saved);

        // 6. an EQ saved with six bands reads as seven, the high cut still last, and sounds the same
        const string oldEq = """
            {"type":"eq","eq":{"on":true,"bands":[{"on":true,"type":"highpass","freq":100,"q":0.7},{"on":true,"type":"peaking","freq":250,"gain":-3,"q":1},
             {"type":"peaking","freq":800},{"on":true,"type":"peaking","freq":2000,"gain":2,"q":1.2},{"type":"peaking","freq":4500},{"on":true,"type":"lowpass","freq":9000,"q":0.7}]}}
            """;
        var old = JsonSerializer.Deserialize<InsertSlot>(oldEq, ProjectStore.Json)!;
        var oldSpec = ChannelDsp.Specs([old], -24)[0];
        bool seven = old.Eq!.Bands.Count == TrackEq.BandCount && old.Eq.Bands[^1].Type == "lowpass" && old.Eq.Bands[^1].Freq == 9000 && !old.Eq.Bands[5].On;
        Row("EQ de 6 bandas pasa a 7", seven ? "sí" : "no", "sí", seven);
        var direct = new TrackEq(Sr);
        direct.Set(true, oldSpec.EqBands.Where((_, i) => i != 5).ToArray());
        var six = new float[noise.Length];
        var withSeven = new float[noise.Length];
        var viaOld = new TrackEq(Sr);
        viaOld.Set(true, oldSpec.EqBands);
        Fill(direct, noise, six);
        Fill(viaOld, noise, withSeven);
        double eqDiff = MaxDiff(six, withSeven);
        Row("EQ con la séptima apagada, igual", $"{eqDiff:0.0e0}", "< 1e-3", eqDiff < 1e-3);

        // 7. a dynamic bell at 1 kHz: under its threshold bit for bit, over it the tone comes down by its range
        var bell = TrackEq.DefaultBands.Select((b, i) => i == 3
            ? b with { On = true, Freq = 1000, Gain = 0, Q = 2, Dyn = true, DynThreshold = -30, DynRange = -6 } : b).ToArray();
        var quiet = Signal(Sr, i => 0.01 * Math.Sin(2 * Math.PI * 1000 * i / Sr));
        var quietOut = RunEq(bell, quiet);
        Row("Dinámico bajo el umbral: intacto", Diff(MaxDiff(quiet, quietOut)), "exacto", MaxDiff(quiet, quietOut) == 0);
        var strong = Signal(Sr, i => 0.5 * Math.Sin(2 * Math.PI * 1000 * i / Sr));
        var strongOut = RunEq(bell, strong);
        double down = Rms(strongOut, Sr / 2, Sr) - Rms(strong, Sr / 2, Sr);
        Row("Dinámico sobre el umbral baja", $"{down:0.0} dB", "-6 dB", Math.Abs(down + 6) < 0.3);
        var other = Signal(Sr, i => 0.5 * Math.Sin(2 * Math.PI * 100 * i / Sr));
        double away = Rms(RunEq(bell, other), Sr / 2, Sr) - Rms(other, Sr / 2, Sr);
        Row("Dinámico no toca otra zona", $"{away:0.00} dB", "> -0.2 dB", away > -0.2);
        var off = bell.Select(b => b with { Dyn = false }).ToArray();
        var offOut = RunEq(off, strong);
        double offDiff = MaxDiff(strong, offOut);
        Row("Sin dinámico y en 0 dB: intacto", Diff(offDiff), "< 1e-6", offDiff < 1e-6);
        return allOk;
    }

    static ITrackInsert Make(string kind, ModParams p)
    {
        var insert = TrackInserts.Create(kind, Sr);
        TrackInserts.Apply(insert, new InsertSpec("m", kind) { Mod = p, StageOn = true });
        insert.Reset();
        return insert;
    }

    static float[] RunEq(EqBandSpec[] bands, float[] x)
    {
        var eq = new TrackEq(Sr);
        eq.Set(true, bands);
        var y = new float[x.Length];
        Fill(eq, x, y);
        return y;
    }

    static void Fill(TrackEq eq, float[] x, float[] y)
    {
        var l = new float[Block];
        var r = new float[Block];
        for (int at = 0; at + Block <= x.Length; at += Block)
        {
            x.AsSpan(at, Block).CopyTo(l);
            x.AsSpan(at, Block).CopyTo(r);
            eq.Process(l, r);
            l.CopyTo(y.AsSpan(at));
        }
        // the tail that doesn't fill a block isn't compared
        for (int i = x.Length / Block * Block; i < x.Length; i++) y[i] = x[i];
    }

    static float[] Run(ITrackInsert insert, float[] x)
    {
        var y = new float[x.Length];
        var l = new float[Block];
        var r = new float[Block];
        for (int at = 0; at + Block <= x.Length; at += Block)
        {
            x.AsSpan(at, Block).CopyTo(l);
            x.AsSpan(at, Block).CopyTo(r);
            insert.Process(l, r);
            l.CopyTo(y.AsSpan(at));
        }
        return y;
    }

    static double Rms(float[] x, int from, int to)
    {
        double sum = 0;
        for (int i = from; i < to; i++) sum += x[i] * x[i];
        return 10 * Math.Log10(Math.Max(sum / (to - from), 1e-20));
    }

    static double MaxDiff(float[] a, float[] b)
    {
        double d = 0;
        for (int i = 0; i < a.Length; i++) d = Math.Max(d, Math.Abs(a[i] - b[i]));
        return d;
    }

    static float[] Signal(int n, Func<int, double> f)
    {
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = (float)f(i);
        return x;
    }

    static float[] Noise(int n, double level)
    {
        var rng = new Random(7);
        return Signal(n, _ => level * (rng.NextDouble() * 2 - 1));
    }
}
