using XtremeDaw.Audio.Midi;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// The piano roll's tools without the window: <c>midi herramienta carpeta --clip id --tool arpeggiate [--rate 5 …]</c> runs any row
/// of <see cref="MidiTools.All"/>, and <see cref="Check"/> (a part of <c>midi comprobar</c>) verifies what each one promises, and that
/// the live arpeggiator and the keyboard's scale do what they say.
/// </summary>
static class MidiToolChecks
{
    public static void Run(Project p, Args a, string clipId)
    {
        string id = a.Get("tool") ?? throw new ArgumentException($"--tool: {string.Join(", ", MidiTools.All.Select(t => t.Id))}.");
        var tool = MidiTools.Find(id) ?? throw new ArgumentException($"--tool: {string.Join(", ", MidiTools.All.Select(t => t.Id))}.");
        var clip = MidiEditing.Find(p, clipId).Clip;
        var notes = clip.Notes.Select(n => n.Copy()).ToList();
        var run = new ToolRun
        {
            Notes = notes, Targets = [.. notes], ClipBeats = clip.CellBeats, OriginBeats = clip.AtSec * p.Settings.Bpm / 60, SnapBeats = a.Double("iman", 0.25),
            Bpm = p.Settings.Bpm, Scale = MusicScale.Find(p.Settings.ScaleRoot, p.Settings.Scale),
            Values = tool.Params.Where(x => x.Kind != ToolParamKind.Text && a.Get(x.Id) != null).ToDictionary(x => x.Id, x => a.Int(x.Id, x.Default)),
            Words = tool.Params.Where(x => x.Kind == ToolParamKind.Text && a.Get(x.Id) != null).ToDictionary(x => x.Id, x => a.Get(x.Id)!),
        };
        MidiTools.Apply(tool, run);
        MidiEditing.SetNotes(p, clipId, notes);
        Console.WriteLine($"{notes.Count} notas.");
    }

    static MidiNote Note(int pitch, double beat, double beats = 1, int velocity = 100) => new() { Pitch = pitch, Beat = beat, Beats = beats, Velocity = velocity };

    static List<MidiNote> Tool(string id, List<MidiNote> notes, Dictionary<string, int>? values = null, Dictionary<string, string>? words = null, MusicScale? scale = null, double clip = 16, double play = 0)
    {
        var run = new ToolRun
        {
            Notes = notes, Targets = [.. notes], Values = values ?? [], Words = words ?? [], ClipBeats = clip, SnapBeats = 0.25, Scale = scale, Bpm = 120, PlayBeat = play,
        };
        MidiTools.Apply(MidiTools.Find(id)!, run);
        return notes;
    }

    static List<MidiNote> Chord() => [Note(60, 0, 4), Note(64, 0, 4), Note(67, 0, 4)];

    static bool Row(string name, bool ok)
    {
        Console.WriteLine($"{"Herramientas: " + name,-62} {(ok ? "igual" : "DISTINTO")}");
        return ok;
    }

    public static bool Check()
    {
        bool ok = true;
        var c = MusicScale.Find(0, "major");
        // every tool runs on nothing and on a clip, in range, with its defaults
        foreach (var t in MidiTools.All)
        {
            var notes = Tool(t.Id, Chord());
            Tool(t.Id, []);
            ok &= notes.All(n => n.Pitch is >= 0 and <= 127 && n.Beat >= 0 && n.Beats > 0);
        }
        ok = Row("herramientas: todas corren, en rango", ok);

        var arp = Tool("arpeggiate", Chord(), new() { ["rate"] = 2 });
        ok &= Row("arpegiar: una nota por paso, de grave a agudo", arp.Count == 4 && arp.Select(n => n.Pitch).SequenceEqual([60, 64, 67, 60]) && arp[1].Beat == 1);
        var two = Tool("arpeggiate", Chord(), new() { ["rate"] = 5, ["octaves"] = 2 });
        ok &= Row("arpegiar: dos octavas", two.Max(n => n.Pitch) == 79);
        var strum = Tool("strum", Chord(), new() { ["time"] = 50 });
        ok &= Row("rasgueo: abre el acorde sin mover los finales", strum[2].Beat > strum[0].Beat && Math.Abs(strum[2].EndBeat - 4) < 1e-9 && strum[0].Beat == 0);
        var down = Tool("strum", Chord(), new() { ["time"] = 50, ["direction"] = 1 });
        ok &= Row("rasgueo: de agudo a grave", down.First().Pitch == 67);
        var legato = Tool("legato", [Note(60, 0, 0.5), Note(62, 2, 0.5), Note(64, 3, 0.5)]);
        ok &= Row("legato: llega hasta la siguiente", legato[0].Beats == 2 && legato[1].Beats == 1);
        var chop = Tool("chop-quick", [Note(60, 0, 1)]);
        ok &= Row("cortar: un pedazo por paso", chop.Count == 4 && chop.All(n => n.Beats == 0.25));
        var glue = Tool("glue", [Note(60, 0, 1), Note(60, 1, 1), Note(62, 1, 1)]);
        ok &= Row("unir: junta las contiguas de la misma fila", glue.Count == 2 && glue[0].Beats == 2);
        var flam = Tool("flam", [Note(60, 1)], new() { ["time"] = 30 });
        ok &= Row("flam: un golpe antes", flam.Count == 2 && flam[0].Beat < 1 && flam[0].Velocity < flam[1].Velocity);
        var limit = Tool("limit", [Note(30, 0), Note(100, 1)], new() { ["low"] = 48, ["high"] = 72 });
        ok &= Row("limitar: todo adentro, misma nota", limit.All(n => n.Pitch is >= 48 and <= 72) && limit[0].Pitch % 12 == 6 && limit[1].Pitch % 12 == 4);
        var flip = Tool("flip", [Note(60, 0, 1), Note(72, 3, 1)], new() { ["across"] = 1, ["upDown"] = 1 });
        ok &= Row("espejar: en tiempo y altura", flip[0].Beat == 0 && flip[0].Pitch == 60 && flip[1].Pitch == 72 && flip[1].Beat == 3);
        var first = Tool("random", [], new() { ["seed"] = 7, ["count"] = 12 }, scale: c);
        var again = Tool("random", [], new() { ["seed"] = 7, ["count"] = 12 }, scale: c);
        ok &= Row("azar: misma semilla, mismas notas, en escala", first.Count > 0 && first.Select(n => (n.Pitch, n.Beat)).SequenceEqual(again.Select(n => (n.Pitch, n.Beat))) && first.All(n => c!.Contains(n.Pitch)));
        var riff = Tool("riff", [], new() { ["seed"] = 3 }, scale: c);
        ok &= Row("riff: en escala, dentro del clip", riff.Count > 8 && riff.All(n => c!.Contains(n.Pitch) && n.Beat < 16));
        var typed = Tool("progression", [], new() { ["bass"] = 0, ["length"] = 1 }, new() { ["chords"] = "Am F C G" }, c);
        ok &= Row("progresión: nombres", typed.Count == 12 && typed.Take(3).All(n => n.Pitch % 12 is 9 or 0 or 4));
        var degrees = ChordProgressions.Parse("I V vi IV", c!);
        ok &= Row("progresión: grados", degrees.Select(x => x.Root).SequenceEqual([0, 7, 9, 5]) && degrees[2].Steps[1] == 3);
        var q = Tool("quantize", [Note(60, 0.1, 1)], new() { ["grid"] = 2, ["strength"] = 50 });
        ok &= Row("cuantizar: la fuerza deja a mitad de camino", Math.Abs(q[0].Beat - 0.05) < 1e-9);
        var off = Tool("quantize", [Note(60, 0.4, 1)], new() { ["grid"] = 2, ["sensitivity"] = 20 });
        ok &= Row("cuantizar: la sensibilidad deja las lejanas", off[0].Beat == 0.4);
        var starts = Tool("quantize-starts", [Note(60, 0.3, 1)]);
        ok &= Row("cuantizar inicios: el final no se mueve", starts[0].Beat == 0.25 && Math.Abs(starts[0].EndBeat - 1.3) < 1e-9);
        var levels = Tool("levels", [Note(60, 0, 1, 100)], new() { ["multiply"] = 100, ["invert"] = 1 });
        ok &= Row("niveles: invertir", levels[0].Velocity == 27);
        var rotate = Tool("rotate", [Note(60, 15.75, 0.25)], new() { ["steps"] = 1 });
        ok &= Row("rotar: da la vuelta", rotate[0].Beat == 0);
        var space = Tool("insert-space", [Note(60, 0), Note(60, 4)], new() { ["steps"] = 4 }, play: 2);
        ok &= Row("insertar espacio", space[1].Beat == 5 && space[0].Beat == 0);
        var gone = Tool("delete-space", [Note(60, 0), Note(60, 1), Note(60, 4)], new() { ["steps"] = 4 }, play: 1);
        ok &= Row("borrar espacio", gone.Count == 2 && gone[1].Beat == 3);
        var muted = new List<MidiNote> { Note(60, 0), Note(62, 1) };
        muted[0].Muted = true;
        ok &= Row("elegir: silenciadas", MidiSelect.Run("muted", muted, [], 0, new Random(1), 0.25).Count == 1);
        ok &= Row("elegir: invertir", MidiSelect.Run("invert", muted, [muted[0]], 0, new Random(1), 0.25).Single() == muted[1]);
        ok &= Row("sellos: una escala por grados", MidiStamps.Notes(MidiStamps.Find("run-up")!, 60, 0, c, 100) is { Count: 8 } run && run[7].Pitch == 72);

        var live = new LiveNotes(48000) { Arp = new LiveArp(0, 0.25, 1, 0.5) };
        live.Press(0, 1);
        live.AllOff();
        live.BeginBlock();
        live.Handle(new MidiEvent { Kind = MidiKind.NoteOn, Pitch = 60, Velocity = 90 });
        live.Handle(new MidiEvent { Kind = MidiKind.NoteOn, Pitch = 64, Velocity = 90 });
        ok &= Row("arpegiador: las teclas no suenan solas", live.EventCount == 0);
        var heard = new List<int>();
        for (int block = 0; block < 64; block++)
        {
            live.BeginBlock();
            live.RunArp(block * 960L, 960, 24000);
            foreach (var e in live.Events)
                if (e.On) heard.Add(e.Sounds);
        }
        ok &= Row("arpegiador: alterna las teclas en la grilla", heard.Count >= 7 && heard.Contains(60) && heard.Contains(64) && heard.Zip(heard.Skip(1)).All(x => x.First != x.Second));
        var snapped = new LiveNotes(48000) { Snap = c };
        snapped.BeginBlock();
        snapped.Handle(new MidiEvent { Kind = MidiKind.NoteOn, Pitch = 61, Velocity = 90 });
        bool lands = snapped.Events[0].Sounds is 60 or 62;
        snapped.Handle(new MidiEvent { Kind = MidiKind.NoteOff, Pitch = 61 });
        ok &= Row("teclado a la escala: cae en la escala y se suelta", lands && !snapped.AnySounding);
        return ok;
    }
}
