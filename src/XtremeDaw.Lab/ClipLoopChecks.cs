using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// xtlab proyecto verificar-bucles: looping by the clip's corner (<see cref="ClipLoops"/>, <see cref="MidiEditing.Loop"/>,
/// <see cref="Automations.Loop"/>) on a project in memory. A take's repeats are whole clips one pass apart, the last one
/// cut where the loop ends, and they follow their source when it moves or is trimmed; a repeat edited by itself leaves
/// the loop, deleting the source takes them, and they are saved and read back. A MIDI clip and an automation block
/// repeat their first pass until their end, and a cut or a trimmed start keeps what sounded.
/// </summary>
static class ClipLoopChecks
{
    public static int Run()
    {
        int failed = 0;
        void Check(string what, bool ok)
        {
            Console.WriteLine($"{(ok ? "ok   " : "FALLA")} {what}");
            if (!ok) failed++;
        }
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-3;

        // a take: 4 s of media, 1 s hidden at its start, from 2 s on the timeline (a pass of 3 s, from 3 to 6)
        var media = new Media { Name = "toma", Audio = "media/toma.wav", DurationSec = 4 };
        var source = new Clip { MediaId = media.Id, AtSec = 2, TrimStartSec = 1 };
        var t = new Track { Name = "Guitarra", Clips = [source] };
        var p = new Project { Media = [media], Tracks = [t] };

        ClipLoops.LoopTo(p, source.Id, 13);
        var reps = ClipLoops.Repeats(t, source.Id).ToList();
        var w = reps.Select(r => ClipEditing.Window(r, media)).ToList();
        Check($"un bucle hasta 13 s hace 3 repeticiones de 3 s ({reps.Count})", reps.Count == 3
            && Near(w[0].Start, 6) && Near(w[0].End, 9) && Near(w[1].Start, 9) && Near(w[2].Start, 12) && Near(w[2].End, 13));
        Check("la última se corta donde termina el bucle, sin tocar el original", Near(reps[2].TrimEndSec, 2) && source.TrimEndSec == 0);
        Check("se arrastra desde una repetición: actúa sobre el original", ClipLoops.SourceId(reps[1]) == source.Id);

        ClipLoops.LoopTo(p, reps[1].Id, 7);
        Check("acortar el bucle saca las que sobran", ClipLoops.Repeats(t, source.Id).Count() == 1);
        ClipLoops.LoopTo(p, source.Id, 5);
        Check("terminarlo dentro del original lo saca", ClipLoops.Repeats(t, source.Id).Count() == 0 && t.Clips.Count == 1);

        ClipLoops.LoopTo(p, source.Id, 12);
        var loops = ClipLoops.Take(p);
        var moved = ClipEditing.Copy(source);
        moved.AtSec = 4;
        ClipEditing.Replace(p, moved);
        ClipLoops.Keep(p, loops);
        var after = ClipLoops.Repeats(t, source.Id).Select(r => ClipEditing.Window(r, media)).ToList();
        Check("mover el original lleva sus repeticiones, el bucle igual de largo",
            after.Count == 2 && Near(after[0].Start, 8) && Near(after[^1].End, 14));

        loops = ClipLoops.Take(p);
        var trimmed = ClipEditing.Copy(t.Clips.Find(c => c.Id == source.Id)!);
        trimmed.TrimEndSec = 1;
        ClipEditing.Replace(p, trimmed);
        ClipLoops.Keep(p, loops);
        var shorter = ClipLoops.Repeats(t, source.Id).Select(r => ClipEditing.Window(r, media)).ToList();
        Check($"recortarlo rehace las repeticiones con la vuelta nueva ({shorter.Count} de 2 s)",
            shorter.Count == 4 && Near(shorter[0].Start, 7) && Near(shorter[0].End, 9) && Near(shorter[^1].End, 14));

        loops = ClipLoops.Take(p);
        var one = ClipLoops.Repeats(t, source.Id).First();
        one.GainDb = -6;
        ClipLoops.Keep(p, loops);
        Check("una repetición editada sola sale del bucle", one.LoopOf == null && ClipLoops.Repeats(t, source.Id).Count() == 3);

        var copies = ClipEditing.DuplicateMany(p, [ClipLoops.Repeats(t, source.Id).Last().Id]);
        Check("su copia es un clip común", t.Clips.Find(c => c.Id == copies[0])!.LoopOf == null);

        string json = System.Text.Json.JsonSerializer.Serialize(p, ProjectStore.Json);
        var back = System.Text.Json.JsonSerializer.Deserialize<Project>(json, ProjectStore.Json)!;
        back.Normalize();
        Check("se guarda y se lee", ClipLoops.Repeats(back.Tracks[0], source.Id).Count() == 3);

        int before = t.Clips.Count;
        ClipEditing.RemoveClip(p, source.Id);
        Check("borrar el original se lleva sus repeticiones", t.Clips.Count == before - 4 && t.Clips.All(c => c.LoopOf == null));

        // MIDI: one bar (4 beats) with a note on each half, looped to 3 bars and a half
        var midi = new MidiClip { AtSec = 0, LengthBeats = 4, Notes = [new() { Pitch = 60, Beat = 0, Beats = 1 }, new() { Pitch = 64, Beat = 2, Beats = 1 }] };
        var mt = new Track { Name = "Teclado", Kind = TrackKinds.Midi, MidiClips = [midi] };
        p.Tracks.Add(mt);
        MidiEditing.Loop(p, midi.Id, 14);
        var heard = MidiFiles.Heard(midi);
        Check($"un clip MIDI en bucle repite su compás ({heard.Count} notas)", midi.LoopBeats == 4 && midi.Passes == 4 && heard.Count == 7
            && heard.Exists(n => n.Pitch == 60 && n.Beat == 12) && !heard.Exists(n => n.Beat >= 14));
        MidiEditing.Loop(p, midi.Id, 20);
        Check("alargarlo de nuevo sigue con la misma vuelta", midi.LoopBeats == 4 && midi.LengthBeats == 20);
        MidiEditing.Pass(p, midi.Id, 2);
        Check($"acortar el original acorta todas las vueltas ({midi.LengthBeats} tiempos)",
            midi.LoopBeats == 2 && midi.LengthBeats == 10 && MidiFiles.Heard(midi).Count == 5 && midi.Notes.Count == 2);
        MidiEditing.Pass(p, midi.Id, 4);
        Check("alargarlo de nuevo trae lo que había", midi.LoopBeats == 4 && midi.LengthBeats == 20 && MidiFiles.Heard(midi).Count == 10);
        string right = MidiEditing.Split(p, midi.Id, 9 * 60 / p.Settings.Bpm);
        var rightClip = MidiEditing.Find(p, right).Clip;
        Check("dividirlo deja todo sonando igual",
            MidiFiles.Heard(midi).Count + MidiFiles.Heard(rightClip).Count(n => !n.Tie) == 10 && rightClip.LoopBeats == null && midi.LoopBeats == 4);
        MidiEditing.TrimStart(p, midi.Id, 2 * 60 / p.Settings.Bpm);
        Check("recortarle el inicio escribe las vueltas como notas", midi.LoopBeats == null && MidiFiles.Heard(midi).Count == 4 && midi.Notes.Exists(n => n.Pitch == 60 && Near(n.Beat, 6)));
        MidiEditing.Loop(p, right, 2);
        Check("terminarlo dentro de la primera vuelta no lo deja en bucle", rightClip.LoopBeats == null);

        // automation: a ramp of 2 s looped to 5 s
        var lane = new AutomationLane { Target = AutoTarget.Volume };
        var block = new AutomationBlock { AtSec = 1, LengthSec = 2, Points = [new() { Sec = 0, Value = 0 }, new() { Sec = 2, Value = 1 }] };
        lane.Blocks.Add(block);
        Automations.Loop(lane, block.Id, 5);
        Check("un bloque de automatización en bucle repite su curva",
            block.LoopSec == 2 && Near(Automations.PositionAt(lane, false, 2), 0.5) && Near(Automations.PositionAt(lane, false, 4), 0.5) && Near(Automations.PositionAt(lane, false, 5.5), 0.25));
        Automations.Pass(lane, block.Id, 1);
        Check("cambiar el largo del original rehace todo el bucle",
            block.LoopSec == 1 && Near(block.LengthSec, 2.5) && Near(Automations.PositionAt(lane, false, 2.5), 0.25) && Near(Automations.PositionAt(lane, false, 3.4), 0.2));
        Automations.Pass(lane, block.Id, 2);
        Check("y volverlo a su largo lo deja como estaba", block.LoopSec == 2 && Near(block.LengthSec, 5) && Near(Automations.PositionAt(lane, false, 4), 0.5));
        Automations.Split(lane, block.Id, 4, false);
        Check("dividirlo suena igual", lane.Blocks.Count == 2 && Near(Automations.PositionAt(lane, false, 3.5), 0.25) && Near(Automations.PositionAt(lane, false, 4.5), 0.75));

        // #281: a loop copied with Shift+drag, D or paste stays a loop (a pass of 3 s from 3 s, looped to 12 s)
        var lm = new Media { Name = "loop", Audio = "media/loop.wav", DurationSec = 4 };
        var ls = new Clip { MediaId = lm.Id, AtSec = 2, TrimStartSec = 1 };
        var lt = new Track { Name = "Loop", Clips = [ls] };
        var lp = new Project { Media = [lm], Tracks = [lt] };
        ClipLoops.LoopTo(lp, ls.Id, 12);
        int Loops() => lt.Clips.Count(c => ClipLoops.IsLooped(lt, c));
        bool Whole(Clip s, double end) => ClipLoops.Repeats(lt, s.Id).Count() == 2 && Near(ClipLoops.EndSec(lp, lt, s), end);
        // ClipEditing.Replace swaps the clip object: the source is read again each time
        Clip Src() => lt.Clips.Find(c => c.Id == ls.Id)!;

        // Shift+drag of the whole loop (source and repeats boxed) 20 s later
        var snap = ClipLoops.Take(lp);
        List<string> carried = [ls.Id, .. ClipLoops.Repeats(lt, ls.Id).Select(r => r.Id)];
        MidiEditing.LeaveCopies(lp, carried);
        foreach (var id in carried)
        {
            var c = ClipEditing.Copy(lt.Clips.Find(x => x.Id == id)!);
            c.AtSec += 20;
            ClipEditing.Replace(lp, c);
        }
        ClipLoops.Keep(lp, snap);
        var left = lt.Clips.Find(c => c.Id != ls.Id && c.LoopOf == null && Near(c.AtSec, 2));
        Check($"Shift+arrastrar un bucle entero deja la copia en bucle ({Loops()} bucles)",
            Loops() == 2 && left != null && Whole(left, 12) && Whole(Src(), 32) && lt.Clips.Count == 6);
        // the window keeps the moved clips selected by id: the repeats made again keep theirs, or the next Supr fails
        Check("las repeticiones rehechas conservan su id (la selección sigue valiendo)",
            carried.All(id => lt.Clips.Exists(c => c.Id == id && (id == ls.Id || c.LoopOf == ls.Id))));

        // Shift+drag of the source alone: its copy keeps the repeats it had
        snap = ClipLoops.Take(lp);
        MidiEditing.LeaveCopies(lp, [ls.Id]);
        var back20 = ClipEditing.Copy(Src());
        back20.AtSec += 20;
        ClipEditing.Replace(lp, back20);
        ClipLoops.Keep(lp, snap);
        var left2 = lt.Clips.Find(c => c.Id != ls.Id && c.LoopOf == null && Near(c.AtSec, 22));
        Check($"Shift+arrastrar solo el original deja la copia con su bucle ({Loops()} bucles)",
            Loops() == 3 && left2 != null && Whole(left2, 32) && Whole(Src(), 52) && lt.Clips.Count == 9);

        // D on a whole loop
        List<string> whole = [ls.Id, .. ClipLoops.Repeats(lt, ls.Id).Select(r => r.Id)];
        var dup = ClipEditing.DuplicateMany(lp, whole);
        var dupSource = lt.Clips.Find(c => c.Id == dup[0])!;
        Check("D sobre un bucle entero lo copia en bucle",
            Loops() == 4 && dupSource.LoopOf == null && Whole(dupSource, 52 + dupSource.AtSec - Src().AtSec));

        // Ctrl+C / Ctrl+V of a whole loop
        var board = ClipClipboard.Take(lp, whole)!;
        var pasted = board.Paste(lp, lt.Id, 100);
        var pastedSource = lt.Clips.Find(c => pasted.Contains(c.Id) && c.LoopOf == null)!;
        Check("copiar y pegar un bucle entero lo pega en bucle",
            Loops() == 5 && pastedSource != null && Whole(pastedSource, 52 + pastedSource.AtSec - Src().AtSec));

        Console.WriteLine(failed == 0 ? "Bucles: todo bien." : $"Bucles: {failed} fallas.");
        return failed == 0 ? 0 : 1;
    }
}
