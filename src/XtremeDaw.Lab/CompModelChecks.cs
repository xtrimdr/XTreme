using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// The compressor models, rows of comprobar-dsp: each comes down over its threshold and leaves alone what stays under it,
/// gives the same floats twice and in any block size, and stays finite when hit hard; then what makes each one itself: the
/// FET's four buttons past flat, the Óptico's release in two stages that grows with the work, the bus compressor's Auto and
/// its detector's high-pass, the VCA's 120 dB/s release and the Vari-Mu's ratio that grows with the level. A stepped knob
/// keeps to its steps.
/// </summary>
static class CompModelChecks
{
    const int Sr = 48000;

    static readonly string[] Kinds = [TrackInserts.Fet, TrackInserts.Opto, TrackInserts.BusComp, TrackInserts.Vca, TrackInserts.VariMu];

    /// <summary>Prints its rows; true when all of them pass.</summary>
    public static bool Check()
    {
        bool allOk = true;
        void Row(string name, string got, string want, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"{name,-44} {got,12} {want,10}  {(ok ? "igual" : "DISTINTO")}");
        }
        static string Diff(double d) => d == 0 ? "exacto" : $"{d:0.0e0}";

        foreach (string kind in Kinds)
        {
            var p = Defaults(kind);
            // a loud tone comes down, a quiet one passes at its level (the color bends only what is loud)
            double loud = Gain(kind, p, Tone(-3, 2));
            double quiet = Gain(kind, p, Tone(-50, 2));
            Row($"{kind}: comprime fuerte", $"{loud:0.0} dB", "< -3 dB", loud < -3);
            Row($"{kind}: deja pasar lo bajo", $"{quiet:0.00} dB", "±0.2 dB", Math.Abs(quiet) < 0.2);

            // the same twice, and the same in any block size
            var noise = Noise(Sr, 0.5);
            var a = Run(Make(kind, p), noise, 128);
            var b = Run(Make(kind, p), noise, 128);
            var c = Run(Make(kind, p), noise, 37);
            Row($"{kind}: igual dos veces", Diff(MaxDiff(a, b)), "exacto", MaxDiff(a, b) == 0);
            Row($"{kind}: igual en bloques de 37", Diff(MaxDiff(a, c)), "exacto", MaxDiff(a, c) == 0);

            // hit hard with everything up it stays finite and bounded
            var hot = Run(Make(kind, Hard(kind)), Noise(Sr, 4), 128);
            double peak = hot.Max(Math.Abs);
            Row($"{kind}: estable al máximo", $"{peak:0.00}", "< 20", hot.All(float.IsFinite) && peak < 20);
        }

        // FET: the four buttons go past flat, so 6 dB more in comes out no louder
        var all = Defaults(TrackInserts.Fet) with { Ratio = CompModelParams.AllButtons, InputDb = 20 };
        double rise = OutDb(TrackInserts.Fet, all, Tone(-6, 1)) - OutDb(TrackInserts.Fet, all, Tone(-12, 1));
        Row("fet: Todos, +6 dB entra, no sale más", $"{rise:+0.00;-0.00} dB", "<= 0.3 dB", rise <= 0.3);

        // Óptico: half lets go fast, the rest slowly, and longer after working longer
        var opto = Defaults(TrackInserts.Opto) with { ThresholdDb = OptoCompInsert.ThresholdFor(0.8) };
        var (shortAt100, shortAt1s) = Recovery(TrackInserts.Opto, opto, 0.3, 0.1, 1);
        var (_, longAt1s) = Recovery(TrackInserts.Opto, opto, 6, 0.1, 1);
        Row("opto: suelta rápido una parte", $"{shortAt100 * 100:0}%", "30-80%", shortAt100 is > 0.3 and < 0.8);
        Row("opto: el resto, lento", $"{shortAt1s * 100:0}%", "< 97%", shortAt1s < 0.97);
        Row("opto: más trabajo, release más largo", $"{longAt1s * 100:0}%", $"< {shortAt1s * 100:0}%", longAt1s < shortAt1s);

        // VCA de bus: Auto lets go of a hit faster than of a sustained level
        var bus = Defaults(TrackInserts.BusComp) with { ThresholdDb = -30, Ratio = 10, AttackMs = 0.1, ReleaseMs = CompModelParams.AutoRelease };
        var (hit, _) = Recovery(TrackInserts.BusComp, bus, 0.03, 0.15, 1);
        var (held, _) = Recovery(TrackInserts.BusComp, bus, 3, 0.15, 1);
        Row("buscomp: Auto suelta antes un golpe", $"{hit * 100:0}% / {held * 100:0}%", "golpe >", hit > held + 0.2);

        // VCA de bus: the detector's high-pass leaves a low tone alone
        var low = Tone(-6, 2, 40);
        var hpfOff = Defaults(TrackInserts.BusComp) with { ThresholdDb = -20 };
        double without = Gain(TrackInserts.BusComp, hpfOff, low), with = Gain(TrackInserts.BusComp, hpfOff with { SideHpfHz = 200 }, low);
        Row("buscomp: SC HPF no comprime los graves", $"{without:0.0} → {with:0.0}", "> 5 dB menos", with - without > 5);

        // VCA clásico: lets go at 120 dB per second
        var vca = Defaults(TrackInserts.Vca) with { ThresholdDb = -40, Ratio = CompModelParams.Infinite };
        double rate = ReleaseRate(vca);
        Row("vca: release de 120 dB/s", $"{rate:0} dB/s", "110-130", rate is > 110 and < 130);

        // Vari-Mu: the ratio grows with the level, so the next 10 dB come down more than the first ones
        var mu = Defaults(TrackInserts.VariMu) with { InputDb = 0, ThresholdDb = -30 };
        double g1 = Gain(TrackInserts.VariMu, mu, Tone(-35, 2)), g2 = Gain(TrackInserts.VariMu, mu, Tone(-25, 2)), g3 = Gain(TrackInserts.VariMu, mu, Tone(-15, 2));
        Row("varimu: el ratio crece con el nivel", $"{g2 - g1:0.0} / {g3 - g2:0.0}", "2do mayor", g3 - g2 < g2 - g1 - 2);

        // the graph tells the truth: each compressor's curve gives the level a steady tone comes out at, its scope a point per
        // hop with what came in and the reduction, and measuring allocates nothing
        foreach (string kind in Kinds.Prepend(TrackInserts.Comp))
        {
            var fx = kind == TrackInserts.Comp ? MakeComp() : Make(kind, Defaults(kind));
            var graph = (ICompGraph)fx;
            var tone = Tone(-12, 3);
            var y = Run(fx, tone, 128);
            double heard = Db(y.Skip(y.Length - Sr / 2).Max(v => Math.Abs((double)v)));
            double drawn = graph.CurveOutDb(-12);
            Row($"{kind}: la curva dice lo que suena", $"{drawn:0.0} / {heard:0.0}", "± 1.5 dB", Math.Abs(drawn - heard) < 1.5);
            var scope = graph.Scope;
            scope.Read(scope.Written - 1, out float inDb, out _, out float gr);
            bool points = scope.Written == tone.Length / CompScope.Hop;
            Row($"{kind}: el gráfico recibe la señal", $"{inDb:0.0} · {gr:0.0} dB", "-12 · < 0", points && Math.Abs(inDb + 12) < 0.2 && gr < 0);
            var l = new float[128];
            var r = new float[128];
            fx.Process(l, r);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 400; i++) fx.Process(l, r);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Row($"{kind}: medir no asigna memoria", $"{bytes} bytes", "0", bytes == 0);
        }

        // a stepped knob: a value between steps snaps to the nearest, and the knob goes step by step
        var ratio = FxDefs.Params(TrackInserts.BusComp).First(x => x.Id == "ratio");
        var slot = InsertSlot.New(TrackInserts.BusComp);
        slot.Stage!.Values["ratio"] = 5;
        ChannelDsp.Specs([slot], -18); // normalizes the slot, as playing it does
        Row("buscomp: ratio 5 queda en un paso", $"{slot.Stage.Values["ratio"]:0}:1", "4:1", slot.Stage.Values["ratio"] == 4);
        Row("perilla por pasos: ida y vuelta", $"{ratio.FromKnob(ratio.ToKnob(10)):0}", "10", ratio.FromKnob(ratio.ToKnob(10)) == 10 && ratio.FromKnob(0.4) == 4);
        return allOk;
    }

    /// <summary>A model's settings as a new slot has them.</summary>
    static CompModelParams Defaults(string kind) => FxDefs.ToCompModel(InsertSlot.New(kind));

    /// <summary>Every knob up but the gain (that only scales), for the stability row.</summary>
    static CompModelParams Hard(string kind) => Defaults(kind) with
    {
        InputDb = 48, ThresholdDb = -50, Ratio = kind == TrackInserts.Fet ? CompModelParams.AllButtons : CompModelParams.Infinite,
        AttackMs = 0.02, ReleaseMs = 50, MakeupDb = 0, Mode = kind == TrackInserts.VariMu ? 6 : 1,
    };

    static ITrackInsert Make(string kind, CompModelParams p)
    {
        switch (kind)
        {
            case TrackInserts.Fet: { var x = new FetCompInsert(Sr); x.Set(p); return x; }
            case TrackInserts.Opto: { var x = new OptoCompInsert(Sr); x.Set(p); return x; }
            case TrackInserts.BusComp: { var x = new BusCompInsert(Sr); x.Set(p); return x; }
            case TrackInserts.Vca: { var x = new VcaCompInsert(Sr); x.Set(p); return x; }
            default: { var x = new VariMuInsert(Sr); x.Set(p); return x; }
        }
    }

    /// <summary>The compressor with its presets, as a new slot has it.</summary>
    static ITrackInsert MakeComp()
    {
        var slot = InsertSlot.New(TrackInserts.Comp);
        slot.On = true;
        var spec = ChannelDsp.Specs([slot], -18)[0];
        var fx = new CompInsert(Sr);
        TrackInserts.Apply(fx, spec);
        return fx;
    }

    static float[] Tone(double db, double sec, double hz = 1000)
    {
        double a = Math.Pow(10, db / 20);
        return [.. Enumerable.Range(0, (int)(sec * Sr)).Select(i => (float)(a * Math.Sin(2 * Math.PI * hz * i / Sr)))];
    }

    static float[] Noise(int n, double amp)
    {
        var rng = new Random(7);
        return [.. Enumerable.Range(0, n).Select(_ => (float)(amp * (rng.NextDouble() * 2 - 1)))];
    }

    /// <summary>The left output of a mono signal fed to both sides, in blocks of <paramref name="block"/>.</summary>
    static float[] Run(ITrackInsert fx, float[] input, int block)
    {
        var l = (float[])input.Clone();
        var r = (float[])input.Clone();
        for (int at = 0; at < l.Length; at += block)
        {
            int n = Math.Min(block, l.Length - at);
            fx.Process(l.AsSpan(at, n), r.AsSpan(at, n));
        }
        return l;
    }

    static double MaxDiff(float[] a, float[] b) => a.Zip(b, (x, y) => Math.Abs((double)x - y)).Max();

    static double Rms(float[] x, int from, int to)
    {
        double sum = 0;
        for (int i = from; i < to; i++) sum += x[i] * (double)x[i];
        return Math.Sqrt(sum / Math.Max(1, to - from));
    }

    static double Db(double x) => 20 * Math.Log10(Math.Max(x, 1e-12));

    /// <summary>The steady output level of a tone, over its last half second, dB.</summary>
    static double OutDb(string kind, CompModelParams p, float[] tone)
    {
        var y = Run(Make(kind, p), tone, 128);
        return Db(Rms(y, y.Length - Sr / 2, y.Length));
    }

    /// <summary>How much a tone comes down once steady, dB (&lt; 0 = reduced).</summary>
    static double Gain(string kind, CompModelParams p, float[] tone) => OutDb(kind, p, tone) - Db(Rms(tone, tone.Length - Sr / 2, tone.Length));

    /// <summary>
    /// A loud tone for <paramref name="burstSec"/>, then a quiet one: how much of its reduction it has given back
    /// <paramref name="at1"/> and <paramref name="at2"/> seconds after (0 = none, 1 = all).
    /// </summary>
    static (double, double) Recovery(string kind, CompModelParams p, double burstSec, double at1, double at2)
    {
        int burst = (int)(burstSec * Sr), n = burst + 3 * Sr;
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = (float)((i < burst ? 0.7 : 0.007) * Math.Sin(2 * Math.PI * 1000 * i / Sr));
        var fx = Make(kind, p);
        var y = Run(fx, x, 128);
        double GainAt(int i) => Db(Rms(y, i - 240, i)) - Db(Rms(x, i - 240, i));
        // the quiet tone is under the threshold: all given back is 0 dB
        double deep = GainAt(burst);
        double Back(double sec) => deep >= 0 ? 1 : (GainAt(burst + (int)(sec * Sr)) - deep) / -deep;
        return (Back(at1), Back(at2));
    }

    /// <summary>The VCA's release, in dB per second, from its reduction meter's slope after a loud tone stops.</summary>
    static double ReleaseRate(CompModelParams p)
    {
        var fx = new VcaCompInsert(Sr);
        fx.Set(p);
        Run(fx, Tone(-6, 1), 128);
        fx.TakeGrDb();
        var silence = new float[Sr / 10];
        var l = (float[])silence.Clone();
        var r = (float[])silence.Clone();
        // the meter keeps the deepest point, which is a short block's first sample: where it starts, and where it got to
        fx.Process(l.AsSpan(0, 48), r.AsSpan(0, 48));
        double start = fx.TakeGrDb();
        fx.Process(l.AsSpan(48), r.AsSpan(48));
        fx.TakeGrDb();
        fx.Process(l.AsSpan(0, 48), r.AsSpan(0, 48));
        double end = fx.TakeGrDb();
        return (end - start) / (silence.Length / (double)Sr);
    }
}
