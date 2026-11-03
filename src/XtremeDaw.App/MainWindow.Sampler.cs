using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using XtremeDaw.App.Audio;
using XtremeDaw.App.Dialogs;
using XtremeDaw.App.Model;
using XtremeDaw.Audio;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The native sampler on MIDI tracks (asked by the user, 2026-11-02): each track with a <see cref="Track.Sampler"/> has
/// its <see cref="Sampler"/> here, kept across the rebuilds of the live tracks so nothing cuts off on an edit, and handed
/// to the track by <see cref="SyncSamplers"/>. The file is read in the background and the sound rebuilt only when
/// something that changes its samples does (reverse, the loop): a knob only swaps the patch. It is a project edit like
/// any other, with its undo, unlike a plugin's state. <see cref="SamplerWindow"/> is its face.
/// </summary>
public partial class MainWindow
{
    sealed class LoadedSampler(Sampler player)
    {
        public readonly Sampler Player = player;
        /// <summary>The media read, or being read.</summary>
        public string? MediaId;
        public float[]? Left, Right;
        public byte[]? Peaks;
        public bool Loading, Missing;
        public (bool, string, double, double, double) Key;
        public SamplerSound? Sound;
        public CancellationTokenSource? Build, Measure;
        public SamplerWindow? Window;
        /// <summary>The note found in the part in use, and what it was measured over.</summary>
        public (int Key, double Cents)? Heard;
        public bool Measuring;
        public (string?, double, double, bool) PitchKey;
    }

    /// <summary>What a sample's wave keeps of it.</summary>
    const int SamplerPeakBins = 3000;
    /// <summary>A flag being dragged rebuilds the sound once it pauses for this long.</summary>
    const int SamplerBuildDelayMs = 60;
    /// <summary>The start or end being dragged measures the pitch again once it pauses for this long.</summary>
    const int SamplerPitchDelayMs = 200;

    readonly Dictionary<string, LoadedSampler> samplers = [];
    /// <summary>Audio just imported as a sample, by media id, so it isn't decoded again.</summary>
    readonly Dictionary<string, (float[] L, float[] R)> samplerImported = [];
    /// <summary>The track whose sampler window is in front: the keyboards play it.</summary>
    string? samplerFront;

    /// <summary>The name the track's instrument goes by, wherever it shows.</summary>
    static string InstrumentName(Track t) => t.Sampler != null ? Loc.T("sampler.name") : t.Granular != null ? Loc.T("granular.name") : t.Instrument?.Name ?? Loc.T("vst.testTone");

    /// <summary>Hands each track its sampler (and its granular) and drops the ones no track wants. After every <see cref="ApplyTracks"/>.</summary>
    void SyncSamplers()
    {
        foreach (var id in samplers.Keys.ToList())
            if (tracks.FirstOrDefault(t => t.Track.Id == id)?.Track.Sampler == null) DropSampler(id);
        foreach (var t in tracks)
        {
            if (t.Track.Sampler == null)
            {
                t.Sampler = null;
                continue;
            }
            if (!samplers.TryGetValue(t.Track.Id, out var loaded)) samplers[t.Track.Id] = loaded = new LoadedSampler(new Sampler(PlaybackEngine.SampleRate));
            if (t.Sampler != loaded.Player) t.Sampler = loaded.Player;
            ApplySampler(t, loaded);
        }
        SyncGranulars();
    }

    void DropSampler(string trackId)
    {
        if (!samplers.Remove(trackId, out var loaded)) return;
        loaded.Build?.Cancel();
        loaded.Window?.Close();
        InstrumentsChanged();
    }

    void DropAllSamplers()
    {
        foreach (var id in samplers.Keys.ToList()) DropSampler(id);
        DropAllGranulars();
        samplerImported.Clear();
    }

    /// <summary>The sampler catches up with the track's settings: the patch now, the sound when it has to change.</summary>
    void ApplySampler(LiveTrack t, LoadedSampler loaded)
    {
        if (store == null || t.Track.Sampler is not { } s) return;
        var sample = SamplerMedia.Of(store.Project, t.Track);
        string? media = sample?.Id;
        // the same take, with its file found again
        if (media != loaded.MediaId || (loaded.Missing && store.Resolve(sample?.Audio) is { } back && File.Exists(back))) ReadSample(t, loaded, media);
        else if (loaded.Left != null && s.SoundKey != loaded.Key) BuildSound(t.Track.Id, loaded, s);
        if (loaded.Left != null && (media, s.Start, s.End, s.Reverse) != loaded.PitchKey) MeasurePitch(t.Track.Id, loaded, s, media);
        loaded.Player.Set(s.Patch(store.Project.Settings.Bpm, loaded.Sound));
        ShowSamplerState(t, loaded);
    }

    void ReadSample(LiveTrack t, LoadedSampler loaded, string? mediaId)
    {
        loaded.MediaId = mediaId;
        loaded.Left = loaded.Right = null;
        loaded.Peaks = null;
        loaded.Sound = null;
        loaded.Player.Sound = null;
        loaded.Build?.Cancel();
        loaded.Measure?.Cancel();
        loaded.Heard = null;
        loaded.Measuring = false;
        loaded.PitchKey = default;
        loaded.Missing = false;
        loaded.Loading = false;
        InstrumentsChanged();
        if (store == null || mediaId == null) return;
        string id = t.Track.Id;
        var settings = t.Track.Sampler!;
        if (samplerImported.Remove(mediaId, out var ready))
        {
            loaded.Loading = true;
            _ = Finish(Task.FromResult(ready));
            return;
        }
        if (store.Resolve(store.Project.FindMedia(mediaId)?.Audio) is not { } path || !File.Exists(path))
        {
            loaded.Missing = true;
            InstrumentsChanged();
            return;
        }
        loaded.Loading = true;
        _ = Finish(Task.Run(() =>
        {
            var audio = MediaDecoder.Load(path, PlaybackEngine.SampleRate, null, SamplerSound.MaxSec);
            return (audio.Left, audio.Right);
        }));

        async Task Finish(Task<(float[] L, float[] R)> read)
        {
            try
            {
                var (l, r) = await read;
                var peaks = await Task.Run(() => MediaDecoder.Peaks(l, r, SamplerPeakBins));
                if (loaded.MediaId != mediaId || !samplers.ContainsValue(loaded)) return;
                (loaded.Left, loaded.Right, loaded.Peaks) = (l, r, peaks);
                BuildSound(id, loaded, settings, 0);
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
                if (tracks.FirstOrDefault(x => x.Track.Id == id) is { } live) ShowSamplerState(live, loaded);
            }
        }
    }

    /// <summary>Builds the sound off the UI thread; a newer edit cancels this one, and new notes take it once it is there.</summary>
    void BuildSound(string trackId, LoadedSampler loaded, SamplerSettings settings, int delayMs = SamplerBuildDelayMs)
    {
        if (loaded.Left is not { } left || loaded.Right is not { } right) return;
        var key = settings.SoundKey;
        loaded.Key = key;
        loaded.Build?.Cancel();
        var build = loaded.Build = new CancellationTokenSource();
        var snapshot = ProjectLibrary.Clone(settings);
        _ = Task.Run(async () =>
        {
            if (delayMs > 0) await Task.Delay(delayMs, build.Token);
            var sound = snapshot.Sound(left, right, build.Token);
            Dispatcher.UIThread.Post(() =>
            {
                if (build != loaded.Build || build.IsCancellationRequested) return;
                loaded.Sound = sound;
                loaded.Player.Sound = sound;
                if (tracks.FirstOrDefault(t => t.Track.Id == trackId) is { } live) ApplySampler(live, loaded);
                InstrumentsChanged();
            });
        }, build.Token);
    }

    /// <summary>Measures the note of the part in use off the UI thread; moving it again cancels this one.</summary>
    void MeasurePitch(string trackId, LoadedSampler loaded, SamplerSettings s, string? media)
    {
        if (loaded.Left is not { } left || loaded.Right is not { } right) return;
        loaded.PitchKey = (media, s.Start, s.End, s.Reverse);
        loaded.Measure?.Cancel();
        var measure = loaded.Measure = new CancellationTokenSource();
        loaded.Measuring = true;
        // the fractions count on the sound as it plays, the audio here is the file's
        int from = (int)((s.Reverse ? 1 - s.End : s.Start) * left.Length), to = (int)((s.Reverse ? 1 - s.Start : s.End) * left.Length);
        _ = Task.Run(async () =>
        {
            await Task.Delay(SamplerPitchDelayMs, measure.Token);
            var heard = SamplerRoot.FromSound(left, right, PlaybackEngine.SampleRate, from, to);
            Dispatcher.UIThread.Post(() =>
            {
                if (measure != loaded.Measure || measure.IsCancellationRequested) return;
                loaded.Heard = heard;
                loaded.Measuring = false;
                if (tracks.FirstOrDefault(t => t.Track.Id == trackId) is { } live) ShowSamplerState(live, loaded);
            });
        }, measure.Token);
    }

    /// <summary>One edit of a sampler. Those of the same key in a row (a knob turning) are one undo step.</summary>
    void EditSampler(string trackId, Action<SamplerSettings> change, string key)
    {
        if (restoring || store == null || store.ReadOnly) return;
        if (tracks.FirstOrDefault(t => t.Track.Id == trackId) is not { Track.Sampler: { } s } live || !samplers.TryGetValue(trackId, out var loaded)) return;
        undo.Checkpoint(store.Project, $"sampler:{trackId}:{key}");
        change(s);
        s.Normalized();
        ApplySampler(live, loaded);
        MarkDirty();
        UpdateUndoButtons();
    }

    /// <summary>A MIDI track plays with the sampler from now on: one undo step, and its window opens.</summary>
    async Task SetSampler(LiveTrack track)
    {
        if (store == null || store.ReadOnly || !track.Track.IsMidi) return;
        string id = track.Track.Id;
        await EditTimeline(p =>
        {
            if (p.Tracks.Find(t => t.Id == id) is not { } t) return null;
            t.Instrument = null;
            t.Granular = null;
            t.Sampler ??= new SamplerSettings();
            return id;
        }, "instrument-set");
        OpenSamplerWindow(id);
    }

    /// <summary>
    /// A file becomes the track's sample, always linked where it is without asking (decided by the user, 2026-11-03:
    /// samples belong to the user's library; what only FFmpeg reads still comes in as a WAV), on a sampler (or with
    /// <paramref name="granular"/>, a granular) it gets if it doesn't have one. Long files keep their first
    /// <see cref="SamplerSound.MaxSec"/> seconds.
    /// </summary>
    async Task LoadSample(string trackId, string path, bool granular = false)
    {
        if (store is not { } st || !CanImport()) return;
        if (!AudioImport.IsSupported(path))
        {
            Toast(Loc.T("imp.fail", Path.GetFileName(path), Loc.T("sampler.notAudio")));
            return;
        }
        string name = Path.GetFileName(path), dir = st.Dir;
        var toast = Toasts.ShowProgress(Loc.T("imp.importing", name));
        AudioImport.Imported file;
        float[] left, right;
        bool cut;
        try
        {
            (file, left, right, cut) = await Task.Run(() =>
            {
                var audio = AudioImport.Read(path, PlaybackEngine.SampleRate, toast.Report);
                var kept = AudioImport.LinkIn(dir, path, audio);
                int most = (int)(SamplerSound.MaxSec * PlaybackEngine.SampleRate);
                bool longer = audio.Left.Length > most;
                return (kept, longer ? audio.Left[..most] : audio.Left, longer ? audio.Right[..most] : audio.Right, longer);
            });
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Toast(Loc.T("imp.fail", name, e.Message.TrimEnd('.')));
            return;
        }
        finally
        {
            toast.Close();
        }
        if (store != st || !CanImport()) return;
        if (cut) Toast(Loc.T("sampler.tooLong", (int)(SamplerSound.MaxSec / 60)));
        await EditTimeline(p =>
        {
            if (p.Tracks.Find(t => t.Id == trackId) is not { } t) return null;
            if (granular) GranularMedia.Set(p, t, file, left, right, PlaybackEngine.SampleRate);
            else SamplerMedia.Set(p, t, file, left, right, PlaybackEngine.SampleRate);
            samplerImported[(t.Sampler?.MediaId ?? t.Granular?.MediaId)!] = (left, right);
            return trackId;
        }, "sampler-sample");
        if (granular) OpenGranularWindow(trackId);
        else OpenSamplerWindow(trackId);
    }

    /// <summary>The Biblioteca's "Abrir en el sampler": on the selected MIDI track if it takes it, else on a new one.</summary>
    async Task SampleToSampler(string path)
    {
        if (store == null) return;
        string? id = sel is { Track.IsMidi: true, Track.Instrument: null } t ? t.Track.Id : null;
        if (id == null)
        {
            await NewMidiTrack();
            id = sel is { Track.IsMidi: true } created ? created.Track.Id : null;
        }
        if (id != null) await LoadSample(id, path);
    }

    async Task PickSample(string trackId, bool granular = false)
    {
        if (!CanImport()) return;
        Window? own = granular ? granulars.GetValueOrDefault(trackId)?.Window : samplers.GetValueOrDefault(trackId)?.Window;
        var top = own?.StorageProvider ?? StorageProvider;
        var files = await top.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Loc.T("sampler.load"), FileTypeFilter = [AudioFiles, FilePickerFileTypes.All] });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await LoadSample(trackId, path, granular);
    }

    /// <summary>Opens the track's window, or brings it to the front.</summary>
    void OpenSamplerWindow(string trackId)
    {
        if (!samplers.TryGetValue(trackId, out var loaded) || tracks.FirstOrDefault(t => t.Track.Id == trackId) is not { } live) return;
        if (loaded.Window is { } open)
        {
            open.Activate();
            return;
        }
        var window = new SamplerWindow(loaded.Player)
        {
            Current = () => tracks.FirstOrDefault(t => t.Track.Id == trackId)?.Track.Sampler,
            Edit = (change, key) => EditSampler(trackId, change, key),
            PickFile = () => _ = PickSample(trackId),
            LoadFile = path => _ = LoadSample(trackId, path),
            DroppedFile = e => DroppedFiles(e).FirstOrDefault(AudioImport.IsSupported),
            ClearSample = () => EditSampler(trackId, s => s.MediaId = null, "clear"),
            Shortcut = e => OnKey(this, e),
            Front = on => SamplerInFront(trackId, on),
            Automate = (control, knob, show) => AutomateOn(control, () => tracks.FirstOrDefault(t => t.Track.Id == trackId), AutoTarget.Sampler(knob), show),
        };
        window.IsEnabledForEdits = store is { ReadOnly: false };
        window.Closed += (_, _) =>
        {
            if (loaded.Window == window) loaded.Window = null;
            SamplerInFront(trackId, false);
        };
        loaded.Window = window;
        ShowSamplerState(live, loaded);
        window.Show(this);
    }

    /// <summary>While its window is in front, the keyboards play that track.</summary>
    void SamplerInFront(string trackId, bool front)
    {
        if (front) samplerFront = trackId;
        else if (samplerFront == trackId) samplerFront = null;
        UpdateKeyTarget();
    }

    /// <summary>Its window shows what there is now.</summary>
    void ShowSamplerState(LiveTrack t, LoadedSampler loaded)
    {
        if (loaded.Window is not { } window || store == null) return;
        var media = SamplerMedia.Of(store.Project, t.Track);
        double seconds = loaded.Left is { Length: > 0 } l ? l.Length / (double)PlaybackEngine.SampleRate : 0;
        var peaks = loaded.Peaks;
        if (peaks != null && t.Track.Sampler is { Reverse: true }) peaks = [.. peaks.Reverse()];
        string? state = loaded.Missing ? Loc.T("sampler.missing") : loaded.Loading ? Loc.T("sampler.reading") : null;
        window.ShowTrack(t.Track.Name, t.Track.Color);
        window.ShowSample(media?.Name, peaks, loaded.Left, loaded.Right, t.Track.Sampler is { Reverse: true }, seconds, media?.SampleRate ?? 0, media?.Channels ?? 0, state);
        window.ShowPitch(loaded.Heard, loaded.Measuring);
    }
}
