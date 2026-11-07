using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// xtlab comprobar-dsp, freezing a track (#240, <see cref="Freezing"/>): the mix with the track frozen (its render, no
/// slots, with its fader, pan and send live) is the mix with it live, sample for sample; the render starts where the
/// track first sounds, a muted track still freezes, and a frozen track's clips are locked. A track with saturation, a
/// compressor and a reverb slot, panned, at a fader under 0 dB and sending to a reverb bus: what is rendered and what
/// stays live are each heard.
/// </summary>
static class FreezeChecks
{
    const int Sr = 48000;
    const double Sec = 6;
    const double AtSec = 0.5;

    static Project Song()
    {
        var bus = new Bus { Name = "Reverb", Inserts = [InsertSlot.New(InsertSlot.ReverbKind)] };
        var t = new Track { Name = "Synth" };
        var sat = InsertSlot.New(InsertSlot.SatKind);
        sat.Sat!.On = true;
        sat.Sat.Amount = 0.6;
        var comp = InsertSlot.New(InsertSlot.CompKind);
        comp.Comp!.On = true;
        t.Channel.Slots.AddRange([sat, comp, InsertSlot.New(InsertSlot.ReverbKind)]);
        t.Channel.Volume = 0.6;
        t.Channel.Pan = 0.4;
        t.Channel.Sends = [new Send { BusId = bus.Id, Level = 0.4 }];
        var p = new Project { Tracks = [t] };
        p.Settings.Buses = [bus];
        return p;
    }

    /// <summary>Two seconds of a decaying 220 Hz tone with a little of its fifth, at <see cref="AtSec"/>.</summary>
    static MixClipSpec Tone()
    {
        var l = new float[Sr * 2];
        var r = new float[Sr * 2];
        for (int i = 0; i < l.Length; i++)
        {
            double t = i / (double)Sr, env = Math.Exp(-1.5 * t);
            l[i] = (float)(0.5 * env * Math.Sin(2 * Math.PI * 220 * t));
            r[i] = (float)(0.5 * env * (0.8 * Math.Sin(2 * Math.PI * 220 * t) + 0.2 * Math.Sin(2 * Math.PI * 330 * t)));
        }
        return new MixClipSpec { Left = l, Right = r, DurationSec = 2, AtSec = AtSec };
    }

    public static bool Check()
    {
        bool allOk = true;
        void Row(string name, string got, string want, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"{name,-36} {got,12} {want,10}  {(ok ? "igual" : "DISTINTO")}");
        }

        var p = Song();
        var t = p.Tracks[0];
        var nodes = ChannelDsp.Nodes(p);
        var live = ChannelDsp.Track(p, nodes, t, [Tone()], -24);
        var (liveL, liveR) = MixRenderer.Render([live], ChannelDsp.Bus(p), Sec, Sr, limit: false);

        // 1. the render, played frozen under the same fader, pan and send, is the live mix
        var made = Freezing.Render([live], 0, p.Settings.Bpm, Sr);
        if (made is not var (fl, fr, at))
        {
            Row("Congelar: el render suena", "nada", "audio", false);
            return false;
        }
        var frozen = ChannelDsp.Track(p, nodes, t, [Freezing.Clip(fl, fr, at, Sr)], -24, frozen: true);
        var (frozenL, frozenR) = MixRenderer.Render([frozen], ChannelDsp.Bus(p), Sec, Sr, limit: false);
        double diff = 0, peak = 0;
        for (int i = 0; i < liveL.Length; i++)
        {
            diff = Math.Max(diff, Math.Max(Math.Abs(liveL[i] - frozenL[i]), Math.Abs(liveR[i] - frozenR[i])));
            peak = Math.Max(peak, Math.Abs(liveL[i]));
        }
        Row("Congelar: suena como en vivo", $"{diff:0.0e0}", "< 1e-4", peak > 0.01 && diff < 1e-4);
        Row("Congelar: no lleva slots", $"{frozen.Inserts?.Count}", "0", frozen.Inserts is { Count: 0 } && frozen.Notes == null);

        // 2. it starts where the track first sounds (a transition's lead at most before), and its reverb rings past the clip
        Row("Congelar: empieza con la pista", $"{at:0.000} s", $"{AtSec - 0.5:0.0}-{AtSec:0.0}", at >= AtSec - 0.5 - 1e-9 && at <= AtSec + 1e-9);
        double end = at + fl.Length / (double)Sr;
        Row("Congelar: la cola del slot entra", $"{end:0.0} s", "> 2,5 s", end > AtSec + 2);

        // 3. a muted track freezes too: the render is taken before its mute
        t.Channel.Muted = true;
        var muted = ChannelDsp.Track(p, nodes, t, [Tone()], -24);
        var again = Freezing.Render([muted], 0, p.Settings.Bpm, Sr);
        Row("Congelar: también muteada", again is { } m ? $"{m.Left.Length}" : "nada", $"{fl.Length}", again is { } m2 && m2.Left.Length == fl.Length);
        t.Channel.Muted = false;

        // 4. frozen, its clips are locked, and the model keeps only a render inside freeze/
        var clipped = new Track { Name = "Gtr", Clips = [new Clip { MediaId = "med" }] };
        clipped.Freeze = new TrackFreeze { File = $"{Freezing.Dir}/x.wav", AtSec = 1 };
        Row("Congelar: bloquea sus clips", ClipEditing.IsLocked(clipped, clipped.Clips[0]) ? "sí" : "no", "sí", ClipEditing.IsLocked(clipped, clipped.Clips[0]));
        var outside = ProjectStore.Parse(System.Text.Json.JsonSerializer.Serialize(new Project
        {
            Tracks = [new Track { Freeze = new TrackFreeze { File = "../afuera.wav" } }, new Track { Freeze = new TrackFreeze { File = $"{Freezing.Dir}/a.wav", AtSec = 2 } }],
        }, ProjectStore.Json));
        bool kept = outside.Tracks[0].Freeze == null && outside.Tracks[1].Freeze is { AtSec: 2 } f && f.File == $"{Freezing.Dir}/a.wav";
        Row("Congelar: se guarda y se valida", kept ? "ok" : "mal", "ok", kept);
        return allOk;
    }
}
