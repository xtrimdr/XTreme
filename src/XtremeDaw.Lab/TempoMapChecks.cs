using System.Globalization;
using System.Text.Json;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// The tempo track's rows of comprobar-dsp (#214, <see cref="TempoMap"/>; also <c>xtlab proyecto verificar-tempo</c>):
/// seconds and beats both ways over steps and ramps, a ramp against a numeric integration, bars and clicks through
/// 4/4 → 7/8 → 3/4, what moves when the map changes (a MIDI clip and a follow-tempo clip keep their beats, a clip in
/// seconds stays), undo and redo of a map edit with a change of speed in between, the notes as the engine plays them, a
/// .mid with tempos and meters back and forth, and a project of before the map (only "bpm").
/// </summary>
static class TempoMapChecks
{
    const int Sr = 48000;

    public static int Run() => Check() ? 0 : 3;

    /// <summary>Prints its rows; true when all of them pass.</summary>
    public static bool Check()
    {
        bool allOk = true;
        void Row(string name, string got, string want, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"{name,-36} {got,12} {want,10}  {(ok ? "igual" : "DISTINTO")}");
        }

        // 120, a step to 90 at beat 8, a ramp 90 → 150 from 16 to 32, then 150
        var map = Map("0:120,8:90,16:90~,32:150");

        double worst = 0;
        for (double b = -4; b <= 64; b += 0.37) worst = Math.Max(worst, Math.Abs(map.BeatAt(map.SecAt(b)) - b));
        for (double s = -2; s <= 30; s += 0.113) worst = Math.Max(worst, Math.Abs(map.SecAt(map.BeatAt(s)) - s));
        Row("Tempo: segundos y tiempos, ida y vuelta", $"{worst:0.0e0}", "< 1e-9", worst < 1e-9);

        // the ramp's length against a numeric integration of 60 / T(x) (Simpson)
        const int n = 200_000;
        double h = 16.0 / n, sum = 0;
        for (int i = 0; i <= n; i++)
        {
            double x = i * h, f = 60 / (90 + 60 * x / 16);
            sum += f * (i == 0 || i == n ? 1 : i % 2 == 1 ? 4 : 2);
        }
        double numeric = sum * h / 3, exact = map.SecAt(32) - map.SecAt(16);
        Row("Tempo: rampa 90→150 contra integral", $"{exact:0.000000}", $"{numeric:0.000000}", Math.Abs(exact - numeric) < 1e-9);
        double startSec = 8 * 0.5 + 8 * 60 / 90.0;
        Row("Tempo: escalón y comienzo de la rampa", $"{map.SecAt(16):0.000000}", $"{startSec:0.000000}", Math.Abs(map.SecAt(16) - startSec) < 1e-12);
        double mid = map.BpmAt(24);
        Row("Tempo: a mitad de la rampa", $"{mid:0.###}", "120", Math.Abs(mid - 120) < 1e-9 && map.BpmAt(10) == 90 && map.BpmAt(40) == 150);

        // 4/4 for two bars, 7/8 for two, then 3/4
        var meters = Map("0:100", "0:4/4,2:7/8,4:3/4");
        double[] starts = [0, 4, 8, 11.5, 15, 18, 21];
        bool bars = true;
        for (int b = 0; b < starts.Length; b++) bars &= Math.Abs(meters.BarStartBeat(b) - starts[b]) < 1e-12 && meters.BarAt(starts[b]) == b && meters.BarAt(starts[b] + 0.25) == b;
        bars &= meters.MeterAt(3) == (7, 8) && meters.MeterAt(9) == (3, 4) && meters.BarBeats(2) == 3.5 && meters.BarAt(-1) == -1;
        meters.BarBeatAt(meters.SecAt(12.5), out int clockBar, out double unit);
        bars &= clockBar == 3 && Math.Abs(unit - 2) < 1e-9;
        Row("Compás: 4/4 → 7/8 → 3/4", $"{meters.BarStartBeat(4):0.##}", "15", bars);

        // the click: quarters in 4/4, eighths in 7/8, accent on every bar's first
        var clicks = new List<(double Beat, bool Accent)>();
        double c = meters.ClickAt(0, false, out bool accent);
        while (c < 21 - 1e-9)
        {
            clicks.Add((c, accent));
            c = meters.ClickAt(c, true, out accent);
        }
        int accents = clicks.Count(x => x.Accent);
        bool clickOk = clicks.Count == 8 + 14 + 6 && accents == 2 + 2 + 2 && clicks[9].Beat == 8.5 && clicks[8].Accent && clicks[15].Accent && clicks[22].Accent
            && clicks.Where(x => x.Accent).Select(x => x.Beat).SequenceEqual([0, 4, 8, 11.5, 15, 18]);
        // the engine finds the next click from a frame with a slack: a frame right on a click still gets it
        double seed = meters.ClickAt(meters.BeatAt(meters.SecAt(11.5)) - 1e-6, false, out bool seedAccent);
        clickOk &= seed == 11.5 && seedAccent;
        Row("Click: tiempos y acentos por compás", $"{clicks.Count} / {accents}", "28 / 6", clickOk);

        // under the ramp each click falls where the integral says
        double clickWorst = 0;
        for (double b = map.ClickAt(16, false, out _); b <= 32; b = map.ClickAt(b, true, out _))
        {
            double x = b - 16, at = map.SecAt(16) + 60 * 16 / 60.0 * Math.Log((90 + 60 * x / 16) / 90);
            clickWorst = Math.Max(clickWorst, Math.Abs(Math.Round(map.SecAt(b) * Sr) - Math.Round(at * Sr)));
        }
        Row("Click: en la rampa, cuadro exacto", $"{clickWorst:0}", "0", clickWorst == 0);

        allOk &= Follow(Row);
        allOk &= Undo(Row);
        allOk &= Notes(Row);
        allOk &= File(Row);
        allOk &= Old(Row);
        return allOk;
    }

    /// <summary>"0:120,8:90,16:90~,32:150" (beat:bpm, ~ = ramp to the next) and "0:4/4,2:7/8" (bar:n/d).</summary>
    public static TempoMap Map(string points, string? meters = null)
    {
        var m = new TempoMap();
        foreach (var part in points.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split(':');
            if (kv.Length != 2) throw new ArgumentException($"tempo-mapa: «{part}» tiene que ser tiempo:bpm (con ~ al final, rampa).");
            bool ramp = kv[1].EndsWith('~');
            m.Points.Add(new()
            {
                Beat = double.Parse(kv[0], CultureInfo.InvariantCulture),
                Bpm = double.Parse(kv[1].TrimEnd('~'), CultureInfo.InvariantCulture),
                Ramp = ramp,
            });
        }
        foreach (var part in (meters ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split(':', '/');
            if (kv.Length != 3) throw new ArgumentException($"--compas: «{part}» tiene que ser compás:n/d.");
            m.Meters.Add(new() { Bar = int.Parse(kv[0]), Num = int.Parse(kv[1]), Den = int.Parse(kv[2]) });
        }
        m.Normalize();
        return m;
    }

    /// <summary>The map as the commands print it back.</summary>
    public static string Describe(TempoMap m) =>
        string.Join(", ", m.Points.Select(p => $"{p.Beat.ToString("0.###", CultureInfo.InvariantCulture)}:{p.Bpm.ToString("0.###", CultureInfo.InvariantCulture)}{(p.Ramp ? "~" : "")}"))
        + " · " + string.Join(", ", m.Meters.Select(x => $"{x.Bar}:{x.Num}/{x.Den}"));

    /// <summary>A song with a MIDI clip on bar 5, a take in seconds at 8 s and a follow-tempo take on beat 8 lasting 4 s.</summary>
    static (Project P, MidiClip Midi, Clip Seconds, Clip Follow) Song()
    {
        var p = new Project();
        p.Settings.TempoMap = Map("0:120");
        string tid = MidiEditing.AddTrack(p);
        var midi = MidiEditing.AddClip(p, tid, 8, 8); // beat 16 at 120
        MidiEditing.AddNote(p, midi.Id, 60, 1, 2);
        var media = new Media { Name = "toma", DurationSec = 4, SampleRate = Sr, Channels = 2 };
        p.Media.Add(media);
        var seconds = new Clip { MediaId = media.Id, AtSec = 8 };
        var follow = new Clip { MediaId = media.Id, AtSec = 4, TrimStartSec = 0.5 }; // sounds from 4.5 s = beat 9
        p.Tracks.Add(new Track { Name = "Segundos", Clips = [seconds] });
        p.Tracks.Add(new Track { Name = "Sigue", FollowTempo = true, Clips = [follow] });
        p.Markers.Add(new Marker { Sec = 6, Name = "Puente" });
        p.Normalize();
        return (p, midi, seconds, follow);
    }

    static bool Follow(Action<string, string, string, bool> row)
    {
        var (p, midi, seconds, follow) = Song();
        double followEnd = ClipEditing.Window(follow, p.FindMedia(follow.MediaId)).End; // 8 s = beat 16
        bool changed = TempoFollow.SetMap(p, Map("0:120,4:80,10:80~,20:160"));
        var m = p.Settings.TempoMap;
        bool midiOk = Math.Abs(midi.StartBeat(m) - 16) < 1e-9 && Math.Abs(midi.EndSec(m) - m.SecAt(24)) < 1e-9;
        bool secondsOk = seconds.AtSec == 8 && seconds.Stretch == 1;
        var w = ClipEditing.Window(follow, p.FindMedia(follow.MediaId));
        double ratio = (m.SecAt(16) - m.SecAt(9)) / (8 - 4.5);
        bool followOk = Math.Abs(m.BeatAt(w.Start) - 9) < 1e-9 && Math.Abs(m.BeatAt(w.End) - 16) < 1e-9 && Math.Abs(follow.Stretch - ratio) < 1e-12 && followEnd == 8;
        bool markerOk = Math.Abs(m.BeatAt(p.Markers[0].Sec) - 12) < 1e-9;
        bool same = !TempoFollow.SetMap(p, m.Copy());
        row("Mapa: MIDI y audio que sigue, en su tiempo", $"{midi.StartBeat(m):0.###}", "16", changed && midiOk && followOk && markerOk && same);
        row("Mapa: audio en segundos no se mueve", $"{seconds.AtSec:0.###}", "8", secondsOk);
        return changed && midiOk && followOk && markerOk && same && secondsOk;
    }

    static bool Undo(Action<string, string, string, bool> row)
    {
        var (p, midi, seconds, follow) = Song();
        var history = new UndoManager();
        double followAt = follow.AtSec;
        history.Checkpoint(p);
        TempoFollow.SetMap(p, p.Settings.TempoMap.WithPoint(8, 90, ramp: true).WithPoint(16, 140));
        var edited = p.Settings.TempoMap;
        // a change of the whole song's speed after the edit is a step of its own (#262)
        history.Checkpoint(p, "tempo");
        TempoFollow.SetBpm(p, 150);
        bool undone = history.Undo(p) && Math.Abs(p.Settings.TempoMap.Bpm - 120) < 1e-9 && p.Settings.TempoMap.SameAs(edited);
        undone &= history.Undo(p);
        var m = p.Settings.TempoMap;
        var back = p.Tracks[0].MidiClips![0];
        bool undoOk = undone && m.SingleTempo && Math.Abs(m.Bpm - 120) < 1e-9 && Math.Abs(back.StartBeat(m) - 16) < 1e-9
            && Math.Abs(Follower(p).AtSec - followAt) < 1e-9 && Math.Abs(p.Tracks[1].Clips[0].AtSec - 8) < 1e-9;
        bool redone = history.Redo(p) && history.Redo(p);
        m = p.Settings.TempoMap;
        bool redoOk = redone && m.Points.Count == 3 && m.Points[1].Ramp && Math.Abs(m.Bpm - 150) < 1e-9 && Math.Abs(m.Points[2].Bpm - 140 * 150 / 120.0) < 1e-9
            && Math.Abs(p.Tracks[0].MidiClips![0].StartBeat(m) - 16) < 1e-9 && Follower(p).Stretch != 1;
        // the step says what it was
        var steps = history.Steps(p, _ => null);
        bool named = steps.Count == 2 && steps[0].Label.Text.Contains("tempo") && steps[1].Label.Text.Contains("150 BPM");
        bool ok = undoOk && redoOk && named && !ReferenceEquals(edited, m);
        row("Mapa: deshacer y rehacer", undoOk ? redoOk ? "bien" : "rehacer" : "deshacer", "bien", ok);
        return ok;
    }

    static Clip Follower(Project p) => p.Tracks.First(t => t.FollowTempo).Clips[0];

    static bool Notes(Action<string, string, string, bool> row)
    {
        var (p, midi, _, _) = Song();
        TempoFollow.SetMap(p, Map("0:120,16:60~,24:180"));
        var m = p.Settings.TempoMap;
        var seq = ChannelDsp.Notes(p.Tracks[0], m, Sr)!;
        var note = seq.Notes[0];
        long from = (long)Math.Round(m.SecAt(17) * Sr), to = (long)Math.Round(m.SecAt(19) * Sr);
        bool ok = note.Start == from && note.Start + note.Length == to;
        row("Mapa: notas en la rampa, al cuadro", $"{note.Start}", $"{from}", ok);
        return ok;
    }

    static bool File(Action<string, string, string, bool> row)
    {
        string path = Path.Combine(Path.GetTempPath(), $"xtlab-tempo-{Environment.ProcessId}.mid");
        try
        {
            var map = Map("0:120,8:90,16:90~,20:150,28:100", "0:4/4,2:7/8,4:6/8");
            MidiFiles.Write(path, [("Notas", [new MidiNote { Pitch = 60, Beat = 0, Beats = 1 }, new MidiNote { Pitch = 62, Beat = 27, Beats = 1 }])], map);
            var read = MidiFiles.Read(path).Map!;
            bool meters = read.Meters.Select(x => (x.Bar, x.Num, x.Den)).SequenceEqual([(0, 4, 4), (2, 7, 8), (4, 6, 8)]);
            bool steps = read.BpmAt(0) == 120 && read.BpmAt(10) == 90 && read.BpmAt(25) == 150 && read.BpmAt(30) == 100;
            // the ramp comes back as short steps that last what it lasted: the beats around it keep their seconds
            double worst = 0;
            foreach (double b in new[] { 8.0, 16, 18, 20, 28, 31 }) worst = Math.Max(worst, Math.Abs(read.SecAt(b) - map.SecAt(b)));
            bool ok = meters && steps && worst < 1e-5 && MidiFiles.Read(path).Tracks[0].LengthBeats == map.BarStartBeat(map.BarAt(28 - 1e-6) + 1);
            row("MIDI: tempos y compases en .mid", $"{worst:0.0e0}", "< 1e-5", ok);
            return ok;
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch (IOException) { }
        }
    }

    static bool Old(Action<string, string, string, bool> row)
    {
        var old = JsonSerializer.Deserialize<Project>("""{"format":"xtreme-project","settings":{"bpm":97.5}}""", ProjectStore.Json)!;
        old.Normalize();
        var m = old.Settings.TempoMap;
        bool migrated = m.Points.Count == 1 && m.Bpm == 97.5 && old.Settings.Bpm == 97.5 && m.Meters.Count == 1 && m.MeterAt(0) == (4, 4);
        // a map goes into the file and comes back the same
        old.Settings.TempoMap = Map("0:97.5,8:130~,16:70", "0:4/4,3:5/4");
        var again = JsonSerializer.Deserialize<Project>(JsonSerializer.Serialize(old, ProjectStore.Json), ProjectStore.Json)!;
        again.Normalize();
        bool same = again.Settings.TempoMap.SameAs(old.Settings.TempoMap) && again.Settings.Bpm == 97.5;
        bool ok = migrated && same;
        row("Mapa: proyecto viejo y guardado", $"{m.Points.Count} · {m.MeterAt(0).Num}/{m.MeterAt(0).Den}", "1 · 4/4", ok);
        return ok;
    }
}
