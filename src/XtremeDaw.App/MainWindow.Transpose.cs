using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Model;
using XtremeDaw.Audio;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The track's Transponer (native only, asked by the user): its takes play moved by <see cref="Track.Transpose"/>
/// semitones, the same length, so they stay in sync with the video. The tempo goes the same way: a clip plays its media
/// made <see cref="Clip.Stretch"/> times as long (<see cref="TempoFollow"/>). Rendering a take takes seconds
/// (<see cref="XtremeDaw.Audio.Dsp.PitchShifter"/>), so it runs in the background and is kept in the project's cache
/// (<see cref="TransposeCache"/>); meanwhile a clip of the same length keeps playing what it played and one that
/// changed length stays silent (asked by the user: never at another speed), and it comes in with a short crossfade on
/// the audio thread when the new sound is ready (<see cref="LiveClip.Sound"/>). A tempo change renders every take, so
/// they go one at a time, after a moment in which the tempo may move on. A value that is no longer wanted (the slider
/// moved on, the project closed) cancels its work. The export uses the same cache.
/// </summary>
public partial class MainWindow
{
    readonly record struct TransposeKey(string MediaId, double Semis, double Stretch);

    sealed class TransposeJob(string trackId)
    {
        public readonly CancellationTokenSource Cts = new();
        public readonly string TrackId = trackId;
        /// <summary>0 to 1, written by the worker.</summary>
        public volatile float Progress;
        /// <summary>Since it started: one that came from the cache in a blink needs no "ready" toast.</summary>
        public readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    }

    /// <summary>A transpose that took at least this long says so with a toast when it is ready.</summary>
    static readonly TimeSpan TransposeToastAfter = TimeSpan.FromSeconds(1);
    /// <summary>A stretch waits this long before it starts: the tempo's wheel and arrows go through many values.</summary>
    static readonly TimeSpan StretchSettle = TimeSpan.FromMilliseconds(300);
    /// <summary>One render at a time: each already uses every core, and a tempo change asks for all the takes at once.</summary>
    static readonly SemaphoreSlim renderTurn = new(1, 1);
    /// <summary>The card that shows how far a tempo change is; null when none is being made.</summary>
    ToastProgress? stretchToast;
    /// <summary>The cache of the open project was cleaned of unused transposes (once per project: the ones tried
    /// meanwhile stay, so going back to a value is immediate).</summary>
    bool transposePruned;

    readonly Dictionary<TransposeKey, ClipSound> transposed = [];
    readonly Dictionary<TransposeKey, TransposeJob> transposing = [];
    readonly HashSet<TransposeKey> transposeFailed = [];
    ProjectStore? transposeStore;
    /// <summary>Refreshes the transposing marks (timeline, mixer) while something is being moved.</summary>
    readonly DispatcherTimer transposeClock = new() { Interval = TimeSpan.FromMilliseconds(250) };

    void InitTranspose()
    {
        transposeClock.Tick += (_, _) => RefreshTransposeNote();
        inspectorRefresh.Add(RefreshTransposeNote);
        Timeline.TransposeProgress = TransposeProgress;
        Timeline.ClipRenderProgress = ClipRenderProgress;
    }

    /// <summary>The Transponer of the selected track, or of all the picked ones, from the inspector.</summary>
    void SetTranspose(double semis)
    {
        if (sel == null) return;
        if (PickedGroup() == null) { SetTranspose(sel, semis); return; }
        semis = Math.Clamp(Math.Round(semis), -PitchShifter.MaxSemitones, PitchShifter.MaxSemitones);
        // a frozen track's pitch is in its render (#240)
        var list = PickTargets().Where(t => !t.Track.Frozen).ToList();
        if (list.All(t => t.Track.Transpose == semis)) return;
        EditTracks(list, t => t.Track.Transpose = semis, "transpose");
        RefreshTranspose();
        RefreshMixer();
    }

    /// <summary>Any track's Transponer (inspector or mixer strip), clamped to what <see cref="PitchShifter"/> does.</summary>
    void SetTranspose(LiveTrack track, double semis)
    {
        semis = Math.Clamp(Math.Round(semis), -PitchShifter.MaxSemitones, PitchShifter.MaxSemitones);
        if (semis == track.Track.Transpose) return;
        if (track.Track.Frozen)
        {
            ToastLocked(track.Track);
            return;
        }
        EditTrack(track, t => t.Track.Transpose = semis, "transpose");
        RefreshTranspose();
        RefreshMixer();
    }

    /// <summary>How far the track's transpose is (0 to 1), or null when it already plays what it should.</summary>
    float? TransposeProgress(LiveTrack track)
    {
        float sum = 0;
        int n = 0;
        foreach (var job in transposing.Values)
            if (job.TrackId == track.Track.Id) { sum += job.Progress; n++; }
        return n == 0 ? null : sum / n;
    }

    /// <summary>How far the sound this clip is waiting for is (0 to 1), or null when it plays what it should: a clip stretched by hand leaves the others alone. Called on every frame.</summary>
    float? ClipRenderProgress(LiveTrack track, LiveClip clip)
    {
        if (transposing.Count == 0 || clip.Media is not { } media) return null;
        return transposing.TryGetValue(new TransposeKey(media.Id, clip.Clip.SemitonesOn(track.Track), TransposeCache.Step(clip.Clip.Stretch)), out var job) ? job.Progress : null;
    }

    /// <summary>The short form the channel shows (timeline header, mixer strip): "+2", "-5".</summary>
    static string TransposeTag(double v) => $"{v:+0;-0}";

    /// <summary>The tooltip of the channel's tag: how far it is moved, and how far the transpose is while it is made.</summary>
    string TransposeTagTip(LiveTrack t) => TransposeProgress(t) is { } p
        ? Loc.T("insp.transpose.working", $"{p * 100:0}") : Loc.T("mix.transpose.tag.tip", TransposeText(t.Track.Transpose));

    /// <summary>The compact strip's mark of a transposed track (the transpose icon and "+2" in the accent, under its
    /// name), hidden at 0. The icon says what the number is (asked by the user, #165: the mixer shows no tooltips).</summary>
    Control TransposeTagText(LiveTrack t)
    {
        var text = new TextBlock { Classes = { "mono" }, FontSize = 10, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var icon = FxIcon.Create(FxIcon.Transpose, 8);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var tag = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { icon, text } };
        void Show()
        {
            double v = t.Track.Transpose;
            tag.IsVisible = v != 0;
            text.Text = TransposeTag(v);
            var brush = TransposeProgress(t) != null ? Palette.Faint : Palette.Accent;
            text.Foreground = brush;
            FxIcon.SetColor(icon, brush);
            ToolTip.SetTip(tag, TransposeTagTip(t));
        }
        mx.Refresh.Add(Show);
        mx.Transpose.Add(Show);
        return tag;
    }

    static string TransposeText(double v) =>
        v == 0 ? Loc.T("insp.transpose.none") : Loc.T(Math.Abs(v) == 1 ? "insp.transpose.one" : "insp.transpose.value", $"{v:+0.##;-0.##}");

    /// <summary>
    /// Points every clip at the sound its track wants: the recording at 0, a transpose already in memory, or keeps
    /// what it plays while the missing one is made. Call it after anything that may change tracks, clips or Transponer.
    /// </summary>
    void RefreshTranspose()
    {
        var st = store;
        if (st != transposeStore)
        {
            foreach (var job in transposing.Values) job.Cts.Cancel();
            transposing.Clear();
            transposed.Clear();
            transposeFailed.Clear();
            transposeStore = st;
            transposePruned = false;
        }
        if (st == null) return;

        var wanted = new HashSet<TransposeKey>();
        foreach (var lt in tracks)
        {
            // a MIDI track's notes just play higher or lower: nothing to render
            if (lt.Track.IsMidi) lt.SetMidi(st.Project.Settings.TempoMap);
            foreach (var lc in lt.Clips)
            {
                if (lc.Audio is not { } audio || lc.Media is not { } media) continue;
                double semis = lc.Clip.SemitonesOn(lt.Track);
                double stretch = TransposeCache.Step(lc.Clip.Stretch);
                if (semis == 0 && stretch == 1)
                {
                    lc.Play(audio.Sound, 0);
                    continue;
                }
                var key = new TransposeKey(media.Id, semis, stretch);
                wanted.Add(key);
                if (transposed.TryGetValue(key, out var sound))
                {
                    lc.Play(sound, semis);
                    continue;
                }
                lc.TurnSound();
                if (lc.Sound?.L.Length != lc.SoundLength) lc.Sound = ClipSound.Silent;
                if (!transposing.ContainsKey(key) && !transposeFailed.Contains(key) && st.Resolve(media.Audio) is { } path)
                    StartTranspose(st, key, path, audio, lt.Track.Id, string.IsNullOrWhiteSpace(lt.Track.Name) ? media.Name : lt.Track.Name);
            }
        }
        foreach (var (key, job) in transposing.Where(x => !wanted.Contains(x.Key)).ToList())
        {
            job.Cts.Cancel();
            transposing.Remove(key);
        }
        foreach (var key in transposed.Keys.Where(k => !wanted.Contains(k)).ToList()) transposed.Remove(key);
        if (transposing.Count == 0 && !transposePruned)
        {
            transposePruned = true;
            PruneTransposeCache(st);
        }
        transposeClock.IsEnabled = transposing.Count > 0;
        RefreshTransposeNote();
    }

    /// <summary>The card of a tempo change: one bar for all its takes, gone when the last one is ready.</summary>
    void RefreshStretchToast()
    {
        var jobs = transposing.Where(j => j.Key.Stretch != 1).Select(j => j.Value).ToList();
        if (jobs.Count == 0)
        {
            stretchToast?.Close();
            stretchToast = null;
            return;
        }
        stretchToast ??= Toasts.ShowProgress(Loc.T("stretch.working"));
        stretchToast.Report(jobs.Average(j => j.Progress));
    }

    void StartTranspose(ProjectStore st, TransposeKey key, string path, MediaAudio audio, string trackId, string name)
    {
        var job = new TransposeJob(trackId);
        transposing[key] = job;
        var ct = job.Cts.Token;
        Task.Run(() =>
            {
                if (key.Stretch != 1 && ct.WaitHandle.WaitOne(StretchSettle)) ct.ThrowIfCancellationRequested();
                renderTurn.Wait(ct);
                try { return TransposeCache.Get(st.CachePath, path, audio.L, audio.R, key.Semis, key.Stretch, p => job.Progress = (float)p, ct); }
                finally { renderTurn.Release(); }
            }, ct)
            .ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                if (!transposing.TryGetValue(key, out var current) || current != job) return; // no longer wanted
                transposing.Remove(key);
                if (t.IsCompletedSuccessfully)
                {
                    transposed[key] = new ClipSound(t.Result.Left, t.Result.Right);
                    // the last of the track's takes is ready: say so, unless it came straight from the cache
                    if (key.Stretch == 1 && job.Clock.Elapsed >= TransposeToastAfter && !transposing.Values.Any(j => j.TrackId == trackId)
                        && tracks.FirstOrDefault(x => x.Track.Id == trackId) is { } lt && lt.Track.Transpose == key.Semis)
                        Toast(Loc.T("transpose.done", name, TransposeText(key.Semis)));
                }
                else if (!t.IsCanceled)
                {
                    transposeFailed.Add(key);
                    Toast(Loc.T(key.Stretch != 1 ? "stretch.error" : "transpose.error", name, t.Exception?.GetBaseException().Message ?? ""));
                }
                RefreshTranspose();
            }), TaskScheduler.Default);
    }

    /// <summary>How far the transposes are, on the channel's tags (timeline header and clips, mixer strip). The inspector
    /// shows nothing of it (asked by the user: a note there moved everything under it).</summary>
    void RefreshTransposeNote()
    {
        RefreshStretchToast();
        if (mx != null && StripsLive) foreach (var r in mx.Transpose) r();
        Timeline.InvalidateVisual();
    }

    /// <summary>Deletes the cached transposes no track uses any more (in the background; a read-only window leaves them).</summary>
    void PruneTransposeCache(ProjectStore st)
    {
        if (st.ReadOnly) return;
        var used = new List<(string Path, int Length, double Semis, double Stretch)>();
        foreach (var lt in tracks)
            foreach (var lc in lt.Clips)
                if ((lc.Clip.SemitonesOn(lt.Track) != 0 || lc.Clip.Stretch != 1) && lc.Audio != null && st.Resolve(lc.Media?.Audio) is { } path)
                    used.Add((path, lc.Audio.L.Length, lc.Clip.SemitonesOn(lt.Track), lc.Clip.Stretch));
        _ = Task.Run(() => TransposeCache.Prune(st.CachePath, used.Select(u => TransposeCache.PathFor(st.CachePath, u.Path, u.Length, u.Semis, u.Stretch)).ToList()));
    }
}
