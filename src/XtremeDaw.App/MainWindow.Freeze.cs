using Avalonia.Controls;
using Avalonia.Threading;
using XtremeDaw.App.Audio;
using XtremeDaw.App.Dialogs;
using XtremeDaw.App.Model;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// Freezing tracks (#240, the user's call): "Congelar" renders the track's instrument and slots offline with the
/// export's chain (<see cref="Freezing.Render"/>, in the background with a progress toast), writes it to <c>freeze/</c>
/// and freezes the track in one undo step. The live track then plays the render (<see cref="LiveTrack.SetFreeze"/>) and
/// its plugins close (<see cref="SyncInstruments"/>, <see cref="SyncEffects"/>): that is the DSP it saves. Its fader,
/// pan, mute, solo and sends stay live; its takes, notes and slots are locked (<see cref="Track.EditLocked"/>) until
/// "Descongelar". The renders load in the background; while one loads, its track's plugins stay closed. A render that
/// is missing leaves the track playing live, with a toast.
/// </summary>
public partial class MainWindow
{
    /// <summary>Renders made in this session, by file: freezing doesn't read back what it just wrote.</summary>
    readonly Dictionary<string, MediaAudio> freshFreezes = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Renders being read, by file.</summary>
    readonly HashSet<string> freezeLoads = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Renders that weren't there or couldn't be read: their tracks play live (and the toast said so once).</summary>
    readonly HashSet<string> freezeMissing = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The track being frozen now: one at a time.</summary>
    string? freezing;

    /// <summary>Its plugins stay closed: it is frozen and its render plays or is on its way.</summary>
    bool FreezeHolds(Track t) => t.Freeze is { } f && !freezeMissing.Contains(f.File);

    /// <summary>
    /// The instrument windows (sampler, granular, GM) ask this before an edit: a frozen track says so with its Descongelar
    /// and the edit doesn't happen, so what plays always matches what the render was made from.
    /// </summary>
    bool RefuseFrozen(string trackId)
    {
        if (tracks.FirstOrDefault(t => t.Track.Id == trackId)?.Track is not { Frozen: true } track) return false;
        ToastLocked(track);
        return true;
    }

    /// <summary>
    /// Hands every live track its render, or none: from <see cref="freshFreezes"/> at once, else read in the background
    /// (the plugins are synced again when it arrives). After every <see cref="ApplyTracks"/> and on opening a project.
    /// </summary>
    void SyncFreezes()
    {
        if (store == null) return;
        var st = store;
        foreach (var t in tracks)
        {
            var want = t.Track.Freeze;
            if (want == null)
            {
                if (t.Freeze != null) t.SetFreeze(null);
                continue;
            }
            if (t.Freeze?.File == want.File && t.Freeze.AtSec == want.AtSec) continue;
            if (freshFreezes.TryGetValue(want.File, out var made))
            {
                t.SetFreeze(new FrozenRender(made, want.AtSec, want.File));
                continue;
            }
            if (freezeMissing.Contains(want.File))
            {
                if (t.Freeze != null) t.SetFreeze(null);
                continue;
            }
            if (!freezeLoads.Add(want.File)) continue;
            string file = want.File, name = t.Track.Name;
            var path = Freezing.PathOf(st.Dir, want);
            _ = Task.Run(() =>
            {
                try { return path == null ? null : new MediaAudio(path); }
                catch (Exception e) when (e is IOException or InvalidDataException or FormatException or UnauthorizedAccessException) { return null; }
            }).ContinueWith(task =>
            {
                freezeLoads.Remove(file);
                if (store != st) return;
                if (task.Result is { } audio) freshFreezes[file] = audio;
                else
                {
                    freezeMissing.Add(file);
                    Toast(Loc.T("freeze.missing", name));
                }
                SyncFreezes();
                SyncInstruments();
                SyncEffects();
                Timeline.InvalidateVisual();
                if (mixerView || MixerOut) RefreshMixer();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    /// <summary>A new project: what was loaded for the last one goes, and renders no track points at are deleted (<see cref="Freezing.Prune"/>).</summary>
    void ResetFreezes(ProjectStore st)
    {
        freshFreezes.Clear();
        freezeMissing.Clear();
        if (st.ReadOnly) return;
        var p = st.Project;
        string dir = st.Dir;
        // a copy of the list: the project belongs to the UI thread
        List<string> keep = [.. p.Tracks.Select(t => t.Freeze?.File).OfType<string>()];
        _ = Task.Run(() => Freezing.Prune(dir, keep));
    }

    /// <summary>"Congelar": renders the track and freezes it (one undo step); "Descongelar" when it already is.</summary>
    async Task FreezeTrack(string trackId)
    {
        if (store is not { ReadOnly: false } st || freezing != null) return;
        var p = st.Project;
        int k = p.Tracks.FindIndex(t => t.Id == trackId);
        if (k < 0) return;
        var track = p.Tracks[k];
        if (track.Frozen) return;
        if (!Freezing.CanFreeze(track))
        {
            Toast(Loc.T("freeze.empty", track.Name));
            return;
        }
        var (mix, _) = ExportDialog.BuildMix(st, tracks);
        double bpm = p.Settings.Bpm;
        string dir = st.Dir, name = track.Name;
        freezing = trackId;
        var toast = Toasts.ShowProgress(Loc.T("freeze.working", name));
        using var cts = new CancellationTokenSource();
        toast.Cancelable(Loc.T("common.cancel"), cts.Cancel);
        (TrackFreeze Freeze, MediaAudio Audio)? made;
        try
        {
            made = await Task.Run(() =>
            {
                if (Freezing.Render(mix, k, bpm, PlaybackEngine.SampleRate, toast.Report, cts.Token) is not var (l, r, at)) return ((TrackFreeze, MediaAudio)?)null;
                var f = Freezing.Write(dir, track, l, r, at, PlaybackEngine.SampleRate);
                return (f, new MediaAudio(Path.Combine(dir, f.File)));
            });
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            Toast(Loc.T("freeze.fail", name, e.Message.TrimEnd('.')));
            return;
        }
        finally
        {
            toast.Close();
            freezing = null;
        }
        if (made is not var (freeze, audio))
        {
            Toast(Loc.T("freeze.silent", name));
            return;
        }
        if (store != st) return;
        freshFreezes[freeze.File] = audio;
        await EditTimeline(q => { Freezing.Set(q, trackId, freeze); return trackId; }, "track-frozen");
    }

    /// <summary>"Descongelar": the track plays live again, editable, its plugins open again (one undo step).</summary>
    Task UnfreezeTrack(string trackId) =>
        EditTimeline(q => { Freezing.Set(q, trackId, null); return trackId; }, "track-unfrozen");

    /// <summary>The track menu's "Congelar" / "Descongelar", the same from the timeline and the mixer.</summary>
    MenuItem FreezeItem(LiveTrack track, bool enabled)
    {
        string id = track.Track.Id;
        bool frozen = track.Track.Frozen;
        return frozen
            ? MenuAction(Loc.T("freeze.off"), () => UnfreezeTrack(id), enabled: enabled, icon: "", tip: Loc.T("freeze.off.tip"))
            : MenuAction(Loc.T("freeze.on"), () => FreezeTrack(id), enabled: enabled && freezing == null && Freezing.CanFreeze(track.Track), icon: "",
                tip: Loc.T("freeze.on.tip"));
    }
}
