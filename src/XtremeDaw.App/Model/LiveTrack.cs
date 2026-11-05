using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using XtremeDaw.App.Audio;
using XtremeDaw.App.Video;
using XtremeDaw.Audio;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Audio.Midi;
using XtremeDaw.Audio.Vst;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Model;

/// <summary>
/// The samples a clip plays: its media's audio as recorded, or moved by its track's Transponer
/// and stretched by the tempo (<see cref="XtremeDaw.Audio.Dsp.PitchShifter"/>). Never changed once built, so the audio thread can hold it.
/// </summary>
public sealed class ClipSound(float[] left, float[] right)
{
    public readonly float[] L = left, R = right;
    /// <summary>What a clip plays while its sound at this tempo is made (asked by the user: never another speed).</summary>
    public static readonly ClipSound Silent = new([], []);

    ClipSound? flipped;
    /// <summary>
    /// The same samples backward, for a reversed clip: a sound of its own, so the audio thread crossfades to it like
    /// to any other and reads it the same way. Made the first time one asks, shared by the clips that play it.
    /// </summary>
    public ClipSound Flipped
    {
        get
        {
            if (flipped != null || L.Length == 0) return flipped ?? this;
            var (l, r) = ((float[])L.Clone(), (float[])R.Clone());
            Array.Reverse(l);
            Array.Reverse(r);
            return flipped = new ClipSound(l, r) { flipped = this };
        }
    }
}

/// <summary>
/// A media file's audio in memory (resampled to the engine's sample rate via <see cref="AudioLoader"/>),
/// with its waveform peaks, measured level and compressor envelope. Shared by its clips.
/// </summary>
public sealed class MediaAudio
{
    public const int PeakBin = 64;

    public float[] L { get; }
    public float[] R { get; }
    /// <summary>The audio as recorded, as a clip plays it.</summary>
    public ClipSound Sound { get; }
    public int SampleRate { get; }
    public int SourceSampleRate { get; }
    public float[] PeakMin { get; }
    public float[] PeakMax { get; }
    /// <summary>Each side's peaks, for a track that shows its stereo clips as two waves; null when <see cref="Mono"/>.</summary>
    public float[]? PeakMinL { get; }
    public float[]? PeakMaxL { get; }
    public float[]? PeakMinR { get; }
    public float[]? PeakMaxR { get; }
    public double RmsDb { get; }
    readonly Lazy<CompPresets.TakeEnvelope> envelope;
    /// <summary>For the compressor's analyze button: read on first use only, from any thread.</summary>
    public CompPresets.TakeEnvelope Envelope => envelope.Value;
    /// <summary>Both sides are the same samples: a one-channel file or a take from a mono input (the mixer shows one bar).</summary>
    public bool Mono { get; }
    public double LengthSec => L.Length / (double)SampleRate;

    public MediaAudio(string path, int targetSampleRate = PlaybackEngine.SampleRate) : this(AudioLoader.Load(path, targetSampleRate)) { }

    /// <summary>From audio already in memory (an import, which had to decode it to check it).</summary>
    public MediaAudio(AudioLoader.LoadedAudio loaded)
    {
        L = loaded.Left;
        R = loaded.Right;
        Sound = new ClipSound(L, R);
        SampleRate = loaded.SampleRate;
        SourceSampleRate = loaded.SourceSampleRate;

        int bins = (L.Length + PeakBin - 1) / PeakBin;
        PeakMin = new float[bins];
        PeakMax = new float[bins];
        for (int b = 0; b < bins; b++)
        {
            float lo = 0, hi = 0;
            for (int i = b * PeakBin, end = Math.Min(L.Length, i + PeakBin); i < end; i++)
            {
                float v = 0.5f * (L[i] + R[i]);
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            PeakMin[b] = lo;
            PeakMax[b] = hi;
        }
        Mono = L.AsSpan().SequenceEqual(R);
        if (!Mono)
        {
            (PeakMinL, PeakMaxL, PeakMinR, PeakMaxR) = (new float[bins], new float[bins], new float[bins], new float[bins]);
            for (int b = 0; b < bins; b++)
            {
                float lo = 0, hi = 0, rlo = 0, rhi = 0;
                for (int i = b * PeakBin, end = Math.Min(L.Length, i + PeakBin); i < end; i++)
                {
                    float l = L[i], r = R[i];
                    if (l < lo) lo = l;
                    if (l > hi) hi = l;
                    if (r < rlo) rlo = r;
                    if (r > rhi) rhi = r;
                }
                (PeakMinL[b], PeakMaxL[b], PeakMinR[b], PeakMaxR[b]) = (lo, hi, rlo, rhi);
            }
        }
        RmsDb = LevelAnalysis.Analyze([L, R], SampleRate).RmsDb;
        envelope = new(() => CompPresets.Analyze([L, R], SampleRate));
    }
}

/// <summary>
/// A clip loaded for playback: its media's audio (null = offline) and its own video decoder. A timeline edit
/// or an undo points <see cref="Clip"/> at the project's new object instead of building another one, so the
/// audio and the decoder carry on; the audio thread reads the reference once per block.
/// </summary>
public sealed class LiveClip
{
    Clip clip;
    /// <summary>Re-pointing it (a timeline edit, an undo) also takes the clip's volume curve.</summary>
    public Clip Clip
    {
        get => clip;
        set { clip = value; RefreshCurve(); }
    }
    /// <summary>The clip's volume curve as the audio thread reads it: swapped whole, never changed in place.</summary>
    public ClipGainCurve.Point[] Curve { get; private set; } = [];

    /// <summary>Takes the curve again after an edit that changed it on the same clip (the inspector edits in place).</summary>
    public void RefreshCurve() => Curve = clip.CurvePoints();
    public Media? Media { get; set; }
    public MediaAudio? Audio { get; }
    public bool Offline => Audio == null;

    volatile ClipSound? sound;
    /// <summary>
    /// What it plays (its <see cref="Audio"/> as recorded, or transposed and stretched; <see cref="ClipSound.Silent"/>
    /// while the one its length needs is made): the UI swaps it whole and the audio thread crossfades to it over
    /// <see cref="LiveTrack.SoundFade"/> samples. The waveform and the level stay the recording's.
    /// </summary>
    public ClipSound? Sound { get => sound; set => sound = value; }
    /// <summary>The semitones <see cref="Sound"/> is moved by (UI thread).</summary>
    public double SoundSemis { get; private set; }
    /// <summary>Whether <see cref="Sound"/> runs backward (UI thread).</summary>
    public bool SoundReversed { get; private set; }
    /// <summary>True while the sound it plays is the one for <paramref name="semis"/> at the clip's stretch and direction (UI thread).</summary>
    public bool Plays(double semis) => SoundSemis == semis && SoundReversed == clip.Reversed && sound?.L.Length == SoundLength;

    /// <summary>Plays <paramref name="forward"/>, moved by <paramref name="semis"/>, the way the clip runs (UI thread).</summary>
    public void Play(ClipSound forward, double semis)
    {
        Sound = clip.Reversed ? forward.Flipped : forward;
        SoundSemis = semis;
        SoundReversed = clip.Reversed;
    }

    /// <summary>Keeps what it plays, the way the clip runs: for while the sound it wants is being made (UI thread).</summary>
    public void TurnSound()
    {
        if (SoundReversed == clip.Reversed) return;
        Sound = sound?.Flipped;
        SoundReversed = clip.Reversed;
    }
    /// <summary>The samples its sound has at the clip's stretch, or 0 offline.</summary>
    public int SoundLength => Audio == null ? 0 : PitchShifter.StretchedLength(Audio.L.Length, TransposeCache.Step(clip.Stretch));
    // audio thread only: the sound it last played and the one it is fading out of
    internal ClipSound? Heard, Fading;
    internal int FadeLeft;

    public VideoDecoder? Video { get; private set; }
    /// <summary>Where <see cref="Audio"/> came from, and <see cref="VideoPath"/> the same for the picture: once its media points elsewhere the clip starts over.</summary>
    public string? AudioPath { get; set; }
    /// <summary>The take's video file (it may be missing), for the lane's thumbnails; null for audio-only media.</summary>
    public string? VideoPath { get; init; }
    public WriteableBitmap? Frame { get; private set; }
    public bool InClip { get; private set; }
    /// <summary>
    /// True once <see cref="Frame"/> holds a picture decoded since the playhead entered the clip: before that it may
    /// still be one from the last time it was shown (another part of the take), so the preview leaves the panel empty.
    /// </summary>
    public bool Fresh { get; private set; }
    /// <summary>How far ahead of a clip's start its decoder is started (FFmpeg takes ~100–300 ms to give the first frame).</summary>
    const double PreRollSec = 1.0;
    /// <summary>
    /// How far its picture shows before its start and after its end: a crossfade's half on each side of the join
    /// (<see cref="ClipTransitions"/>), set every tick by <see cref="LiveTrack.UpdateTransitions"/>. Zero otherwise.
    /// </summary>
    public double Lead { get; internal set; }
    public double Tail { get; internal set; }
    volatile ClipAudioJoins? audioJoins;
    /// <summary>
    /// What the transitions do to its sound (<see cref="ClipTransitions"/>), null = nothing: swapped whole by
    /// <see cref="LiveTrack.UpdateTransitions"/> when they change, read by the audio thread once per block.
    /// </summary>
    public ClipAudioJoins? AudioJoins { get => audioJoins; internal set => audioJoins = value; }
    public ProjectSettings? Settings { get; set; }
    long frameVersion;
    byte[]? rawFrame;
    byte[]? gradedFrame;
    ExportColorSpec? lastColorSpec;
    int lastGrainLevels = -1;
    int lastFrameSeed = int.MinValue;
    VideoDetail.Processor? detail;
    (int, int, int, VideoDetail.Amounts) lastDetailKey;

    /// <summary>
    /// How many rows of <see cref="Frame"/> its panel shows (cover fit and zoom), set by the preview when it draws:
    /// sharpness, clarity and glow size their radius by it, as the export does by the cropped panel. 0 = all of them.
    /// </summary>
    public double VisibleHeight { get; set; }

    public LiveClip(Clip clip, Media? media, MediaAudio? audio)
    {
        this.clip = clip;
        RefreshCurve();
        Media = media;
        Audio = audio;
        sound = clip.Stretch != 1 ? audio == null ? null : ClipSound.Silent : clip.Reversed ? audio?.Sound.Flipped : audio?.Sound;
        SoundReversed = clip.Reversed;
    }

    /// <summary>Its own name, or its media's.</summary>
    public string Name => Clip.Name ?? Media?.Name ?? Loc.T("track.noMedia");
    /// <summary>Its media's own length; <see cref="LengthSec"/> is what it takes on the timeline.</summary>
    public double MediaLengthSec => Audio?.LengthSec ?? Media?.DurationSec ?? 0;
    public double LengthSec => MediaLengthSec * Clip.Stretch;
    public ClipEnvelope.ClipWindow Window => ClipEnvelope.Window(LengthSec, Clip.AtSec, Clip.TrimStartSec, Clip.TrimEndSec, Clip.EffectiveSyncMs(Media));
    public double OffsetSec => Window.Origin;
    public double ClipStartSec => Window.Start;
    public double ClipEndSec => Window.End;
    public ClipEnvelope.FadePair AudioFades => ClipEnvelope.Fades(Window, Clip.FadeInSec, Clip.FadeOutSec).Audio;
    public float Gain => (float)Math.Pow(10, Clip.GainDb / 20);

    /// <summary>True once <see cref="OpenVideo"/> ran, so a picture that can't be opened isn't retried on every edit.</summary>
    public bool VideoTried { get; private set; }

    public void OpenVideo(ProjectStore store)
    {
        VideoTried = true;
        if (store.Resolve(Media?.Video) is { } path) Video = VideoDecoder.Open(path);
    }

    /// <summary>
    /// Asks the decoder for the frame at a timeline time, applies the 3D LUT trilinear color grade and
    /// monochrome film grain (<see cref="VideoColorProcessor"/>), then sharpness, clarity and glow (<see cref="VideoDetail"/>, the look's share included),
    /// and copies the result into <see cref="Frame"/>.
    /// Returns true when the picture changed (new decoded frame, color/grain/detail edit while paused, or animated grain).
    /// Outside the clip the panel is empty.
    /// </summary>
    public bool UpdateVideo(double timelineSec, ColorGrade? color = null, VideoFx? videoFx = null)
    {
        var video = Video;
        if (video == null) return false;
        bool inside = timelineSec >= ClipStartSec - Lead && timelineSec < ClipEndSec + Tail;
        bool changed = inside != InClip;
        InClip = inside;
        double videoOffset = Clip.EffectiveVideoOffsetMs(Media) / 1000;
        if (!inside)
        {
            // starting FFmpeg takes a few frames: done ahead of time, a cut doesn't freeze the picture
            double until = ClipStartSec - Lead - timelineSec;
            if (until > 0 && until <= PreRollSec) video.Want(VideoTime(ClipStartSec - Lead, videoOffset));
            else video.Idle();
            if (changed) Fresh = false;
            return changed;
        }
        if (changed)
        {
            Fresh = false;
            frameVersion = -1; // the decoder's current frame counts again, if it's from around here
        }

        video.Want(VideoTime(timelineSec, videoOffset));
        Frame ??= new WriteableBitmap(new PixelSize(video.Width, video.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

        bool newRaw = video.TryCopyLatest(ref frameVersion, buf =>
        {
            if (rawFrame == null || rawFrame.Length != buf.Length)
                rawFrame = new byte[buf.Length];
            Buffer.BlockCopy(buf, 0, rawFrame, 0, buf.Length);
        });

        if (newRaw && !Fresh)
        {
            Fresh = true;
            changed = true;
        }
        if (rawFrame == null) return changed;

        var colSpec = PreviewVideoFx.ToSpec(color ?? Settings?.Color);
        var fxSpec = PreviewVideoFx.ToSpec(videoFx ?? Settings?.VideoFx);
        int grainLevels = VideoColorProcessor.GrainLevels(fxSpec);
        int frameSeed = (int)Math.Floor(Math.Max(0, timelineSec) * VideoDecoder.Fps);
        var detailAmounts = VideoDetail.For(colSpec, fxSpec);
        bool hasDetail = detailAmounts.Any;
        double visibleH = VisibleHeight > 0 ? Math.Min(VisibleHeight, video.Height) : video.Height;
        var detailKey = hasDetail ? VideoDetail.Processor.Key(detailAmounts, video.Width, video.Height, visibleH) : default;

        bool needWrite = newRaw
            || lastColorSpec != colSpec
            || lastGrainLevels != grainLevels
            || (grainLevels > 0 && frameSeed != lastFrameSeed)
            || lastDetailKey != detailKey;

        if (!needWrite) return changed;

        lastColorSpec = colSpec;
        lastGrainLevels = grainLevels;
        lastFrameSeed = frameSeed;
        lastDetailKey = detailKey;

        byte[] srcToCopy;
        if (VideoColorProcessor.IsNeutralGrade(colSpec) && grainLevels <= 0 && !hasDetail)
        {
            srcToCopy = rawFrame;
        }
        else
        {
            if (gradedFrame == null || gradedFrame.Length != rawFrame.Length)
                gradedFrame = new byte[rawFrame.Length];
            var lutTable = PreviewVideoFx.GetLutTable(colSpec);
            VideoColorProcessor.GradeAndGrainBgraParallel(
                rawFrame,
                gradedFrame,
                video.Width,
                video.Height,
                lutTable,
                grainLevels,
                frameSeed);
            // after the color and the grain, like the export, which filters the cropped panel
            if (hasDetail) (detail ??= new()).Apply(gradedFrame, video.Width, video.Height, visibleH, detailAmounts);
            srcToCopy = gradedFrame;
        }

        using (var fb = Frame.Lock())
        {
            int row = video.Width * 4;
            for (int y = 0; y < video.Height; y++)
                Marshal.Copy(srcToCopy, y * row, fb.Address + y * fb.RowBytes, row);
        }
        return true;
    }

    /// <summary>
    /// The video time for a timeline time. Past the clip's edges (a transition's overhang) it stays within the
    /// recording, so a side with no hidden video holds its first or last frame, as the export does.
    /// </summary>
    double VideoTime(double timelineSec, double videoOffset)
    {
        double sec = Clip.MediaSec(timelineSec - OffsetSec) + videoOffset;
        if (timelineSec < ClipStartSec) sec = Math.Max(0, sec);
        if (timelineSec >= ClipEndSec) sec = Math.Min(sec, Math.Max(0, MediaLengthSec - 2 / VideoDecoder.Fps));
        return sec;
    }

    /// <summary>Closes the decoder; <see cref="OpenVideo"/> may open it again (a project switched back from solo audio).</summary>
    public void CloseVideo() => DetachVideo()?.Dispose();

    /// <summary>
    /// Lets go of the decoder without closing it, on the UI thread, so a clip still on screen is never ticked while
    /// another thread clears it (that threw inside the tick and stopped its timer for good). The caller disposes it.
    /// </summary>
    public VideoDecoder? DetachVideo()
    {
        var video = Video;
        Video = null;
        VideoTried = false;
        InClip = false;
        Fresh = false;
        rawFrame = null;
        gradedFrame = null;
        detail = null;
        return video;
    }
}

/// <summary>
/// A track loaded for playback: its clips, the full per-track DSP chain (<see cref="TrackDspChain"/>:
/// clip fades → the channel's insert slots → panner → fader → its output and its sends)
/// and the live meters. Timeline edits re-point <see cref="Track"/> and swap <see cref="Clips"/> for a new list
/// (never changed in place: the audio thread may be walking the old one), keeping the chain's state.
/// </summary>
/// <summary>What a track's clips are: none with audio, all mono, all stereo, or both (<see cref="LiveTrack.Layout"/>).</summary>
public enum ChannelLayout { None, Mono, Stereo, Mixed }

public sealed class LiveTrack
{
    /// <summary>10 ms at 48 kHz: a clip that starts playing its transposed audio, or goes silent or comes back from it, crossfades, never clicks.</summary>
    public const int SoundFade = 480;
    /// <summary>The block plays one place of the song (<see cref="MixInto"/>).</summary>
    public const long NoSecondRead = long.MinValue;
    readonly int sampleRate;

    public Track Track { get; set; }
    public Channel Channel => Track.Channel;
    public IReadOnlyList<LiveClip> Clips { get; set; }
    public TrackDspChain Chain { get; }

    /// <summary>
    /// Mono, stereo or both among its clips, for the header's badge and the inspector. A clip whose sides are the same
    /// samples counts as mono, as the mixer shows it (<see cref="MediaAudio.Mono"/>); an offline one goes by its file.
    /// </summary>
    public ChannelLayout Layout
    {
        get
        {
            bool mono = false, stereo = false;
            // by index: the header reads it every frame, and a foreach over the interface allocates its enumerator
            var clips = Clips;
            for (int i = 0; i < clips.Count; i++)
            {
                var c = clips[i];
                if (c.Audio is { } a) { if (a.Mono) mono = true; else stereo = true; }
                else if (c.Media is { Channels: > 0 } m) { if (m.Channels == 1) mono = true; else stereo = true; }
            }
            return mono && stereo ? ChannelLayout.Mixed : stereo ? ChannelLayout.Stereo : mono ? ChannelLayout.Mono : ChannelLayout.None;
        }
    }

    // engine scratch + meters (written on the audio thread, read by the UI)
    public readonly float[] BufL = new float[TrackDspChain.MaxBlockSize];
    public readonly float[] BufR = new float[TrackDspChain.MaxBlockSize];
    public float MeterL, MeterR;
    public double GrDb;
    public float ShowL, ShowR; // UI side, with fall-off
    public float InputL, InputR; // UI side: the input's peak while armed, shown on the track's meter
    public double GrPeak, GrShow; // UI side: this tick's reduction, and the mixer's (instant attack, slow release)
    /// <summary>Silences the track while a take that replaces its clips is being recorded (set by the UI).</summary>
    public volatile bool RecordMuted;
    /// <summary>
    /// Where its output goes (<see cref="PlaybackEngine.SetBuses"/>): its folder's node, its bus's, or null for the
    /// master. Set by the UI, read by the audio thread once per block.
    /// </summary>
    public volatile BusDspChain? Out;
    /// <summary>
    /// The engine's folders and buses by id (<see cref="PlaybackEngine.SetBuses"/>), for <see cref="ApplySends"/>; null
    /// until the engine hands them over. UI thread only.
    /// </summary>
    public IReadOnlyDictionary<string, BusDspChain>? Nodes { get; set; }
    /// <summary>The Escuchar tool is playing this track alone (native only): a solo that is not saved and does not light S.</summary>
    public volatile bool ListenSolo;
    /// <summary>Its folder is soloed (<see cref="ChannelDsp.FolderSoloed"/>), which solos it too; set by the UI in <see cref="PlaybackEngine.SetBuses"/>.</summary>
    public volatile bool FolderSolo;
    public bool Soloed => Channel.Solo || FolderSolo || ListenSolo;
    /// <summary>Its folder is muted: only shown (dimmed), the folder's node is what goes silent. Set with <see cref="FolderSolo"/>.</summary>
    public bool FolderMuted;
    public bool Silenced => Channel.Muted || FolderMuted;

    public LiveTrack(Track track, IReadOnlyList<LiveClip> clips, int sampleRate)
    {
        this.sampleRate = sampleRate;
        Track = track;
        Clips = clips;
        Chain = new TrackDspChain(sampleRate) { Keys = LiveKeys.For };
        Keys = new LiveNotes(sampleRate) { Transpose = track.Transpose };
        Preview = new NotePreview(sampleRate);
        ApplyAll();
        started = true;
    }

    /// <summary>Level the saturation's Drive knob is measured against: the first clip with audio (one take per track for now).</summary>
    public double RmsDb => Clips.FirstOrDefault(c => c.Audio != null)?.Audio!.RmsDb ?? -24;

    /// <summary>The first clip's audio, which the compressor's analyze button measures.</summary>
    public MediaAudio? FirstAudio => Clips.FirstOrDefault(c => c.Audio != null)?.Audio;

    public bool HasPanel => Track.Video && Clips.Count > 0;

    /// <summary>Both clips of a join have a picture on this track.</summary>
    public bool Pictured(LiveClip from, LiveClip to) => Track.Video && from.VideoPath != null && to.VideoPath != null;

    /// <summary>
    /// A join offers a transition between videos only (asked by the user), in a project with video
    /// (<paramref name="video"/>). One already stored stays reachable anywhere, so it can be changed or removed.
    /// </summary>
    public bool OffersTransition(LiveClip from, LiveClip to, bool video) =>
        video && Pictured(from, to) || ClipTransitions.Parse(to.Clip.Transition) != TransitionKind.Cut;

    public double EndSec => Math.Max(MidiEndSec, Clips.Count == 0 ? 0 : Clips.Max(c => c.ClipEndSec));

    volatile NoteSequence? notes;
    /// <summary>A MIDI track's notes in frames (<see cref="ChannelDsp.Notes"/>), swapped whole by <see cref="SetMidi"/>; null = none.</summary>
    public NoteSequence? Notes => notes;
    /// <summary>Where its last MIDI clip ends (seconds), for the song's length.</summary>
    public double MidiEndSec { get; private set; }

    double bpm = 120;

    /// <summary>
    /// The keys of a MIDI keyboard played on this track (the selected MIDI track, or an armed one): the engine hands
    /// them over at the start of each block and they sound with its instrument (or the test tone) through its chain.
    /// </summary>
    public LiveNotes Keys { get; }

    /// <summary>The piano roll's notes on a track without an instrument: the test tone, through its chain like everything it plays.</summary>
    public NotePreview Preview { get; }

    /// <summary>Takes the track's notes again at <paramref name="tempo"/> (after an edit or a tempo change). UI thread.</summary>
    public void SetMidi(double tempo)
    {
        // a VST3 effect follows the tempo (its synced delays and LFOs)
        bool retempo = tempo != bpm && Channel.Slots.Exists(s => s.Type == InsertSlot.VstKind);
        bpm = tempo;
        if (retempo) ApplyInserts();
        Keys.Transpose = Track.Transpose;
        Keys.Tempo = tempo;
        Keys.Arp = Track.Arp?.Live();
        notes = ChannelDsp.Notes(Track, tempo, sampleRate);
        MidiEndSec = Track.MidiClips is { Count: > 0 } mc ? mc.Max(c => c.EndSec(tempo)) : 0;
        if (instrument is { } player)
        {
            player.Notes = notes;
            player.Tempo = tempo;
        }
        if (native is { } own) own.Notes = notes;
    }

    /// <summary>
    /// The native instrument that plays the notes instead of the test tone (<see cref="Sampler"/> or <see cref="Granular"/>),
    /// set by the UI as soon as the track has one (it stays silent until its sample is read). A track never has it along
    /// with a plugin. Read once per block.
    /// </summary>
    volatile NoteInstrument? native;

    public NoteInstrument? Native => native;

    /// <summary>The native sampler, if that is the track's instrument. Setting null takes away only a sampler.</summary>
    public Sampler? Sampler
    {
        get => native as Sampler;
        set
        {
            if (value != null)
            {
                value.Notes = notes;
                value.Curves = Automations.SamplerCurves(Track);
                native = value;
            }
            else if (native is Sampler) native = null;
        }
    }

    /// <summary>The native granular, if that is the track's instrument. Setting null takes away only a granular.</summary>
    public Granular? Granular
    {
        get => native as Granular;
        set
        {
            if (value != null)
            {
                value.Notes = notes;
                value.Curves = Automations.GranularCurves(Track);
                native = value;
            }
            else if (native is Granular) native = null;
        }
    }

    volatile InstrumentPlayer? instrument;
    /// <summary>
    /// The instrument plugin that plays the notes instead of the test tone (<see cref="InstrumentPlayer"/>), set by the
    /// UI once its process is up; null = the test tone. The audio thread reads it once per block.
    /// </summary>
    public InstrumentPlayer? Instrument
    {
        get => instrument;
        set
        {
            if (value != null)
            {
                value.Notes = notes;
                value.Tempo = bpm;
                value.Curves = Automations.InstrumentCurves(Track);
            }
            instrument = value;
        }
    }

    /// <summary>The clip whose picture shows in the panel: the last one under the playhead, the one on top in the timeline and the export.</summary>
    public LiveClip? ShownClip => Clips.LastOrDefault(c => c.InClip);

    // the clips' windows and transitions, refilled every tick (grown only when the track gets more clips)
    ClipEnvelope.ClipWindow[] windows = [];
    TransitionKind[] kinds = [];
    double[] secs = [];
    int[] takes = [];
    AudioJoin[] ins = [], outs = [];
    /// <summary>The video transition under the playhead (<see cref="UpdateTransitions"/>), or null.</summary>
    public ClipTransitions.Join? Transition { get; private set; }
    /// <summary>The playhead's time when <see cref="Transition"/> was found, for <see cref="ClipTransitions.Weights"/>.</summary>
    public double TransitionAt { get; private set; }

    /// <summary>
    /// Finds this track's transitions (<see cref="ClipTransitions"/>, the export's same math). The sound: each clip's
    /// <see cref="LiveClip.AudioJoins"/>, for the audio thread. The picture (video tracks): each clip's
    /// <see cref="LiveClip.Lead"/> / <see cref="LiveClip.Tail"/> so its decoder runs past its edges during a crossfade,
    /// and <see cref="Transition"/> to the one under <paramref name="t"/>. Called every UI tick before the clips'
    /// <see cref="LiveClip.UpdateVideo"/>; allocates only when a transition changed. Returns true when the preview must be redrawn
    /// (inside a transition while the playhead moves, or one appeared, went or changed).
    /// </summary>
    public bool UpdateTransitions(double t)
    {
        var clips = Clips;
        int n = clips.Count;
        if (windows.Length < n)
        {
            int size = Math.Max(n, 2 * windows.Length);
            windows = new ClipEnvelope.ClipWindow[size];
            kinds = new TransitionKind[size];
            secs = new double[size];
            takes = new int[size];
            ins = new AudioJoin[size];
            outs = new AudioJoin[size];
        }
        for (int i = 0; i < n; i++)
        {
            var c = clips[i];
            windows[i] = c.Window;
            kinds[i] = ClipTransitions.Parse(c.Clip.Transition);
            secs[i] = c.Clip.TransitionSec;
            takes[i] = i;
            for (int k = 0; k < i; k++)
                if (c.Media != null && clips[k].Media?.Id == c.Media.Id && clips[k].Clip.Reversed == c.Clip.Reversed && clips[k].Clip.Stretch == c.Clip.Stretch && clips[k].Clip.Pitch == c.Clip.Pitch) { takes[i] = k; break; }
            c.Lead = c.Tail = 0;
        }

        // the sound: every track, video or not; a clip gets a new (immutable) set only when its joins changed
        ClipTransitions.AudioJoins(windows.AsSpan(0, n), kinds.AsSpan(0, n), secs.AsSpan(0, n), takes.AsSpan(0, n), ins, outs);
        for (int i = 0; i < n; i++)
        {
            var c = clips[i];
            var now = c.AudioJoins;
            bool has = ins[i].Exists || outs[i].Exists;
            if (now == null ? has : !has || now.In != ins[i] || now.Out != outs[i])
                c.AudioJoins = has ? new ClipAudioJoins(ins[i], outs[i]) : null;
        }

        ClipTransitions.Join? active = null;
        if (Track.Video)
        {
            var w = windows.AsSpan(0, n);
            for (int i = 0; i < n; i++)
            {
                if (ClipTransitions.Find(w, i, kinds[i], secs[i]) is not { } j) continue;
                if (j.Overhang > 0)
                {
                    clips[j.In].Lead = Math.Max(clips[j.In].Lead, j.Overhang);
                    clips[j.Out].Tail = Math.Max(clips[j.Out].Tail, j.Overhang);
                }
                if (t >= j.Start && t < j.End) active = j;
            }
        }
        bool redraw = active != Transition || active != null && t != TransitionAt;
        Transition = active;
        TransitionAt = t;
        return redraw;
    }

    /// <summary>Associates the project's settings with all clips on this track so <see cref="LiveClip.UpdateVideo"/> follows color and video FX changes.</summary>
    public void SetProjectSettings(ProjectSettings? settings)
    {
        for (int i = 0; i < Clips.Count; i++)
            Clips[i].Settings = settings;
    }

    /// <summary>
    /// Updates the entire channel strip (its insert slots, fader, pan, mute/solo and sends) from the UI
    /// thread without audio dropouts or torn coefficients: the slots keep their state by id, and a new list of slots
    /// fades over (<see cref="TrackDspChain.SetInserts"/>). If <paramref name="ch"/> is provided, replaces
    /// <c>Track.Channel</c> first.
    /// </summary>
    public void ApplyChannel(Channel? ch = null)
    {
        if (ch != null) Track.Channel = ch;
        var cur = Track.Channel;
        Chain.Volume = cur.Volume;
        Chain.Pan = cur.Pan;
        Chain.Muted = cur.Muted;
        Chain.Solo = cur.Solo || FolderSolo || ListenSolo;
        ApplyAutomation();
        ApplyInserts();
        ApplySends();
    }

    InsertAutomator? automator;
    double autoSec;

    /// <summary>The fader's lanes and the instrument's, swapped whole. The slots' and the sends' go with theirs.</summary>
    void ApplyAutomation()
    {
        Chain.Automation = Automations.Compile(Track);
        if (instrument is { } player) player.Curves = Automations.InstrumentCurves(Track);
        if (native is Sampler sampler) sampler.Curves = Automations.SamplerCurves(Track);
        else if (native is Granular granular) granular.Curves = Automations.GranularCurves(Track);
    }

    /// <summary>
    /// UI thread, every tick: the lanes on the native effects at the playhead (the fader's, the sends' and the plugins'
    /// are read on the audio thread). Allocates only when a lane moved, as much as turning that knob by hand. True when
    /// the fader or the pan a header shows moved.
    /// </summary>
    public bool TickAutomation(double sec, bool playing)
    {
        autoSec = sec;
        if (!playing) Chain.Frame = (long)(sec * sampleRate);
        if (automator?.At(sec) is { } specs) Chain.SetInserts(specs);
        var a = Chain.Automation;
        double? volume = a?.Volume?.At(sec), pan = a?.Pan?.At(sec);
        bool moved = Far(volume, autoVolume) || Far(pan, autoPan);
        if (moved) (autoVolume, autoPan) = (volume, pan);
        return moved;
    }

    static bool Far(double? a, double? b) => a is { } x && b is { } y ? Math.Abs(x - y) >= ShownStep : a != b;

    /// <summary>A lane's move the timeline's header waits for before it redraws.</summary>
    const double ShownStep = 0.01;
    double? autoVolume, autoPan;
    /// <summary>Where the fader and the pan are, with their lanes: what the controls show.</summary>
    public double ShownVolume => autoVolume ?? Channel.Volume;
    public double ShownPan => autoPan ?? Channel.Pan;

    /// <summary>
    /// Hands the chain its sends to buses as a new immutable array (<see cref="TrackDspChain.Sends"/>): the ones that are
    /// on, above 0 and whose bus the engine has. UI thread, after any edit of the sends or the buses.
    /// </summary>
    public void ApplySends()
    {
        var next = PlaybackEngine.SendsTo(Nodes, Channel, Chain.Sends, Track);
        if (next.Length > 0 || Chain.Sends.Length > 0) Chain.Sends = next;
    }

    public void ApplyAll() => ApplyChannel();

    /// <summary>Hands the slots to the chain (a spare the edit turned on becomes a slot first).</summary>
    public void ApplyInserts()
    {
        Channel.AdoptSpares();
        var live = LiveVst.Of(Track.Id);
        automator = InsertAutomator.Create(Track, RmsDb, bpm, live, PlaybackEngine.FxBypass);
        Chain.SetInserts(automator?.At(autoSec, force: true) ?? ChannelDsp.Specs(Track, RmsDb, bpm, live, PlaybackEngine.FxBypass), fade: started);
    }

    /// <summary>False only while the constructor sets the chain up, before the audio thread can run it.</summary>
    bool started;

    /// <summary>
    /// Recomputes a compressor's threshold, ratio, attack/release, makeup and CompEq from its preset and amount, writes
    /// them back into <paramref name="comp"/> (the channel's first compressor when null) and updates the live DSP chain.
    /// </summary>
    public void RecalculateCompPreset(CompSettings? comp = null)
    {
        ChannelDsp.RecalculateComp(comp ?? Channel.Comp);
        ApplyInserts();
    }

    /// <summary>
    /// Samples all clips on this track for frames <c>[posFrame .. posFrame + n)</c> with their trim window,
    /// <c>SyncMs</c> offset, linear fade-in/out envelope and clip gain, then runs the full insert chain and
    /// accumulates into its output (<see cref="Out"/>'s buffer, or <paramref name="masterL"/> / <paramref name="masterR"/>)
    /// and its sends' buses, from <paramref name="sendOffset"/> on in theirs (the block's part where the tracks play).
    /// Zero allocations. During a seek crossfade or a loop's splice (<paramref name="fromFrame"/> other than
    /// <see cref="NoSecondRead"/>; a splice near zero reads before it) the clips at that position, weighted by
    /// <paramref name="wOut"/>, are summed with the ones at <paramref name="posFrame"/>, weighted by <paramref name="wIn"/>,
    /// before the chain, so its state stays continuous. <paramref name="loopEnd"/> above 0 is the end of the loop the
    /// engine turns on, from it to <paramref name="turnTo"/>: an instrument plugin plays ahead of the playhead and needs it.
    /// </summary>
    public void MixInto(
        long posFrame,
        int n,
        bool anySolo,
        Span<float> masterL,
        Span<float> masterR,
        int sendOffset,
        long fromFrame = NoSecondRead,
        ReadOnlySpan<float> wIn = default,
        ReadOnlySpan<float> wOut = default,
        int turnAt = -1,
        long turnTo = 0,
        long loopEnd = 0,
        ReadOnlySpan<float> fade = default)
    {
        long t0 = Stopwatch.GetTimestamp();
        var bl = Chain.BufferL(n);
        var br = Chain.BufferR(n);
        bl.Clear();
        br.Clear();
        Chain.Frame = posFrame;
        var seq = notes;
        var inst = instrument;
        var own = native;
        long played;
        if (fromFrame == NoSecondRead)
        {
            AddClips(posFrame, n, bl, br, default);
            played = Stopwatch.GetTimestamp();
            if (inst != null) inst.Play(posFrame, n, bl, br, Keys, turnTo, loopEnd);
            else if (own != null) own.Play(posFrame, n, bl, br, Keys);
            else if (seq != null) NoteSynth.Render(seq, posFrame, n, sampleRate, bl, br);
        }
        else
        {
            AddClips(fromFrame, n, bl, br, wOut);
            AddClips(posFrame, n, bl, br, wIn);
            played = Stopwatch.GetTimestamp();
            // a plugin can't play two places at once: in a seek it jumps (its notes go off) and plays on from the new
            // one; a loop's turn it already played, ahead of the playhead
            if (inst != null) inst.Play(posFrame, n, bl, br, Keys, turnTo, loopEnd);
            // the sampler lets its old notes go and picks up the new ones; a loop tells it the exact frame
            else if (own != null) own.Play(posFrame, n, bl, br, Keys, turnAt, turnTo);
            else if (seq != null)
            {
                NoteSynth.Render(seq, fromFrame, n, sampleRate, bl, br, wOut);
                NoteSynth.Render(seq, posFrame, n, sampleRate, bl, br, wIn);
            }
        }
        Interlocked.Add(ref instrumentTicks, Stopwatch.GetTimestamp() - played);
        // the transport's fade goes on what the track plays, before its slots: a reverb or a delay rings on after a stop
        if (fade.Length == n)
            for (int i = 0; i < n; i++)
            {
                bl[i] *= fade[i];
                br[i] *= fade[i];
            }
        tailLeft = TailFrames;
        quietFrames = 0;
        // the keyboard: through the plugin above, or the test tone here
        if (inst != null || own != null) Keys.Skip(n);
        else Keys.Render(n, bl, br);
        Preview.Mix(n, bl, br);
        Output(n, anySolo, masterL, masterR, sendOffset);
        Interlocked.Add(ref busyTicks, Stopwatch.GetTimestamp() - t0);
    }

    /// <summary>
    /// While stopped: an instrument plugin keeps running (its tails, the piano roll's keys, the keyboard through it and
    /// the keys in its own window) and, while it has something to play, its sound goes through the chain to its output like when it plays. Without
    /// one, the keyboard's test tone and the piano roll's do the same while they sound (and a little after, for a reverb slot's tail).
    /// Zero allocations.
    /// </summary>
    public void MixIdle(int n, bool anySolo, Span<float> masterL, Span<float> masterR)
    {
        long t0 = Stopwatch.GetTimestamp();
        MixIdleTimed(n, anySolo, masterL, masterR, t0);
        Interlocked.Add(ref busyTicks, Stopwatch.GetTimestamp() - t0);
    }

    void MixIdleTimed(int n, bool anySolo, Span<float> masterL, Span<float> masterR, long t0)
    {
        if (instrument is { } inst)
        {
            bool sounds = inst.Idle(n, Chain.BufferL(n), Chain.BufferR(n), Keys);
            Interlocked.Add(ref instrumentTicks, Stopwatch.GetTimestamp() - t0);
            if (sounds) Output(n, anySolo, masterL, masterR, 0);
            else Ring(n, anySolo, masterL, masterR);
            Keys.Skip(n);
            return;
        }
        if (native is { } own)
        {
            bool sounds = own.Idle(n, Chain.BufferL(n), Chain.BufferR(n), Keys, Chain.Frame);
            Interlocked.Add(ref instrumentTicks, Stopwatch.GetTimestamp() - t0);
            if (sounds) Output(n, anySolo, masterL, masterR, 0);
            else Ring(n, anySolo, masterL, masterR);
            Keys.Skip(n);
            return;
        }
        if (!Keys.Active && !Preview.Active)
        {
            Ring(n, anySolo, masterL, masterR);
            return;
        }
        var bl = Chain.BufferL(n);
        var br = Chain.BufferR(n);
        bl.Clear();
        br.Clear();
        Keys.Render(n, bl, br);
        Preview.Mix(n, bl, br);
        Output(n, anySolo, masterL, masterR, 0);
    }

    // time spent on the audio thread, written there and taken by the UI (TakeLoad)
    long busyTicks, instrumentTicks;

    /// <summary>The audio thread's time on this track since the last call, and on its instrument (plugin, sampler or test tone) alone, in <see cref="Stopwatch"/> ticks. UI thread.</summary>
    public (long Busy, long Instrument) TakeLoad() => (Interlocked.Exchange(ref busyTicks, 0), Interlocked.Exchange(ref instrumentTicks, 0));

    /// <summary>The share of real time the last <see cref="TakeLoad"/> measured, for the DSP tooltip: the whole track and its instrument, in %.</summary>
    public double LoadPercent, InstrumentPercent;

    /// <summary>The longest a track's slots ring on after it stops playing: past the longest reverb (14 s).</summary>
    const double TailSec = 15;
    /// <summary>A tail is over once its output stays this quiet (-100 dB) for <see cref="QuietSec"/>.</summary>
    const float TailSilence = 1e-5f;
    /// <summary>Longer than the longest delay, so a quiet gap between two echoes doesn't end the tail.</summary>
    const double QuietSec = DelayBus.MaxDelaySec + 0.5;

    int TailFrames => (int)(TailSec * sampleRate);
    // audio thread: how long the slots still ring stopped, and how long their output has been quiet
    int tailLeft, quietFrames;
    /// <summary>The block's loudest output, from <see cref="RunChain"/>.</summary>
    float blockPeak;

    /// <summary>Its slots still ring after the song stopped (a reverb, a delay): the engine keeps running the chain, and doesn't reset it on Play.</summary>
    public bool Ringing => tailLeft > 0;

    /// <summary>The panic (<see cref="XtremeDaw.App.Audio.PlaybackEngine.CutAudio"/>): its notes go off, its instrument lets go of its
    /// voices and its slots forget their tails. Audio thread, after the engine faded the output out.</summary>
    public void Silence()
    {
        Keys.AllOff();
        instrument?.Panic();
        native?.Panic();
        Chain.Reset();
        tailLeft = 0;
        quietFrames = 0;
    }

    /// <summary>Stopped, with nothing new to play: the chain runs on silence while its slots ring.</summary>
    void Ring(int n, bool anySolo, Span<float> masterL, Span<float> masterR)
    {
        if (tailLeft <= 0) return;
        Chain.BufferL(n).Clear();
        Chain.BufferR(n).Clear();
        Output(n, anySolo, masterL, masterR, 0);
        quietFrames = blockPeak < TailSilence ? quietFrames + n : 0;
        tailLeft = quietFrames >= QuietSec * sampleRate ? 0 : tailLeft - n;
    }

    /// <summary>The engine runs <see cref="MixIdle"/> for it: an instrument, the keyboard or the piano roll still sounding, or its slots' tail.</summary>
    public bool RunsIdle => instrument != null || native != null || Keys.Active || Preview.Active || tailLeft > 0;

    /// <summary>The chain over what the block filled in, into its output (its folder, a bus or the master) and its sends.</summary>
    void Output(int n, bool anySolo, Span<float> masterL, Span<float> masterR, int sendOffset)
    {
        var to = Out;
        Chain.KeyOffset = sendOffset;
        if (to != null) RunChain(n, anySolo, to.BufferL(sendOffset + n).Slice(sendOffset), to.BufferR(sendOffset + n).Slice(sendOffset));
        else RunChain(n, anySolo, masterL, masterR);
        var sends = Chain.BlockSends;
        for (int k = 0; k < sends.Length; k++)
        {
            var bus = sends[k].To;
            if (bus == null) continue;
            Chain.AddSend(n, sends[k], bus.BufferL(sendOffset + n).Slice(sendOffset),
                bus.BufferR(sendOffset + n).Slice(sendOffset));
        }
    }

    /// <summary>Adds every clip's samples for frames <c>[posFrame .. posFrame + n)</c>, times <paramref name="weight"/> if given.</summary>
    void AddClips(long posFrame, int n, Span<float> bl, Span<float> br, ReadOnlySpan<float> weight)
    {
        double invSr = 1.0 / sampleRate;
        var clips = Clips;
        for (int cIdx = 0; cIdx < clips.Count; cIdx++)
        {
            var c = clips[cIdx];
            var a = c.Sound;
            if (a == null) continue;
            if (a != c.Heard)
            {
                c.Fading = c.Heard;
                c.Heard = a;
                c.FadeLeft = c.Fading != null ? SoundFade : 0;
            }
            var old = c.FadeLeft > 0 ? c.Fading : null;
            int fadeLeft = c.FadeLeft;
            if (a.L.Length == 0 && old == null) continue;
            var w = c.Window;
            if (w.End <= w.Start) continue;
            // a transition plays the clip past its edges, into the audio its trims hide (MixRenderer does the same)
            var join = c.AudioJoins;
            long startFrame = Math.Max(0, (long)Math.Round((join != null ? join.From(w.Start) : w.Start) * sampleRate));
            long endFrame = (long)Math.Round((join != null ? join.Until(w.End) : w.End) * sampleRate);
            if (posFrame + n <= startFrame || posFrame >= endFrame) continue;

            long originFrame = (long)Math.Round(w.Origin * sampleRate);
            var fades = c.AudioFades;
            float cg = c.Gain;
            var curve = c.Curve;
            float k = 1, dk = 0;
            float[] left = a.L;
            float[] right = a.R;
            // a sound of another length (the tempo changed) fades from the same place of the song
            double oldStep = old == null || old.L.Length == left.Length ? 1 : old.L.Length / (w.Dur * sampleRate);

            for (int i = 0; i < n; i++)
            {
                long f = posFrame + i;
                if (curve.Length > 0 && i % ClipGainCurve.Step == 0)
                {
                    double t = (f - originFrame) * invSr;
                    k = (float)ClipGainCurve.GainAt(curve, t);
                    dk = ((float)ClipGainCurve.GainAt(curve, t + ClipGainCurve.Step * invSr) - k) / ClipGainCurve.Step;
                }
                if (f < startFrame || f >= endFrame) continue;
                long src = f - originFrame;
                bool here = src >= 0 && src < left.Length;
                if (!here && (old == null || fadeLeft <= 0 || src < 0)) continue;
                double timeSec = f * invSr;
                double g = join == null ? ClipEnvelope.GainAt(timeSec, w.Start, w.End, fades.FadeIn, fades.FadeOut)
                    : ClipEnvelope.GainAt(Math.Clamp(timeSec, w.Start, w.End), w.Start, w.End, fades.FadeIn, fades.FadeOut) * join.Gain(timeSec);
                float env = (float)g * cg * (k + dk * (i % ClipGainCurve.Step));
                if (!weight.IsEmpty) env *= weight[i];
                float sl = here ? left[src] : 0, sr = here ? right[src] : 0;
                if (old != null && fadeLeft > 0)
                {
                    float wOld = fadeLeft-- / (float)SoundFade;
                    long at = oldStep == 1 ? src : (long)(src * oldStep);
                    float ol = at < old.L.Length ? old.L[at] : 0, or = at < old.L.Length ? old.R[at] : 0;
                    sl += (ol - sl) * wOld;
                    sr += (or - sr) * wOld;
                }
                bl[i] += sl * env;
                br[i] += sr * env;
            }
            c.FadeLeft = fadeLeft;
        }
    }

    void RunChain(int n, bool anySolo, Span<float> outL, Span<float> outR)
    {
        var ch = Channel;
        Chain.Volume = ch.Volume;
        Chain.Pan = ch.Pan;
        Chain.Muted = ch.Muted || RecordMuted;
        Chain.Solo = ch.Solo || FolderSolo || ListenSolo;

        Chain.ProcessFilledBuffer(
            n,
            anySolo,
            outL,
            outR,
            out float pkL,
            out float pkR,
            out double gr);

        GrDb = Math.Min(GrDb, gr);
        blockPeak = Math.Max(pkL, pkR);
        if (pkL > MeterL) MeterL = pkL;
        if (pkR > MeterR) MeterR = pkR;
    }

    public void CloseVideo()
    {
        foreach (var c in Clips) c.CloseVideo();
    }
}
