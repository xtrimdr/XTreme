using System.Text;
using NAudio.Midi;
using XtremeDaw.Audio.Midi;
using XtremeDaw.Projects;
using MidiNote = XtremeDaw.Projects.MidiNote;

namespace XtremeDaw.Lab;

/// <summary>
/// xtlab midi exportar-mid, importar-mid and partitura, and their rows of midi comprobar: a .mid written and read back
/// keeps its notes, a foreign type-0 file is split by channel, and a score has bars that add up and the pages it says.
/// </summary>
static class MidiFileChecks
{
    /// <summary>midi exportar-mid carpeta --clip id --a x.mid: what the clip plays, at the project's tempo.</summary>
    public static int Export(Args a, Project p)
    {
        var (t, c) = MidiEditing.Find(p, a.Get("clip") ?? throw new ArgumentException("Falta --clip."));
        string path = Path.GetFullPath(a.Get("a") ?? throw new ArgumentException("Falta --a archivo.mid."));
        MidiFiles.Write(path, [(t.Name, MidiFiles.Heard(c))], p.Settings.Bpm);
        Console.WriteLine(path);
        return 0;
    }

    /// <summary>midi partitura carpeta --clip id --a x.pdf: the clip as a score, in the project's scale.</summary>
    public static int Sheet(Args a, Project p)
    {
        var (t, c) = MidiEditing.Find(p, a.Get("clip") ?? throw new ArgumentException("Falta --clip."));
        string path = Path.GetFullPath(a.Get("a") ?? throw new ArgumentException("Falta --a archivo.pdf."));
        int pages = ScoreSheet.Write(path, t.Name, MidiFiles.Heard(c), c.LengthBeats, MusicScale.Find(p.Settings.ScaleRoot, p.Settings.Scale));
        Console.WriteLine($"{path} ({pages} {(pages == 1 ? "página" : "páginas")})");
        return 0;
    }

    public static bool Check()
    {
        string dir = Directory.CreateTempSubdirectory("xtreme-midi").FullName;
        try { return CheckRoundTrip(dir) & CheckForeign(dir) & CheckImport() & CheckBars() & CheckPdf(dir); }
        finally { Directory.Delete(dir, true); }
    }

    static bool Row(string what, string value, string want, bool good)
    {
        Console.WriteLine($"{what,-36} {value,12} {want,10}  {(good ? "igual" : "DISTINTO")}");
        return good;
    }

    /// <summary>Same notes, name and tempo after a trip through a file; a muted note stays out, a retriggered key stays two notes.</summary>
    static bool CheckRoundTrip(string dir)
    {
        List<MidiNote> notes =
        [
            new() { Pitch = 60, Beat = 0, Beats = 1, Velocity = 100 },
            new() { Pitch = 60, Beat = 1, Beats = 0.5, Velocity = 77, Release = 40 },
            new() { Pitch = 64, Beat = 1.0625, Beats = 2.375, Velocity = 1 },
            new() { Pitch = 127, Beat = 3.25, Beats = 1.5, Velocity = 127, Release = 127 },
            new() { Pitch = 72, Beat = 2, Beats = 1, Muted = true },
        ];
        string path = Path.Combine(dir, "ida.mid");
        MidiFiles.Write(path, [("Patrón", notes), ("Vacía", [])], 97.5);
        var read = MidiFiles.Read(path);
        var kept = notes.Where(n => !n.Muted).ToList();
        var back = read.Tracks.FirstOrDefault();
        bool same = back is { Name: "Patrón", LengthBeats: 8 } && read.Tracks.Count == 1 && back.Notes.Count == kept.Count && kept.Zip(back.Notes).All(x =>
            x.First.Pitch == x.Second.Pitch && x.First.Beat == x.Second.Beat && x.First.Beats == x.Second.Beats
            && x.First.Velocity == x.Second.Velocity && x.First.Release == x.Second.Release);
        bool bpm = read.Bpm is { } b && Math.Abs(b - 97.5) < 0.01;
        return Row("MIDI: ida y vuelta por .mid", $"{back?.Notes.Count ?? 0} notas", $"{kept.Count}", same && bpm);
    }

    /// <summary>A type-0 file at another resolution: split by channel, a note-on at 0 closes, a note left open ends with the track.</summary>
    static bool CheckForeign(string dir)
    {
        var events = new MidiEventCollection(0, 480);
        events.AddTrack();
        events.AddEvent(new NoteEvent(0, 1, MidiCommandCode.NoteOn, 60, 90), 0);
        events.AddEvent(new NoteEvent(240, 1, MidiCommandCode.NoteOn, 60, 0), 0);
        events.AddEvent(new NoteEvent(480, 2, MidiCommandCode.NoteOn, 40, 70), 0);
        events.AddEvent(new NoteEvent(1920, 2, MidiCommandCode.NoteOn, 41, 70), 0);
        events.AddEvent(new NoteEvent(2400, 2, MidiCommandCode.NoteOff, 41, 33), 0);
        events.AddEvent(new MetaEvent(MetaEventType.EndTrack, 0, 2880), 0);
        string path = Path.Combine(dir, "ajeno.mid");
        MidiFile.Export(path, events);
        var read = MidiFiles.Read(path);
        bool ok = read is { Bpm: null, Tracks: [{ Name: "Canal 1", LengthBeats: 4, Notes: [{ Pitch: 60, Beat: 0, Beats: 0.5, Velocity: 90 }] },
            { Name: "Canal 2", LengthBeats: 8, Notes: [{ Pitch: 40, Beat: 1, Beats: 5 }, { Pitch: 41, Beat: 4, Beats: 1, Release: 33 }] }] };

        string junk = Path.Combine(dir, "roto.mid");
        File.WriteAllText(junk, "esto no es un MIDI");
        bool refused = false;
        try { MidiFiles.Read(junk); }
        catch (ArgumentException) { refused = true; }
        return Row("MIDI: archivo tipo 0 y archivo roto", $"{read.Tracks.Count} pistas", "2", ok && refused);
    }

    /// <summary>On a MIDI track everything is one clip; without one, a new track per file track.</summary>
    static bool CheckImport()
    {
        var data = new MidiFileData(120,
        [
            new("Bajo", [new MidiNote { Pitch = 40 }], 4),
            new("Piano", [new MidiNote { Pitch = 60, Beat = 5 }], 8),
        ]);
        var p = new Project();
        string target = MidiEditing.AddTrack(p);
        var one = MidiFiles.Import(p, data, target, 2);
        bool merged = one.Count == 1 && MidiEditing.Find(p, one[0]) is var (t, c) && t.Id == target && c is { AtSec: 2, LengthBeats: 8, Notes.Count: 2 };
        var two = MidiFiles.Import(p, data, null, 1);
        bool split = two.Count == 2 && p.Tracks.Count == 3 && p.Tracks[1] is { Name: "Bajo", IsMidi: true } && p.Tracks[2].Name == "Piano"
            && MidiEditing.Find(p, two[1]).Clip is { AtSec: 1, LengthBeats: 8 };
        return Row("MIDI: importar a pista y a pistas nuevas", $"{one.Count}+{two.Count} clips", "1+2", merged && split);
    }

    static int Sum(List<ScoreEvent> voice) => voice.Sum(e => e.Value);

    /// <summary>Every voice fills its bar; held notes tie over the bar line; chords share an event; a voice appears only where needed; keys spell.</summary>
    static bool CheckBars()
    {
        // a chord held over the bar line, a melody over a held bass, and a muted note that isn't there
        List<MidiNote> notes =
        [
            new() { Pitch = 60, Beat = 3, Beats = 2 },
            new() { Pitch = 64, Beat = 3, Beats = 2 },
            new() { Pitch = 72, Beat = 2, Beats = 1, Muted = true },
            new() { Pitch = 36, Beat = 4, Beats = 4 },
            new() { Pitch = 40, Beat = 4, Beats = 1 },
            new() { Pitch = 43, Beat = 5, Beats = 1 },
        ];
        var music = ScoreSheet.Bars(notes, 8, null);
        bool full = music.Bars.All(b => b.Treble.Concat(b.Bass).All(v => Sum(v) == ScoreSheet.StepsPerBar));
        var first = music.Bars[0].Treble[0];
        bool tied = first[^1] is { Start: 12, Value: 4, Heads: [{ TieOut: true }, { TieOut: true }] } && music.Bars[1].Treble[0][0].Heads is [{ TieIn: true, TieOut: false }, _];
        bool voices = music.Bars[0].Bass.Count == 1 && music.Bars[1].Bass.Count == 2 && music.Bars[1].Bass[1] is [{ Value: 16 }];

        // a syncopation doesn't hide the beat, a rest has no dot, and a dotted quarter is kept where it reads
        bool spell = ScoreSheet.Spell(2, 4, false).SequenceEqual([(2, 2), (4, 2)]) && ScoreSheet.Spell(0, 6, false).SequenceEqual([(0, 6)])
            && ScoreSheet.Spell(0, 6, true).SequenceEqual([(0, 4), (4, 2)]) && ScoreSheet.Spell(4, 8, false).SequenceEqual([(4, 4), (8, 4)]);

        // Bb in F major is the key's, B natural takes a sign and the next one doesn't; flats in a flat key, sharps in a sharp one
        var f = ScoreSheet.Bars([new MidiNote { Pitch = 70, Beats = 1 }, new MidiNote { Pitch = 71, Beat = 1, Beats = 1 }, new MidiNote { Pitch = 71, Beat = 2, Beats = 1 }, new MidiNote { Pitch = 61, Beat = 3, Beats = 1 }], 4, MusicScale.Find(5, "major"));
        var heads = f.Bars[0].Treble[0].Select(e => e.Heads[0]).ToList();
        bool key = f.Fifths == -1 && heads is [{ Accidental: null, Alter: -1 }, { Accidental: 0, Alter: 0 }, { Accidental: null }, { Accidental: -1, Step: 29 }]
            && ScoreSheet.Bars([], 4, MusicScale.Find(9, "minor")).Fifths == 0 && ScoreSheet.Bars([], 4, MusicScale.Find(4, "minor")).Fifths == 1
            && ScoreSheet.Bars([], 4, MusicScale.Find(0, "minor")).Fifths == -3 && ScoreSheet.Bars([], 4, MusicScale.Find(2, "dorian")).Fifths == 0
            && ScoreSheet.Bars([new MidiNote { Pitch = 70 }], 4, MusicScale.Find(2, "major")).Bars[0].Treble[0][0].Heads[0] is { Accidental: 1, Alter: 1 };
        return Row("Partitura: compases, voces y armadura", full && tied && voices ? "bien" : "mal", "bien", full && tied && voices && spell && key);
    }

    /// <summary>A real file: its frame, a cross-reference table that points at its objects, and as many pages as the layout said.</summary>
    static bool CheckPdf(string dir)
    {
        var notes = new List<MidiNote>();
        const int Bars = 64;
        for (int i = 0; i < Bars * 16; i++)
            notes.Add(new MidiNote { Pitch = 60 + i % 13, Beat = i * 0.25, Beats = 0.25 });
        for (int i = 0; i < Bars; i++)
            notes.Add(new MidiNote { Pitch = 36 + i % 12, Beat = i * 4, Beats = 8 });
        string path = Path.Combine(dir, "partitura.pdf");
        int pages = ScoreSheet.Write(path, "Partitura larga (¿con eñes?)", notes, Bars * 4, MusicScale.Find(3, "major"));
        int laidOut = ScoreSheet.Pages(ScoreSheet.Bars(notes, Bars * 4, MusicScale.Find(3, "major"))).Count;
        string text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        int pageObjects = 0;
        for (int at = text.IndexOf("/Type /Page ", StringComparison.Ordinal); at >= 0; at = text.IndexOf("/Type /Page ", at + 1, StringComparison.Ordinal)) pageObjects++;
        int xref = int.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
        bool frame = text.StartsWith("%PDF-1.4") && text.EndsWith("%%EOF") && text.AsSpan(xref).StartsWith("xref") && text.Contains($"/Count {pages} ");
        // a sixteenths' bar fills its line, so these 64 bars can't fit in fewer than this many systems of about 130 points
        bool many = pages >= Bars * 120 / 760;
        return Row("Partitura: PDF con sus páginas", $"{pages} páginas", $"{laidOut}", frame && pages == laidOut && pageObjects == pages && many);
    }
}
