using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.Lab;

/// <summary>
/// The compressor's presets, rows of comprobar-dsp: the table holds together, and every preset does what it says on a
/// few kinds of take (sustained, plucked, hits, spoken), at every amount, through the real compressor. A preset is only
/// numbers: a typo in one shows here, not as an error. Also the fixed values against what an analysis finds.
/// </summary>
static class CompPresetChecks
{
    const int Sr = 48000;
    const double Seconds = 2.5;
    static readonly double[] Amounts = [0, 0.5, 1];

    /// <summary>Prints its rows; true when all of them pass.</summary>
    public static bool Check()
    {
        bool allOk = true;
        void Row(string name, string got, string want, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"{name,-36} {got,12} {want,10}  {(ok ? "igual" : "DISTINTO")}");
        }

        var all = CompPresets.All;
        var groups = CompPresets.Groups;
        int repeated = all.Length - all.Select(p => p.Id).Distinct().Count() + all.Length - all.Select(p => (p.Group, p.Label)).Distinct().Count();
        Row("Presets: ids y nombres sin repetir", $"{all.Length}", "sin repetir", repeated == 0);
        int orphan = all.Count(p => !groups.Any(g => g.Id == p.Group)) + groups.Count(g => all.Count(p => p.Group == g.Id) < 3);
        Row("Presets: todos en una categoría", $"{groups.Length} categorías", "ninguno afuera", orphan == 0);
        bool ordered = all.Select(p => p.Group).Distinct().SequenceEqual(groups.Select(g => g.Id))
            && Enumerable.Range(1, all.Length - 1).Count(i => all[i].Group != all[i - 1].Group) == groups.Length - 1;
        Row("Presets: categorías sin partir", ordered ? "en orden" : "partidas", "en orden", ordered);
        bool old = CompPresets.NormalizePreset("clean") == "arpeggio" && CompPresets.NormalizePreset("acoustic") == "rhythm" && CompPresets.NormalizePreset("level") == "rhythm"
            && all.All(p => CompPresets.NormalizePreset(p.Id) == p.Id);
        Row("Presets: los ids viejos siguen", old ? "sí" : "no", "sí", old);

        int badRange = 0;
        foreach (var p in all)
        {
            static bool In((double Min, double Max) r, double lo, double hi) => r.Min >= lo && r.Min <= hi && r.Max >= lo && r.Max <= hi;
            bool ok = In(p.Gr, 0, 24) && p.Gr.Max > p.Gr.Min && In(p.Ratio, 1, 20) && p.Ratio.Max >= p.Ratio.Min && In(p.AttackMs, 0.1, 100) && p.AttackMs.Max <= p.AttackMs.Min
                && In(p.ReleaseMs, 5, 800) && In(p.Knee, 0, 12) && In(p.Hpf, 10, 1000) && In(p.Mud, -12, 12) && In(p.Presence, -12, 12) && In(p.Air, -12, 12)
                && p.PresenceHz is >= 500 and <= 8000 && (double.IsNaN(p.TargetRmsDb) || p.TargetRmsDb is >= -32 and <= -14);
            if (!ok)
            {
                badRange++;
                Console.WriteLine($"  fuera de rango: {p.Id}");
            }
        }
        Row("Presets: valores dentro del rango", $"{badRange} mal", "0 mal", badRange == 0);

        // every preset on every take: the reduction it aims for, the level it promises, and nothing the compressor can't take
        (string Name, float[] Signal)[] takes = [("sostenida", Sustained()), ("punteada", Plucked()), ("golpes", Hits()), ("hablada", Spoken())];
        double worstGr = 0, worstLevel = 0, worstPeak = -120;
        string whereGr = "", whereLevel = "";
        int broken = 0;
        foreach (var (name, signal) in takes)
        {
            var env = CompPresets.Analyze([signal, signal], Sr);
            foreach (var p in all)
                foreach (double amount in Amounts)
                {
                    var s = CompPresets.UpdateComp(CompState.Defaults with { On = true, Preset = p.Id, Amount = amount }, env);
                    string at = $"{p.Id} {amount:0.0} {name}";
                    double aim = p.Gr.Min + (p.Gr.Max - p.Gr.Min) * amount;
                    double off = Math.Abs((s.EstGrDb ?? 0) - aim);
                    if (off > worstGr) (worstGr, whereGr) = (off, at);

                    // the level without LIM: it is what the make-up promises, and LIM only takes off the hits above its ceiling
                    var run = Run(signal, s with { Limit = false });
                    var limited = Run(signal, s with { Limit = true });
                    if (run.Any(x => !float.IsFinite(x)) || limited.Any(x => !float.IsFinite(x))) broken++;
                    worstPeak = Math.Max(worstPeak, Db(limited.Max(Math.Abs)));
                    double aimLevel = double.IsNaN(p.TargetRmsDb) ? env.RmsDb : p.TargetRmsDb;
                    double level = Math.Abs(CompPresets.Analyze([run, run], Sr).RmsDb - aimLevel);
                    if (level > worstLevel) (worstLevel, whereLevel) = (level, at);
                }
        }
        Console.WriteLine($"  peor reducción: {whereGr}; peor nivel: {whereLevel}");
        Row("Presets: reducción pedida", $"±{worstGr:0.0} dB", "±2 dB", worstGr <= 2);
        Row("Presets: nivel prometido", $"±{worstLevel:0.0} dB", "±5 dB", worstLevel <= 5);
        Row("Presets: pico con LIM", $"{worstPeak:0.0} dB", "<= -2.9 dB", worstPeak <= -2.9);
        Row("Presets: sin valores rotos", $"{broken}", "0", broken == 0);

        // the fixed values: the same without any take, and an analysis lands on them, and they carry it
        double worstTrim = 0, worstCarry = 0;
        string whereTrim = "";
        var near = CompPresets.Analyze([Scale(Sustained(), RefGain()), Scale(Sustained(), RefGain())], Sr);
        var quiet = CompPresets.Analyze([Scale(Sustained(), RefGain() * 0.1), Scale(Sustained(), RefGain() * 0.1)], Sr);
        bool follows = true;
        foreach (var p in all)
        {
            var start = CompState.Defaults with { On = true, Preset = p.Id, Amount = 0.5 };
            var at = CompPresets.UpdateComp(start, near);
            if (Math.Abs(at.ThresholdTrim) > worstTrim) (worstTrim, whereTrim) = (Math.Abs(at.ThresholdTrim), p.Id);
            var again = CompPresets.Apply(at);
            worstCarry = Math.Max(worstCarry, Math.Max(Math.Abs(again.Threshold - at.Threshold), Math.Abs(again.Makeup - at.Makeup)));
            var low = CompPresets.UpdateComp(start, quiet);
            follows &= CompPresets.Apply(low with { Amount = 0.7 }).Threshold < CompPresets.Apply(start with { Amount = 0.7 }).Threshold - 10;
        }
        Console.WriteLine($"  peor umbral fijo: {whereTrim}");
        Row("Presets: fijo contra medido", $"±{worstTrim:0.0} dB", "±2 dB", worstTrim <= 2);
        Row("Presets: el análisis queda igual", $"{worstCarry:0.0} dB", "0 dB", worstCarry == 0);
        Row("Presets: Cantidad sigue lo medido", follows ? "sí" : "no", "sí", follows);
        bool noTake = all.All(p => CompPresets.Apply(CompState.Defaults with { Preset = p.Id, Amount = 0.5 }) is { Threshold: < 0 and > -60, Makeup: >= -12 and <= 30 });
        Row("Presets: fijos sin toma", noTake ? "sí" : "no", "sí", noTake);
        return allOk;
    }

    /// <summary>What brings <see cref="Sustained"/> to the fixed values' typical peak.</summary>
    static double RefGain() => Math.Pow(10, CompPresets.RefPeakDb / 20) / Sustained().Max(Math.Abs);

    static float[] Scale(float[] x, double g) => [.. x.Select(v => (float)(v * g))];

    static double Db(double x) => x > 1e-9 ? 20 * Math.Log10(x) : -180;

    /// <summary>The take through the compressor, as a mono signal.</summary>
    static float[] Run(float[] signal, CompState s)
    {
        var l = (float[])signal.Clone();
        var r = (float[])signal.Clone();
        var comp = new Compressor(Sr)
        {
            Bypass = false, Threshold = s.Threshold, Ratio = s.Ratio, Attack = s.Attack, Release = s.Release, Knee = s.Knee, Makeup = s.Makeup,
            Limit = s.Limit, Ceiling = CompPresets.TrackLimitCeilingDb,
        };
        comp.Process(l, r);
        return l;
    }

    static float[] Make(double seconds, Func<double, double> f)
    {
        var x = new float[(int)(seconds * Sr)];
        for (int i = 0; i < x.Length; i++) x[i] = (float)f(i / (double)Sr);
        return x;
    }

    static float[] Sustained() => Make(Seconds, t => (0.15 + 0.1 * Math.Sin(2 * Math.PI * 0.7 * t)) * (Math.Sin(2 * Math.PI * 220 * t) + 0.4 * Math.Sin(2 * Math.PI * 660 * t)));

    static float[] Plucked() => Make(Seconds, t =>
    {
        double since = t % 0.4;
        double level = (int)(t / 0.4) % 3 == 0 ? 0.5 : 0.25;
        return level * Math.Exp(-since * 5) * Math.Sin(2 * Math.PI * 330 * t);
    });

    static float[] Hits()
    {
        var rng = new Random(7);
        return Make(Seconds, t =>
        {
            double since = t % 0.5;
            double level = (int)(t / 0.5) % 2 == 0 ? 0.7 : 0.4;
            return level * Math.Exp(-since * 22) * (0.6 * Math.Sin(2 * Math.PI * 90 * since) + 0.4 * (rng.NextDouble() * 2 - 1));
        });
    }

    static float[] Spoken() => Make(Seconds, t =>
    {
        double word = t % 0.9;
        double level = word < 0.55 ? 0.2 + 0.15 * Math.Sin(2 * Math.PI * 4 * t) : 0.002;
        return level * Math.Abs(Math.Sin(2 * Math.PI * 3 * t)) * (Math.Sin(2 * Math.PI * 140 * t) + 0.5 * Math.Sin(2 * Math.PI * 1100 * t));
    });
}
