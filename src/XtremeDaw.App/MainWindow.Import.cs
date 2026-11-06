using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using XtremeDaw.App.Audio;
using XtremeDaw.App.Model;
using XtremeDaw.Audio;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// Bringing outside audio or video into the project (native only, asked by the user): "Importar audio…" in the new-track
/// menu, dropping files on the window, or a sample from the Biblioteca. <see cref="ImportMode"/> says whether each file is
/// copied into the project's media/ or linked where it is (asking first, by default). Each one is decoded in the
/// background, with a progress toast, and lands on its own track at the playhead, as one undo step; the
/// selected empty track takes the first one, like a take does. Dropped on the timeline it lands there instead, on the
/// track under it when that stretch is free. Several files dropped together go one under the other, from the track
/// under the pointer down, and the ones that find no free track there make new ones (<see cref="AudioImport.Stack"/>,
/// drawn as ghosts while they are carried). The decoded audio goes straight to the clip, so the file is read once.
/// </summary>
public partial class MainWindow
{
    static readonly FilePickerFileType AudioFiles = new("Audio")
    {
        Patterns = [.. MediaDecoder.AudioExtensions.Concat(MediaDecoder.VideoExtensions).Select(e => "*" + e)],
    };

    /// <summary>Samples carried from the Biblioteca: their paths, one per line, in the order of the list.</summary>
    static readonly DataFormat<string> SampleDrag = DataFormat.CreateStringApplicationFormat("xtreme-sample");
    /// <summary>A take of the project carried from the Biblioteca: its media id. It is already in media/, so nothing is copied.</summary>
    static readonly DataFormat<string> TakeDrag = DataFormat.CreateStringApplicationFormat("xtreme-take");

    /// <summary>What part of a file's bar is decoding it; the rest is copying it into media/.</summary>
    const double ReadShare = 0.8;

    /// <summary>Audio decoded by an import, by media id, for <see cref="ApplyTracks"/> to take instead of reading the file again.</summary>
    readonly Dictionary<string, MediaAudio> importedAudio = [];

    /// <summary>The Biblioteca's files being carried, whose lengths the ghosts take; empty for a drag from elsewhere.</summary>
    IReadOnlyList<SampleFile> carried = [];
    /// <summary>The last plan shown while dragging, made again only when the place or the files change.</summary>
    (double Sec, string? TrackId, int Count, string?[] Targets)? dropPlan;
    /// <summary>The same for a take of the project: its ghost, made again only when the place changes.</summary>
    (double Sec, string? TrackId, string MediaId, Controls.TimelineView.DropGhost[] Ghosts)? takePlan;

    void InitImport()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.DataTransfer.Contains(PatternDrag))
            {
                bool onMidi = PatternTarget(e) != null;
                e.DragEffects = onMidi ? DragDropEffects.Copy : DragDropEffects.None;
                if (onMidi) Timeline.ShowDrop(e.GetPosition(Timeline), e.KeyModifiers);
                else Timeline.HideDrop();
                return;
            }
            // what comes from the Biblioteca only lands on the timeline: letting go of it over the list must do nothing
            bool can = CanImport() && (FromLibrary(e) ? Timeline.DropTarget(e.GetPosition(Timeline), e.KeyModifiers) != null : DroppedFiles(e).Any());
            e.DragEffects = can ? DragDropEffects.Copy : DragDropEffects.None;
            if (!can) Timeline.HideDrop();
            else if (e.DataTransfer.TryGetValue(TakeDrag) is { } carriedTake) ShowTakeGhost(e, carriedTake);
            else ShowDropGhosts(e);
        });
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => { Timeline.HideDrop(); dropPlan = null; takePlan = null; });
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (e.DataTransfer.TryGetValue(PatternDrag) is { } pattern)
            {
                var target = PatternTarget(e);
                e.Handled = true;
                Timeline.HideDrop();
                if (target is { } to) await InsertPattern(to.TrackId, pattern, to.Sec);
                return;
            }
            var files = DroppedFiles(e).ToList();
            e.Handled = true;
            var place = Timeline.DropTarget(e.GetPosition(Timeline), e.KeyModifiers);
            var targets = place is { } planned ? DropTargets(files, planned.Sec, planned.TrackId) : null;
            Timeline.HideDrop();
            dropPlan = null;
            takePlan = null;
            if (e.DataTransfer.TryGetValue(TakeDrag) is { } take)
            {
                if (place is { } spot && CanImport()) await EditTimeline(p => ClipEditing.PlaceMedia(p, take, spot.Sec, spot.TrackId), null);
                return;
            }
            if (files.Count == 0) return;
            if (files.Any(SampleLibrary.IsMidiFile))
            {
                // MIDI files go onto the MIDI track under the pointer, or new ones, like "Importar .mid…"
                if (!CanImport() || (place == null && e.DataTransfer.Contains(SampleDrag))) return;
                string? midiTrack = place?.TrackId is { } id && tracks.Any(t => t.Track.Id == id && t.Track.IsMidi) ? id : null;
                foreach (string m in files.Where(SampleLibrary.IsMidiFile)) await ImportMidi(m, place?.Sec ?? engine.PositionSec, midiTrack);
                files = [.. files.Where(f => !SampleLibrary.IsMidiFile(f))];
                if (files.Count == 0) return;
                targets = place is { } again ? DropTargets(files, again.Sec, again.TrackId) : null;
            }
            if (files.Count == 1 && place?.TrackId is { } onto && tracks.FirstOrDefault(t => t.Track.Id == onto) is { Track.IsMidi: true, Track.Instrument: null } && CanImport())
            {
                await LoadSample(onto, files[0]);
                return;
            }
            if (place is { } at) await ImportAudio(files, at.Sec, at.TrackId, BringsPicture(files[0]), targets);
            else if (!e.DataTransfer.Contains(SampleDrag)) await ImportAudio(files, picture: BringsPicture(files[0]));
        });
    }

    static bool FromLibrary(DragEventArgs e) => e.DataTransfer.Contains(SampleDrag) || e.DataTransfer.Contains(TakeDrag);

    static IEnumerable<string> DroppedFiles(DragEventArgs e) =>
        e.DataTransfer.TryGetValue(SampleDrag) is { } sample ? sample.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            : (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>().Where(f => File.Exists(f));

    /// <summary>Where each file lands, one under the other from <paramref name="trackId"/> down (null = a new track).</summary>
    string?[]? DropTargets(IReadOnlyList<string> files, double sec, string? trackId)
    {
        if (store == null || files.Count == 0) return null;
        if (dropPlan is { } last && last.Sec == sec && last.TrackId == trackId && last.Count == files.Count) return last.Targets;
        var lengths = files.Select(f => carried.FirstOrDefault(c => c.Path == f)?.DurationSec is > 0 and var d ? d : 0).ToList();
        var targets = AudioImport.Stack(store.Project, Timeline.TracksFrom(trackId), sec, lengths);
        dropPlan = (sec, trackId, files.Count, targets);
        return targets;
    }

    /// <summary>The files being carried as ghosts where they would land: on their track, or new rows under the last.
    /// MIDI files follow the drop's own rule (the MIDI track under the pointer, else new tracks), with their length.</summary>
    void ShowDropGhosts(DragEventArgs e)
    {
        var all = DroppedFiles(e).ToList();
        var midi = all.Where(SampleLibrary.IsMidiFile).ToList();
        var files = midi.Count == 0 ? all : [.. all.Where(f => !SampleLibrary.IsMidiFile(f))];
        if (store == null || Timeline.DropTarget(e.GetPosition(Timeline), e.KeyModifiers) is not { } at
            || (files.Count > 0 ? DropTargets(files, at.Sec, at.TrackId) : []) is not { } targets)
        {
            Timeline.HideDrop();
            return;
        }
        string? midiTrack = at.TrackId is { } id && store.Project.Tracks.Find(t => t.Id == id) is { IsMidi: true } ? id : null;
        double beatSec = 60 / Math.Max(1, store.Project.Settings.Bpm);
        var ghosts = new Controls.TimelineView.DropGhost[all.Count];
        for (int k = 0; k < midi.Count; k++) ghosts[k] = new(midiTrack, MidiFileBeats(midi[k]) * beatSec, Path.GetFileName(midi[k]));
        for (int k = 0; k < files.Count; k++)
        {
            var file = carried.FirstOrDefault(c => c.Path == files[k]);
            ghosts[midi.Count + k] = new(targets[k], file?.DurationSec is > 0 and var d ? d : 0, Path.GetFileName(files[k]));
        }
        Timeline.ShowDrop(at.Sec, ghosts);
    }

    /// <summary>A carried .mid file's length in beats (what its clip will take), read once per file and change; 0 = unreadable.</summary>
    readonly Dictionary<string, (DateTime Written, double Beats)> midiFileBeats = [];

    double MidiFileBeats(string path)
    {
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
        if (midiFileBeats.TryGetValue(path, out var known) && known.Written == written) return known.Beats;
        double beats;
        try { beats = MidiFiles.Read(path).Tracks.Select(t => t.LengthBeats).DefaultIfEmpty(0).Max(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or FormatException or InvalidDataException) { beats = 0; }
        midiFileBeats[path] = (written, beats);
        return beats;
    }

    /// <summary>A take of the project carried from the Biblioteca, drawn like a carried sample: a ghost of its length on
    /// the track that takes it, or on a new row (<see cref="ClipEditing.PlanMedia"/>, the same rule as the drop).</summary>
    void ShowTakeGhost(DragEventArgs e, string mediaId)
    {
        if (store == null || Timeline.DropTarget(e.GetPosition(Timeline), e.KeyModifiers) is not { } at)
        {
            Timeline.HideDrop();
            return;
        }
        if (takePlan is not { } last || last.Sec != at.Sec || last.TrackId != at.TrackId || last.MediaId != mediaId)
        {
            var plan = ClipEditing.PlanMedia(store.Project, mediaId, at.Sec, at.TrackId);
            Controls.TimelineView.DropGhost[] ghosts = plan is { } g ? [new(g.TrackId, g.LengthSec, g.Name)] : [];
            takePlan = last = (at.Sec, at.TrackId, mediaId, ghosts);
        }
        if (last.Ghosts.Length == 0) Timeline.HideDrop();
        else Timeline.ShowDrop(at.Sec, last.Ghosts);
    }

    bool CanImport() => store != null && !store.ReadOnly && !restoring && !Projects.IsVisible && !RecordingBusy;

    async Task PickAudioToImport()
    {
        if (!CanImport()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("imp.title"),
            AllowMultiple = true,
            FileTypeFilter = [AudioFiles, FilePickerFileTypes.All],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0) await ImportAudio(paths, picture: BringsPicture(paths[0]));
    }

    /// <summary>True = link, false = copy, null = the import was cancelled. Nothing to ask when no file can come in.</summary>
    async Task<bool?> LinkOrCopy(IReadOnlyList<string> paths)
    {
        if (ImportMode.Mode != ImportMode.Ask) return ImportMode.Mode == ImportMode.Link;
        if (!paths.Any(AudioImport.IsSupported)) return false;
        var choice = await ImportAsk.Ask(paths);
        if (choice == Dialogs.ImportDialog.Choice.Cancel) return null;
        bool link = choice == Dialogs.ImportDialog.Choice.Link;
        if (ImportAsk.Remember) ImportMode.Set(link ? ImportMode.Link : ImportMode.Copy);
        return link;
    }

    /// <param name="atSec">Where they land; null = the playhead.</param>
    /// <param name="trackId">The track it was asked on, which takes it when that stretch is free; null = the rule above.</param>
    /// <param name="picture">A video brings its picture too; without it, only its sound.</param>
    /// <param name="targets">The track each file goes to (<see cref="AudioImport.Stack"/>, null = a new one), instead of the rule above.</param>
    /// <returns>The id of the last clip it made; null if none.</returns>
    async Task<string?> ImportAudio(IReadOnlyList<string> paths, double? atSec = null, string? trackId = null, bool picture = false, IReadOnlyList<string?>? targets = null)
    {
        if (!CanImport()) { Toast(Loc.T("imp.busy")); return null; }
        if (await LinkOrCopy(paths) is not { } link) return null;
        if (!CanImport()) { Toast(Loc.T("imp.busy")); return null; } // the question stayed open while something else started
        var st = store!;
        string dir = st.Dir;
        int done = 0;
        string? last = null;
        var toast = Toasts.ShowProgress(Loc.T("imp.importing", Path.GetFileName(paths[0])));
        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i], name = Path.GetFileName(path);
                if (!AudioImport.IsSupported(path))
                {
                    Toast(Loc.T("imp.unknown", name));
                    continue;
                }
                toast.Text = paths.Count == 1 ? Loc.T("imp.importing", name) : Loc.T("imp.importing.many", i + 1, paths.Count, name);
                int index = i;
                void Report(double from, double to, double part) =>
                    toast.Report((index + from + (to - from) * part) / paths.Count);
                AudioImport.Imported file;
                MediaAudio audio;
                bool wav;
                try
                {
                    (file, audio, wav) = await Task.Run(() =>
                    {
                        var loaded = AudioImport.Read(path, PlaybackEngine.SampleRate, p => Report(0, ReadShare, p));
                        var media = new MediaAudio(loaded);
                        bool video = picture && MediaDecoder.IsVideo(path);
                        AudioImport.Imported copied;
                        if (link)
                        {
                            copied = AudioImport.LinkIn(dir, path, loaded);
                            if (video) copied = AudioImport.LinkVideoIn(path, copied);
                        }
                        else
                        {
                            copied = AudioImport.CopyIn(dir, path, loaded, p => Report(ReadShare, 1, p));
                            if (video) copied = AudioImport.CopyVideoIn(dir, path, copied, p => Report(ReadShare, 1, p));
                        }
                        return (copied, media, loaded.Converted && !video);
                    });
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    Toast(Loc.T("imp.fail", name, e.Message.TrimEnd('.')));
                    continue;
                }
                // the project may have changed while the file was copied
                if (store != st || !CanImport()) { Toast(Loc.T(link && !wav ? "imp.switched.link" : "imp.switched", name)); return null; }
                if (link && wav) Toast(Loc.T("imp.link.wav", name));
                double at = atSec ?? engine.PositionSec;
                string? selected = targets != null ? (i < targets.Count ? targets[i] : null) : done == 0 ? trackId ?? sel?.Track.Id : null;
                await EditTimeline(p =>
                {
                    var track = AudioImport.AddClip(p, file, at, selected, fit: trackId != null || targets != null);
                    importedAudio[track.Clips[^1].MediaId] = audio;
                    last = track.Clips[^1].Id;
                    return track.Id;
                }, "audio-imported");
                done++;
            }
        }
        finally
        {
            toast.Close();
        }
        if (done > 0) Status(done == 1 ? Loc.T("imp.done.one") : Loc.T("imp.done.many", done));
        return last;
    }
}
