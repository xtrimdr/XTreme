using System.Text.Json;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// The native effects' shared parts, rows of comprobar-dsp: <see cref="FxShell"/> (Mix, output, on and off), the master's
/// stages as slots against the fixed chain, the list of parameters (<see cref="FxDefs"/>), the slot's trip through the
/// project, and the limiter slot's latency in the export.
/// </summary>
static class FxChecks
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

        var tone = Signal(Sr, i => 0.4 * Math.Sin(2 * Math.PI * 330 * i / Sr));
        var sat = SatParams.From(true, SatMode.Tube, 0.7);

        // 1. on, Wet 100 %, Dry 0 %, 0 dB: the effect's own output, bit for bit; off: the dry signal
        var direct = new SatInsert(Sr);
        direct.Set(sat);
        var wet = Run(direct, tone);
        var spec = new InsertSpec("a", TrackInserts.Sat) { Sat = sat };
        double on = MaxDiff(Run(Shell(spec), tone), wet);
        Row("Efecto al 100 %: igual que solo", Diff(on), "exacto", on == 0);
        double off = MaxDiff(Run(Shell(spec with { Sat = sat with { Bypass = true } }), tone), tone);
        Row("Efecto apagado = sin tocar", Diff(off), "exacto", off == 0);

        // 2. Dry, Wet and output, each on its own
        var half = Run(Shell(spec with { Wet = 0.5, Dry = 0.5 }), tone);
        double mixDiff = 0;
        for (int i = 0; i < half.Length; i++) mixDiff = Math.Max(mixDiff, Math.Abs(half[i] - (tone[i] + wet[i]) / 2));
        Row("Dry y Wet al 50 %: mitad y mitad", $"{mixDiff:0.0e0}", "< 1e-6", mixDiff < 1e-6);
        var both = Run(Shell(spec with { Wet = 1, Dry = 1 }), tone);
        double bothDiff = 0;
        for (int i = 0; i < both.Length; i++) bothDiff = Math.Max(bothDiff, Math.Abs(both[i] - (tone[i] + wet[i])));
        Row("Dry y Wet al 100 %: en paralelo", $"{bothDiff:0.0e0}", "< 1e-6", bothDiff < 1e-6);
        var quiet = Run(Shell(spec with { OutputDb = -6 }), tone);
        double outDiff = 0;
        for (int i = 0; i < quiet.Length; i++) outDiff = Math.Max(outDiff, Math.Abs(quiet[i] - wet[i] * Math.Pow(10, -6.0 / 20)));
        Row("Salida -6 dB", $"{outDiff:0.0e0}", "< 1e-6", outDiff < 1e-6);

        // 3. turning a strong effect on while it plays glides: no sample jumps past what the tone itself moves
        var loud = new InsertSpec("a", TrackInserts.Sat) { Sat = sat, OutputDb = 12 };
        var shell = Shell(loud with { Sat = sat with { Bypass = true } });
        var moved = new float[tone.Length];
        var l = new float[Block];
        var r = new float[Block];
        for (int at = 0; at + Block <= tone.Length; at += Block)
        {
            if (at == Sr / 2) TrackInserts.Apply(shell, loud);
            tone.AsSpan(at, Block).CopyTo(l);
            tone.AsSpan(at, Block).CopyTo(r);
            shell.Process(l, r);
            l.CopyTo(moved.AsSpan(at));
        }
        double jump = 0, step = 0;
        for (int i = Sr / 2 - Block; i < Sr / 2 + 2 * FxShell.FadeFrames; i++)
        {
            jump = Math.Max(jump, Math.Abs(moved[i] - moved[i - 1]));
            step = Math.Max(step, Math.Abs(tone[i] - tone[i - 1]));
        }
        Row("Prender mientras suena, sin clic", $"{jump:0.000}", $"< {step * 6:0.000}", jump < step * 6);

        // 4. a project's fixed chain moved to slots sounds exactly like it did (the soft clip stays in Salida)
        var master = new MasterSettings { On = true, LowCutHz = 40, MudDb = -2, LowsDb = 1.5, PresenceDb = 2, AirDb = 1, TiltDb = 0.5, TapeAmount = 0.4, Width = 1.2, MonoBassHz = 100, ClipDb = 1, PickAmount = 0.5 };
        var fixedBus = new MixGraphBus(Sr);
        fixedBus.Configure(new MixBusSpec { Master = master.ToParams() });
        fixedBus.Reset();
        var song = Signal(Sr * 2, i => 0.7 * Math.Sin(2 * Math.PI * 110 * i / Sr) * (i % 12000 < 4000 ? 1 : 0.2) + 0.2 * Math.Sin(2 * Math.PI * 3100 * i / Sr));
        var fixedOut = RunBus(fixedBus, song);
        var chained = ChannelInserts.AddMasterChain(master);
        bool chainOk = chained is { Count: 5 } && master.On && ChannelInserts.AddMasterChain(master) == null;
        Row("Cadena completa: cinco etapas", $"{chained?.Count ?? 0}", "5", chainOk);
        var slotBus = new MixGraphBus(Sr);
        slotBus.Configure(new MixBusSpec { Master = master.ToParams(), MasterInserts = ChannelDsp.MasterSpecs(master, 120) });
        slotBus.Reset();
        double chainDiff = MaxDiff(RunBus(slotBus, song), fixedOut);
        Row("Cadena en slots = cadena del master", Diff(chainDiff), "exacto", chainDiff == 0);
        // the same through a project saved before: read and normalized, it brings its slots
        var old = JsonSerializer.Deserialize<MasterSettings>("""{"on":true,"lowCutHz":40,"mudDb":-2,"lowsDb":1.5,"presenceDb":2,"airDb":1,"tiltDb":0.5,"tapeAmount":0.4,"width":1.2,"monoBassHz":100,"clipDb":1,"pickAmount":0.5}""", ProjectStore.Json)!;
        var project = new Project();
        project.Settings.Master = old;
        project.Normalize();
        var movedBus = new MixGraphBus(Sr);
        movedBus.Configure(new MixBusSpec { Master = old.ToParams(), MasterInserts = ChannelDsp.MasterSpecs(old, 120) });
        movedBus.Reset();
        double movedDiff = old.Slots.Count == 5 ? MaxDiff(RunBus(movedBus, song), fixedOut) : 1;
        Row("Proyecto viejo: migra y suena igual", Diff(movedDiff), "exacto", movedDiff == 0);
        var untouched = new Project();
        untouched.Normalize();
        Row("Proyecto nuevo: master sin efectos", $"{untouched.Settings.Master.Slots.Count}", "0", untouched.Settings.Master.Slots.Count == 0);
        // a look lands on the slots wherever they were moved to, and leaves the rest alone
        ChannelInserts.Move(old.Slots, old.Slots[4].Id, -4);
        var width = old.Slots[0];
        old.ApplyLook(MasterLooks.All.First(l => l.Id == "front"), 1);
        bool lookOk = old.Slots.Count == 5 && old.Slots[0] == width && width.Stage!.Values["width"] == 0.9 && old.Slots.Find(s => s.Type == TrackInserts.Color)!.Stage!.Values["presence"] == 2.5;
        Row("Look sobre la cadena reordenada", lookOk ? "sí" : "no", "sí", lookOk);

        // 5. the list: every default in its range and every knob comes back to its value
        int bad = 0, count = 0;
        foreach (string kind in new[] { InsertSlot.SatKind, InsertSlot.CompKind, InsertSlot.GateKind, InsertSlot.ReverbKind, InsertSlot.DelayKind,
            TrackInserts.Clean, TrackInserts.Bands, TrackInserts.Color, TrackInserts.Tape, TrackInserts.Width, TrackInserts.Clip, TrackInserts.Limit })
        {
            var slot = InsertSlot.New(kind);
            foreach (var p in FxDefs.Params(kind).Concat(FxDefs.Common(kind)))
            {
                count++;
                bool range = p.OffAtZero ? p.Default == 0 || p.Default >= p.Min : p.Default >= p.Min && p.Default <= p.Max;
                double back = p.FromKnob(p.ToKnob(p.Default));
                if (!range || Math.Abs(back - p.Default) > Math.Max(p.Step, 1e-9)) bad++;
                p.Set(slot, p.Default);
                if (Math.Abs(p.Get(slot) - p.Default) > 1e-9) bad++;
            }
        }
        Row($"Parámetros: {count} en rango", $"{bad} mal", "0", bad == 0);

        // 6. the slot's trip through the project; what is broken or missing comes back at its default
        var saved = InsertSlot.New(TrackInserts.Width);
        saved.On = true;
        saved.Wet = 0.6;
        saved.Dry = 0.3;
        saved.OutputDb = -3;
        saved.Stage!.Values["width"] = 1.5;
        var read = JsonSerializer.Deserialize<InsertSlot>(JsonSerializer.Serialize(saved, ProjectStore.Json), ProjectStore.Json)!;
        var broken = JsonSerializer.Deserialize<InsertSlot>("""{"type":"limiter","mix":9,"stage":{"on":true,"values":{"ceiling":-400}}}""", ProjectStore.Json)!;
        var specs = ChannelDsp.Specs([read, broken], -24);
        bool savedOk = specs.Length == 2 && specs[0].Stage.Width == 1.5 && specs[0].StageOn && specs[0].Wet == 0.6 && specs[0].Dry == 0.3 && specs[0].OutputDb == -3
            && specs[1].Stage.LimCeilingDb == MasterLimiter.CeilingMinDb && specs[1].Stage.LimReleaseMs == MasterLimiter.ReleaseMs && specs[1].Wet == 1 && specs[1].Dry == 0;
        var oldMix = JsonSerializer.Deserialize<InsertSlot>("""{"type":"sat","mix":0.25}""", ProjectStore.Json)!;
        var oldSpec = ChannelDsp.Specs([oldMix], -24)[0];
        Row("Mix de antes pasa a Dry y Wet", $"{oldSpec.Dry:0.00} / {oldSpec.Wet:0.00}", "0.75 / 0.25", oldSpec.Wet == 0.25 && oldSpec.Dry == 0.75);
        Row("Efecto del master: guarda y repara", savedOk ? "sí" : "no", "sí", savedOk);

        // 7. a limiter slot on the master: its look-ahead is cut from the export, so nothing moves in time
        var click = new float[Sr];
        click[Sr / 2] = 0.5f;
        var clips = new[] { new MixClipSpec { Left = click, Right = click, DurationSec = 1 } };
        var tracks = new[] { new MixTrackSpec { Clips = clips } };
        var limiter = InsertSlot.New(TrackInserts.Limit);
        limiter.On = true;
        var (plain, _) = MixRenderer.Render(tracks, new MixBusSpec(), 1, Sr, limit: false);
        var (limited, _) = MixRenderer.Render(tracks, new MixBusSpec { MasterInserts = ChannelDsp.Specs([limiter], -24) }, 1, Sr, limit: false);
        int at0 = Array.FindIndex(plain, v => v != 0), at1 = Array.FindIndex(limited, v => v != 0);
        Row("Limitador en el master, sin correrse", $"{at1 - at0} cuadros", "0", at0 == at1 && at0 == Sr / 2);
        // its Dry comes out as late as its Wet: nothing combs
        limiter.Wet = 0;
        limiter.Dry = 1;
        var (bypassed, _) = MixRenderer.Render(tracks, new MixBusSpec { MasterInserts = ChannelDsp.Specs([limiter], -24) }, 1, Sr, limit: false);
        double limDiff = MaxDiff(bypassed, plain);
        Row("Limitador: su Dry llega alineado", Diff(limDiff), "exacto", limDiff == 0);

        return allOk;
    }

    static ITrackInsert Shell(InsertSpec spec)
    {
        var insert = TrackInserts.Create(spec.Kind, Sr);
        TrackInserts.Apply(insert, spec);
        return insert;
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

    static float[] RunBus(MixGraphBus bus, float[] x)
    {
        var y = new float[x.Length];
        var l = new float[Block];
        var r = new float[Block];
        for (int at = 0; at + Block <= x.Length; at += Block)
        {
            x.AsSpan(at, Block).CopyTo(l);
            for (int i = 0; i < Block; i++) r[i] = l[i] * 0.8f;
            bus.Process(l, r);
            l.CopyTo(y.AsSpan(at));
        }
        return y;
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
}
