using System.Diagnostics;
using XtremeDaw.Audio.Midi;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// A MIDI take that survives the program dying: <c>xtlab midi cortar</c> really is a process killed mid-take, with a
/// thread playing notes the way the audio thread does, and the checks open what it left. The other cases (it died after
/// saving, in the count-in, a file cut anywhere, a track that isn't there) build the same files without the kill.
/// </summary>
static class MidiTakeChecks
{
    const int Sr = 48000;
    const double Bpm = 120, StartSec = 2.7, StepSec = 0.01, NoteSec = 0.005;
    const int HeldPitch = 100, FirstPitch = 40, Pitches = 40;
    /// <summary>Long enough for several flushes to reach the disk before the kill.</summary>
    const int RunMs = 1200;

    /// <summary>Child process: records until it kills itself, with no Close and no save.</summary>
    public static int Die(string dir)
    {
        var store = ProjectStore.Open(dir);
        if (store.ReadOnly) throw new InvalidOperationException(store.ReadOnlyReason);
        var track = store.Project.Tracks.First(t => t.IsMidi);
        var j = new ProjectStore.MidiTakeJournal { ClipId = Ids.New("mid"), TrackId = track.Id, TrackName = track.Name, StartSec = StartSec };
        var take = new MidiTake();
        long pos = (long)(StartSec * Sr);
        var writer = new MidiTakeWriter(store.MidiNotesPath(j.ClipId), take, Sr, () => Interlocked.Read(ref pos));
        store.BeginMidiTake(j);
        GC.KeepAlive(writer);
        var player = new Thread(() =>
        {
            long at = pos;
            take.Add(at, new LiveNote { On = true, Pitch = HeldPitch, Sounds = HeldPitch, Velocity = 90 });
            for (int i = 0; ; i++)
            {
                byte pitch = (byte)(FirstPitch + i % Pitches);
                take.Add(at, new LiveNote { On = true, Pitch = pitch, Sounds = pitch, Velocity = 100 });
                take.Add(at + (long)(NoteSec * Sr), new LiveNote { On = false, Pitch = pitch, Sounds = pitch });
                at += (long)(StepSec * Sr);
                Interlocked.Exchange(ref pos, at);
                Thread.Sleep(1);
            }
        }) { IsBackground = true };
        player.Start();
        Thread.Sleep(RunMs);
        Process.GetCurrentProcess().Kill();
        return 3;
    }

    /// <summary>Writes a take's files the way a recording leaves them, closed but with its journal still there.</summary>
    static string Leave(ProjectStore store, Track? track, double startSec, double grid, long endFrame, params (long Frame, bool On, byte Pitch)[] events)
    {
        var j = new ProjectStore.MidiTakeJournal { ClipId = Ids.New("mid"), TrackId = track?.Id, TrackName = track?.Name ?? "Teclas", StartSec = startSec, GridBeats = grid };
        var take = new MidiTake();
        foreach (var e in events) take.Add(e.Frame, new LiveNote { On = e.On, Pitch = e.Pitch, Sounds = e.Pitch, Velocity = 80 });
        new MidiTakeWriter(store.MidiNotesPath(j.ClipId), take, Sr, () => endFrame).Close();
        store.BeginMidiTake(j);
        return j.ClipId;
    }

    static ProjectStore NewProject(string dir)
    {
        var p = new Project { Name = "Corte" };
        p.Settings.Bpm = Bpm;
        var store = ProjectStore.Create(dir, p);
        MidiEditing.AddTrack(p);
        p.Tracks[^1].Name = "Teclas";
        store.Save();
        return store;
    }

    static List<string> Left(ProjectStore store) =>
        [.. Directory.GetFiles(Path.Combine(store.Dir, ProjectStore.MediaDir)).Where(f => f.Contains(".notas") || f.Contains(".grabando-midi"))];

    public static bool Run(Action<string, string, string, bool> show)
    {
        string root = Path.Combine(Path.GetTempPath(), $"xtlab-toma-midi-{Guid.NewGuid():N}");
        int n = 0;
        string Folder() => Path.Combine(root, (++n).ToString());
        bool all = true;
        void Show(string what, string value, string want, bool good)
        {
            all &= good;
            show(what, value, want, good);
        }
        Directory.CreateDirectory(root);
        try
        {
            Killed(Folder(), Show);

            // it died after saving the clip, before deleting the journal: the clip isn't doubled
            string dir = Folder();
            using (var store = NewProject(dir))
            {
                var t = store.Project.Tracks[^1];
                string id = Leave(store, t, 0, 0, Sr, (100, true, 60), (2000, false, 60));
                MidiEditing.AddRecorded(store.Project, t.Id, 0, 1, [(0, 0.1, 60, 80)], 0, id);
                store.Save();
            }
            using (var store = ProjectStore.Open(dir))
                Show("Toma cortada: ya estaba guardada", $"{store.Project.Tracks[^1].MidiClips!.Count} clip", "1 clip",
                    store.Project.Tracks[^1].MidiClips!.Count == 1 && Left(store).Count == 0 && store.Notices.Count == 0);

            // only the count-in: the same as cancelling it
            dir = Folder();
            using (var store = NewProject(dir))
                Leave(store, store.Project.Tracks[^1], 1, 0, Sr, (Sr / 2, true, 60), (Sr * 3 / 4, false, 60));
            using (var store = ProjectStore.Open(dir))
                Show("Toma cortada: solo la cuenta", $"{store.Project.Tracks[^1].MidiClips?.Count ?? 0} clips", "0 clips",
                    (store.Project.Tracks[^1].MidiClips?.Count ?? 0) == 0 && Left(store).Count == 0);

            // a file cut in the middle of a record, zeros after a power cut, the track gone and quantized as it was going to be
            dir = Folder();
            string cut;
            using (var store = NewProject(dir))
            {
                cut = Leave(store, null, 0, 0.25, Sr * 2, (Sr / 10 + 200, true, 60), (Sr / 5, false, 60), (Sr * 11 / 10, true, 64));
                using var fs = new FileStream(store.MidiNotesPath(cut), FileMode.Append);
                fs.Write(new byte[17]);
            }
            using (var store = ProjectStore.Open(dir))
            {
                var t = store.Project.Tracks.Find(x => MidiEditing.TryFind(store.Project, cut) is { } c && x.MidiClips?.Contains(c) == true);
                var c = MidiEditing.TryFind(store.Project, cut);
                bool good = t is { IsMidi: true, Name: "Teclas" } && store.Project.Tracks.Count(x => x.IsMidi) == 2 && c is { AtSec: 0, Notes.Count: 2 }
                    && Math.Abs(c.Notes[0].Beat - 0.25) < 1e-9 && Math.Abs(c.Notes[1].Beat - 2.25) < 1e-9 && Math.Abs(c.Notes[1].Beats - 1.8) < 1e-6;
                Show("Toma cortada: archivo roto, sin pista", c == null ? "nada" : $"{c.Notes.Count} notas", "2 notas", good);
            }

            // what can't be read is kept, with a notice, never thrown away
            dir = Folder();
            string bad;
            using (var store = NewProject(dir))
            {
                bad = Leave(store, store.Project.Tracks[^1], 0, 0, Sr, (0, true, 60));
                File.WriteAllBytes(store.MidiNotesPath(bad), [1, 2, 3]);
            }
            using (var store = ProjectStore.Open(dir))
                Show("Toma cortada: ilegible", $"{Left(store).Count} archivos", "2 archivos", Left(store).Count == 2 && store.Notices.Count == 1);

            dir = Folder();
            using (var store = NewProject(dir))
            {
                bad = Leave(store, store.Project.Tracks[^1], 0, 0, Sr, (0, true, 60));
                File.WriteAllText(Path.Combine(store.Dir, ProjectStore.MediaDir, bad + ".grabando-midi.json"), "{ roto");
            }
            using (var store = ProjectStore.Open(dir))
                Show("Toma cortada: diario ilegible", $"{Left(store).Count} archivos", "2 archivos", Left(store).Count == 2 && store.Notices.Count == 1);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        return all;
    }

    /// <summary>A real process, killed mid-take: every note that reached the file comes back in order, the held key ends at the playhead, and a second open adds nothing.</summary>
    static void Killed(string dir, Action<string, string, string, bool> show)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        using (NewProject(dir)) { }
        string exe = Path.Combine(AppContext.BaseDirectory, "xtlab.exe");
        var start = new ProcessStartInfo(exe, ["midi", "cortar", dir]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var child = Process.Start(start)!)
        {
            string err = child.StandardError.ReadToEnd();
            child.WaitForExit();
            if (child.ExitCode is 0 or 2 or 3) throw new InvalidOperationException($"midi cortar no murió como debía ({child.ExitCode}): {err}");
        }
        using (var store = ProjectStore.Open(dir))
        {
            var clips = store.Project.Tracks[^1].MidiClips ?? [];
            var notes = clips.Count == 1 ? clips[0].Notes : [];
            var played = notes.Where(x => x.Pitch != HeldPitch).OrderBy(x => x.Beat).ToList();
            var held = notes.Find(x => x.Pitch == HeldPitch);
            double beat(double sec) => sec / 60 * Bpm;
            bool inOrder = true;
            for (int i = 0; i < played.Count; i++)
                inOrder &= played[i].Pitch == FirstPitch + i % Pitches && Math.Abs(played[i].Beat - (beat(StartSec + i * StepSec) - clips[0].AtSec / 60 * Bpm)) < 1e-3;
            bool good = !store.ReadOnly && clips.Count == 1 && played.Count >= 20 && inOrder && Left(store).Count == 0
                && held != null && held.Beats >= beat((played.Count - 1) * StepSec) - 1e-3 && store.Notices.Count == 1
                && store.Project.Log.Exists(e => e.Type == "recording-recovered");
            show("Toma cortada: proceso matado", $"{played.Count} notas", ">= 20", good);
        }
        using (var store = ProjectStore.Open(dir))
            show("Toma cortada: abrir de nuevo", $"{store.Project.Tracks[^1].MidiClips?.Count ?? 0} clip", "1 clip", store.Project.Tracks[^1].MidiClips?.Count == 1 && store.Notices.Count == 0);
    }
}
