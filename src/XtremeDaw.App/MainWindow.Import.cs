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
/// track under it when that stretch is free. The decoded audio goes straight to the clip, so the file is read once.
/// </summary>
public partial class MainWindow
{
    static readonly FilePickerFileType AudioFiles = new("Audio")
    {
        Patterns = [.. MediaDecoder.AudioExtensions.Concat(MediaDecoder.VideoExtensions).Select(e => "*" + e)],
    };

    /// <summary>A sample carried from the Biblioteca: its path.</summary>
    static readonly DataFormat<string> SampleDrag = DataFormat.CreateStringApplicationFormat("xtreme-sample");
    /// <summary>A take of the project carried from the Biblioteca: its media id. It is already in media/, so nothing is copied.</summary>
    static readonly DataFormat<string> TakeDrag = DataFormat.CreateStringApplicationFormat("xtreme-take");

    /// <summary>What part of a file's bar is decoding it; the rest is copying it into media/.</summary>
    const double ReadShare = 0.8;

    /// <summary>Audio decoded by an import, by media id, for <see cref="ApplyTracks"/> to take instead of reading the file again.</summary>
    readonly Dictionary<string, MediaAudio> importedAudio = [];

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
            if (can) Timeline.ShowDrop(e.GetPosition(Timeline), e.KeyModifiers);
            else Timeline.HideDrop();
        });
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => Timeline.HideDrop());
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
            Timeline.HideDrop();
            if (e.DataTransfer.TryGetValue(TakeDrag) is { } take)
            {
                if (place is { } spot && CanImport()) await EditTimeline(p => ClipEditing.PlaceMedia(p, take, spot.Sec, spot.TrackId), null);
                return;
            }
            if (files.Count == 0) return;
            if (place?.TrackId is { } onto && tracks.FirstOrDefault(t => t.Track.Id == onto) is { Track.IsMidi: true, Track.Instrument: null } && CanImport())
            {
                await LoadSample(onto, files[0]);
                return;
            }
            if (place is { } at) await ImportAudio(files, at.Sec, at.TrackId, BringsPicture(files[0]));
            else if (!e.DataTransfer.Contains(SampleDrag)) await ImportAudio(files, picture: BringsPicture(files[0]));
        });
    }

    static bool FromLibrary(DragEventArgs e) => e.DataTransfer.Contains(SampleDrag) || e.DataTransfer.Contains(TakeDrag);

    static IEnumerable<string> DroppedFiles(DragEventArgs e) =>
        e.DataTransfer.TryGetValue(SampleDrag) is { } sample ? [sample]
            : (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>().Where(f => File.Exists(f));

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
    async Task ImportAudio(IReadOnlyList<string> paths, double? atSec = null, string? trackId = null, bool picture = false)
    {
        if (!CanImport()) { Status(Loc.T("imp.busy")); return; }
        if (await LinkOrCopy(paths) is not { } link) return;
        if (!CanImport()) { Status(Loc.T("imp.busy")); return; } // the question stayed open while something else started
        var st = store!;
        string dir = st.Dir;
        int done = 0;
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
                if (store != st || !CanImport()) { Toast(Loc.T(link && !wav ? "imp.switched.link" : "imp.switched", name)); return; }
                if (link && wav) Toast(Loc.T("imp.link.wav", name));
                double at = atSec ?? engine.PositionSec;
                string? selected = done == 0 ? trackId ?? sel?.Track.Id : null;
                await EditTimeline(p =>
                {
                    var track = AudioImport.AddClip(p, file, at, selected, fit: trackId != null);
                    importedAudio[track.Clips[^1].MediaId] = audio;
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
    }
}
