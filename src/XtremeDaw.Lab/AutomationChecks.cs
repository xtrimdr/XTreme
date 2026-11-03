using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// xtlab comprobar-dsp, the lanes (<see cref="Automations"/>) through the export's mix: a flat lane changes nothing, and
/// the fader, the pan, the mute, a send that starts at nothing and a native effect's knob each move the sound to the
/// value their law says; closing a gap keeps both sides, and a lane survives being saved. A steady signal (0.5 on both
/// sides) makes every gain readable off one sample.
/// </summary>
static class AutomationChecks
{
    const int Sr = 48000;
    const double Sec = 2;

    static float[] Steady => Enumerable.Repeat(0.5f, (int)(Sr * Sec)).ToArray();

    static Project With(Track t, params Bus[] buses)
    {
        var p = new Project { Tracks = [t] };
        p.Settings.Buses = [.. buses];
        return p;
    }

    static (float[] L, float[] R) Mix(Project p)
    {
        var nodes = ChannelDsp.Nodes(p);
        var clip = new MixClipSpec { Left = Steady, Right = Steady, DurationSec = Sec };
        return MixRenderer.Render([ChannelDsp.Track(p, nodes, p.Tracks[0], [clip], -24)], ChannelDsp.Bus(p), Sec, Sr, limit: false);
    }

    static AutomationLane Lane(string target, params (double Sec, double Value)[] points) => new()
    {
        Target = target,
        Blocks = [new() { LengthSec = points[^1].Sec + 1, Points = [.. points.Select(x => new AutomationPoint { Sec = x.Sec, Value = x.Value })] }],
    };

    public static bool Check()
    {
        bool ok = true;
        void Say(string what, bool good, string detail = "")
        {
            Console.WriteLine($"{(good ? "ok   " : "FALLA")} automatización: {what} {detail}");
            ok &= good;
        }
        bool Near(double a, double b, double tol = 0.01) => Math.Abs(a - b) <= tol;
        int At(double sec) => (int)(sec * Sr);

        var plain = Mix(With(new Track()));
        var flat = Mix(With(new Track { Automation = [Lane(AutoTarget.Volume, (0, Math.Sqrt(1 / 1.5)))] }));
        Say("un carril plano suena igual", Near(flat.L[At(1)], plain.L[At(1)], 1e-4), $"{flat.L[At(1)]:0.0000} / {plain.L[At(1)]:0.0000}");

        // the fader's position goes down from 1 to 0 in 2 s: at 1 s it is at 0.5, 1.5 × 0.5² = 0.375
        var fader = Mix(With(new Track { Automation = [Lane(AutoTarget.Volume, (0, 1), (2, 0))] }));
        Say("el volumen sigue la ley del fader", Near(fader.L[At(1)], 0.5 * 0.375), $"{fader.L[At(1)]:0.0000}");
        double jump = 0;
        for (int i = 1; i < fader.L.Length; i++) jump = Math.Max(jump, Math.Abs(fader.L[i] - fader.L[i - 1]));
        Say("el volumen no da saltos", jump < 1e-3, $"{jump:0.00000}");

        // a full tension goes as (e^(10t) - 1) / (e^10 - 1): halfway it is 1 / (e^5 + 1) of the way down from 1 to 0
        var bentLane = Lane(AutoTarget.Volume, (0, 1), (2, 0));
        bentLane.Blocks[0].Points[0].Tension = 1;
        bentLane.Touch();
        double mid = 1 - 1 / (Math.Exp(5) + 1);
        var bent = Mix(With(new Track { Automation = [bentLane] }));
        Say("una curva cambia el tramo", Near(bent.L[At(1)], 0.5 * 1.5 * mid * mid), $"{bent.L[At(1)]:0.0000}");
        Say("la curva se ve como suena", Near(Automations.PositionAt(bentLane, false, 1), mid, 1e-6));
        Say("la curva se guarda", ProjectLibrary.Clone(bentLane).Blocks[0].Points[0].Tension == 1);
        Say("la tensión pasa por donde se la lleva", Near(AutoShapes.Exp(0.5, AutoShapes.TensionThrough(0.2)), 0.2, 1e-9));

        // each shape keeps its ends, is saved by its name, and a block cut anywhere sounds the same
        foreach (var shape in Enum.GetValues<AutoShape>())
        {
            var lane = Lane(AutoTarget.Pan, (0, 0.1), (2, 0.9));
            lane.Blocks[0].Points[0].Shape = shape;
            lane.Blocks[0].Points[0].Tension = shape == AutoShape.Curve || shape == AutoShape.Double ? 0.6 : -0.5;
            lane.Touch();
            var json = System.Text.Json.JsonSerializer.Serialize(lane, ProjectStore.Json);
            var back = System.Text.Json.JsonSerializer.Deserialize<AutomationLane>(json, ProjectStore.Json)!;
            bool ends = Near(AutoShapes.Along(shape, 0, 0.3), 0, 1e-9) && (shape == AutoShape.Hold || Near(AutoShapes.Along(shape, 0.99999, 0.3), 1, 1e-3));
            Say($"forma {shape}: empieza y llega a sus puntos, se guarda", ends && back.Blocks[0].Points[0].Shape == shape && back.Blocks[0].Points[0].Tension == lane.Blocks[0].Points[0].Tension, json.Contains("\"shape\"") || shape == AutoShape.Curve ? "" : "sin nombre");
            double worst = 0;
            var cuts = ProjectLibrary.Clone(lane);
            Automations.Split(cuts, cuts.Blocks[0].Id, 0.7, false);
            Automations.Split(cuts, cuts.Blocks[^1].Id, 1.31, false);
            for (double t = 0; t <= 2; t += 0.0037)
                worst = Math.Max(worst, Math.Abs(Automations.PositionAt(cuts, false, t) - Automations.PositionAt(lane, false, t)));
            // the wave and the smooth stairs are written as short lines; the rest stays exact
            bool lines = shape is AutoShape.Wave or AutoShape.SmoothStairs;
            Say($"forma {shape}: dividir no cambia el sonido", worst < (lines ? 0.02 : 1e-6), $"{worst:0.000000}");
        }

        // the first bends were powers: an old project's curve becomes the one through the same middle
        var bentOld = System.Text.Json.JsonSerializer.Deserialize<AutomationLane>("{\"target\":\"pan\",\"blocks\":[{\"lengthSec\":3,\"points\":[{\"sec\":0,\"value\":0,\"curve\":1},{\"sec\":2,\"value\":1}]}]}", ProjectStore.Json)!;
        var oldBent = new Project { Tracks = [new Track { Automation = [bentOld] }] };
        oldBent.Normalize();
        Say("una curva vieja pasa por el mismo medio", Near(Automations.PositionAt(bentOld, false, 1), 0.0625, 1e-4)
            && !System.Text.Json.JsonSerializer.Serialize(bentOld, ProjectStore.Json).Contains("\"curve\""), $"{Automations.PositionAt(bentOld, false, 1):0.0000}");

        // hard left at the end: the left side takes both, the right one goes silent
        var pan = Mix(With(new Track { Automation = [Lane(AutoTarget.Pan, (0, 0.5), (1, 0))] }));
        Say("el paneo sigue su carril", Near(pan.R[At(1.5)], 0) && Near(pan.L[At(1.5)], 1), $"L {pan.L[At(1.5)]:0.000} R {pan.R[At(1.5)]:0.000}");

        var mute = Mix(With(new Track { Automation = [Lane(AutoTarget.Mute, (0, 0), (1, 1))] }));
        Say("el mute es un escalón", Near(mute.L[At(0.9)], 0.5) && Near(mute.L[At(1.1)], 0), $"{mute.L[At(0.9)]:0.000} -> {mute.L[At(1.1)]:0.000}");

        // a send at level 0 still sounds when its lane raises it: dry 0.5 + the bus's share
        var bus = new Bus { Name = "B" };
        var sent = new Track { Automation = [Lane(AutoTarget.Send(bus.Id), (0, 0), (2, 1))] };
        sent.Channel.Sends.Add(new Send { BusId = bus.Id, Level = 0, On = true });
        var send = Mix(With(sent, bus));
        Say("un envío en cero sigue su carril", Near(send.L[At(1)], 0.75), $"{send.L[At(1)]:0.000}");

        // an effect's output: -24..12 dB, its middle is -6 dB; the 10 ms glide is over long before the second
        var slot = InsertSlot.New(InsertSlot.EqKind);
        slot.On = true;
        var fx = new Track { Automation = [Lane(AutoTarget.Fx(slot.Id, "out"), (0, 2.0 / 3), (0.5, 0.5), (2, 0.5))] };
        fx.Channel.Inserts = [slot];
        var fxMix = Mix(With(fx));
        Say("la perilla de un efecto sigue su carril", Near(fxMix.L[At(1)], 0.5 * Math.Pow(10, -6.0 / 20)), $"{fxMix.L[At(1)]:0.0000}");
        var off = new Track { Automation = [Lane(AutoTarget.Fx(slot.Id, "on"), (0, 1), (1, 0))] };
        off.Channel.Inserts = [ProjectLibrary.Clone(slot)];
        Say("el encendido de un efecto es un interruptor", Automations.Stepped(off, off.Automation![0].Target));

        var cut = new Track { Automation = [Lane(AutoTarget.Volume, (0, 0), (1, 1), (3, 0), (4, 1))] };
        Automations.CloseGap(cut, 1, 3);
        var left = cut.Automation![0];
        Say("cerrar un hueco corre lo que sigue", Near(Automations.PositionAt(left, false, 2), 1), $"{Automations.PositionAt(left, false, 2):0.000}");

        // a block of a second going 0 -> 1, repeated: each lap starts again, and after the last it holds
        var laps = new AutomationLane { Target = AutoTarget.Pan, Blocks = [new() { LengthSec = 1, Points = [new() { Sec = 0, Value = 0 }, new() { Sec = 1, Value = 1 }] }] };
        Automations.Repeat(laps, laps.Blocks[0].Id, 3);
        Say("repetir un bloque N veces", laps.Blocks.Count == 4 && Near(Automations.PositionAt(laps, false, 2.5), 0.5) && Near(Automations.PositionAt(laps, false, 3.25), 0.25) && Near(Automations.PositionAt(laps, false, 9), 1));
        var copyOf = laps.Blocks[1];
        copyOf.Points[1].Value = 0.5;
        laps.Touch();
        Say("las copias son independientes", Near(Automations.PositionAt(laps, false, 0.5), 0.5) && Near(Automations.PositionAt(laps, false, 1.5), 0.25));

        // two blocks with a gap: it holds the first one's end, and the second comes in at once
        var gap = new AutomationLane { Target = AutoTarget.Pan, Blocks =
        [
            new() { AtSec = 0, LengthSec = 1, Points = [new() { Sec = 0, Value = 0.2 }] },
            new() { AtSec = 2, LengthSec = 1, Points = [new() { Sec = 0, Value = 0.8 }] },
        ] };
        Say("entre bloques mantiene el valor", Near(Automations.PositionAt(gap, false, 1.9), 0.2) && Near(Automations.PositionAt(gap, false, 2.01), 0.8));

        var split = Lane(AutoTarget.Volume, (0, 0), (2, 1));
        split.Blocks[0].Points[0].Tension = 1;
        split.Touch();
        double before = Automations.PositionAt(split, false, 1.5);
        Automations.Split(split, split.Blocks[0].Id, 1, false);
        Say("dividir un bloque no cambia el sonido", split.Blocks.Count == 2 && Near(Automations.PositionAt(split, false, 1.5), before, 1e-6), $"{before:0.000} / {Automations.PositionAt(split, false, 1.5):0.000}");

        var old = System.Text.Json.JsonSerializer.Deserialize<AutomationLane>("{\"target\":\"pan\",\"points\":[{\"sec\":1,\"value\":0.25}]}", ProjectStore.Json)!;
        var oldTrack = new Track { Automation = [old] };
        ProjectLibrary.Clone(oldTrack);
        var project = new Project { Tracks = [oldTrack] };
        project.Normalize();
        Say("un carril viejo pasa a un bloque", old.Blocks.Count == 1 && Near(Automations.PositionAt(old, false, 1), 0.25));

        var saved = ProjectLibrary.Clone(fx);
        Say("el carril se guarda", saved.Automation is [{ Blocks: [{ Points.Count: 3 }] }] && saved.Automation[0].Target == AutoTarget.Fx(slot.Id, "out"));
        return ok;
    }
}
