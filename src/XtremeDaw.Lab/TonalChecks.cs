using System.Text.Json;
using XtremeDaw.Audio;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// The tonal balance (#210): rows of comprobar-dsp (the slot passes the sound bit for bit, pink noise reads flat, a
/// reference made of a known tilt has that tilt, Mid and Side read what they should, the punch of a sine is its 3 dB, the
/// live tap reads the same as the file, the slot keeps its own target) and <c>xtlab balance</c>, which reads a file against
/// a target the way the panel does.
/// </summary>
static class TonalChecks
{
    const int Sr = 48000, Block = 128;
    /// <summary>The test noises' length: 2^20 samples, about 22 s.</summary>
    const int NoiseLen = 1 << 20;

    /// <summary>Prints its rows; true when all of them pass.</summary>
    public static bool Check()
    {
        bool allOk = true;
        void Row(string name, string got, string want, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"{name,-36} {got,12} {want,10}  {(ok ? "igual" : "DISTINTO")}");
        }

        var pinkL = Colored(NoiseLen, 1, 1);
        var pinkR = Colored(NoiseLen, 1, 2);

        // 1. the slot never touches the sound, on or off, and goes without the shell
        var slots = new List<InsertSlot>();
        var added = ChannelInserts.Add(slots, TrackInserts.Tonal)!;
        foreach (bool on in new[] { true, false })
        {
            added.On = on;
            var spec = ChannelDsp.Specs(slots, -24)[0];
            var insert = TrackInserts.Create(spec.Kind, Sr);
            TrackInserts.Apply(insert, spec);
            var l = (float[])pinkL.Clone();
            var r = (float[])pinkR.Clone();
            for (int at = 0; at + Block <= Sr * 2; at += Block) insert.Process(l.AsSpan(at, Block), r.AsSpan(at, Block));
            bool same = l.AsSpan().SequenceEqual(pinkL) && r.AsSpan().SequenceEqual(pinkR) && insert is TonalInsert && insert.LatencySamples == 0;
            Row($"Balance: pasa bit a bit ({(on ? "ON" : "off")})", same ? "exacto" : "cambia", "exacto", same);
        }

        // 1b. the mixer's FX bypass and the master's OFF turn it off too (asked by the user, #210)
        added.On = true;
        var master = new MasterSettings();
        var onMaster = InsertSlot.New(TrackInserts.Tonal);
        onMaster.On = true;
        master.Slots.Add(onMaster);
        master.On = false;
        bool bypassOff = !ChannelDsp.Specs(slots, -24, bypass: true)[0].StageOn;
        bool masterOff = !ChannelDsp.MasterSpecs(master, 120)[0].StageOn;
        master.On = true;
        bool masterOn = ChannelDsp.MasterSpecs(master, 120)[0].StageOn;
        bool offOk = bypassOff && masterOff && masterOn;
        Row("Balance: se apaga con bypass/OFF", offOk ? "sí" : "no", "sí", offOk);

        // 1c. on the master it reads the output, after Salida (asked by the user, #210): loud pink noise into the
        // limiter reads at the limited level, not at the level that went in
        var bus = new MixGraphBus(Sr);
        bus.Configure(new MixBusSpec { Master = master.ToParams(), MasterInserts = ChannelDsp.MasterSpecs(master, 120) });
        var tonal = (TonalInsert)bus.Slots.Find(onMaster.Id)!;
        var sent = new TonalAnalyzer(Sr) { TimeSec = 1e9 };
        var outL = new float[Block];
        var outR = new float[Block];
        for (int at = 0; at + Block <= Sr * 2; at += Block)
        {
            for (int i = 0; i < Block; i++) { outL[i] = pinkL[at + i] * 8; outR[i] = pinkR[at + i] * 8; }
            bus.Process(outL, outR);
            tonal.Meter.Pull(tonal.Tap);
            sent.Feed(outL, outR);
        }
        double postOff = Math.Abs(tonal.Meter.LevelDb(TonalMode.Stereo) - sent.LevelDb(TonalMode.Stereo));
        Row("Balance en el master: después de Salida", $"{postOff:0.00} dB", "< 0,1 dB", postOff < 0.1);

        // 2. pink noise reads flat: the scale is constant-Q
        var meter = new TonalAnalyzer(Sr) { TimeSec = 1e9 };
        meter.Feed(pinkL, pinkR);
        var curve = new double[TonalScale.Points];
        meter.Read(TonalMode.Stereo, curve);
        double worst = 0;
        for (int i = 0; i < TonalScale.Points; i++)
            if (TonalScale.Freq(i) is >= 40 and <= 16000) worst = Math.Max(worst, Math.Abs(curve[i]));
        Row("Balance: ruido rosa plano 40-16k", $"{worst:0.00} dB", "< 1 dB", worst < 1);
        Console.WriteLine($"{"  (punch del ruido rosa)",-36} {$"{meter.CrestDb(TonalMode.Stereo):0.0} dB",12}");

        // 3. a reference made of pink noise is a pink target: the reading sits in it, band by band
        var pinkTarget = TonalReference.From(TonalReference.Pieces(pinkL, pinkR, Sr))!;
        Span<double> bands = stackalloc double[TonalScale.Bands], tMean = stackalloc double[TonalScale.Bands],
            tMin = stackalloc double[TonalScale.Bands], tMax = stackalloc double[TonalScale.Bands];
        TonalScale.BandValues(curve, bands);
        pinkTarget.Bands(tMean, tMin, tMax);
        double bandOff = 0;
        bool inside = true;
        for (int b = 0; b < TonalScale.Bands; b++)
        {
            bandOff = Math.Max(bandOff, Math.Abs(bands[b] - tMean[b]));
            inside &= bands[b] > tMin[b] && bands[b] < tMax[b];
        }
        Row("Balance: rosa contra referencia rosa", $"{bandOff:0.00} dB", "< 0,5 dB", bandOff < 0.5 && inside);

        // 4. a reference of brown noise (1/f²) falls 3 dB per octave on this scale, with a narrow range (steady sound)
        var brownL = Colored(NoiseLen, 2, 3);
        var brownR = Colored(NoiseLen, 2, 4);
        var brown = TonalReference.From(TonalReference.Pieces(brownL, brownR, Sr))!;
        int at100 = 14, at6k = at100 + 36;
        double slope = (brown.Mean[at6k] - brown.Mean[at100]) / 6;
        double width = brown.Max[30] - brown.Min[30];
        Row("Balance: referencia marrón, pendiente", $"{slope:0.00} dB/oct", "-3 ± 0,3", Math.Abs(slope + 3) < 0.3);
        Row("Balance: referencia estable, angosta", $"{width:0.0} dB", "< 4 dB", width < 4);

        // 5. Mid and Side: a pure side signal is empty in Mid and pink in Side; a mono one the other way round
        var neg = pinkL.Select(x => -x).ToArray();
        var ms = new TonalAnalyzer(Sr) { TimeSec = 1e9 };
        ms.Feed(pinkL.AsSpan(0, Sr * 6), neg.AsSpan(0, Sr * 6));
        bool midEmpty = !ms.Read(TonalMode.Mid, curve) && ms.LevelDb(TonalMode.Mid) < TonalAnalyzer.SilenceDb;
        bool sideRead = ms.Read(TonalMode.Side, curve) && Math.Abs(curve[30]) < 2;
        Row("Balance: Side puro, Mid vacío", $"{ms.LevelDb(TonalMode.Mid):0} dB", "vacío", midEmpty && sideRead);
        var mono = new TonalAnalyzer(Sr) { TimeSec = 1e9 };
        mono.Feed(pinkL.AsSpan(0, Sr * 6), pinkL.AsSpan(0, Sr * 6));
        bool sideEmpty = !mono.Read(TonalMode.Side, curve) && mono.Read(TonalMode.Mid, curve);
        Row("Balance: mono, Side vacío", $"{mono.LevelDb(TonalMode.Side):0} dB", "vacío", sideEmpty);

        // 6. the punch of a steady 60 Hz sine is its crest factor, 3 dB
        var sine = new float[Sr * 4];
        for (int i = 0; i < sine.Length; i++) sine[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 60 * i / Sr));
        var punch = new TonalAnalyzer(Sr) { TimeSec = 1e9 };
        punch.Feed(sine, sine);
        double crest = punch.CrestDb(TonalMode.Stereo);
        Row("Balance: punch de un seno de 60 Hz", $"{crest:0.00} dB", "3,0 ± 0,3", Math.Abs(crest - 3.01) < 0.3);

        // 7. the live path (the slot's tap, read now and then) reads exactly what the file read gives
        var live = new TonalInsert(Sr);
        int sincePull = 0;
        var l2 = new float[Block];
        var r2 = new float[Block];
        for (int at = 0; at + Block <= Sr * 8; at += Block)
        {
            pinkL.AsSpan(at, Block).CopyTo(l2);
            pinkR.AsSpan(at, Block).CopyTo(r2);
            live.Process(l2, r2);
            if ((sincePull += Block) >= 2400) { sincePull = 0; live.Meter.Pull(live.Tap); }
        }
        live.Meter.Pull(live.Tap);
        var direct = new TonalAnalyzer(Sr);
        direct.Feed(pinkL.AsSpan(0, Sr * 8), pinkR.AsSpan(0, Sr * 8));
        var a = new double[TonalScale.Points];
        var b2 = new double[TonalScale.Points];
        live.Meter.Read(TonalMode.Stereo, a);
        direct.Read(TonalMode.Stereo, b2);
        double liveDiff = a.Zip(b2, (x, y) => Math.Abs(x - y)).Max();
        Row("Balance: en vivo, igual que directo", liveDiff == 0 ? "exacto" : $"{liveDiff:0.0e0}", "exacto", liveDiff == 0);

        // 8. the slot keeps its own target in the project; without one, "Personalizado" falls back to the first genre
        var own = InsertSlot.New(TrackInserts.Tonal);
        own.Tonal = TonalCurveSettings.From(brown, "Ref", [@"C:\x\ref.wav"]);
        own.Stage!.Values["target"] = FxDefs.TonalCustom;
        own.Stage.Values["mode"] = 2;
        var back = JsonSerializer.Deserialize<InsertSlot>(JsonSerializer.Serialize(own, ProjectStore.Json), ProjectStore.Json)!;
        _ = ChannelDsp.Specs([back], -24); // normalizes it, as opening a project does
        bool kept = FxDefs.TonalTargetOf(back) is { Id: "custom" } t && Math.Abs(t.Mean[20] - brown.Mean[20]) < 0.01
            && FxDefs.TonalModeOf(back) == TonalMode.Side && back.Tonal!.Files is ["ref.wav"];
        var bare = InsertSlot.New(TrackInserts.Tonal);
        bare.Stage!.Values["target"] = FxDefs.TonalCustom;
        _ = ChannelDsp.Specs([bare], -24);
        bool fell = FxDefs.TonalTargetOf(bare)?.Id == TonalTargets.Factory[0].Id;
        Row("Balance: guarda su objetivo propio", kept && fell ? "sí" : "no", "sí", kept && fell);

        // 9. the factory targets are levelled and their ranges hold their curve
        int good = 0;
        foreach (var f in TonalTargets.Factory)
        {
            var m = (double[])f.Mean.Clone();
            TonalScale.Level(m);
            bool ok = m.Zip(f.Mean, (x, y) => Math.Abs(x - y)).Max() < 1e-9 && f.Min.Zip(f.Max, (lo, hi) => hi > lo).All(x => x)
                && f.Mean.Select((v, i) => v > f.Min[i] && v < f.Max[i]).All(x => x) && f.CrestMax > f.CrestMin;
            if (ok) good++;
        }
        Row("Balance: objetivos de fábrica", $"{good}/{TonalTargets.Factory.Length}", "todos", good == TonalTargets.Factory.Length);

        // 10. what 0.9.19 saved keeps its meaning (#265): the first six genres 0..5, the slot's own 6; every genre has a value
        string[] old = [TonalTargets.Pop, TonalTargets.Rock, TonalTargets.Edm, TonalTargets.HipHop, TonalTargets.Acoustic, TonalTargets.Orchestral];
        bool same6 = old.Select(FxDefs.TonalValue).SequenceEqual(Enumerable.Range(0, 6)) && FxDefs.TonalCustom == 6;
        var values = TonalTargets.Factory.Select(f => FxDefs.TonalValue(f.Id)).ToArray();
        bool allValued = values.Distinct().Count() == values.Length && !values.Contains(FxDefs.TonalCustom);
        bool roundTrip = TonalTargets.Factory.All(f =>
        {
            var s = InsertSlot.New(TrackInserts.Tonal);
            s.Stage!.Values["target"] = FxDefs.TonalValue(f.Id);
            var b = JsonSerializer.Deserialize<InsertSlot>(JsonSerializer.Serialize(s, ProjectStore.Json), ProjectStore.Json)!;
            _ = ChannelDsp.Specs([b], -24);
            return FxDefs.TonalTargetOf(b)?.Id == f.Id;
        });
        bool keptOwn = FxDefs.TonalTargetOf(back)?.Id == "custom";
        bool compat = same6 && allValued && roundTrip && keptOwn;
        Row("Balance: objetivos guardados", compat ? "sí" : "no", "sí", compat);

        // 11. the dubstep family (#265): the sub peaks at 40-70 Hz with the high-pass under 30 Hz; modern dubstep has a
        // denser 150-300 Hz and a hotter 2-4 kHz than EDM, riddim limits hardest, deep dubstep is darker and freer
        var dub = TonalTargets.Find(TonalTargets.Dubstep)!;
        var rid = TonalTargets.Find(TonalTargets.Riddim)!;
        var deep = TonalTargets.Find(TonalTargets.DeepDubstep)!;
        var edm = TonalTargets.Find(TonalTargets.Edm)!;
        double Avg(TonalTarget t, double lo, double hi)
        {
            double sum = 0;
            int c = 0;
            for (int i = 0; i < TonalScale.Points; i++)
                if (TonalScale.Freq(i) is var f && f >= lo && f <= hi) { sum += t.Mean[i]; c++; }
            return sum / c;
        }
        double PeakHz(TonalTarget t)
        {
            int best = 0;
            for (int i = 1; i < TonalScale.Points; i++) if (t.Mean[i] > t.Mean[best]) best = i;
            return TonalScale.Freq(best);
        }
        bool subOk = new[] { dub, rid, deep }.All(t => PeakHz(t) is >= 40 and <= 70 && Avg(t, 20, 30) < Avg(t, 45, 63) - 4);
        (string Name, bool Ok)[] parts =
        [
            ("sub", subOk),
            ("growl", Avg(dub, 150, 300) > Avg(edm, 150, 300)),
            ("1-5k", Avg(dub, 2000, 4000) > Avg(edm, 2000, 4000)),
            ("punch", rid.CrestMax < dub.CrestMax && deep.CrestMax > dub.CrestMax),
            ("deep", Avg(deep, 6000, 16000) < Avg(dub, 6000, 16000) - 2 && Avg(deep, 40, 63) > Avg(dub, 40, 63)),
        ];
        var failed = parts.Where(p => !p.Ok).Select(p => p.Name).ToArray();
        Row("Balance: familia dubstep", failed.Length == 0 ? "sí" : string.Join(",", failed), "sí", failed.Length == 0);
        Console.WriteLine($"{"  (dubstep: pico sub)",-36} {$"{PeakHz(dub):0} Hz",12}");
        return allOk;
    }

    /// <summary>
    /// Gaussian noise whose power falls as 1/f^<paramref name="exponent"/> (1 = pink, 2 = brown), shaped in one FFT the size
    /// of the whole signal, at about −20 dBFS RMS.
    /// </summary>
    static float[] Colored(int n, double exponent, int seed)
    {
        var rnd = new Random(seed);
        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < n; i++)
            re[i] = Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
        var fft = new TonalFft(n);
        fft.Transform(re, im);
        re[0] = im[0] = 0;
        for (int k = 1; k <= n / 2; k++)
        {
            double g = Math.Pow(k, -exponent / 2);
            re[k] *= g; im[k] *= g;
            if (k < n / 2) { re[n - k] *= g; im[n - k] *= g; }
        }
        fft.Transform(re, im, inverse: true);
        double rms = Math.Sqrt(re.Sum(x => x * x) / n);
        return [.. re.Select(x => (float)(0.1 * x / rms))];
    }

    static readonly string[] ModeNames = ["estereo", "mid", "side"];
    static readonly string[] BandNames = ["Graves", "Medios graves", "Medios agudos", "Agudos"];

    /// <summary>
    /// xtlab balance archivo [--objetivo pop|rock|edm|hiphop|acoustic|orchestral|dubstep|riddim|deepdubstep|…] [--referencia a.wav,b.wav]
    /// [--modo estereo|mid|side] [--curva]: the file's tonal balance against a target, as the panel shows it.
    /// </summary>
    public static int Run(Args a)
    {
        if (a.Flag("comprobar")) return Check() ? 0 : 3;
        string file = a.Positional.ElementAtOrDefault(0) ?? throw new ArgumentException("balance: falta el archivo.");
        var mode = (TonalMode)Math.Max(0, Array.IndexOf(ModeNames, a.Get("modo", "estereo")));
        TonalTarget target;
        if (a.Get("referencia") is { } refs)
        {
            var paths = refs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            target = TonalReference.AnalyzeFiles(paths, Sr) ?? throw new ArgumentException("balance: las referencias no tienen sonido.");
            Console.WriteLine($"Objetivo: {paths.Length} referencia(s) · punch {target.CrestMin:0.0}–{target.CrestMax:0.0} dB");
        }
        else
        {
            string id = a.Get("objetivo", TonalTargets.Pop);
            target = TonalTargets.Find(id) ?? throw new ArgumentException($"balance: no conozco el objetivo «{id}»: {string.Join(", ", TonalTargets.Factory.Select(t => t.Id))}.");
            Console.WriteLine($"Objetivo: {id}");
        }
        var audio = MediaDecoder.Load(file, Sr);
        var meter = new TonalAnalyzer(Sr) { TimeSec = 1e9 };
        meter.Feed(audio.Left, audio.Right);
        var curve = new double[TonalScale.Points];
        if (!meter.Read(mode, curve))
        {
            Console.WriteLine($"Sin señal en {ModeNames[(int)mode]}.");
            return 1;
        }
        Span<double> bands = stackalloc double[TonalScale.Bands], mean = stackalloc double[TonalScale.Bands],
            min = stackalloc double[TonalScale.Bands], max = stackalloc double[TonalScale.Bands];
        TonalScale.BandValues(curve, bands);
        target.Bands(mean, min, max);
        Console.WriteLine($"Modo: {ModeNames[(int)mode]} · nivel {meter.LevelDb(mode):0.0} dBFS");
        for (int b = 0; b < TonalScale.Bands; b++)
        {
            string where = bands[b] < min[b] ? "por debajo" : bands[b] > max[b] ? "por encima" : "dentro";
            Console.WriteLine($"  {BandNames[b],-14} {bands[b],6:+0.0;-0.0} dB   objetivo {min[b],6:+0.0;-0.0} a {max[b],5:+0.0;-0.0}   {where}");
        }
        double crest = meter.CrestDb(mode);
        Console.WriteLine($"  {"Punch (graves)",-14} {crest,6:0.0} dB   objetivo {target.CrestMin,6:0.0} a {target.CrestMax,5:0.0}   "
            + (double.IsNaN(crest) ? "sin graves" : crest < target.CrestMin ? "por debajo" : crest > target.CrestMax ? "por encima" : "dentro"));
        if (a.Flag("curva"))
            for (int i = 0; i < TonalScale.Points; i++)
                Console.WriteLine($"    {TonalScale.Freq(i),7:0} Hz {curve[i],6:+0.0;-0.0}   [{target.Min[i],6:+0.0;-0.0} {target.Max[i],6:+0.0;-0.0}]");
        return 0;
    }
}
