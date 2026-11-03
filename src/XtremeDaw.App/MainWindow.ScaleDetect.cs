using Avalonia.Controls;
using Avalonia.Threading;
using XtremeDaw.App.Model;
using XtremeDaw.Audio;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// "Detectar escala y acordes" on an audio clip (asked by the user): <see cref="ScaleDetector"/> listens to what the clip
/// plays (transposed if its track is) on a worker thread, one clip at a time, with the clip showing how far it is like
/// "Detectar tempo"; unless the first reading is very sure it tries a second method (<see cref="ScaleDetector.DetectBest"/>),
/// which can take a while: the clip menu skips it, and it waits while a take is being recorded.
/// <see cref="Dialogs.ScaleDialog"/> sums it up and, if its check is on, the chords go to their own
/// strip on the timeline (<see cref="ProjectMarkers.SetChords"/>) in one undo step, replacing what was detected before
/// in that stretch; the strip shows itself if it was hidden, since they were just asked for.
/// </summary>
public partial class MainWindow
{
    /// <summary>The clip being listened to and how far it is (0 to 1, written by the worker); null when idle.</summary>
    sealed class ScaleJob(LiveClip clip)
    {
        public readonly LiveClip Clip = clip;
        public readonly CancellationTokenSource Skip = new();
        public volatile float Progress;
        /// <summary>Whether the separation has to wait for a take; only the UI thread knows.</summary>
        public volatile bool Hold;
    }
    ScaleJob? scaleJob;
    /// <summary>Redraws the clip's bar while it listens.</summary>
    DispatcherTimer? scaleClock;

    MenuItem DetectScaleItem(LiveClip clip, bool enabled)
    {
        if (scaleJob is { } job)
            return MenuAction(Loc.T("scale.skip"), () =>
            {
                job.Skip.Cancel();
                return Task.CompletedTask;
            }, icon: "\ue90b", tip: Loc.T("scale.skip.tip"));
        return MenuAction(Loc.T("scale.detect"), () => DetectScale(clip), enabled: enabled && clip.Audio != null && !RecordingBusy,
            icon: "\ue90b", tip: Loc.T(clip.Audio == null ? "tempo.detect.offline" : "scale.detect.tip"));
    }

    async Task DetectScale(LiveClip clip)
    {
        if (clip.Audio is not { } audio) return;
        if (clip.Sound is { L.Length: 0 }) { Toast(Loc.T("stretch.wait")); return; }
        var sound = clip.Sound ?? audio.Sound;
        var w = clip.Window;
        int sr = audio.SampleRate;
        int start = (int)Math.Max(0, (w.Start - w.Origin) * sr);
        int count = (int)Math.Max(0, (w.End - w.Start) * sr);
        string name = clip.Name;
        var st = store;
        var job = scaleJob = new ScaleJob(clip);
        Timeline.ScaleProgress ??= c => scaleJob is { } j && j.Clip == c ? j.Progress : null;
        if (scaleClock == null)
        {
            scaleClock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            scaleClock.Tick += (_, _) =>
            {
                if (scaleJob is { } j) j.Hold = RecordingBusy;
                Timeline.InvalidateVisual();
            };
        }
        scaleClock.Start();
        Timeline.InvalidateVisual();
        ScaleDetector.Result? r;
        try
        {
            // another clip being separated has the models: this one makes do without them
            var options = new ScaleDetector.Options(stemJob == null, () => job.Hold, job.Skip.Token);
            r = await Task.Run(() => ScaleDetector.DetectBest(sound.L, sound.R, sr, start, count, p => job.Progress = (float)p, options));
        }
        finally
        {
            scaleJob = null;
            scaleClock.Stop();
            job.Skip.Dispose();
            Timeline.InvalidateVisual();
        }
        // another project opened meanwhile: what it heard isn't about this one
        if (store == null || store != st || restoring) return;
        if (r == null || r.Tonal < ScaleDetector.MinTonal)
        {
            Toast(Loc.T(count < ScaleDetector.MinSec * sr ? "tempo.detect.short" : "scale.none", name, ScaleDetector.MinSec));
            return;
        }
        Status($"{name}: {Dialogs.ScaleDialog.Label(r.Scale)}");
        if (!await ScaleResult.Show(name, r, CanEditMarkers) || store != st) return;
        int added = 0;
        MarkerEdit(p => added = ProjectMarkers.SetChords(p, w.Start, w.End, r.Chords.Select(c => (w.Start + c.StartSec, w.Start + c.EndSec, c.Name))));
        chordStripButton.IsChecked = true;
        Toast(Loc.T("scale.done", added));
    }
}
