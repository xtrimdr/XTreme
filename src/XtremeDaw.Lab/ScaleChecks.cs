using XtremeDaw.Audio;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// xtlab escala (the scale of a file, like "Detectar escala" on a clip) and the rows of comprobar-dsp for
/// <see cref="ScaleDetector"/>: plucked chord progressions in major and minor keys, a pentatonic riff, a recording
/// out of tune, a 44.1 kHz file, how notes are spelled, noise, too short and silence.
/// </summary>
static class ScaleChecks
{
    const int Sr = 48000;
    /// <summary>What a played progression has to reach; a mix with voice and effects reads lower and is still shown.</summary>
    const double ClearEnough = 0.4;

    /// <summary>xtlab escala --comprobar (only its rows of comprobar-dsp), or escala archivo [--desde s] [--hasta s].</summary>
    public static int Run(Args a)
    {
        if (a.Flag("comprobar")) return Check() ? 0 : 3;
        string file = a.Positional.ElementAtOrDefault(0) ?? throw new ArgumentException("escala: falta el archivo.");
        var audio = AudioLoader.Load(file, Sr);
        int from = (int)(a.Double("desde", 0) * audio.SampleRate);
        int to = a.Get("hasta") != null ? (int)(a.Double("hasta", 0) * audio.SampleRate) : audio.Left.Length;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = a.Flag("espectro") ? ScaleDetector.Detect(audio.Left, audio.Right, audio.SampleRate, from, to - from)
            : ScaleDetector.DetectBest(audio.Left, audio.Right, audio.SampleRate, from, to - from);
        sw.Stop();
        if (r == null)
            Console.WriteLine($"No hay escala: dura menos de {ScaleDetector.MinSec:0} s o es silencio.");
        else
            Console.WriteLine($"{Label(r.Scale)} (seguridad {r.Confidence:0.00}, acordes {r.ChordFit:0.00}, {r.Method}{(r.Retried ? ", con reintento" : "")}, tonal {r.Tonal:0.00}{(r.Tonal < ScaleDetector.MinTonal ? ", sin notas claras" : "")}): " +
                $"{string.Join(" ", r.Scale.Notes)}; relativa {Label(r.Scale.Relative)}; afinación {r.TuningCents:+0;-0;0} cents. ({sw.ElapsedMilliseconds} ms)");
        if (r != null && !a.Flag("callado"))
            foreach (var c in r.Chords)
                Console.WriteLine($"{c.StartSec + from / (double)audio.SampleRate,8:0.00} s  {c.EndSec - c.StartSec,6:0.00} s  {c.Name}");
        if (r != null && a.Get("bajo") is { } bassFile) CompareBass(r, bassFile, from / (double)audio.SampleRate);
        return 0;
    }

    /// <summary>--bajo stem.wav: how often the chord's root is what the bass plays, the nearest thing to the truth when
    /// nobody wrote the chords down (a bass on the third or the fifth counts as a miss, so 100 % isn't reachable).</summary>
    static void CompareBass(ScaleDetector.Result r, string bassFile, double offset)
    {
        const double Step = 0.1;
        var bass = AudioLoader.Load(bassFile, Sr);
        var notes = AudioToMidi.Extract(bass.Left, bass.Right, bass.SampleRate, 0, bass.Left.Length, new(AudioToMidi.Mode.Melody));
        int played = 0, hits = 0, sharing = 0;
        foreach (var n in notes)
            for (double t = n.StartSec; t < n.EndSec; t += Step)
            {
                double at = t - offset;
                int k = -1;
                for (int i = 0; i < r.Chords.Count; i++)
                    if (r.Chords[i].StartSec <= at && at < r.Chords[i].EndSec) { k = i; break; }
                played++;
                if (k < 0) continue;
                var c = r.Chords[k];
                int gap = ((n.Pitch - c.Root) % 12 + 12) % 12;
                if (gap == 0) hits++;
                if (gap is 0 or 7 or 3 or 4) sharing++;
            }
        double minutes = r.Chords.Count == 0 ? 1 : (r.Chords[^1].EndSec - r.Chords[0].StartSec) / 60;
        Console.WriteLine($"Contra el bajo: raíz {100.0 * hits / played:0} %, nota del acorde {100.0 * sharing / played:0} %, {r.Chords.Count / minutes:0} acordes por minuto.");
    }

    static string Label(ScaleDetector.Scale s) => $"{s.Root} {(s.Minor ? "menor" : "mayor")}";

    /// <summary>Prints its rows; true when all of them pass.</summary>
    public static bool Check()
    {
        bool allOk = true;
        void Row(string name, string got, string want, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"{name,-36} {got,12} {want,10}  {(ok ? "igual" : "DISTINTO")}");
        }
        static string Got(ScaleDetector.Result? r) => r == null ? "nada" : Label(r.Scale);
        void Expect(string name, ScaleDetector.Result? r, ScaleDetector.Scale want) =>
            Row($"Escala: {name}", Got(r), Label(want), r != null && r.Scale == want && r.Confidence >= ClearEnough);

        // I V vi IV, the same song on several tonics
        foreach (int tonic in new[] { 0, 4, 6, 10 })
        {
            var (l, r) = Pluck(Progression(tonic, [(0, false), (7, false), (9, true), (5, false)]), tonic, Sr);
            var want = new ScaleDetector.Scale(tonic, false);
            Expect($"I V vi IV en {want.Root}", ScaleDetector.Detect(l, r, Sr), want);
        }
        // i VI III VII
        foreach (int tonic in new[] { 9, 4, 2, 1 })
        {
            var (l, r) = Pluck(Progression(tonic, [(0, true), (8, false), (3, false), (10, false)]), tonic, Sr);
            var want = new ScaleDetector.Scale(tonic, true);
            Expect($"i VI III VII en {want.Root}m", ScaleDetector.Detect(l, r, Sr), want);
        }

        // E minor pentatonic up and down, starting and ending on the E
        int[] riff = [40, 43, 45, 47, 50, 52, 55, 57, 59, 57, 55, 52, 50, 47, 45, 43, 40, 40];
        var played = Enumerable.Range(0, 3).SelectMany(k => riff.Select((p, i) => (At: 0.2 + (k * riff.Length + i) * 0.3, Len: 0.28, Pitch: p))).ToList();
        var (rl, rr) = Pluck(played, 7, Sr);
        var em = new ScaleDetector.Scale(4, true);
        Expect("riff pentatónico en Em", ScaleDetector.Detect(rl, rr, Sr), em);

        // the same audio told it runs slower sounds that many cents flat
        double flat = -30;
        var (cl, cr) = Pluck(Progression(0, [(0, false), (7, false), (9, true), (5, false)]), 3, Sr);
        var off = ScaleDetector.Detect(cl, cr, (int)Math.Round(Sr * Math.Pow(2, flat / 1200)));
        Expect("30 cents abajo sigue en C", off, new(0, false));
        Row("Escala: mide la afinación", off == null ? "nada" : $"{off.TuningCents:0} cents", $"{flat:0} cents", off != null && Math.Abs(off.TuningCents - flat) <= 8);

        const int Sr44 = 44100;
        var (al, ar) = Pluck(Progression(7, [(0, false), (7, false), (9, true), (5, false)]), 5, Sr44);
        Expect("I V vi IV en G a 44,1 kHz", ScaleDetector.Detect(al, ar, Sr44), new(7, false));

        // only the stretch asked for: C major, then Eb major
        var (bl, br) = Pluck(Progression(3, [(0, false), (7, false), (9, true), (5, false)]), 9, Sr);
        float[] jl = [.. cl, .. bl], jr = [.. cr, .. br];
        Expect("solo el tramo pedido", ScaleDetector.Detect(jl, jr, Sr, cl.Length, bl.Length), new(3, false));

        string Notes(int tonic, bool minor) => string.Join(" ", new ScaleDetector.Scale(tonic, minor).Notes);
        Row("Escala: notas de Bb mayor", Notes(10, false), "Bb C D Eb F G A", Notes(10, false) == "Bb C D Eb F G A");
        Row("Escala: notas de F# mayor", Notes(6, false), "F# G# A# B C# D# E#", Notes(6, false) == "F# G# A# B C# D# E#");
        Row("Escala: notas de C# menor", Notes(1, true), "C# D# E F# G# A B", Notes(1, true) == "C# D# E F# G# A B");
        Row("Escala: relativa de A menor", Label(new ScaleDetector.Scale(9, true).Relative), "C mayor", new ScaleDetector.Scale(9, true).Relative == new ScaleDetector.Scale(0, false));

        // the chords: the names in order, each one where it was played
        const double Bar = 1.6;
        var (pl, pr) = Pluck(Progression(9, [(0, true), (8, false), (3, false), (10, false)]), 13, Sr);
        var song = ScaleDetector.Detect(pl, pr, Sr);
        string names = song == null ? "nada" : string.Join(" ", song.Chords.Take(5).Select(c => c.Name));
        Row("Escala: acordes en orden", names, "Am F C G Am", names == "Am F C G Am");
        double worst = song == null || song.Chords.Count != 17 ? double.PositiveInfinity
            : song.Chords.Select((c, i) => Math.Abs(c.StartSec - (0.2 + i * Bar))).Max();
        Row("Escala: acordes a tiempo", $"{worst:0.00} s", "<= 0.25 s", worst <= 0.25);
        Row("Escala: cuántos acordes", $"{song?.Chords.Count}", "17", song?.Chords.Count == 17);

        // power chords: no third, so none is called major or minor
        var power = new List<(double At, double Len, int Pitch)>();
        foreach (var (root, i) in new[] { 40, 43, 45, 40 }.Select((x, i) => (x, i)))
            foreach (int step in new[] { 0, 7, 12 }) power.Add((0.2 + i * Bar, Bar - 0.1, root + step));
        var (ol, or) = Pluck(power, 17, Sr);
        string fives = string.Join(" ", ScaleDetector.Detect(ol, or, Sr)?.Chords.Select(c => c.Name) ?? []);
        Row("Escala: quintas", fives, "E5 G5 A5 E5", fives == "E5 G5 A5 E5");

        // from notes: the same progression written out, and the retry keeps the surer reading
        var written = Progression(9, [(0, true), (8, false), (3, false), (10, false)]).Select(x => new AudioToMidi.Note(x.At, x.At + x.Len, x.Pitch, 90)).ToList();
        var byNotes = ScaleDetector.FromNotes(written, written.Max(x => x.EndSec) + 0.2);
        Row("Escala: desde notas, Am", Got(byNotes), "A menor", byNotes != null && byNotes.Scale == new ScaleDetector.Scale(9, true) && byNotes.Method == ScaleDetector.Method.Notes);
        string noteChords = byNotes == null ? "nada" : string.Join(" ", byNotes.Chords.Take(5).Select(c => c.Name));
        Row("Escala: acordes desde notas", noteChords, "Am F C G Am", noteChords == "Am F C G Am");
        Row("Escala: sin notas", Got(ScaleDetector.FromNotes([], 10)), "nada", ScaleDetector.FromNotes([], 10) == null);
        var again = ScaleDetector.DetectBest(pl, pr, Sr);
        Row("Escala: reintentar no empeora", $"{again?.Score:0.00}", $">= {song?.Score:0.00}", again != null && song != null && again.Score >= song.Score);

        // the chords in the project: on their own, replaced by a new detection, undone and following a closed gap
        var p = new Project();
        ProjectMarkers.Add(p, 3, "Estribillo");
        ProjectMarkers.SetChords(p, 0, 10, [(0, 2, "Am"), (2, 4, "F"), (8, 10, "G")]);
        var undo = new UndoManager();
        undo.Checkpoint(p);
        ProjectMarkers.SetChords(p, 0, 4, [(0, 4, "C")]);
        string Now() => string.Join(" ", p.ChordMarkers.Select(c => c.Name));
        Row("Escala: detectar otra vez reemplaza", Now(), "C G", Now() == "C G" && p.Markers.Count == 1);
        undo.Undo(p);
        Row("Escala: se deshace", Now(), "Am F G", Now() == "Am F G");
        ProjectMarkers.CloseGap(p, 1, 3);
        string spans = string.Join(" ", p.ChordMarkers.Select(c => $"{c.Sec:0}-{c.EndSec:0}"));
        Row("Escala: siguen el hueco cerrado", spans, "0-1 1-2 6-8", spans == "0-1 1-2 6-8");
        ProjectMarkers.RemoveChords(p, [p.ChordMarkers[0].Id, p.ChordMarkers[2].Id]);
        Row("Escala: borrar marcados", Now(), "F", Now() == "F" && p.Markers.Count == 1);
        ProjectMarkers.ClearChords(p);
        Row("Escala: borrar todos", $"{p.ChordMarkers.Count} acordes, {p.Markers.Count} marcador", "0 acordes, 1 marcador", p.ChordMarkers.Count == 0 && p.Markers.Count == 1);

        var rng = new Random(7);
        var noise = new float[Sr * 15];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(0.2 * (rng.NextDouble() * 2 - 1));
        var hiss = ScaleDetector.Detect(noise, noise, Sr);
        Row("Escala: ruido sin notas", hiss == null ? "nada" : $"{hiss.Tonal:0.00}", $"< {ScaleDetector.MinTonal:0.00}", hiss == null || hiss.Tonal < ScaleDetector.MinTonal);

        var brief = new float[Sr * 2];
        Row("Escala: 2 s es muy poco", Got(ScaleDetector.Detect(brief, brief, Sr)), "nada", ScaleDetector.Detect(brief, brief, Sr) == null);
        var quiet = new float[Sr * 10];
        Row("Escala: silencio", Got(ScaleDetector.Detect(quiet, quiet, Sr)), "nada", ScaleDetector.Detect(quiet, quiet, Sr) == null);
        return allOk;
    }

    /// <summary>Four loops of strummed triads with their root in the bass, ending back on the first chord, from
    /// <paramref name="tonic"/>: each chord is its step above the tonic and whether it is minor.</summary>
    static List<(double At, double Len, int Pitch)> Progression(int tonic, (int Step, bool Minor)[] chords)
    {
        const double Bar = 1.6;
        const int LowestRoot = 40;
        var notes = new List<(double, double, int)>();
        var song = Enumerable.Repeat(chords, 4).SelectMany(x => x).Append(chords[0]).ToList();
        for (int c = 0; c < song.Count; c++)
        {
            var (step, minor) = song[c];
            int root = LowestRoot + ((tonic + step - LowestRoot) % 12 + 12) % 12;
            int[] shape = [root, root + 7, root + 12, root + (minor ? 15 : 16), root + 19];
            for (int s = 0; s < shape.Length; s++) notes.Add((0.2 + c * Bar + s * 0.012, Bar - 0.1, shape[s]));
        }
        return notes;
    }

    static (float[] L, float[] R) Pluck(List<(double At, double Len, int Pitch)> notes, int seed, int sr) => ToMidiChecks.Pluck(notes, seed, sr);
}
