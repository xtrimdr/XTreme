using Avalonia.Controls;
using Avalonia.Threading;
using XtremeDaw.App.Model;
using XtremeDaw.Audio;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// "Separar en stems" (asked by the user): what a clip plays is pulled apart into voice, drums and so on, chosen in
/// <see cref="Dialogs.StemsDialog"/>. <see cref="StemSeparator"/> works on a worker thread, one clip at a time, and the
/// clip shows how far it is. It can take minutes, so a toast stays up with "Cancelar" (the clip menu has it too, in case
/// that toast was closed), and it waits while a take is being recorded (nothing may get in the way of a take). Each stem is a new WAV in media/ on its own track, inside a new folder under
/// the clip's track, which gets muted; the original stays as it was. Avanzado (#146) can instead leave the track
/// sounding or take the clip out, and separate only the timeline's selection over the clip. It is one undo step.
/// </summary>
public partial class MainWindow
{
    /// <summary>The clip being separated; null when idle.</summary>
    sealed class StemJob(LiveClip clip)
    {
        public readonly LiveClip Clip = clip;
        public readonly CancellationTokenSource Cancel = new();
        public volatile float Progress;
        /// <summary>Whether the worker has to wait; only the UI thread knows.</summary>
        public volatile bool Hold;
    }
    StemJob? stemJob;
    /// <summary>Redraws the clip's bar while it works, and tells the job when a take starts.</summary>
    DispatcherTimer? stemClock;

    /// <summary>Fluent's Equalizer: separate bars, in the clip menu.</summary>
    const string StemsGlyph = "\ue9e9";

    MenuItem StemsItem(LiveTrack track, LiveClip clip, bool enabled)
    {
        if (stemJob is { } job)
            return MenuAction(Loc.T("stems.cancel"), () =>
            {
                CancelStems(job);
                return Task.CompletedTask;
            }, icon: StemsGlyph, tip: Loc.T("stems.cancel.tip"));
        if (!StemSeparator.AnyAvailable)
            return MenuAction(Loc.T("stems.install"), () =>
            {
                ComponentsHelp.Show();
                return Task.CompletedTask;
            }, icon: StemsGlyph, tip: Loc.T("stems.noModel"));
        bool can = enabled && clip.Audio != null && !RecordingBusy;
        return MenuAction(Loc.T("stems.menu"), () => SeparateClip(track, clip), enabled: can, icon: StemsGlyph,
            tip: Loc.T(clip.Audio == null ? "toMidi.offline" : "stems.tip"));
    }

    /// <summary>The job disposes of its token when it ends, and a button may still be up.</summary>
    void CancelStems(StemJob job)
    {
        if (stemJob == job) job.Cancel.Cancel();
    }

    async Task SeparateClip(LiveTrack from, LiveClip clip)
    {
        if (clip.Audio is not { } audio || store == null || stemJob != null) return;
        if (clip.Sound is { L.Length: 0 }) { Toast(Loc.T("stretch.wait")); return; }
        var w = clip.Window;
        string name = clip.Name, trackId = from.Track.Id, clipId = clip.Clip.Id;
        // the timeline's selection, where it covers this clip on its track (#146: Avanzado can separate just that)
        (double Start, double End)? range = Timeline.Range is { } sel && sel.TrackIds.Contains(trackId)
            && Math.Min(sel.End, w.End) - Math.Max(sel.Start, w.Start) > 0.05
            ? (Math.Max(sel.Start, w.Start), Math.Min(sel.End, w.End)) : null;
        var choice = await Stems.Ask(name, w.End - w.Start, range is { } over ? over.End - over.Start : null);
        if (choice == null || store == null || stemJob != null || clip.Audio == null) return;
        var settings = choice.Settings;
        var (startSec, endSec) = choice.OnlyRange && range is { } part ? part : (w.Start, w.End);
        var sound = clip.Sound ?? audio.Sound;
        int sr = audio.SampleRate;
        int start = (int)Math.Max(0, (startSec - w.Origin) * sr);
        int count = (int)Math.Max(0, (endSec - startSec) * sr);
        var st = store;
        string dir = st.Dir;
        var job = stemJob = new StemJob(clip);
        Timeline.StemsProgress ??= c => stemJob is { } j && j.Clip == c ? j.Progress : null;
        if (stemClock == null)
        {
            stemClock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            stemClock.Tick += (_, _) =>
            {
                if (stemJob is { } j) j.Hold = RecordingBusy;
                Timeline.InvalidateVisual();
            };
        }
        stemClock.Start();
        Timeline.InvalidateVisual();
        var closeToast = Toasts.Show(Loc.T("stems.working", name), sticky: true, action: (Loc.T("common.cancel"), () => CancelStems(job)));
        List<StemSeparator.Stem> stems;
        try
        {
            var ct = job.Cancel.Token;
            stems = await Task.Run(() => StemSeparator.Separate(sound.L, sound.R, sr, start, count, settings,
                p => job.Progress = (float)p, () => job.Hold, ct), ct);
        }
        catch (OperationCanceledException)
        {
            Status(Loc.T("stems.canceled", name));
            return;
        }
        catch (Exception e)
        {
            Toast(Loc.T("stems.failed", name, e.Message));
            return;
        }
        finally
        {
            stemJob = null;
            closeToast();
            stemClock.Stop();
            job.Cancel.Dispose();
            Timeline.InvalidateVisual();
        }
        // another project opened meanwhile: these stems aren't about this one
        if (store == null || store != st || store.ReadOnly || restoring) return;
        List<AudioImport.Imported> files;
        try
        {
            var labels = stems.Select(s => Loc.T("stems." + s.Id)).ToList();
            files = await Task.Run(() => stems.Select((s, i) =>
                AudioImport.WriteIn(dir, $"{name} - {labels[i]}", s.L, s.R, sr) with { Name = labels[i] }).ToList());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Toast(Loc.T("stems.failed", name, e.Message.TrimEnd('.')));
            return;
        }
        if (store != st) return;
        string folder = Loc.T("stems.folder", name);
        await EditTimeline(p =>
        {
            AudioImport.AddStems(p, trackId, clipId, startSec, files, folder, choice.Original);
            return trackId;
        }, "stems-separated");
        Toast(Loc.T(choice.Original switch
        {
            StemOriginal.Keep => "stems.done.keep",
            StemOriginal.Remove => "stems.done.remove",
            _ => "stems.done",
        }, files.Count, name));
    }
}
