using System.Text.Json;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// The timeline's edits on MIDI clips (a part of <c>midi comprobar</c>): a division doesn't change what sounds, frame by
/// frame and sample by sample, with and without a pattern, with notes that cross the cut; the range takes its stretch out
/// of the clips; every edit undoes; and the piano roll's test tone goes through the track's chain without allocating.
/// </summary>
static class MidiSplitChecks
{
    const int Sr = 48000;
    const double Bpm = 120;

    static bool Row(string name, bool ok)
    {
        Console.WriteLine($"{"Dividir: " + name,-62} {(ok ? "igual" : "DISTINTO")}");
        return ok;
    }

    static Project New(out Track t)
    {
        var p = new Project();
        p.Settings.Bpm = Bpm;
        string id = MidiEditing.AddTrack(p);
        t = p.Tracks.Find(x => x.Id == id)!;
        return p;
    }

    static MidiNote Note(int pitch, double beat, double beats, int velocity = 100) => new() { Pitch = pitch, Beat = beat, Beats = beats, Velocity = velocity };

    static double Sec(MidiClip c, double beat) => c.AtSec + beat * 60 / Bpm;

    /// <summary>Everything a track sends to the engine, in an order that doesn't depend on how the notes came.</summary>
    static string Sound(Track t)
    {
        if (ChannelDsp.Notes(t, Bpm, Sr) is not { } s) return "";
        static string Event(NoteEvent n) => $"{n.Start} {n.Length} {n.Hz:R} {n.Gain:R} {n.Pitch} {n.Velocity} {n.Release} {n.Pan:R} {n.BendSemis:R} {n.BendFrames} {n.Phase:R} {n.EnvFrames} {n.Tied}"
            + (n.Bend is { } b ? $" {b.At(0):R} {b.At(n.Length / 2):R} {b.At(n.Length):R}" : "");
        var lines = s.Notes.Select(n => "n " + Event(n)).Concat(s.Tone.Select(n => "t " + Event(n))).Concat(s.Controls.Select(c => $"c {c.Frame} {c.Controller} {c.Value:R}")).Order();
        return string.Join("\n", lines);
    }

    /// <summary>The test tone's samples of the whole track.</summary>
    static float[] Rendered(Track t, int frames)
    {
        var l = new float[frames];
        var r = new float[frames];
        if (ChannelDsp.Notes(t, Bpm, Sr) is { } s) NoteSynth.Render(s, 0, frames, Sr, l, r);
        return l;
    }

    static bool SameSound(Track t, string sound, float[] samples) => Sound(t) == sound && Rendered(t, samples.Length).SequenceEqual(samples);

    static string Saved(Project p) => JsonSerializer.Serialize(p.Tracks, ProjectStore.Json);

    public static bool Check()
    {
        bool ok = true;
        ok &= CheckPlain();
        ok &= CheckExpression();
        ok &= CheckPattern();
        ok &= CheckParts();
        ok &= CheckRange();
        ok &= CheckUndo();
        ok &= CheckPreview();
        ok &= CheckTrimStart();
        return ok;
    }

    /// <summary>The left edge of a clip: the notes stay where they sound, the end stays, a note crossing the start is cut there.</summary>
    static bool CheckTrimStart()
    {
        var p = New(out var t);
        var c = MidiEditing.AddClip(p, t.Id, 2, 8);
        c.Notes.Add(Note(60, 0, 2));
        c.Notes.Add(Note(62, 3, 2));
        c.Notes.Add(Note(64, 6, 2));
        double end = c.AtSec + c.LengthBeats * 60 / Bpm;
        MidiEditing.TrimStart(p, c.Id, c.AtSec + 4 * 60 / Bpm);
        bool ok = Math.Abs(c.AtSec + c.LengthBeats * 60 / Bpm - end) < 1e-6 && Math.Abs(c.LengthBeats - 4) < 1e-9
            && c.Notes.Count == 2 && c.Notes[0].Pitch == 62 && Math.Abs(c.Notes[0].Beat - 0) < 1e-9 && Math.Abs(c.Notes[0].Beats - 1) < 1e-9
            && c.Notes[1].Pitch == 64 && Math.Abs(c.Notes[1].Beat - 2) < 1e-9;
        Console.WriteLine($"{"Recortar inicio de clip MIDI",-62} {(ok ? "igual" : "DISTINTO")}");
        return ok;
    }

    static bool CheckPlain()
    {
        var p = New(out var t);
        var c = MidiEditing.AddClip(p, t.Id, 1, 8);
        MidiEditing.SetNotes(p, c.Id,
        [
            Note(60, 1, 4), Note(64, 1, 4), Note(67, 1, 4), // a chord across the cut
            new MidiNote { Pitch = 69, Beat = 2.5, Beats = 1, Pan = 40, ModX = 30, ModY = 90, Release = 50 }, // one that expresses
            Note(71, 2.75, 0.25 + 0.01), // its piece on the right is shorter than a note can be
            Note(72, 3, 1), // starts on the cut
            Note(74, 2, 1), // ends on the cut
            Note(76, 7, 4), // cut by the clip's end
            Note(77, 9, 1), // past the end: kept, not heard
        ]);
        string sound = Sound(t);
        var samples = Rendered(t, 8 * Sr);

        string right = MidiEditing.Split(p, c.Id, Sec(c, 3));
        var r = MidiEditing.Find(p, right).Clip;
        bool ok = Row("dos clips pegados", t.MidiClips!.Count == 2 && c.LengthBeats == 3 && r.LengthBeats == 5 && r.AtSec == Sec(c, 3));
        ok &= Row("sin patrón no cambia lo que suena", SameSound(t, sound, samples));
        ok &= Row("una nota cortada queda en los dos pedazos",
            c.Notes.Count(n => n.Pitch == 60 && n.EndBeat == 3) == 1 && r.Notes.Count(n => n.Pitch == 60 && n.Beat == 0 && n.Beats == 2 && n.Tie) == 1);
        ok &= Row("lo que no cruza va a su pedazo", c.Notes.Any(n => n.Pitch == 74) && r.Notes.All(n => n.Pitch != 74) && r.Notes.Single(n => n.Pitch == 72) is { Beat: 0, Tie: false });
        ok &= Row("lo que no se oía sigue guardado", r.Notes.Single(n => n.Pitch == 77).Beat == 6 && c.Notes.All(n => n.Pitch != 77));

        // a piece of a piece: the tie goes on
        MidiEditing.Split(p, right, Sec(r, 1));
        ok &= Row("dividir de nuevo tampoco cambia nada", t.MidiClips.Count == 3 && SameSound(t, sound, samples));

        // apart, a tie is a note with its attack
        MidiEditing.Move(p, t.MidiClips[2].Id, t.MidiClips[2].AtSec + 2);
        long moved = (long)(t.MidiClips[2].AtSec * Sr);
        ok &= Row("separados, el pedazo vuelve a atacar", ChannelDsp.Notes(t, Bpm, Sr)!.Notes.Any(n => n.Pitch == 60 && n.Start == moved));

        // a copy doesn't go on from anything
        var ties = t.MidiClips[1];
        ok &= Row("una copia no queda atada", ties.Notes.Any(n => n.Tie) && ties.Copy().Notes.All(n => !n.Tie) && ties.Copy(ties: true).Notes.Any(n => n.Tie));

        // the edges, a lock
        var q = New(out var u);
        var e = MidiEditing.AddClip(q, u.Id, 0, 4);
        bool Refuses(Action edit)
        {
            try { edit(); return false; }
            catch (ArgumentException) { return true; }
        }
        ok &= Row("no se corta pegado a un borde", Refuses(() => MidiEditing.Split(q, e.Id, 0.05)) && Refuses(() => MidiEditing.Split(q, e.Id, 1.95)) && u.MidiClips!.Count == 1);
        u.Locked = true;
        ok &= Row("una pista bloqueada no se divide", Refuses(() => MidiEditing.Split(q, e.Id, 1)) && u.MidiClips!.Count == 1);
        return ok;
    }

    /// <summary>Slides and portas around the cut, for a plugin (held notes and bends), the sampler (paths) and the test tone (tied events).</summary>
    static bool CheckExpression()
    {
        var p = New(out var t);
        var c = MidiEditing.AddClip(p, t.Id, 0, 16);
        MidiEditing.SetNotes(p, c.Id,
        [
            Note(60, 0, 2), new MidiNote { Pitch = 64, Beat = 2, Beats = 3, Slide = true }, // the slide crosses the cut at 4
            Note(67, 6, 3), new MidiNote { Pitch = 72, Beat = 7, Beats = 2, Porta = true }, // a porta with a tie under it at 8
            Note(48, 10, 4), new MidiNote { Pitch = 50, Beat = 11, Beats = 2, Slide = true }, // the held note crosses the cut at 12
        ]);
        string sound = Sound(t);
        var samples = Rendered(t, 10 * Sr);
        foreach (double beat in new[] { 4.0, 8.0, 12.0 })
            MidiEditing.Split(p, t.MidiClips!.Last(x => x.AtSec < beat / 2).Id, beat / 2);
        return Row("slides y portas alrededor del corte", t.MidiClips!.Count == 4 && SameSound(t, sound, samples));
    }

    static bool CheckPattern()
    {
        var p = New(out var t);
        var c = MidiEditing.AddClip(p, t.Id, 0, 16);
        MidiEditing.SetNotes(p, c.Id, [Note(60, 0, 3), Note(64, 3, 2), Note(67, 1, 1)]); // 64 is cut short by the pass
        MidiPatterns.Create(p, c.Id);
        MidiPatterns.SetBeats(p, c.Id, 4);
        MidiEditing.Place(p, c.Id, 0, 16);
        string other = MidiEditing.Duplicate(p, c.Id);
        var pattern = c.Notes.Select(n => (n.Pitch, n.Beat, n.Beats)).ToList();
        string sound = Sound(t);
        var samples = Rendered(t, 16 * Sr);

        // on a seam between passes both stay in the pattern
        var seam = MidiEditing.Find(p, MidiEditing.Split(p, c.Id, Sec(c, 8))).Clip;
        bool ok = Row("en la raya de una vuelta sigue en el patrón",
            seam.PatternId == c.PatternId && c.LengthBeats == 8 && seam.LengthBeats == 8 && seam.CellBeats == 4 && SameSound(t, sound, samples));
        MidiEditing.AddNote(p, seam.Id, 72, 2);
        ok &= Row("los dos pedazos siguen enlazados", c.Notes.Any(n => n.Pitch == 72) && MidiEditing.Find(p, other).Clip.Notes.Any(n => n.Pitch == 72));
        c.Notes.RemoveAll(n => n.Pitch == 72);
        MidiPatterns.Sync(p, c);

        // in the middle of a pass the right piece holds what it sounded, written out
        var mid = MidiEditing.Find(p, MidiEditing.Split(p, c.Id, Sec(c, 5.5))).Clip;
        ok &= Row("en medio de una vuelta suena igual", mid.PatternId == null && c.LengthBeats == 5.5 && mid.LengthBeats == 2.5 && SameSound(t, sound, samples));
        ok &= Row("el patrón no cambia", c.PatternId != null && c.Notes.Select(n => (n.Pitch, n.Beat, n.Beats)).SequenceEqual(pattern)
            && MidiEditing.Find(p, other).Clip.Notes.Select(n => (n.Pitch, n.Beat, n.Beats)).SequenceEqual(pattern) && MidiPatterns.Clips(p, c.PatternId!).Count() == 3);
        ok &= Row("el pedazo de la derecha no se repite", mid.Passes == 1 && mid.Notes.Count(n => n.Pitch == 60 && n.Tie) == 1);
        return ok;
    }

    static bool CheckParts()
    {
        var p = New(out var t);
        string second = MidiEditing.AddTrack(p);
        var u = p.Tracks.Find(x => x.Id == second)!;
        var c = MidiEditing.AddClip(p, t.Id, 0, 8);
        MidiEditing.SetNotes(p, c.Id, [Note(60, 0, 4)]);
        MidiPatterns.Create(p, c.Id);
        var drums = MidiPatterns.AddPart(p, c.Id, second);
        MidiEditing.SetNotes(p, drums.Id, [Note(36, 1, 2)]);
        string a = Sound(t), b = Sound(u);
        var sa = Rendered(t, 8 * Sr);
        var sb = Rendered(u, 8 * Sr);
        MidiEditing.Split(p, c.Id, Sec(c, 2));
        bool ok = Row("los instrumentos del patrón se cortan juntos", t.MidiClips!.Count == 2 && u.MidiClips!.Count == 2 && SameSound(t, a, sa) && SameSound(u, b, sb));
        // one of them locked stays as it is
        u.Locked = true;
        MidiEditing.Split(p, t.MidiClips[1].Id, Sec(c, 3));
        return ok & Row("el bloqueado no se corta", t.MidiClips.Count == 3 && u.MidiClips!.Count == 2);
    }

    static bool CheckRange()
    {
        var p = New(out var t);
        var c = MidiEditing.AddClip(p, t.Id, 0, 16);
        MidiEditing.SetNotes(p, c.Id, [Note(60, 2, 4), Note(64, 5, 1), Note(67, 6, 4), Note(72, 12, 1)]);
        var inside = MidiEditing.AddClip(p, t.Id, 3, 1); // whole inside the range
        var after = MidiEditing.AddClip(p, t.Id, 10, 4);
        MidiEditing.SetNotes(p, after.Id, [Note(62, 0, 1)]);

        // 2..4 s: the middle of the first one and all of the second one, the gap stays
        int changed = ClipEditing.DeleteRange(p, t.Id, 2, 4);
        bool ok = Row("borrar rango corta y saca", changed == 2 && t.MidiClips!.Count == 3 && c.LengthBeats == 4 && MidiEditing.TryFind(p, inside.Id) == null && after.AtSec == 10);

        p = New(out t);
        c = MidiEditing.AddClip(p, t.Id, 0, 16);
        MidiEditing.SetNotes(p, c.Id, [Note(60, 2, 4), Note(64, 5, 1), Note(67, 6, 4), Note(72, 12, 1)]);
        after = MidiEditing.AddClip(p, t.Id, 10, 4);
        MidiEditing.SetNotes(p, after.Id, [Note(62, 0, 1)]);
        ClipEditing.DeleteRange(p, t.Id, 2, 4); // beats 4..8: a note sticks out at each side
        var right = t.MidiClips!.Find(x => x != c && x != after)!;
        var s = ChannelDsp.Notes(t, Bpm, Sr)!;
        long from = 2 * Sr, to = 4 * Sr;
        ok &= Row("borrar rango sin patrón", c.LengthBeats == 4 && right.AtSec == 4 && right.LengthBeats == 8 && after.AtSec == 10
            && s.Notes.All(n => n.Start + n.Length <= from || n.Start >= to));
        ok &= Row("lo que sobra del rango ataca de nuevo", s.Notes.Any(n => n.Pitch == 67 && n.Start == to && n.Length == Sr / 2 * 2) && right.Notes.All(n => !n.Tie));

        // a pattern: what it keeps stays in it, the rest sounds as it did, and the other clip doesn't notice
        var q = New(out var u);
        var d = MidiEditing.AddClip(q, u.Id, 0, 16);
        MidiEditing.SetNotes(q, d.Id, [Note(60, 0, 3), Note(64, 2, 1)]);
        MidiPatterns.Create(q, d.Id);
        MidiPatterns.SetBeats(q, d.Id, 4);
        MidiEditing.Place(q, d.Id, 0, 16);
        string other = MidiPatterns.Insert(q, u.Id, d.PatternId!, 20).Id;
        var cell = d.Notes.Select(n => (n.Pitch, n.Beat, n.Beats)).ToList();
        ClipEditing.DeleteRange(q, u.Id, 2.5, 5); // beats 5..10
        var tail = u.MidiClips!.Find(x => x.AtSec == 5)!;
        var kept = ChannelDsp.Notes(u, Bpm, Sr)!;
        ok &= Row("borrar rango con patrón", d.PatternId != null && d.LengthBeats == 5 && tail.PatternId == null && tail.LengthBeats == 6
            && kept.Notes.All(n => n.Start + n.Length <= 2.5 * Sr || n.Start >= 5 * Sr)
            && d.Notes.Select(n => (n.Pitch, n.Beat, n.Beats)).SequenceEqual(cell) && MidiEditing.Find(q, other).Clip.Notes.Count == cell.Count);

        // closing the gap: what follows slides, and a note that stuck out starts over
        var w = New(out var v);
        var e = MidiEditing.AddClip(w, v.Id, 0, 16);
        MidiEditing.SetNotes(w, e.Id, [Note(60, 2, 8)]);
        var later = MidiEditing.AddClip(w, v.Id, 10, 4);
        ClipEditing.DeleteRangeClosing(w, [v.Id], 2, 3);
        var heard = ChannelDsp.Notes(v, Bpm, Sr)!.Notes;
        ok &= Row("cerrar el hueco corre lo de después", later.AtSec == 9 && v.MidiClips!.Find(x => x != e && x != later)!.AtSec == 2
            && heard.Length == 2 && heard.All(n => n.Pitch == 60) && heard[1].Start == 2 * Sr);

        // a locked track stays whole
        var y = New(out var z);
        var f = MidiEditing.AddClip(y, z.Id, 0, 16);
        z.Locked = true;
        ok &= Row("una pista bloqueada no cambia", ClipEditing.DeleteRange(y, z.Id, 1, 2) == 0 && ClipEditing.DeleteRangeClosing(y, [z.Id], 1, 2) == 0 && z.MidiClips!.Count == 1 && f.LengthBeats == 16);
        return ok;
    }

    static bool CheckUndo()
    {
        var p = New(out var t);
        var c = MidiEditing.AddClip(p, t.Id, 0, 16);
        MidiEditing.SetNotes(p, c.Id, [Note(60, 2, 4), Note(67, 8, 4)]);
        var history = new UndoManager();
        string before = Saved(p);

        history.Checkpoint(p);
        string right = MidiEditing.Split(p, c.Id, 2);
        string split = Saved(p);
        bool ok = Row("una división se deshace en un paso", history.Undo(p) && Saved(p) == before && p.Tracks[0].MidiClips!.Count == 1);
        ok &= Row("y se rehace, con la nota atada", history.Redo(p) && Saved(p) == split && MidiEditing.TryFind(p, right)?.Notes.Any(n => n.Tie) == true);

        // saved and read back, a tie is still one
        var read = JsonSerializer.Deserialize<Project>(JsonSerializer.Serialize(p, ProjectStore.Json), ProjectStore.Json)!;
        read.Normalize();
        ok &= Row("la nota atada se guarda", Sound(read.Tracks[0]) == Sound(p.Tracks[0]));

        history.Checkpoint(p);
        ClipEditing.DeleteRange(p, p.Tracks[0].Id, 1.5, 2.5);
        ok &= Row("un rango se deshace en un paso", history.Undo(p) && Saved(p) == split);
        history.Checkpoint(p);
        ClipEditing.DeleteRangeClosing(p, [p.Tracks[0].Id], 1.5, 2.5);
        ok &= Row("cerrar el hueco también", history.Undo(p) && Saved(p) == split);

        // a pattern's cut: both undo as one
        var q = New(out var u);
        var d = MidiEditing.AddClip(q, u.Id, 0, 16);
        MidiEditing.SetNotes(q, d.Id, [Note(60, 0, 2)]);
        MidiPatterns.Create(q, d.Id);
        MidiPatterns.SetBeats(q, d.Id, 4);
        MidiEditing.Place(q, d.Id, 0, 16);
        string whole = Saved(q);
        history = new UndoManager();
        history.Checkpoint(q);
        MidiEditing.Split(q, d.Id, 2.5);
        ok &= Row("con patrón también", history.Undo(q) && Saved(q) == whole);
        return ok;
    }

    /// <summary>What the track does with a block: <see cref="NotePreview"/> into its buffer, then its chain, like <c>LiveTrack.MixIdle</c>.</summary>
    static float[] ThroughChain(TrackDspChain chain, NotePreview preview, float[]? note, int frames, int block = 256)
    {
        var outL = new float[frames];
        var outR = new float[frames];
        if (note != null) preview.Post(note);
        for (int at = 0; at < frames; at += block)
        {
            int n = Math.Min(block, frames - at);
            var bl = chain.BufferL(n);
            var br = chain.BufferR(n);
            bl.Clear();
            br.Clear();
            preview.Mix(n, bl, br);
            chain.ProcessFilledBuffer(n, false, outL.AsSpan(at, n), outR.AsSpan(at, n), out _, out _, out _);
        }
        return outL;
    }

    static bool CheckPreview()
    {
        var note = NoteSynth.Audition(60, 100, Sr);
        var ch = new Channel();
        ChannelInserts.Add(ch, InsertSlot.DelayKind);
        var specs = ChannelDsp.Specs(ch, -24);

        TrackDspChain Chain(double volume = 1, bool muted = false)
        {
            var chain = new TrackDspChain(Sr) { Volume = volume, Muted = muted };
            chain.SetInserts(specs, fade: false);
            return chain;
        }
        int frames = note.Length + Sr;
        var dry = ThroughChain(new TrackDspChain(Sr), new NotePreview(Sr), note, frames);
        var wet = ThroughChain(Chain(), new NotePreview(Sr), note, frames);
        bool ok = Row("tono de prueba: sin slots sale la nota", dry.Take(note.Length).Select((v, i) => Math.Abs(v - note[i])).Max() < 1e-5);
        ok &= Row("tono de prueba: pasa por los slots", wet.Select((v, i) => Math.Abs(v - dry[i])).Max() > 1e-3);
        ok &= Row("tono de prueba: pasa por el fader", ThroughChain(Chain(volume: 0), new NotePreview(Sr), note, frames).All(v => v == 0)
            && ThroughChain(Chain(muted: true), new NotePreview(Sr), note, frames).All(v => v == 0));

        // the same whatever the block, after a chain with no memory of it
        ok &= Row("tono de prueba: igual en cualquier bloque",
            ThroughChain(new TrackDspChain(Sr), new NotePreview(Sr), note, frames, 37).Select((v, i) => Math.Abs(v - dry[i])).Max() < 1e-5);

        // the chain keeps running for a tail, then stops
        var preview = new NotePreview(Sr);
        var chainNow = Chain();
        ThroughChain(chainNow, preview, note, note.Length + Sr);
        bool tail = preview.Active;
        ThroughChain(chainNow, preview, null, 3 * Sr);
        ok &= Row("tono de prueba: deja sonar la cola y termina", tail && !preview.Active);

        // a note on top of another fades it, without a step
        var cut = new NotePreview(Sr);
        var l = new float[8192];
        var r = new float[8192];
        var flat = Enumerable.Repeat(0.5f, 8192).ToArray();
        cut.Post(flat);
        cut.Mix(1024, l, r);
        cut.Post(new float[8192]);
        l.AsSpan().Clear();
        cut.Mix(1024, l, r);
        ok &= Row("tono de prueba: una nota corta a la otra con fundido", l[0] > 0.49f && l[1023] == 0 && l.Take(1023).Zip(l.Skip(1)).All(x => Math.Abs(x.First - x.Second) < 0.005f));

        // no allocations on the audio thread
        var quiet = new NotePreview(Sr);
        var left = new float[256];
        var right = new float[256];
        quiet.Post(note);
        quiet.Mix(256, left, right);
        quiet.Post(note);
        long was = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            quiet.Mix(256, left, right);
            if (i == 100) quiet.Post(note);
            _ = quiet.Active;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - was;
        ok &= Row("tono de prueba: no asigna en el hilo de audio", allocated == 0);
        return ok;
    }
}
