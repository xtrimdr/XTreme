using Avalonia.Threading;
using XtremeDaw.App.Audio;
using XtremeDaw.App.Dialogs;
using XtremeDaw.App.Model;
using XtremeDaw.Audio;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The native granular on MIDI tracks (asked by the user, 2026-11-03: an instrument of its own, next to the sampler):
/// each track with a <see cref="Track.Granular"/> has its <see cref="Granular"/> here, kept across the rebuilds of the
/// live tracks so nothing cuts off on an edit, and handed to the track by <see cref="SyncGranulars"/>. The file is read
/// and its sound built in the background once per sample; a knob only swaps the patch. It is a project edit like any
/// other, with its undo. <see cref="GranularWindow"/> is its face; loading a file goes through the sampler's
/// <see cref="LoadSample"/>.
/// </summary>
public partial class MainWindow
{
    sealed class LoadedGranular(Granular player)
    {
        public readonly Granular Player = player;
        /// <summary>The media read, or being read.</summary>
        public string? MediaId;
        public float[]? Left, Right;
        public byte[]? Peaks;
        public bool Loading, Missing;
        public SamplerSound? Sound;
        public CancellationTokenSource? Build;
        public GranularWindow? Window;
    }

    readonly Dictionary<string, LoadedGranular> granulars = [];

    /// <summary>Hands each track its granular and drops the ones no track wants. After every <see cref="ApplyTracks"/>, with the samplers.</summary>
    void SyncGranulars()
    {
        foreach (var id in granulars.Keys.ToList())
            if (tracks.FirstOrDefault(t => t.Track.Id == id)?.Track.Granular == null) DropGranular(id);
        foreach (var t in tracks)
        {
            if (t.Track.Granular == null)
            {
                t.Granular = null;
                continue;
            }
            if (!granulars.TryGetValue(t.Track.Id, out var loaded)) granulars[t.Track.Id] = loaded = new LoadedGranular(new Granular(PlaybackEngine.SampleRate));
            if (t.Granular != loaded.Player) t.Granular = loaded.Player;
            ApplyGranular(t, loaded);
        }
    }

    void DropGranular(string trackId)
    {
        if (!granulars.Remove(trackId, out var loaded)) return;
        loaded.Build?.Cancel();
        loaded.Window?.Close();
        InstrumentsChanged();
    }

    void DropAllGranulars()
    {
        foreach (var id in granulars.Keys.ToList()) DropGranular(id);
    }

    /// <summary>The granular catches up with the track's settings: the patch now, the sound when the sample changed.</summary>
    void ApplyGranular(LiveTrack t, LoadedGranular loaded)
    {
        if (store == null || t.Track.Granular is not { } g) return;
        var sample = GranularMedia.Of(store.Project, t.Track);
        string? media = sample?.Id;
        if (media != loaded.MediaId || (loaded.Missing && store.Resolve(sample?.Audio) is { } back && File.Exists(back))) ReadGranularSample(t, loaded, media);
        loaded.Player.Set(g.Patch(store.Project.Settings.Bpm, loaded.Sound));
        ShowGranularState(t, loaded);
    }

    void ReadGranularSample(LiveTrack t, LoadedGranular loaded, string? mediaId)
    {
        loaded.MediaId = mediaId;
        loaded.Left = loaded.Right = null;
        loaded.Peaks = null;
        loaded.Sound = null;
        loaded.Player.Sound = null;
        loaded.Build?.Cancel();
        loaded.Missing = false;
        loaded.Loading = false;
        InstrumentsChanged();
        if (store == null || mediaId == null) return;
        string id = t.Track.Id;
        Task<(float[] L, float[] R)> read;
        if (samplerImported.Remove(mediaId, out var ready)) read = Task.FromResult(ready);
        else if (store.Resolve(store.Project.FindMedia(mediaId)?.Audio) is not { } path || !File.Exists(path))
        {
            loaded.Missing = true;
            InstrumentsChanged();
            return;
        }
        else
            read = Task.Run(() =>
            {
                var audio = MediaDecoder.Load(path, PlaybackEngine.SampleRate, null, SamplerSound.MaxSec);
                return (audio.Left, audio.Right);
            });
        loaded.Loading = true;
        var build = loaded.Build = new CancellationTokenSource();
        _ = Finish();

        async Task Finish()
        {
            try
            {
                var (l, r) = await read;
                var (peaks, sound) = await Task.Run(() => (MediaDecoder.Peaks(l, r, SamplerPeakBins), GranularSettings.Sound(l, r, build.Token)), build.Token);
                if (loaded.MediaId != mediaId || build != loaded.Build || !granulars.ContainsValue(loaded)) return;
                (loaded.Left, loaded.Right, loaded.Peaks, loaded.Sound) = (l, r, peaks, sound);
                loaded.Player.Sound = sound;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or FormatException)
            {
                if (loaded.MediaId != mediaId) return;
                loaded.Missing = true;
                Toast(Loc.T("sampler.readFailed", e.Message.TrimEnd('.')));
            }
            finally
            {
                if (loaded.MediaId == mediaId) loaded.Loading = false;
                InstrumentsChanged();
            }
            if (tracks.FirstOrDefault(x => x.Track.Id == id) is { } live) ApplyGranular(live, loaded);
        }
    }

    /// <summary>One edit of a granular. Those of the same key in a row (a knob turning) are one undo step.</summary>
    void EditGranular(string trackId, Action<GranularSettings> change, string key)
    {
        if (restoring || store == null || store.ReadOnly) return;
        if (tracks.FirstOrDefault(t => t.Track.Id == trackId) is not { Track.Granular: { } g } live || !granulars.TryGetValue(trackId, out var loaded)) return;
        if (RefuseFrozen(trackId))
        {
            loaded.Window?.Refresh();
            return;
        }
        undo.Checkpoint(store.Project, $"granular:{trackId}:{key}");
        change(g);
        g.Normalized();
        ApplyGranular(live, loaded);
        MarkDirty();
        UpdateUndoButtons();
    }

    /// <summary>A MIDI track plays with the granular from now on: one undo step, and its window opens.</summary>
    async Task SetGranular(LiveTrack track)
    {
        if (store == null || store.ReadOnly || !track.Track.IsMidi) return;
        string id = track.Track.Id;
        await EditTimeline(p =>
        {
            if (p.Tracks.Find(t => t.Id == id) is not { } t) return null;
            t.Instrument = null;
            t.Sampler = null;
            t.Granular ??= new GranularSettings();
            return id;
        }, "instrument-set");
        OpenGranularWindow(id);
    }

    /// <summary>The Biblioteca's "Abrir en el granular": on the selected MIDI track if it takes it, else on a new one.</summary>
    async Task SampleToGranular(string path)
    {
        if (store == null) return;
        string? id = sel is { Track.IsMidi: true, Track.Instrument: null } t ? t.Track.Id : null;
        if (id == null)
        {
            await NewMidiTrack();
            id = sel is { Track.IsMidi: true } created ? created.Track.Id : null;
        }
        if (id != null) await LoadSample(id, path, true);
    }

    /// <summary>Opens the track's window, or brings it to the front.</summary>
    void OpenGranularWindow(string trackId)
    {
        if (!granulars.TryGetValue(trackId, out var loaded) || tracks.FirstOrDefault(t => t.Track.Id == trackId) is not { } live) return;
        if (loaded.Window is { } open)
        {
            open.Activate();
            return;
        }
        var window = new GranularWindow(loaded.Player)
        {
            Current = () => tracks.FirstOrDefault(t => t.Track.Id == trackId)?.Track.Granular,
            Edit = (change, key) => EditGranular(trackId, change, key),
            PickFile = () => _ = PickSample(trackId, true),
            LoadFile = path => _ = LoadSample(trackId, path, true),
            DroppedFile = e => DroppedFiles(e).FirstOrDefault(AudioImport.IsSupported),
            ClearSample = () => EditGranular(trackId, g => g.MediaId = null, "clear"),
            Shortcut = e => OnKey(this, e),
            Front = on => SamplerInFront(trackId, on),
            Automate = (control, knob, show) => AutomateOn(control, () => tracks.FirstOrDefault(t => t.Track.Id == trackId)?.Track, AutoTarget.Granular(knob), show),
        };
        window.IsEnabledForEdits = store is { ReadOnly: false };
        window.Closed += (_, _) =>
        {
            if (loaded.Window == window) loaded.Window = null;
            SamplerInFront(trackId, false);
        };
        ForwardKeyUps(window);
        loaded.Window = window;
        ShowGranularState(live, loaded);
        window.Show(this);
    }

    /// <summary>Its window shows what there is now.</summary>
    void ShowGranularState(LiveTrack t, LoadedGranular loaded)
    {
        if (loaded.Window is not { } window || store == null) return;
        var media = GranularMedia.Of(store.Project, t.Track);
        double seconds = loaded.Left is { Length: > 0 } l ? l.Length / (double)PlaybackEngine.SampleRate : 0;
        string? state = loaded.Missing ? Loc.T("sampler.missing") : loaded.Loading ? Loc.T("sampler.reading") : null;
        window.ShowTrack(t.Track.Name, t.Track.Color);
        window.ShowSample(media?.Name, loaded.Peaks, loaded.Left, loaded.Right, seconds, media?.SampleRate ?? 0, media?.Channels ?? 0, state);
    }
}
