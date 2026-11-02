using System.Diagnostics;
using XtremeDaw.Audio;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// wtlab estirar and the time-stretch rows of comprobar-dsp: <see cref="PitchShifter.Render"/> measured on signals whose
/// right answer is known (a sine keeps its frequency and level, an attack lands where the stretch puts it), its cache
/// made again from nothing, and <see cref="TempoFollow"/>: the song goes to a tempo and comes back, undo included.
/// </summary>
static class StretchChecks
{
    const int Sr = 48000;

    public static int Run(Args a)
    {
        if (a.Flag("comprobar")) return Check() ? 0 : 1;
        string file = a.Positional.ElementAtOrDefault(0) ?? throw new ArgumentException("estirar: falta el archivo.");
        double stretch = a.Get("de") != null ? a.Double("de", 120) / a.Double("a", 120) : a.Double("razon", 1.25);
        if (!(stretch > 0)) throw new ArgumentException("estirar: la razón tiene que ser mayor que 0.");
        double semis = a.Double("semitonos", 0);
        string output = a.Get("salida", Path.ChangeExtension(file, null) + $"_x{stretch:0.###}.wav");
        var audio = AudioLoader.Load(file, Sr);
        var sw = Stopwatch.StartNew();
        var (l, r) = PitchShifter.Render(audio.Left, audio.Right, semis, stretch);
        sw.Stop();
        Wav.Write(output, l, r, Sr);
        double sec = l.Length / (double)Sr;
        Console.WriteLine($"{Path.GetFileName(file)}: {audio.DurationSec:0.0} s a {sec:0.0} s en {sw.Elapsed.TotalSeconds:0.0} s ({sec / Math.Max(0.001, sw.Elapsed.TotalSeconds):0}× tiempo real)");
        Console.WriteLine($"Escrito: {Path.GetFullPath(output)}");
        return 0;
    }

    /// <summary>The stretch rows of comprobar-dsp; true when all of them pass.</summary>
    public static bool Check()
    {
        bool ok = true;

        // a sine keeps its frequency and its level at any speed, and the length is exactly the one asked for
        foreach (double s in new[] { 0.1, 0.25, 0.5, 0.75, 0.9, 1.1, 1.25, 1.5, 2.0, 4.0, 10.0 })
        {
            var sine = TransposeCommands.Tone(Sr * 3, i => 0.5 * Math.Sin(2 * Math.PI * 440 * i / Sr));
            var (l, _) = PitchShifter.Render(sine, sine, 0, s);
            int mid = l.Length / 2, w = Math.Min(Sr / 2, l.Length / 4);
            double hz = TransposeCommands.Frequency(l, mid - w, mid + w);
            double db = 20 * Math.Log10(TransposeCommands.Rms(l, mid - w, mid + w) / TransposeCommands.Rms(sine, Sr, Sr * 2));
            bool pass = l.Length == PitchShifter.StretchedLength(sine.Length, s) && Math.Abs(hz - 440) < 0.05 && Math.Abs(db) < 0.1;
            ok &= pass;
            TransposeCommands.Row($"Seno x{s:0.00}: Hz, |nivel|", $"{hz:0.00} {Math.Abs(db):0.00} dB", "440 <0,1 dB", pass);
        }

        // plucks land where the stretch puts them, with their edge: what a guitar on the grid needs
        var rnd = new Random(5);
        int[] hits = [Sr / 2, Sr * 3 / 2, Sr * 5 / 2];
        var noise = new float[Sr * 4];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(rnd.NextDouble() * 2 - 1);
        var plucks = TransposeCommands.Tone(Sr * 4, i =>
        {
            double v = 0;
            foreach (int h in hits)
            {
                if (i < h) continue;
                double t = (i - h) / (double)Sr;
                v += Math.Exp(-t * 6) * (0.3 * Math.Sin(2 * Math.PI * 196 * t) + 0.15 * Math.Sin(2 * Math.PI * 392 * t)) + Math.Exp(-t * 400) * 0.4 * noise[i];
            }
            return v;
        });
        // a frame of 85 ms would spread what it stretches; TimeMap keeps them where they go, however wide the change
        foreach (double s in new[] { 0.25, 0.5, 0.8, 0.9, 1.1, 1.25, 1.5, 2.0, 4.0 })
        {
            var (l, _) = PitchShifter.Render(plucks, plucks, 0, s);
            double worst = 0, pre = -200;
            foreach (int h in hits)
            {
                int at = (int)(h * s);
                worst = Math.Max(worst, Math.Abs(Onset(l, at - Sr / 8, at + Sr / 8) - at) * 1000.0 / Sr);
                pre = Math.Max(pre, 10 * Math.Log10(TransposeCommands.Energy(l, at - Sr * 40 / 1000, at - Sr * 3 / 1000) / TransposeCommands.Energy(l, at, at + Sr * 40 / 1000) + 1e-30));
            }
            bool pass = worst <= 2 && pre < -25;
            ok &= pass;
            TransposeCommands.Row($"Ataques x{s:0.00}: lugar, pre-eco", $"{worst:0.0} ms {pre:0} dB", "2 ms <-25", pass);
        }

        // a rhythm loop: a hit every 100 ms, sped up and slowed down, and each one still lands on its place
        int[] loopHits = [.. Enumerable.Range(0, 30).Select(k => Sr / 4 + k * Sr / 10)];
        var loop = TransposeCommands.Tone(Sr * 4, i =>
        {
            double v = 0;
            foreach (int h in loopHits)
            {
                if (i < h) continue;
                v += Math.Exp(-(i - h) / (double)Sr * 200) * 0.6 * noise[i];
            }
            return v;
        });
        foreach (double s in new[] { 0.5, 2.0, 4.0 })
        {
            var (l, _) = PitchShifter.Render(loop, loop, 0, s);
            double worst = 0;
            foreach (int h in loopHits)
            {
                int at = (int)(h * s);
                worst = Math.Max(worst, Math.Abs(Onset(l, at - Sr / 45, at + Sr / 45) - at) * 1000.0 / Sr);
            }
            bool pass = worst <= 6;
            ok &= pass;
            TransposeCommands.Row($"Loop cada 100 ms x{s:0.00}: lugar", $"{worst:0.0} ms", "6 ms", pass);
        }

        // what has no attacks keeps the even map: noise doesn't count as hits, and every take of the loop is found
        var flat = TransposeCommands.Tone(Sr * 4, i => 0.3 * noise[i]);
        int falseHits = TimeMap.Attacks(flat, flat).Length, foundHits = TimeMap.Attacks(loop, loop).Length;
        bool heard = falseHits <= 4 && foundHits == loopHits.Length;
        ok &= heard;
        TransposeCommands.Row("Ataques: ruido parejo, loop", $"{falseHits} {foundHits}", $"<=4 {loopHits.Length}", heard);

        // both at once, in one pass; a mono take stays mono; neither is the take itself
        var tone = TransposeCommands.Tone(Sr * 3, i => 0.5 * Math.Sin(2 * Math.PI * 330 * i / Sr));
        var (bl, br) = PitchShifter.Render(tone, tone, 7, 1.25);
        double moved = TransposeCommands.Frequency(bl, bl.Length / 2 - Sr / 2, bl.Length / 2 + Sr / 2), want = 330 * Math.Pow(2, 7.0 / 12), monoDiff = 0;
        for (int i = 0; i < bl.Length; i++) monoDiff = Math.Max(monoDiff, Math.Abs(bl[i] - br[i]));
        var (same, _) = PitchShifter.Render(tone, tone, 0, 1);
        bool both = Math.Abs(moved - want) < 0.1 && monoDiff < 1e-5 && same.AsSpan().SequenceEqual(tone);
        ok &= both;
        TransposeCommands.Row("+7 st x1,25; mono; x1 = igual", $"{moved:0.00} {monoDiff:0.0e0}", $"{want:0.00} <1e-5", both);

        // the cache: read back the same, the name of a plain transpose as it was, and rendered again when it is gone
        string dir = Path.Combine(Path.GetTempPath(), "wtlab-estirar-" + Guid.NewGuid().ToString("N"));
        try
        {
            string source = Path.Combine(dir, "toma.wav");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(source, [1]);
            string cache = Path.Combine(dir, "cache");
            var first = TransposeCache.Get(cache, source, tone, tone, 0, 1.25);
            var read = TransposeCache.Get(cache, source, tone, tone, 0, 1.25);
            Directory.Delete(cache, true);
            var again = TransposeCache.Get(cache, source, tone, tone, 0, 1.25);
            bool named = Path.GetFileName(TransposeCache.PathFor(cache, source, 10, 2)).Split('.').Length == 5
                && TransposeCache.PathFor(cache, source, 10, 2) != TransposeCache.PathFor(cache, source, 10, 2, 1.25);
            bool cached = read.Left.AsSpan().SequenceEqual(first.Left) && again.Left.AsSpan().SequenceEqual(first.Left) && named;
            ok &= cached;
            TransposeCommands.Row("Caché: leer, borrar y rehacer", cached ? "igual" : "distinto", "igual", cached);
        }
        finally
        {
            try { Directory.Delete(dir, true); }
            catch (IOException) { }
        }

        ok &= CheckTempo();
        return ok;
    }

    /// <summary>The song at a new tempo: every time moves with its bar, the way back is the recording, and undo comes back at the tempo there is.</summary>
    static bool CheckTempo()
    {
        var media = new Media { Name = "Toma", DurationSec = 10 };
        var p = new Project { Tracks = [new Track { Name = "Guitarra" }] };
        p.Media.Add(media);
        p.Settings.Bpm = 120;
        var clip = new Clip { MediaId = media.Id, AtSec = 2, TrimStartSec = 1, TrimEndSec = 3, FadeInSec = 0.5, GainCurve = [new GainPoint { Sec = 4, Db = -3 }] };
        p.Tracks[0].Clips.Add(clip);
        p.Tracks[0].Automation = [new AutomationLane { Blocks = [new AutomationBlock { LengthSec = 8, Points = [new AutomationPoint { Sec = 4, Value = 0.5 }] }] }];
        p.Markers.Add(new Marker { Name = "Solo", Sec = 8 });
        p.ChordMarkers.Add(new ChordMarker { Name = "Am", Sec = 4, EndSec = 6 });
        p.Settings.LoopStartSec = 4;
        p.Settings.LoopEndSec = 8;

        var history = new UndoManager();
        history.Checkpoint(p, "gain");
        clip.GainDb = -2;
        TempoFollow.SetBpm(p, 100);
        const double f = 1.2;
        var w = ClipEditing.Window(clip, media);
        bool moved = Near(clip.Stretch, f) && Near(w.Dur, 12) && Near(w.Start, 2 * f + 1 * f) && Near(w.End, (2 + 10 - 3) * f) && Near(clip.FadeInSec, 0.5 * f)
            && Near(clip.GainCurve[0].Sec, 4 * f) && Near(p.Tracks[0].Automation![0].Blocks[0].Points[0].Sec, 4 * f) && Near(p.Markers[0].Sec, 8 * f)
            && Near(p.ChordMarkers[0].EndSec, 6 * f) && Near(p.Settings.LoopEndSec!.Value, 8 * f);
        TransposeCommands.Row("Tempo: todo sigue en su compás", moved ? "igual" : "distinto", "igual", moved);

        history.Undo(p);
        var undone = p.Tracks[0].Clips[0];
        bool undo = p.Settings.Bpm == 100 && undone.GainDb == 0 && Near(undone.AtSec, 2 * f) && Near(undone.Stretch, f) && Near(p.Markers[0].Sec, 8 * f);
        TransposeCommands.Row("Deshacer vuelve al tempo actual", undo ? "igual" : "distinto", "igual", undo);

        TempoFollow.SetBpm(p, 137.3);
        TempoFollow.SetBpm(p, 120);
        var back = p.Tracks[0].Clips[0];
        bool home = back.Stretch == 1 && Near(back.AtSec, 2) && Near(p.Markers[0].Sec, 8);
        TransposeCommands.Row("Volver al tempo = lo grabado", home ? "igual" : "distinto", "igual", home);

        TempoFollow.SetGrid(p, 90);
        bool grid = p.Settings.Bpm == 90 && back.Stretch == 1 && Near(back.AtSec, 2);
        TransposeCommands.Row("Detectar tempo: el audio queda", grid ? "igual" : "distinto", "igual", grid);
        return moved && undo && home && grid && CheckHand() && CheckPitch();
    }

    /// <summary>A clip's own pitch: it adds to the track's up to the shifter's limit, a lock refuses it, undo brings it back and it survives a save.</summary>
    static bool CheckPitch()
    {
        var media = new Media { Name = "Toma", DurationSec = 10 };
        var clip = new Clip { MediaId = media.Id };
        var p = new Project { Tracks = [new Track { Name = "Guitarra", Transpose = 2, Clips = [clip] }] };
        p.Media.Add(media);

        ClipEditing.SetPitch(p, clip.Id, 3.4);
        bool adds = clip.Pitch == 3 && clip.SemitonesOn(p.Tracks[0]) == 5;
        ClipEditing.SetPitch(p, clip.Id, 40);
        adds &= clip.Pitch == 12 && clip.SemitonesOn(p.Tracks[0]) == PitchShifter.MaxSemitones;
        TransposeCommands.Row("Tono del clip: suma al de la pista", adds ? "igual" : "distinto", "igual", adds);

        var history = new UndoManager();
        history.Checkpoint(p, "gain");
        ClipEditing.SetPitch(p, clip.Id, -5);
        history.Undo(p);
        bool undo = p.Tracks[0].Clips[0].Pitch == 12;
        TransposeCommands.Row("Deshacer vuelve al tono del clip", undo ? "igual" : "distinto", "igual", undo);

        var kept = p.Tracks[0].Clips[0];
        kept.Locked = true;
        bool refused = false;
        try { ClipEditing.SetPitch(p, kept.Id, 1); } catch (ArgumentException) { refused = true; }
        kept.Locked = false;
        TransposeCommands.Row("Un clip bloqueado no cambia de tono", refused ? "igual" : "distinto", "igual", refused);

        string json = System.Text.Json.JsonSerializer.Serialize(kept, ProjectStore.Json);
        bool stored = System.Text.Json.JsonSerializer.Deserialize<Clip>(json, ProjectStore.Json)!.Pitch == 12
            && !System.Text.Json.JsonSerializer.Serialize(new Clip(), ProjectStore.Json).Contains("pitch")
            && new Clip { MediaId = "m", Pitch = 2 }.TakeKey() != new Clip { MediaId = "m" }.TakeKey();
        TransposeCommands.Row("Tono del clip: se guarda y separa tomas", stored ? "igual" : "distinto", "igual", stored);
        return adds && undo && refused && stored;
    }

    /// <summary>A clip stretched by its edges: the other edge stays, the limits hold, a lock refuses it, undo brings it back and the tempo goes on top.</summary>
    static bool CheckHand()
    {
        var media = new Media { Name = "Toma", DurationSec = 10 };
        var p = new Project { Tracks = [new Track { Name = "Guitarra" }] };
        p.Media.Add(media);
        p.Settings.Bpm = 120;
        // heard from 3 to 9
        var clip = new Clip { MediaId = media.Id, AtSec = 2, TrimStartSec = 1, TrimEndSec = 3, FadeInSec = 0.4, GainCurve = [new GainPoint { Sec = 4, Db = -3 }] };
        p.Tracks[0].Clips.Add(clip);
        Clip Pull(ClipEditMode mode, double dx, double grid = 0) => ClipEditing.Drag(clip, media, mode, dx, grid, grid == 0);

        var longer = Pull(ClipEditMode.StretchEnd, 1.5);
        var w = ClipEditing.Window(longer, media);
        bool end = Near(longer.Stretch, 1.25) && Near(w.Start, 3) && Near(w.End, 10.5) && Near(longer.FadeInSec, 0.5) && Near(longer.GainCurve[0].Sec, 5);
        TransposeCommands.Row("Estirar el final: el inicio queda", end ? "igual" : "distinto", "igual", end);

        var earlier = Pull(ClipEditMode.StretchStart, -1.5);
        w = ClipEditing.Window(earlier, media);
        bool start = Near(earlier.Stretch, 1.25) && Near(w.Start, 1.5) && Near(w.End, 9);
        TransposeCommands.Row("Estirar el inicio: el final queda", start ? "igual" : "distinto", "igual", start);

        // no limit on the amount: only the shortest clip and the start of the song hold it
        const double Sample = 1.0 / 48000;
        var far = ClipEditing.Window(Pull(ClipEditMode.StretchEnd, 94), media);
        var tiny = ClipEditing.Window(Pull(ClipEditMode.StretchEnd, -100), media);
        bool limits = Math.Abs(far.End - far.Start - 100) < Sample && Math.Abs(tiny.End - tiny.Start - ClipEnvelope.MinClipSec) < Sample
            && Near(ClipEditing.Window(Pull(ClipEditMode.StretchStart, -100), media).Start, 0);
        TransposeCommands.Row("Estirar a mano: sin tope, solo el cero", limits ? "igual" : "distinto", "igual", limits);

        bool snapped = Near(ClipEditing.Window(Pull(ClipEditMode.StretchEnd, 1.6, 0.5), media).End, 10.5);
        TransposeCommands.Row("Estirar a mano: imán a la grilla", snapped ? "igual" : "distinto", "igual", snapped);

        // only the stretch changes when a whole clip is pulled by its end: the lock has to look at it too
        var whole = new Clip { MediaId = media.Id, Locked = true };
        p.Tracks[0].Clips.Add(whole);
        bool refused = false;
        try { ClipEditing.Replace(p, ClipEditing.Drag(whole, media, ClipEditMode.StretchEnd, 2, 0, true)); }
        catch (ArgumentException) { refused = true; }
        p.Tracks[0].Clips.Remove(whole);
        TransposeCommands.Row("Un clip bloqueado no se estira", refused ? "igual" : "distinto", "igual", refused);

        bool pieces = new Clip { MediaId = "m", Stretch = 1.25 }.TakeKey() != new Clip { MediaId = "m", Stretch = 1.5 }.TakeKey()
            && new Clip { MediaId = "m" }.TakeKey() == "m";
        TransposeCommands.Row("Distinto estiramiento, otra toma", pieces ? "igual" : "distinto", "igual", pieces);

        var history = new UndoManager();
        history.Checkpoint(p, "gain");
        ClipEditing.Replace(p, longer);
        history.Undo(p);
        var undone = p.Tracks[0].Clips[0];
        TempoFollow.SetBpm(p, 60);
        bool stacked = Near(undone.Stretch, 2);
        ClipEditing.Replace(p, ClipEditing.Drag(undone, media, ClipEditMode.StretchEnd, -3, 0, true));
        var back = ClipEditing.Find(p, undone.Id).Clip;
        stacked &= Near(back.Stretch, 1.5);
        TempoFollow.SetBpm(p, 120);
        stacked &= Near(back.Stretch, 0.75);
        TransposeCommands.Row("Deshacer y tempo sobre lo estirado", stacked ? "igual" : "distinto", "igual", stacked);
        return end && start && limits && snapped && refused && pieces && stacked;
    }

    static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

    /// <summary>First sample of a stretch above a tenth of its peak.</summary>
    static int Onset(float[] x, int from, int to)
    {
        float peak = 0;
        for (int i = Math.Max(0, from); i < Math.Min(x.Length, to); i++) peak = Math.Max(peak, Math.Abs(x[i]));
        for (int i = Math.Max(0, from); i < Math.Min(x.Length, to); i++)
            if (Math.Abs(x[i]) > peak * 0.1f) return i;
        return -1;
    }
}
