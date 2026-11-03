using System.Text.Json;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// The native granular, rows of comprobar-dsp and the command <c>granular</c>: the pitch follows the key and not the
/// speed of the playhead, Estirar and Recorrer put the playhead where they say, the level stays that of the sound, the
/// random draws and the block size change nothing between two renders, the audio thread allocates nothing, and the
/// export plays what the live path plays.
/// </summary>
static class GranularChecks
{
    const int Sr = 48000;

    /// <summary>Renders a file on a key to a WAV, to listen to it: <c>granular archivo.wav [--nota 60] [--velocidad 1] [--densidad 20] [--tamano 0.08] [--salida x.wav]</c>.</summary>
    public static int Run(Args a)
    {
        if (a.Positional.Count == 0)
            throw new ArgumentException("granular archivo.wav [--nota 60] [--segundos 4] [--modo stretch|scrub] [--posicion 0] [--velocidad 1] [--densidad 20] [--tamano 0.08] [--spray 0] [--jitter 0] [--salida x.wav]");
        var audio = XtremeDaw.Audio.MediaDecoder.Load(a.Positional[0], Sr);
        var g = new GranularSettings
        {
            Mode = a.Get("modo", GranularSettings.Stretch),
            Position = a.Double("posicion", 0),
            Speed = a.Double("velocidad", 1),
            Density = a.Double("densidad", 20),
            SizeSec = a.Double("tamano", 0.08),
            SpraySec = a.Double("spray", 0),
            Jitter = a.Double("jitter", 0),
        };
        g.Adopt(Path.GetFileNameWithoutExtension(a.Positional[0]), audio.Left, audio.Right, Sr);
        g.Normalized();
        Console.WriteLine($"Raíz: {g.Root} ({g.Fine:+0;-0;0} cents) · {g.Mode}");
        int note = (int)a.Double("nota", g.Root);
        double sec = a.Double("segundos", 4);
        var sound = GranularSettings.Sound(audio.Left, audio.Right);
        var (l, r) = Render(g.Patch(120, sound), sound, [On(0, (long)(sec * Sr), note)], (int)((sec + g.Release + 0.5) * Sr));
        Console.WriteLine($"Pico: {l.Concat(r).Max(Math.Abs):0.000}");
        if (a.Get("salida") is { } path) Wav.Write(path, l, r, Sr);
        return 0;
    }

    static NoteEvent On(long start, long length, int pitch, int velocity = 127) => new(start, length, NoteSynth.Hz(pitch), 1, pitch, velocity);

    static (float[] L, float[] R) Render(GranularPatch patch, SamplerSound? sound, NoteEvent[] notes, int frames, int block = 256, GranularCurves? curves = null, Action<Granular, long>? during = null)
    {
        var s = new Granular(Sr) { Sound = sound, Notes = new NoteSequence(notes, Sr), Curves = curves };
        s.Set(patch);
        var l = new float[frames];
        var r = new float[frames];
        for (int at = 0; at < frames; at += block)
        {
            int n = Math.Min(block, frames - at);
            s.Play(at, n, l.AsSpan(at, n), r.AsSpan(at, n));
            during?.Invoke(s, at + n);
        }
        return (l, r);
    }

    static SamplerSound Sound(float[] x) => new(x, x, false, SamplerLoop.Off, 0, 1, 0);

    static float[] Sine(double hz, double sec, double level = 0.5)
    {
        var x = new float[(int)(sec * Sr)];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(level * Math.Sin(2 * Math.PI * hz * i / Sr));
        return x;
    }

    static double Rms(float[] x, int from, int to)
    {
        double sum = 0;
        for (int i = from; i < to; i++) sum += x[i] * x[i];
        return Math.Sqrt(sum / Math.Max(1, to - from));
    }

    /// <summary>The strongest frequency between two frames, by a DFT scan in 1 Hz steps around <paramref name="near"/>.</summary>
    static double Peak(float[] x, int from, int to, double near, double reach = 200)
    {
        double best = 0, bestHz = 0;
        for (double hz = Math.Max(20, near - reach); hz <= near + reach; hz += 1)
        {
            double re = 0, im = 0, w = 2 * Math.PI * hz / Sr;
            for (int i = from; i < to; i++)
            {
                re += x[i] * Math.Cos(w * i);
                im += x[i] * Math.Sin(w * i);
            }
            double m = re * re + im * im;
            if (m > best) (best, bestHz) = (m, hz);
        }
        return bestHz;
    }

    static double MaxDiff(float[] a, float[] b)
    {
        double d = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++) d = Math.Max(d, Math.Abs(a[i] - b[i]));
        return d;
    }

    static double Db(double ratio) => 20 * Math.Log10(Math.Max(ratio, 1e-9));

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

        var tone = Sine(440, 4);
        var sound = Sound(tone);
        var plain = new GranularPatch { VelAmp = 0, Amp = new(0, 0.1, 1, 0.01), Density = 20, SizeSec = 0.1 };
        int sec2 = 2 * Sr;

        // 1. the pitch is the key's, whatever the playhead does
        {
            var (root, _) = Render(plain, sound, [On(0, sec2, 60)], sec2);
            double hz = Peak(root, Sr / 2, Sr / 2 + 9600, 440);
            Row("Granular: en la raíz, el tono del sample", $"{hz:0} Hz", "440", Math.Abs(hz - 440) <= 2);
            var (up, _) = Render(plain, sound, [On(0, sec2, 72)], sec2);
            double upHz = Peak(up, Sr / 2, Sr / 2 + 9600, 880);
            Row("Granular: una octava arriba", $"{upHz:0} Hz", "880", Math.Abs(upHz - 880) <= 3);
            var (slow, _) = Render(plain with { Speed = 0.5 }, sound, [On(0, sec2, 60)], sec2);
            double slowHz = Peak(slow, Sr / 2, Sr / 2 + 9600, 440);
            Row("Granular: más lento, el mismo tono", $"{slowHz:0} Hz", "440", Math.Abs(slowHz - 440) <= 2);
            double level = Db(Rms(root, Sr / 2, sec2) / Rms(tone, 0, Sr));
            Row("Granular: el nivel del sample", $"{level:+0.0;-0.0} dB", "±3", Math.Abs(level) < 3);
        }

        // 2. the playhead: Estirar runs at its speed, Recorrer stands at its place
        {
            var heads = new float[Granular.MaxVoices];
            double at = -1;
            Render(plain with { Speed = 0.5 }, sound, [On(0, sec2, 60)], Sr, during: (g, frame) => { if (frame == Sr) at = g.Heads(heads) > 0 ? heads[0] : -1; });
            // one second at half speed over a four second sound: an eighth of it
            Row("Estirar: el cabezal a su velocidad", $"{at:0.000}", "0.125", Math.Abs(at - 0.125) < 0.002);
            var two = new float[4 * Sr];
            for (int i = 0; i < two.Length; i++) two[i] = (float)(0.5 * Math.Sin(2 * Math.PI * (i < 2 * Sr ? 220 : 660) * i / Sr));
            var (scrub, _) = Render(plain with { Mode = GranularMode.Scrub, Position = 0.75 }, Sound(two), [On(0, sec2, 60)], sec2);
            double hz = Peak(scrub, Sr / 2, Sr / 2 + 9600, 440, 300);
            Row("Recorrer: suena donde está la posición", $"{hz:0} Hz", "660", Math.Abs(hz - 660) <= 3);
            var (region, _) = Render(plain with { Start = 0.5, End = 1, Speed = 2 }, Sound(two), [On(0, 3 * Sr, 60)], 3 * Sr);
            double inside = Peak(region, 2 * Sr, 2 * Sr + 9600, 440, 300);
            Row("Estirar: gira dentro de la parte elegida", $"{inside:0} Hz", "660", Math.Abs(inside - 660) <= 3);
        }

        // 3. the random draws are the note's: the same notes, the same floats, in any block size
        {
            var wild = plain with { Jitter = 0.8, SpraySec = 0.3, Spread = 1, Density = 60, SizeSec = 0.05 };
            NoteEvent[] tune = [On(1000, Sr, 60), On(Sr / 2, Sr, 64, 90), On(Sr, Sr / 3, 67, 70)];
            var (a, _) = Render(wild, sound, tune, sec2, 256);
            var (b, _) = Render(wild, sound, tune, sec2, 37);
            var (c, _) = Render(wild, sound, tune, sec2, 1024);
            double d = Math.Max(MaxDiff(a, b), MaxDiff(a, c));
            Row("Bloques: el mismo sonido con Jitter y Spray", Diff(d), "exacto", d == 0);
            var (moved, _) = Render(wild, sound, [On(1001, Sr, 60)], sec2);
            Row("Azar: otra nota, otros granos", Diff(MaxDiff(a, moved)), "> 0", MaxDiff(a, moved) > 0);
        }

        // 4. a jump into the middle of a note picks it up sounding
        {
            var g = new Granular(Sr) { Sound = sound, Notes = new NoteSequence([On(0, 3 * Sr, 60)], Sr) };
            g.Set(plain);
            var l = new float[Sr / 2];
            var r = new float[Sr / 2];
            for (int at = 0; at < l.Length; at += 256) g.Play(Sr + at, Math.Min(256, l.Length - at), l.AsSpan(at), r.AsSpan(at));
            Row("Salto: retoma la nota a la mitad", $"{Rms(l, Sr / 4, Sr / 2):0.00}", "> 0.1", Rms(l, Sr / 4, Sr / 2) > 0.1);
        }

        // 5. the lanes move what they drive, and the knob laws go both ways
        {
            var two = new float[4 * Sr];
            for (int i = 0; i < two.Length; i++) two[i] = (float)(0.5 * Math.Sin(2 * Math.PI * (i < 2 * Sr ? 220 : 660) * i / Sr));
            var t = new Track { Kind = TrackKinds.Midi, Granular = new GranularSettings(), Automation = [Lane(AutoTarget.Granular(GranularKnob.Position), (0, 0.1), (1, 0.1), (1.01, 0.9))] };
            var curves = Automations.GranularCurves(t);
            var (moving, _) = Render(plain with { Mode = GranularMode.Scrub }, Sound(two), [On(0, sec2, 60)], sec2, curves: curves);
            double early = Peak(moving, Sr / 4, Sr / 4 + 9600, 440, 300), late = Peak(moving, Sr + Sr / 2, Sr + Sr / 2 + 9600, 440, 300);
            Row("Carril: la posición se mueve", $"{early:0}→{late:0}", "220→660", Math.Abs(early - 220) <= 3 && Math.Abs(late - 660) <= 3);
            bool laws = true;
            foreach (var k in Enum.GetValues<GranularKnob>())
                foreach (double x in (double[])[0, 0.25, 0.5, 1]) laws &= Math.Abs(GranularKnobs.Position(k, GranularKnobs.Value(k, x)) - x) < 1e-9;
            Row("Carril: las perillas van y vuelven", laws ? "sí" : "no", "sí", laws);
            var target = AutoTarget.Granular(GranularKnob.Spray);
            Row("Carril: el destino se lee de vuelta", AutoTarget.GranularOf(target)?.ToString() ?? "-", "Spray", AutoTarget.GranularOf(target) == GranularKnob.Spray && AutoTarget.SamplerOf(target) == null);
        }

        // 6. the project: broken values come back in range, the sample counts as in use, one instrument per track
        {
            var p = new Project();
            var t = new Track { Kind = TrackKinds.Midi, Granular = new GranularSettings { MediaId = "med_g", Voices = 900, Density = double.NaN, Start = 0.9, End = 0.1, Mode = "nada" } };
            p.Tracks.Add(t);
            p.Normalize();
            Row("Proyecto: valores rotos vuelven al rango", $"{t.Granular!.Voices} · {t.Granular.Density}", "16 · 20", t.Granular.Voices == 16 && t.Granular.Density == 20 && t.Granular.End == 1 && t.Granular.Mode == "stretch");
            Row("Proyecto: el sample cuenta como usado", ProjectHistory.UsedMediaIds(p).Contains("med_g") ? "sí" : "no", "sí", ProjectHistory.UsedMediaIds(p).Contains("med_g"));
            string json = JsonSerializer.Serialize(t.Granular, ProjectStore.Json);
            var back = JsonSerializer.Deserialize<GranularSettings>(json, ProjectStore.Json)!;
            Row("Proyecto: va y vuelve", back.MediaId ?? "-", "med_g", back.MediaId == "med_g" && JsonSerializer.Serialize(back, ProjectStore.Json) == json);
            var both = new Track { Kind = TrackKinds.Midi, Sampler = new SamplerSettings(), Granular = new GranularSettings() };
            var audio = new Track { Granular = new GranularSettings() };
            p.Tracks.Add(both);
            p.Tracks.Add(audio);
            p.Normalize();
            Row("Proyecto: un instrumento por pista", both.Granular == null && both.Sampler != null ? "sampler" : "los dos", "sampler", both.Granular == null && both.Sampler != null);
            Row("Proyecto: una pista de audio no lleva", audio.Granular == null ? "null" : "granular", "null", audio.Granular == null);
        }

        // 7. the export plays exactly what the voices play
        {
            var wild = plain with { Jitter = 0.5, SpraySec = 0.1, Spread = 0.5 };
            var rig = new GranularRig(wild, sound);
            NoteEvent[] tune = [On(0, Sr, 60), On(Sr / 2, Sr, 67)];
            var l = new float[sec2];
            MixRenderer.RenderTo([new MixTrackSpec { Notes = new NoteSequence(tune, Sr), Granular = rig, Inserts = [] }], new MixBusSpec(), 2,
                (at, a, _) => { if (at < l.Length) a[..Math.Min(a.Length, l.Length - at)].CopyTo(l.AsSpan(at)); }, Sr, limit: false, master: false);
            var (live, _) = Render(wild, sound, tune, sec2);
            Row("Exportación: lo mismo que en vivo", Diff(MaxDiff(l, live)), "< 1e-6", MaxDiff(l, live) < 1e-6);
        }

        // 8. the audio thread allocates nothing, with lanes
        {
            var t = new Track { Kind = TrackKinds.Midi, Granular = new GranularSettings(), Automation = [Lane(AutoTarget.Granular(GranularKnob.Size), (0, 0.2), (2, 0.8)), Lane(AutoTarget.Granular(GranularKnob.Gain), (0, 0.7), (2, 0.8))] };
            var player = new Granular(Sr) { Sound = sound, Notes = new NoteSequence([On(0, Sr, 60), On(100, Sr, 64)], Sr), Curves = Automations.GranularCurves(t) };
            player.Set(plain with { Jitter = 0.5, SpraySec = 0.2, Density = 200 });
            var l = new float[256];
            var r = new float[256];
            void Lap() { for (int at = 0; at < Sr; at += 256) player.Play(at, 256, l, r); player.Play(100, 256, l, r); }
            Lap();
            long grew = GC.GetAllocatedBytesForCurrentThread();
            Lap();
            grew = GC.GetAllocatedBytesForCurrentThread() - grew;
            Row("Granular: no asigna memoria", $"{grew} bytes", "0", grew == 0);
        }
        return allOk;
    }

    static AutomationLane Lane(string target, params (double Sec, double Value)[] points) =>
        new() { Target = target, Blocks = [new() { LengthSec = points[^1].Sec + 1, Points = [.. points.Select(x => new AutomationPoint { Sec = x.Sec, Value = x.Value })] }] };
}
