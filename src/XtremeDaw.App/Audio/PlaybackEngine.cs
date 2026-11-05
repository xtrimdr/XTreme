using System.Diagnostics;
using XtremeDaw.App.Model;
using XtremeDaw.Audio;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Audio.Midi;
using XtremeDaw.Audio.Vst;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Audio;

/// <summary>
/// Plays the timeline and records takes through ASIO on a single sample clock (port of
/// <c>StudioAudioEngine</c> in <c>audioEngine.ts</c> + <c>mixGraph.ts</c>):
/// per track: clip fades → saturator → compressor EQ → compressor → 6-band track EQ → pan → fader;
/// its output into its folder, a bus or the master, and post-fader (or pre-fader) sends into buses; the folders and buses
/// (<see cref="BusDspChain"/> nodes, <see cref="SetBuses"/>, native only) run in order, each into the next or the master;
/// the master into the <see cref="MasterChain"/> (native only) and the <see cref="MasterLimiter"/>; click mixed outside the limiter (delayed by
/// <see cref="MasterLimiter.Latency"/> and the plugin delay compensation, <see cref="PdcFrames"/>, so it lands on the
/// same sample as the limited tracks).
/// Also processes input 1-2 (<see cref="InputProcessor"/>: channel mode, pre-amp gain (0 dB default), soft-limiter,
/// optional software monitoring), runs count-in + recording on the same sample clock via <see cref="TakeRecorder"/>,
/// and runs round-trip latency calibration (<see cref="CalibrationSession"/>).
/// Native only: every driver input is open, and each armed track has an <see cref="InputTap"/> with its own
/// input; all of them record at once, one WAV each, starting on the same sample. The tuner listens to the first
/// armed input (else 1-2) through <see cref="Pitch"/>, and the master's loudness goes to <see cref="Loudness"/>
/// while it plays: the callback only copies and filters, the reading happens on the UI's tick.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    public const int SampleRate = 48000;

    AsioDuplex? io;
    volatile LiveTrack[] tracks = [];
    /// <summary>The folders and buses (<see cref="BusDspChain"/>) in the order they run, swapped whole like the tracks; by id on the UI side.</summary>
    volatile BusDspChain[] nodes = [];
    readonly Dictionary<string, BusDspChain> nodeById = [];
    /// <summary>The buses and folders the nodes were last built from, so a tempo change can update their delays.</summary>
    IReadOnlyList<Bus>? busList;
    IReadOnlyList<Folder>? folderList;
    /// <summary>UI side: the folders whose native effects follow lanes, with their node (<see cref="TickNodeAutomation"/>).</summary>
    (BusDspChain Node, InsertAutomator Lanes)[] nodeAutomators = [];
    /// <summary>UI side: where <see cref="TickNodeAutomation"/> last read the lanes, so a rebuilt automator starts there.</summary>
    double nodeAutoSec;
    readonly float[] rampGain = new float[TrackDspChain.MaxBlockSize];
    readonly MixGraphBus buses = new(SampleRate) { Slots = { Keys = LiveKeys.For } };
    /// <summary>The order the tracks run in each block: a sidechain's source before the tracks that listen to it (empty = the list's).</summary>
    volatile int[] trackOrder = [];
    readonly InputProcessor inputProcessor = new();
    readonly float[] inProcL = new float[TrackDspChain.MaxBlockSize];
    readonly float[] inProcR = new float[TrackDspChain.MaxBlockSize];
    readonly float[] mixL = new float[TrackDspChain.MaxBlockSize];
    readonly float[] mixR = new float[TrackDspChain.MaxBlockSize];

    ClickSound clickSound = ClickSound.Madera;
    double clickVolumeDb;
    volatile float[] clickHi = ClickSynth.Render(ClickSound.Madera, true, SampleRate);
    volatile float[] clickLo = ClickSynth.Render(ClickSound.Madera, false, SampleRate);
    float[]? click;
    int clickPos;
    // a single click asked for by the sound menu; the callback takes it, so it also sounds while stopped
    float[]? previewClick;

    /// <summary>The Biblioteca's sample preview, summed to the output like the click.</summary>
    public SampleVoice Sample { get; } = new();

    /// <summary>The MIDI inputs the keys come from (<see cref="MidiInputs"/>), set by the window; null = none.</summary>
    volatile MidiInputs? midiInputs;
    /// <summary>The tracks that hear the keyboard now (<see cref="SetMidiListeners"/>), swapped whole.</summary>
    volatile MidiListener[] midiListeners = [];
    /// <summary>Audio thread: the listeners of the last block, to let go of the keys of a track that stopped listening.</summary>
    MidiListener[] heardListeners = [];
    /// <summary>How late the output is heard (frames): a note recorded goes where the song was heard when it was played.</summary>
    long midiLatencyFrames;
    /// <summary>How late the whole mix is heard because of the VST3 effects (<see cref="UpdateLatency"/>), in frames.</summary>
    volatile int pdcFrames;
    /// <summary>How late the mix reaches the master's slots (the PDC before them), for a slot on the beat.</summary>
    volatile int masterArrive;
    /// <summary>The PDC the take was placed with: the take is moved back by it, and the mix stays that late until it ends (<see cref="VstInsert.Steady"/>).</summary>
    int recPdcFrames;
    /// <summary>What the master is putting out now, after the PDC (<see cref="HeardSec"/>).</summary>
    readonly HeardClock heard = new();
    /// <summary><see cref="heard"/> knows this run of the transport: set by the audio thread on its first block.</summary>
    volatile bool rolling;
    /// <summary>What the last <see cref="UpdateLatency"/> worked from, so a tick with nothing new costs one pass.</summary>
    int latencyKey;

    double bpm = 120;
    long position; // timeline frame (before recStartFrame, maybe negative, during recording count-in)
    /// <summary>Timeline frame where the take's first sample goes: the playhead when recording started.</summary>
    long recStartFrame;
    double playStartSec;
    volatile bool playing;
    volatile bool recording;
    volatile bool loop;
    /// <summary>The A–B range the loop repeats, in frames; null = the whole song (0 to the end). Swapped whole, like the tracks.</summary>
    sealed record LoopFrames(long Start, long End);
    volatile LoopFrames? loopRange;
    volatile bool softwareMonitor;
    volatile InputTap[] taps = [];
    volatile CalibrationSession? activeCalibration;
    int recCountInBeats;
    /// <summary>First beat of the project's grid at or after <see cref="recStartFrame"/>: where the click goes after the count-in.</summary>
    long recFirstBeat;
    bool recUsedCountIn;
    bool recUsedClick;
    long recStartTimestamp;
    double? calibratedLatencyMs;
    int pendingTrackReset;
    /// <summary>Play: resets the chains whose tail is over (<see cref="LiveTrack.Ringing"/>); a ringing one goes on.</summary>
    int pendingQuietReset;
    /// <summary><see cref="CutAudio"/> asked, taken by the audio thread once stopped: that block fades out to silence.</summary>
    int pendingCut;
    /// <summary>Audio thread: the cut's fade is done, the next block starts with every tail, note and chain cleared.</summary>
    bool cutReset;
    /// <summary>Audio thread: frames left of the cut's fade.</summary>
    int cutLeft;
    /// <summary>The cut's fade, like the transport's: short enough to feel instant, long enough not to click.</summary>
    const int CutFrames = (int)(TransportFadeSec * SampleRate);
    /// <summary>No seek waiting for the audio thread in <see cref="pendingSeek"/>.</summary>
    const long NoSeek = long.MinValue;
    /// <summary>Frame a seek asked for while playing; the audio thread takes it (see <see cref="Seek"/>).</summary>
    long pendingSeek = NoSeek;
    /// <summary>
    /// Length of the seek crossfade, and the default of the play / stop fade, in seconds. Fixed in time instead of
    /// one buffer: a 4 ms block (and less with smaller buffers) was short enough to click on loud, saturated material.
    /// </summary>
    const double TransportFadeSec = 0.010;
    static readonly float FadeStep = (float)(1 / (TransportFadeSec * SampleRate));
    /// <summary>Range and default of <see cref="TransportFadeMs"/> (Configuración, "audio.transportFadeMs").</summary>
    public const double MinTransportFadeMs = 2, MaxTransportFadeMs = 50, DefaultTransportFadeMs = TransportFadeSec * 1000;
    // per-sample step of the play / stop fade; the audio thread reads it once per block
    volatile float rampStep = FadeStep;
    // audio thread only. fadeMode: 0 = none, 1 = fading in, -2 = fading out to stop. fadeLevel: 0..1 progress,
    // shaped by a smoothstep when applied
    int fadeMode;
    float fadeLevel = 1;
    // audio thread only: the seek crossfade. xfFrom = where the old position is (-1 = none), xfLevel = 0..1 progress
    long xfFrom = -1;
    float xfLevel;
    readonly float[] xfIn = new float[TrackDspChain.MaxBlockSize];
    readonly float[] xfOut = new float[TrackDspChain.MaxBlockSize];
    /// <summary>Set by <see cref="Play"/> (fade in) and <see cref="FadeOutAndPause"/>; <see cref="cancelPause"/> undoes a pause in flight.</summary>
    int pendingFadeIn, pendingPause, cancelPause;
    /// <summary>A fade-out stop asked for and not done yet (see <see cref="Stopping"/>).</summary>
    volatile bool stopping;
    int pendingBusReset;
    volatile Exception? lastRecordingError;
    long lastRecordingDroppedFrames;

    long endFrame;
    long nextBeat;
    float inL, inR, outL, outR;
    volatile bool inClip;

    public string Status { get; private set; } = Loc.T("engine.off");
    public string? DriverName => io?.Driver;
    public bool IsOpen => io != null;
    /// <summary>The driver's buffer size in samples (0 with no audio).</summary>
    public int BufferFrames => io?.FramesPerBuffer ?? 0;
    public int InputOffset { get; private set; }
    public int OutputOffset { get; private set; }
    /// <summary>Output channels of the open driver (the Configuración "Dispositivo" lists them in pairs).</summary>
    public int OutputChannels => io?.DriverOutputChannels ?? 0;
    /// <summary>The output side of the calibration key: each input + output pair keeps its own measurement.</summary>
    public string CalibrationOutputId => $"{InputOffset}:{OutputOffset}";
    public bool Playing => playing;
    /// <summary>
    /// Still <see cref="Playing"/> but fading out to a stop (<see cref="FadeOutAndPause"/>): the transport treats it
    /// as stopped, so pressing play in the meantime fades back in instead of asking to stop again.
    /// </summary>
    public bool Stopping => playing && stopping;
    public bool Recording => recording;
    /// <summary>
    /// Stopwatch time at which the take's first sample (timeline 0) was captured (timeline at the take start), 0 until then (still counting
    /// in). The camera's video counts its frames from it.
    /// </summary>
    public long RecordingStartTimestamp => Interlocked.Read(ref recStartTimestamp);
    public bool Calibrating => activeCalibration != null;
    public bool ClickOn { get; set; }

    /// <summary>
    /// Listen to the master in mono (the mixer's MONO): checks a doubled guitar or a stereo reverb for what cancels
    /// when it plays on one speaker. Only what you hear: the meters, the loudness and the export stay stereo. Folds in
    /// and out over <see cref="MonoFoldSamples"/>, so it never clicks.
    /// </summary>
    public bool MonoCheck { get; set; }

    /// <summary>
    /// The mixer's FX switch (UI thread): every track's and bus's slots go to the engine off, as their own power would
    /// (<see cref="ChannelDsp.Specs(IReadOnlyList{InsertSlot}, double, double, Func{InsertSlot, Vst.VstBridge?}?, bool)"/>).
    /// Only what you hear, like <see cref="MonoCheck"/>: the project and the export keep them as they are; not saved.
    /// </summary>
    public static bool FxBypass { get; set; }
    /// <summary>10 ms at 48 kHz.</summary>
    const int MonoFoldSamples = 480;
    // audio thread only: 0 = stereo, 1 = mono
    float monoLevel;

    /// <summary>Disk error encountered by the active or last recording session, if any.</summary>
    public Exception? RecordingDiskError
    {
        get
        {
            foreach (var t in taps)
                if (t.Recorder?.DiskError is { } e) return e;
            return lastRecordingError;
        }
    }

    /// <summary>Human-readable message for <see cref="RecordingDiskError"/>, or <c>null</c> if none.</summary>
    public string? RecordingError => RecordingDiskError?.Message;

    /// <summary>Samples dropped because the recording ring buffer overflowed (or could not be flushed on disk error).</summary>
    public long RecordingDroppedFrames => recording ? taps.Sum(t => t.Recorder?.DroppedFrames ?? 0) : Interlocked.Read(ref lastRecordingDroppedFrames);

    /// <summary>Driver inputs the stream opened (all of them), for the armed tracks' input selector.</summary>
    public int InputChannels => io?.InputChannels ?? 0;

    /// <summary>The armed tracks' inputs, in the order <see cref="SetInputs"/> got them (meters read their peaks).</summary>
    public IReadOnlyList<InputTap> Taps => taps;

    /// <summary>The MIDI inputs whose keys the engine plays (and records); null = none.</summary>
    public MidiInputs? MidiInputs { get => midiInputs; set => midiInputs = value; }

    /// <summary>
    /// The tracks that hear the keyboard (the selected MIDI track and the armed ones), each with the input it listens
    /// to and, while recording, where its notes go. A track left out lets go of what it held at the next block.
    /// </summary>
    public void SetMidiListeners(MidiListener[] list) => midiListeners = list;

    public bool Loop
    {
        get => loop;
        set => loop = value;
    }

    /// <summary>
    /// The A–B range the loop repeats (<see cref="ProjectMarkers.SetLoop"/>); null = the whole song. Crossing B while
    /// the loop is on turns to A on the exact sample (<see cref="LoopSplice"/>), so A comes in whole and the effects keep their state; a playhead already past B
    /// plays on to the end and loops back to A from there.
    /// </summary>
    public void SetLoopRange((double Start, double End)? range) =>
        loopRange = range is { } r ? new LoopFrames((long)(r.Start * SampleRate), (long)(r.End * SampleRate)) : null;

    /// <summary>
    /// Length of the fade when playback starts and stops (play, stop, the playhead drag), in ms. The seek crossfade
    /// stays at <see cref="TransportFadeSec"/>: longer, a jump would blur the two positions together.
    /// </summary>
    public double TransportFadeMs
    {
        get => 1 / (rampStep * SampleRate) * 1000;
        set => rampStep = (float)(1 / (Math.Clamp(double.IsFinite(value) ? value : DefaultTransportFadeMs, MinTransportFadeMs, MaxTransportFadeMs) / 1000 * SampleRate));
    }

    public bool SoftwareMonitor
    {
        get => softwareMonitor;
        set => softwareMonitor = value;
    }

    public InputProcessor Input => inputProcessor;

    public InputChannelMode InputChannelMode
    {
        get => inputProcessor.ChannelMode;
        set => inputProcessor.ChannelMode = value;
    }

    public double InputGainDb
    {
        get => inputProcessor.InputGainDb;
        set
        {
            inputProcessor.InputGainDb = value;
            foreach (var t in taps) t.GainDb = value;
        }
    }

    public ClickSound ClickSound
    {
        get => clickSound;
        set => SetClickSound(value, clickVolumeDb);
    }

    public double ClickVolumeDb
    {
        get => clickVolumeDb;
        set => SetClickSound(clickSound, value);
    }

    public double Bpm
    {
        get => bpm;
        set
        {
            bpm = Math.Clamp(value, 20, 300);
            // the buses' delays follow the tempo
            ApplyBusSlots();
        }
    }

    public MixGraphBus Buses => buses;
    /// <summary>The tuner's ring: the first armed input, or input 1-2 when none is armed.</summary>
    public PitchTap Pitch { get; } = new(SampleRate);
    /// <summary>The master's loudness (after the limiter, without the click), measured while it plays.</summary>
    public LoudnessMeter Loudness { get; } = new(SampleRate);
    /// <summary>True peak of the master out (after the limiter, without the click), for the Volumen panel.</summary>
    public TruePeak TruePeak { get; } = new();
    public MasterLimiter Limiter => buses.Limiter;
    public MasterChain Master => buses.Master;

    public double LoadPercent { get; private set; }

    // callback timing for AudioGapLog: written by the audio thread, read and reset by the UI
    long lastCallback, worstGap, worstRun;
    /// <summary>Where the block's time went: when the input and the keyboard were done, and when the tracks (with their plugins) were.</summary>
    long inputDone, tracksDone;
    /// <summary>The same two stretches of the longest callback (<see cref="TakeTimings"/>).</summary>
    long worstInput, worstTracks;
    int lastBlock;
    int seeks;

    /// <summary>
    /// No callback for this long with the driver open means it stopped (the interface was unplugged or its driver
    /// hung): ASIO calls back all the time while open, also with the transport stopped.
    /// </summary>
    static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The open driver stopped calling back (see <see cref="StallTimeout"/>). False until its first callback.</summary>
    public bool Stalled => io != null && Volatile.Read(ref lastCallback) is var t && t != 0 && Stopwatch.GetElapsedTime(t) > StallTimeout;

    /// <summary>Longest wait between callbacks and longest callback since the last call (ms), the part of that callback spent on the input and on the tracks, the block length, and seeks made.</summary>
    public (double GapMs, double RunMs, double InputMs, double TracksMs, double BlockMs, int Seeks) TakeTimings()
    {
        double ms = 1000.0 / Stopwatch.Frequency;
        var res = (Interlocked.Exchange(ref worstGap, 0) * ms, Interlocked.Exchange(ref worstRun, 0) * ms,
            Interlocked.Read(ref worstInput) * ms, Interlocked.Read(ref worstTracks) * ms,
            1000.0 * lastBlock / SampleRate, Interlocked.Exchange(ref seeks, 0));
        return res;
    }

    /// <summary>Current timeline position in seconds (held at the take's start during count-in, like <c>App.tsx:1068</c> holds it at 0).</summary>
    public double PositionSec => Math.Max(recording ? Interlocked.Read(ref recStartFrame) : 0,
        Interlocked.Read(ref pendingSeek) is var seek && seek != NoSeek ? seek : Interlocked.Read(ref position)) / (double)SampleRate;

    /// <summary>
    /// What the eye follows: <see cref="PositionSec"/> as late as the master (<see cref="PdcFrames"/>) while it plays, so the
    /// picture, the playhead and the clock go with the sound; stopped, or after a seek, the same as it. Only for what is
    /// shown: recording, edits and the native lanes go by <see cref="PositionSec"/>, where the tracks are being read.
    /// </summary>
    public double HeardSec
    {
        get
        {
            if (!rolling || !playing || Interlocked.Read(ref pendingSeek) != NoSeek) return PositionSec;
            return Math.Max(recording ? Interlocked.Read(ref recStartFrame) : 0, heard.At(Interlocked.Read(ref position), pdcFrames)) / (double)SampleRate;
        }
    }

    /// <summary>Where the take being recorded starts in the timeline.</summary>
    public double RecordStartSec => Interlocked.Read(ref recStartFrame) / (double)SampleRate;

    /// <summary>During a recording count-in, returns the 1-based beat index (1..countInBeats); otherwise <c>null</c>.</summary>
    public int? CountdownBeat
    {
        get
        {
            if (!recording) return null;
            long pos = Interlocked.Read(ref position);
            long recStart = Interlocked.Read(ref recStartFrame);
            if (pos >= recStart || recCountInBeats <= 0) return null;
            double beatLen = SampleRate * 60.0 / bpm;
            long startFrame = recStart - (long)Math.Round(recCountInBeats * beatLen);
            int beat = (int)((pos - startFrame) / beatLen) + 1;
            return Math.Clamp(beat, 1, recCountInBeats);
        }
    }

    public bool IsCountingIn => CountdownBeat != null;

    /// <summary>The open driver asked to be reopened (new buffer size from its panel). Raised on the driver's thread.</summary>
    public event Action? DriverResetRequested;

    /// <summary>Opens the driver's control panel; with no driver open, the one <see cref="Open"/> would pick.</summary>
    public void ShowDriverPanel()
    {
        if (io != null) io.ShowControlPanel();
        else AsioDuplex.ShowControlPanel(AsioDuplex.PickDriver(null));
    }

    public void Open(string? wantedDriver = null, int inputOffset = 0, int outputOffset = 0)
    {
        try
        {
            io?.Dispose();
            io = null;
            Volatile.Write(ref lastCallback, 0); // the old driver's last callback must not count as a stall
            string driver = AsioDuplex.PickDriver(wantedDriver);
            io = new AsioDuplex(driver, SampleRate, inputOffset, 0, outputOffset, Process);
            io.ResetRequested += () => DriverResetRequested?.Invoke();
            InputOffset = inputOffset;
            OutputOffset = outputOffset;
            io.Start();
            for (int i = 0; i < 100 && io.FramesPerBuffer == 0; i++) Thread.Sleep(10);
            // a VST3 effect is heard one driver block late
            if (io.FramesPerBuffer > 0) VstInsert.LiveBlock = io.FramesPerBuffer;
            double ms = 1000.0 * io.FramesPerBuffer / SampleRate;
            Status = Loc.T("engine.status", driver, SampleRate / 1000, io.FramesPerBuffer, $"{ms:0.0}");

            // like the web, a saved measurement applies even when stale (Configuración warns about it);
            // a pair with nothing saved goes back to the driver's estimate
            calibratedLatencyMs = LatencyCalibration.ReadCalibration(driver, CalibrationOutputId)?.LatencyMs;
        }
        catch (Exception e)
        {
            io?.Dispose();
            io = null;
            Status = Loc.T("engine.noAudio", e.Message);
        }
    }

    /// <summary>
    /// The folders and buses (native only): each keeps its <see cref="BusDspChain"/> by id (its slots keep their state, a
    /// new list of slots fades over), the audio thread gets them as one array in the order they run
    /// (<see cref="ChannelDsp.Nodes"/>), and every node and track points its output and sends at them. A new node is
    /// routed before the audio thread sees it, a known one right after, so a routing edit while playing can lose one block
    /// of what moved, never leave sound in a buffer nobody clears. UI thread, after any edit of folders, buses, outputs,
    /// sends or tracks.
    /// </summary>
    public void SetBuses(Project p, IReadOnlyList<LiveTrack> list)
    {
        busList = p.Settings.Buses;
        folderList = p.Folders;
        var ids = ChannelDsp.Nodes(p);
        var next = new BusDspChain[ids.Count];
        var known = new bool[ids.Count];
        bool same = next.Length == nodes.Length;
        var map = new Dictionary<string, BusDspChain>(ids.Count);
        var automated = new List<(BusDspChain, InsertAutomator)>();
        for (int i = 0; i < next.Length; i++)
        {
            known[i] = nodeById.TryGetValue(ids[i], out var node);
            node ??= new BusDspChain(SampleRate) { Keys = LiveKeys.For };
            // a new node isn't on the audio thread yet: it starts with its slots, no fade
            if (p.Settings.Buses.Find(b => b.Id == ids[i]) is { } bus)
            {
                node.Volume = bus.Volume;
                node.Pan = bus.Pan;
                node.Muted = bus.Muted;
                node.SetInserts(ChannelDsp.BusSpecs(bus, bpm, LiveVst.Of(bus.Id), FxBypass), fade: known[i]);
            }
            else if (p.Folders.Find(f => f.Id == ids[i]) is { } folder)
            {
                // a folder is a channel too (#134): its fader, pan, mute and slots over its tracks' sum
                node.Volume = folder.Volume;
                node.Pan = folder.Pan;
                node.Muted = folder.Muted;
                node.Automation = Automations.Compile(folder);
                var lanes = InsertAutomator.Create(folder, ChannelDsp.BusRmsDb, bpm, LiveVst.Of(folder.Id), FxBypass);
                if (lanes != null) automated.Add((node, lanes));
                node.SetInserts(lanes?.At(nodeAutoSec, force: true) ?? ChannelDsp.FolderSpecs(folder, bpm, LiveVst.Of(folder.Id), FxBypass), fade: known[i]);
            }
            next[i] = node;
            map[ids[i]] = node;
            // && and not &=: with more nodes than before, nodes[i] would be past its end
            same = same && nodes[i] == node;
        }
        IRouted Routed(string id) => (IRouted?)p.Folders.Find(f => f.Id == id) ?? p.Settings.Buses.Find(b => b.Id == id)!;
        void Route(int i)
        {
            var r = Routed(ids[i]);
            next[i].Out = r.Output != null ? map.GetValueOrDefault(r.Output) : null;
            next[i].Sends = SendsTo(map, r, next[i].Sends, r as IAutomated);
        }
        for (int i = 0; i < next.Length; i++) if (!known[i]) Route(i);
        if (!same) nodes = next;
        for (int i = 0; i < next.Length; i++) if (known[i]) Route(i);
        nodeById.Clear();
        foreach (var (id, node) in map) nodeById[id] = node;
        nodeAutomators = [.. automated];
        foreach (var t in list)
        {
            t.Out = ChannelDsp.TrackOutput(t.Track) is { } to ? map.GetValueOrDefault(to) : null;
            t.FolderSolo = ChannelDsp.FolderSoloed(p, t.Track);
            t.FolderMuted = TrackFolders.Of(p, t.Track) is { Muted: true };
            t.Nodes = nodeById;
            t.ApplySends();
        }
    }

    /// <summary>
    /// What <paramref name="r"/> sends, as the audio thread reads it: the sends that are on, above 0 and whose bus the
    /// engine has. Each takes the PDC delay of the send to the same bus in <paramref name="old"/>, so it keeps what it holds.
    /// </summary>
    public static TrackSend[] SendsTo(IReadOnlyDictionary<string, BusDspChain>? map, IRouted r, TrackSend[]? old = null, IAutomated? track = null)
    {
        bool Sounds(Send s) => s.On && (s.Level > 0 || track != null && Automations.SendCurve(track, s.BusId) != null);
        int n = 0;
        foreach (var s in r.Sends)
            if (Sounds(s) && map != null && map.ContainsKey(s.BusId)) n++;
        if (n == 0) return [];
        var next = new TrackSend[n];
        int k = 0;
        foreach (var s in r.Sends)
            if (Sounds(s) && map!.TryGetValue(s.BusId, out var to))
                next[k++] = new TrackSend(to, -1, (float)Math.Clamp(s.Level, 0, 1), s.Pre, old?.FirstOrDefault(o => o.To == to)?.Delay, track == null ? null : Automations.SendCurve(track, s.BusId));
        return next;
    }

    /// <summary>
    /// UI thread, every tick: the folders' lanes on their native effects at the playhead (their fader's, pan's, mute's
    /// and sends' are read on the audio thread), like <c>LiveTrack.TickAutomation</c>. Allocates only when a lane moved.
    /// </summary>
    public void TickNodeAutomation(double sec)
    {
        nodeAutoSec = sec;
        var list = nodeAutomators;
        for (int i = 0; i < list.Length; i++)
            if (list[i].Lanes.At(sec) is { } specs) list[i].Node.SetInserts(specs);
    }

    /// <summary>The slots of every bus and folder again (the tempo changed: their delays follow it; a VST3 effect came up or went away; the FX switch).</summary>
    public void ApplyBusSlots()
    {
        foreach (var b in busList ?? [])
            if (nodeById.TryGetValue(b.Id, out var node)) node.SetInserts(ChannelDsp.BusSpecs(b, bpm, LiveVst.Of(b.Id), FxBypass));
        foreach (var f in folderList ?? [])
            if (!f.Separator && nodeById.TryGetValue(f.Id, out var node))
            {
                var lanes = InsertAutomator.Create(f, ChannelDsp.BusRmsDb, bpm, LiveVst.Of(f.Id), FxBypass);
                node.SetInserts(lanes?.At(nodeAutoSec, force: true) ?? ChannelDsp.FolderSpecs(f, bpm, LiveVst.Of(f.Id), FxBypass));
                for (int i = 0; i < nodeAutomators.Length; i++)
                    if (nodeAutomators[i].Node == node) nodeAutomators[i] = (node, lanes ?? nodeAutomators[i].Lanes);
            }
    }

    /// <summary>
    /// The plugin delay compensation (PDC), live: when a track's or a bus's slots put out their sound late (a VST3 effect
    /// is heard one block plus its own latency late), every other path into the same node and into the master waits as
    /// much (<see cref="MixLatency"/>), and so do the click, a recorded take and what is shown (<see cref="PdcFrames"/>,
    /// <see cref="HeardSec"/>). UI thread, on
    /// every tick: it only works it out again when a latency, an output or a list of sends changed.
    /// </summary>
    public void UpdateLatency()
    {
        var list = tracks;
        var ns = nodes;
        var h = new HashCode();
        h.Add(list);
        h.Add(ns);
        foreach (var t in list)
        {
            h.Add(t.Chain.LatencySamples);
            h.Add(t.Chain.KeyIds);
            h.Add(t.Out);
            h.Add(t.Chain.Sends);
        }
        foreach (var n in ns)
        {
            h.Add(n.LatencySamples);
            h.Add(n.Out);
            h.Add(n.Sends);
        }
        h.Add(buses.Slots.LatencySamples);
        // a take keeps the delay it was placed with, even if a plugin goes away under it
        int floor = recording ? Math.Max(0, recPdcFrames - buses.Slots.LatencySamples) : 0;
        h.Add(floor);
        int key = h.ToHashCode();
        if (key == latencyKey) return;
        latencyKey = key;
        var ids = list.Select(t => t.Track.Id).ToList();
        trackOrder = [.. Sidechains.Levels(list.Length, k => list[k].Chain.KeyIds.Select(id => ids.IndexOf(id))).SelectMany(g => g)];
        int Index(BusDspChain? b) => b == null ? -1 : Array.IndexOf(ns, b);
        var plan = MixLatency.Compute(
            [.. list.Select(t => new MixLatency.Source(t.Chain.LatencySamples, Index(t.Out), [.. t.Chain.Sends.Select(s => Index(s.To))]))],
            [.. ns.Select(n => new MixLatency.Source(n.LatencySamples, Index(n.Out), [.. n.Sends.Select(s => Index(s.To))]))], floor);
        for (int k = 0; k < list.Length; k++)
        {
            list[k].Chain.OutDelay.Set(plan.TrackOut[k]);
            var sends = list[k].Chain.Sends;
            for (int i = 0; i < sends.Length; i++) sends[i].Delay.Set(plan.TrackSends[k][i]);
        }
        for (int j = 0; j < ns.Length; j++)
        {
            ns[j].OutDelay.Set(plan.NodeOut[j]);
            ns[j].InputLatency = plan.NodeArrive[j];
            var sends = ns[j].Sends;
            for (int i = 0; i < sends.Length; i++) sends[i].Delay.Set(plan.NodeSends[j][i]);
        }
        masterArrive = plan.Master;
        pdcFrames = plan.Master + buses.Slots.LatencySamples;
    }

    /// <summary>How late the whole mix is heard because of the VST3 effects (frames); 0 without them.</summary>
    public int PdcFrames => pdcFrames;

    /// <summary>A folder's or bus's node (for its strip's meter), or null.</summary>
    public BusDspChain? Node(string id) => nodeById.GetValueOrDefault(id);

    /// <summary>One of the master's slots by its id, for what the window shows live.</summary>
    public ITrackInsert? MasterInsert(string id) => buses.Slots.Find(id);

    public void SetTracks(IEnumerable<LiveTrack> list, ProjectSettings? settings = null)
    {
        playing = false;
        var arr = list.ToArray();
        trackOrder = [];
        tracks = arr;
        LiveKeys.Attach(arr);
        if (settings != null) SetSettings(settings, glideDelay: false);
        Interlocked.Exchange(ref pendingTrackReset, 1);
        double maxEnd = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            if (arr[i].EndSec > maxEnd) maxEnd = arr[i].EndSec;
        }
        Interlocked.Exchange(ref endFrame, (long)(maxEnd * SampleRate));
        Interlocked.Exchange(ref position, 0);
        playStartSec = 0;
    }

    /// <summary>
    /// Swaps in the track list after a timeline edit while it keeps playing: no stop, the playhead stays and
    /// neither the tracks' chains nor the buses are reset (reused tracks keep their compressor and tails).
    /// </summary>
    public void UpdateTracks(IReadOnlyList<LiveTrack> list)
    {
        tracks = list.ToArray();
        RefreshClipTiming();
    }

    /// <summary>
    /// Recomputes <see cref="LengthSec"/> (<c>endFrame</c>) from the current <see cref="LiveTrack"/> list
    /// without stopping playback, moving the playhead or resetting DSP chains/buses.
    /// </summary>
    public void RefreshClipTiming()
    {
        var arr = tracks;
        double maxEnd = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            if (arr[i].EndSec > maxEnd) maxEnd = arr[i].EndSec;
        }
        Interlocked.Exchange(ref endFrame, (long)(maxEnd * SampleRate));
    }

    public void SetSettings(ProjectSettings s, bool glideDelay = false)
    {
        bpm = Math.Clamp(s.Bpm, 20, 300);
        ApplyMaster(s.Master);
        var arr = tracks;
        for (int i = 0; i < arr.Length; i++)
            arr[i].SetProjectSettings(s);
    }

    /// <summary>The master's EQ, multiband and limiter, live: on and off crossfade (<see cref="MasterChain"/>).</summary>
    public void ApplyMaster(MasterSettings m)
    {
        buses.SetMaster(m.ToParams());
        buses.Slots.Set(ChannelDsp.MasterSpecs(m, bpm, FxBypass));
    }

    public double LengthSec => endFrame / (double)SampleRate;
    public double PlayStartSec => playStartSec;

    public void Play()
    {
        if (recording || activeCalibration != null) return;
        Interlocked.Exchange(ref pendingPause, 0);
        Interlocked.Exchange(ref cancelPause, 1);
        Interlocked.Exchange(ref pendingCut, 0);
        stopping = false;
        if (playing) return; // a fade-out pause still in flight: it fades back in where it is
        rolling = false;
        if (endFrame > 0 && Interlocked.Read(ref position) >= endFrame) Seek(loop && loopRange is { } lr ? lr.Start / (double)SampleRate : 0);
        long pos = Interlocked.Read(ref position);
        Interlocked.CompareExchange(ref pendingSeek, NoSeek, pos); // a seek made while stopped already moved it
        playStartSec = Math.Max(0.0, pos / (double)SampleRate);
        double beatLen = SampleRate * 60.0 / bpm;
        nextBeat = (long)Math.Ceiling(pos / beatLen - 1e-6);
        Interlocked.Exchange(ref pendingQuietReset, 1);
        Interlocked.Exchange(ref pendingFadeIn, 1);
        playing = true;
    }

    /// <summary>
    /// Pauses like <see cref="Pause"/> but fades out over <see cref="TransportFadeMs"/> first, like a DAW's stop:
    /// the play / stop key, ■ and the playhead drag. The tracks fade before the return buses, so reverb and delay
    /// tails ring on. <see cref="Playing"/> stays true until the fade ends (<see cref="Stopping"/> tells it apart);
    /// seeks made meanwhile apply once it stops.
    /// </summary>
    public void FadeOutAndPause()
    {
        if (!playing || recording) return;
        stopping = true;
        Interlocked.Exchange(ref pendingPause, 1);
    }

    /// <summary>
    /// The panic (asked by the user): stops like <see cref="FadeOutAndPause"/> and then silences everything that rings
    /// on: slot tails on tracks, folders, buses and the master, held notes and the instruments' voices. The block that
    /// takes it fades out, so the cut doesn't click. Not while recording.
    /// </summary>
    public void CutAudio()
    {
        if (recording) return;
        FadeOutAndPause();
        Interlocked.Exchange(ref pendingCut, 1);
    }

    /// <summary>
    /// Stops at once, keeping the playhead where it is. Only for what must not hear another block: starting a
    /// recording, an export, swapping or closing the project. The transport uses <see cref="FadeOutAndPause"/>.
    /// </summary>
    public void Stop()
    {
        playing = false;
        stopping = false;
    }

    /// <summary>Same as <see cref="Stop"/>: an immediate pause at the current playhead position.</summary>
    public void Pause() => Stop();

    /// <summary>
    /// Port of <c>stopTransport</c> (<c>App.tsx:1740</c>): if playing, fades out and returns to where playback
    /// started; if already stopped (or fading out to a stop), rewinds to 0. The seek waits for the fade.
    /// </summary>
    public void StopTransport()
    {
        if (recording) return;
        bool wasPlaying = playing && !stopping;
        double back = playStartSec;
        FadeOutAndPause();
        Seek(wasPlaying ? back : 0);
    }

    /// <summary>
    /// Moves the playhead. While playing, the jump is a request the audio thread takes at the next block: writing the
    /// position from here raced the callback (which writes it back at the end of each block, losing the seek but
    /// still resetting the effects mid-note), and the cut itself clicked. There, like Logic or Studio Pro, the clips
    /// crossfade from the old position to the new one over <see cref="TransportFadeSec"/> (equal power, so the level
    /// never dips: fading out and then in left a hiccup), before the insert chain, so the effects see one continuous
    /// signal and keep their state. A seek during a crossfade waits for it to end (at most that long). Stopped, it
    /// moves at once (and the request keeps the order with one still pending). <see cref="PositionSec"/> shows the
    /// target right away, so the playhead never steps back.
    /// </summary>
    public void Seek(double sec)
    {
        if (recording) return;
        double maxSec = LengthSec > 0 ? Math.Max(LengthSec, sec) : Math.Max(0, sec);
        long frame = (long)(Math.Clamp(sec, 0, maxSec) * SampleRate);
        playStartSec = frame / (double)SampleRate;
        Interlocked.Increment(ref seeks);
        Interlocked.Exchange(ref pendingSeek, frame);
        if (playing) return;
        Interlocked.Exchange(ref position, frame);
        nextBeat = BeatAt(frame);
    }

    long BeatAt(long frame) => (long)Math.Ceiling(frame / (SampleRate * 60.0 / bpm) - 1e-6);

    /// <summary>Equal-power weights for the next <paramref name="n"/> samples of the seek crossfade (cos out, sin in).</summary>
    static void CrossfadeWeights(ref float level, int n, float[] wIn, float[] wOut)
    {
        float x = level;
        for (int i = 0; i < n; i++)
        {
            x = Math.Min(1f, x + FadeStep);
            wIn[i] = MathF.Sin(x * MathF.PI / 2);
            wOut[i] = MathF.Cos(x * MathF.PI / 2);
        }
        level = x;
    }

    /// <summary>
    /// Moves <paramref name="level"/> one step per sample toward 1 (fade in) or 0 and scales the block's master and every
    /// node's input by its smoothstep, so the fade starts and ends without a corner; the buses run after it, so their
    /// tails ring on. Once at 0 the rest is silence.
    /// </summary>
    /// <summary>Folds the master toward mono by <paramref name="level"/>, moving it toward <paramref name="on"/> sample by sample.</summary>
    static void FoldMono(ref float level, bool on, Span<float> l, Span<float> r)
    {
        float target = on ? 1 : 0, step = 1f / MonoFoldSamples;
        if (level == 0 && !on) return;
        for (int i = 0; i < l.Length; i++)
        {
            if (level != target) level = on ? Math.Min(1, level + step) : Math.Max(0, level - step);
            float mid = (l[i] + r[i]) * 0.5f;
            l[i] += (mid - l[i]) * level;
            r[i] += (mid - r[i]) * level;
        }
    }

    /// <summary>The transport's fade for this block into <paramref name="gain"/>. Each track puts it on what it plays, before its slots, so their tails ring on after a stop.</summary>
    static void TransportRamp(ref float level, bool fadeIn, float rate, int n, float[] gain)
    {
        float x = level, step = fadeIn ? rate : -rate;
        for (int i = 0; i < n; i++)
        {
            x = Math.Clamp(x + step, 0f, 1f);
            gain[i] = x * x * (3 - 2 * x);
        }
        level = x;
    }

    // --- Recording (§5.8) ------------------------------------------------------------------------

    /// <summary>
    /// The armed tracks' inputs (null = the main input): metered and, with software monitoring, heard, each
    /// input once. A tap with the same route as before is kept. Ignored while recording.
    /// </summary>
    public void SetInputs(IReadOnlyList<InputRoute?> routes)
    {
        if (recording) return;
        var old = taps;
        var next = new InputTap[routes.Count];
        for (int i = 0; i < routes.Count; i++)
        {
            bool first = true;
            for (int k = 0; k < i; k++) first &= routes[k] != routes[i];
            var keep = Array.Find(old, t => t.Route == routes[i] && t.Monitored == first && Array.IndexOf(next, t) < 0);
            next[i] = keep ?? new InputTap(routes[i], InputGainDb) { Monitored = first };
        }
        taps = next;
    }

    /// <summary>
    /// Starts recording one take per input on the shared ASIO sample clock, each to its WAV.
    /// When <paramref name="useCountIn"/> is true, plays <paramref name="countInBeats"/> beats of count-in
    /// before <paramref name="startSec"/> (the playhead; 0 like the web); there, backing tracks, click, and disk
    /// capture start on the exact same sample. The count-in counts from the start, the click after it follows the grid.
    /// </summary>
    public void StartRecording(IReadOnlyList<(InputRoute? Route, string WavPath)> takes, bool useCountIn = true, int countInBeats = 4, double startSec = 0,
        bool notesOnly = false)
    {
        if (io == null) throw new InvalidOperationException(Loc.T("engine.openFirst"));
        if (recording || activeCalibration != null) return;
        // with only MIDI tracks armed there is no take: the keyboard's notes are the recording
        if (takes.Count == 0 && !notesOnly) throw new ArgumentException(Loc.T("engine.nothingToRecord"), nameof(takes));
        int outSamples = io.ReportedOutputLatency > 0 ? io.ReportedOutputLatency : io.FramesPerBuffer;

        SetInputs(takes.Select(t => t.Route).ToList());
        var recorders = new List<TakeRecorder>();
        try
        {
            foreach (var t in takes) recorders.Add(new TakeRecorder(t.WavPath, SampleRate));
        }
        catch
        {
            foreach (var r in recorders) r.Finish(discard: true);
            throw;
        }

        // recording over the playback: the block under way would put its position (or its stop) on top of the take's
        bool wasPlaying = playing;
        playing = false;
        if (wasPlaying) WaitForBlock();
        // a take has one offset: the slots stop being able to move the mix, and the delay they will keep is known now,
        // before the first sample. What you hear while you play comes this much late: the take and the keyboard's notes
        // go back by as much
        VstInsert.Steady = true;
        UpdateLatency();
        recPdcFrames = pdcFrames;
        Interlocked.Exchange(ref midiLatencyFrames, outSamples + buses.Limiter.Latency + recPdcFrames);
        lastRecordingError = null;
        Interlocked.Exchange(ref lastRecordingDroppedFrames, 0);
        Interlocked.Exchange(ref pendingBusReset, 1);
        Interlocked.Exchange(ref pendingTrackReset, 1);

        int beats = useCountIn ? Math.Max(0, countInBeats) : 0;
        recCountInBeats = beats;
        recUsedCountIn = beats > 0;
        recUsedClick = ClickOn;
        Interlocked.Exchange(ref recStartTimestamp, 0);

        double beatLen = SampleRate * 60.0 / bpm;
        long recStart = (long)(Math.Max(0, startSec) * SampleRate);
        long startFrame = recStart - (beats > 0 ? (long)Math.Round(beats * beatLen) : 0);
        recFirstBeat = (long)Math.Ceiling(recStart / beatLen - 1e-6);
        nextBeat = beats > 0 ? -beats : recFirstBeat;
        Interlocked.Exchange(ref recStartFrame, recStart);
        click = null;
        clickPos = 0;

        var cur = taps;
        for (int i = 0; i < cur.Length; i++) cur[i].Recorder = recorders[i];
        Interlocked.Exchange(ref pendingSeek, NoSeek);
        Interlocked.Exchange(ref position, startFrame);
        playStartSec = recStart / (double)SampleRate;
        rolling = false;
        recording = true;
        playing = true;
    }

    /// <summary>The longest <see cref="WaitForBlock"/> waits: a block of 8192 frames, and a driver that stopped calling.</summary>
    const int BlockWaitMs = 200;

    /// <summary>
    /// Waits for the next block to begin, so the one that may be under way has ended: after clearing <c>playing</c>, what is
    /// written next isn't overwritten by that block. Never on the audio thread.
    /// </summary>
    void WaitForBlock()
    {
        long seen = Volatile.Read(ref lastCallback);
        if (seen == 0) return;
        var waited = Stopwatch.StartNew();
        while (Volatile.Read(ref lastCallback) == seen && waited.ElapsedMilliseconds < BlockWaitMs) Thread.Sleep(1);
    }

    /// <summary>Cancels the recording while still in the count-in (<c>Esc</c> in <c>App.tsx</c>).</summary>
    public bool CancelCountIn()
    {
        if (!recording || !IsCountingIn) return false;
        StopRecording(discard: true);
        return true;
    }

    /// <summary>
    /// Stops the active recording, flushes and closes the WAV files, and returns each take's metadata in the
    /// order they started (null for one discarded or of 0.3 s or less); empty when nothing was recording.
    /// </summary>
    public RecordedTakeResult?[] StopRecording(
        bool discard = false,
        double defaultOverdubOffsetMs = 0,
        bool isFirstTake = false)
    {
        var cur = taps;
        var recs = cur.Select(t => t.Recorder).ToArray();
        if (!recording && recs.All(r => r == null)) return [];
        recording = false;
        playing = false;
        VstInsert.Steady = false;
        click = null;
        // the block under way writes its position when it ends: that one must not win
        WaitForBlock();
        // back to where the take started, like the web goes back to 0
        Interlocked.Exchange(ref position, Interlocked.Read(ref recStartFrame));
        foreach (var t in cur) t.Recorder = null;

        double latencyMs = GetLatencyInfo().AppliedMs + recPdcFrames * 1000.0 / SampleRate;
        var results = new RecordedTakeResult?[recs.Length];
        Exception? error = null;
        long dropped = 0;
        for (int i = 0; i < recs.Length; i++)
        {
            if (recs[i] is not { } rec) continue;
            results[i] = rec.Finish(
                discard,
                latencyMs,
                defaultOverdubOffsetMs,
                isFirstTake,
                recUsedCountIn,
                recUsedClick || ClickOn);
            error ??= rec.DiskError;
            dropped += rec.DroppedFrames;
        }
        lastRecordingError = error;
        Interlocked.Exchange(ref lastRecordingDroppedFrames, dropped);
        return results;
    }

    // --- Latency & Calibration (§5.9) ------------------------------------------------------------

    public LatencyInfo GetLatencyInfo()
    {
        int outSamples = io != null ? (io.ReportedOutputLatency > 0 ? io.ReportedOutputLatency : io.FramesPerBuffer) : (int)(0.012 * SampleRate);
        int inSamples = io != null ? Math.Max(io.FramesPerBuffer, 64) : (int)(0.010 * SampleRate);
        double outputMs = Math.Round(((outSamples + buses.Limiter.Latency) / (double)SampleRate) * 1000.0, 1);
        double inputMs = Math.Round((inSamples / (double)SampleRate) * 1000.0, 1);
        double totalMs = Math.Round(outputMs + inputMs, 1);
        double applied = calibratedLatencyMs ?? totalMs;
        return new LatencyInfo(
            outputMs,
            inputMs,
            totalMs,
            SampleRate,
            SampleRate,
            calibratedLatencyMs,
            applied);
    }

    public double GetEstimatedRoundtripLatencyMs() => GetLatencyInfo().TotalMs;

    public void SetCalibratedLatency(double? ms) => calibratedLatencyMs = ms;

    /// <summary>
    /// Measures the real round-trip latency on the open ASIO device (<c>loopback</c> or <c>tap</c>).
    /// Port of <c>StudioAudioEngine.measureLatency</c> (<c>audioEngine.ts:522-606</c>).
    /// </summary>
    public async Task<CalibrationResult?> MeasureLatencyAsync(
        CalibrationMethod method,
        Action<CalibrationProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        if (io == null) throw new InvalidOperationException(Loc.T("engine.openFirst"));
        if (recording) throw new InvalidOperationException(Loc.T("engine.cantCalibrate"));

        playing = false;
        var session = new CalibrationSession(method, SampleRate, buses.Limiter.Latency, clickSound);
        activeCalibration = session;
        try
        {
            while (!session.IsFinished)
            {
                if (ct.IsCancellationRequested) return null;
                if (session.GetProgress() is { } p) onProgress?.Invoke(p);
                await Task.Delay(30, ct).ConfigureAwait(false);
            }
            return session.Analyze();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            activeCalibration = null;
        }
    }

    // --- Meters & Callback -----------------------------------------------------------------------

    /// <summary>The RMS of what goes out (the same signal as the master's peak), for the mixer's master meter.</summary>
    public RmsTap OutRms { get; } = new();
    /// <summary>The master's correlation (after the limiter, before MONO and the click), for the mixer's COR.</summary>
    public CorrelationTap Correlation { get; } = new();

    /// <summary>Peak levels since the last call (input 1-2 after pre-amp gain + soft limit, master out).</summary>
    public (float InL, float InR, float OutL, float OutR) TakeMeters()
    {
        var m = (inL, inR, outL, outR);
        inL = inR = outL = outR = 0;
        return m;
    }

    public (float Left, float Right, bool Clip) TakeInputPeaks()
    {
        var res = (inL, inR, inClip);
        inL = inR = 0;
        inClip = false;
        return res;
    }


    void Process(float[][] input, float[][] output, int n)
    {
        long t0 = Stopwatch.GetTimestamp();
        if (lastCallback != 0 && t0 - lastCallback > Interlocked.Read(ref worstGap)) Interlocked.Exchange(ref worstGap, t0 - lastCallback);
        lastCallback = t0;
        lastBlock = n;
        if (n <= 0 || n > mixL.Length) return;

        var ch0 = input.Length > 0 ? input[0].AsSpan(0, n) : Span<float>.Empty;
        var ch1 = input.Length > 1 ? input[1].AsSpan(0, n) : ch0;
        var procL = inProcL.AsSpan(0, n);
        var procR = inProcR.AsSpan(0, n);

        if (ch0.Length == n)
        {
            inputProcessor.Process(ch0, ch1, procL, procR, out float maxL, out float maxR, out bool clip);
            if (maxL > inL) inL = maxL;
            if (maxR > inR) inR = maxR;
            if (clip) inClip = true;
        }
        else
        {
            procL.Clear();
            procR.Clear();
        }
        var curTaps = taps;
        for (int i = 0; i < curTaps.Length; i++) curTaps[i].Process(input, n, inProcL, inProcR);
        if (curTaps.Length > 0) Pitch.Write(curTaps[0].BlockL, curTaps[0].BlockR);
        else Pitch.Write(procL, procR);

        DispatchMidi(t0);
        RunArps(n);
        inputDone = Stopwatch.GetTimestamp();

        var cal = activeCalibration;
        if (cal != null)
        {
            cal.ProcessBlock(procL, procR, output[0].AsSpan(0, n), output[1].AsSpan(0, n), n);
            UpdateLoad(t0, n);
            return;
        }

        var curNodes = nodes;
        if (playing) cutLeft = 0; // played again in the middle of the cut's fade
        if (cutReset)
        {
            cutReset = false;
            buses.Reset();
            for (int i = 0; i < curNodes.Length; i++) curNodes[i].Reset();
            var curTracks = tracks;
            for (int i = 0; i < curTracks.Length; i++) curTracks[i].Silence();
        }
        if (Interlocked.Exchange(ref pendingBusReset, 0) != 0)
        {
            buses.Reset();
            for (int i = 0; i < curNodes.Length; i++) curNodes[i].Reset();
        }

        if (Interlocked.Exchange(ref pendingTrackReset, 0) != 0)
        {
            var curTracks = tracks;
            for (int i = 0; i < curTracks.Length; i++) curTracks[i].Chain.Reset();
        }
        if (Interlocked.Exchange(ref pendingQuietReset, 0) != 0)
        {
            // Play: a tail still ringing goes on under the song, the rest start clean
            var curTracks = tracks;
            for (int i = 0; i < curTracks.Length; i++)
                if (!curTracks[i].Ringing) curTracks[i].Chain.Reset();
        }

        var ml = mixL.AsSpan(0, n);
        var mr = mixR.AsSpan(0, n);
        ml.Clear();
        mr.Clear();
        for (int i = 0; i < curNodes.Length; i++) curNodes[i].Begin(n);
        // a source that doesn't play this block leaves silence in its key, never the last block
        var keyList = LiveKeys.All;
        for (int i = 0; i < keyList.Length; i++) keyList[i].Begin(0, n);
        var order = trackOrder;

        var preview = Interlocked.Exchange(ref previewClick, null);
        if (preview != null)
        {
            click = preview;
            clickPos = 0;
        }

        if (playing)
        {
            bool isRec = recording;
            if (!isRec)
            {
                if (fadeMode == -2 && Interlocked.Exchange(ref cancelPause, 0) != 0) fadeMode = 1; // played again meanwhile
                if (fadeMode != -2 && Interlocked.Exchange(ref pendingPause, 0) != 0)
                {
                    Interlocked.Exchange(ref cancelPause, 0);
                    fadeMode = -2;
                }
                else if (fadeMode != -2 && xfFrom < 0 && Interlocked.Read(ref pendingSeek) != NoSeek)
                {
                    // the position moves first and the request clears after, so PositionSec never shows the old one
                    long req = Interlocked.Read(ref pendingSeek);
                    heard.Start(req);
                    xfFrom = Interlocked.Exchange(ref position, req);
                    Interlocked.CompareExchange(ref pendingSeek, NoSeek, req);
                    xfLevel = 0;
                    nextBeat = BeatAt(req);
                }
                else if (fadeMode == 0 && Interlocked.Exchange(ref pendingFadeIn, 0) != 0)
                {
                    fadeLevel = 0;
                    fadeMode = 1;
                }
            }
            long pos = Interlocked.Read(ref position);
            if (!rolling)
            {
                heard.Start(pos);
                rolling = true;
            }

            // During recording count-in (pos < recStart), backing tracks and disk recording start only once f >= recStart.
            long recStart = isRec ? recStartFrame : 0;
            int playOffset = pos < recStart ? (int)Math.Min(n, recStart - pos) : 0;
            int playCount = n - playOffset;

            if (fadeMode != 0) TransportRamp(ref fadeLevel, fadeMode > 0, rampStep, n, rampGain);
            if (playCount > 0)
            {
                var fade = fadeMode != 0 ? rampGain.AsSpan(playOffset, playCount) : default;
                long trackPos = Math.Max(0, pos + playOffset);
                var subMl = ml.Slice(playOffset, playCount);
                var subMr = mr.Slice(playOffset, playCount);

                var list = tracks;
                bool anySolo = false;
                for (int i = 0; i < list.Length; i++)
                {
                    if (list[i].Soloed) { anySolo = true; break; }
                }

                bool xf = xfFrom >= 0 && !isRec;
                if (xf) CrossfadeWeights(ref xfLevel, playCount, xfIn, xfOut);
                // a seek's crossfade has the second read; a loop turning under one just turns (it lasts 10 ms)
                var lap = loopRange;
                long lapStart = lap?.Start ?? 0, lapEnd = lap?.End ?? endFrame;
                bool splice = !xf && !isRec && loop && LoopSplice.Weights(trackPos, playCount, lapStart, lapEnd, xfIn, xfOut);
                long second = xf ? xfFrom : splice ? trackPos - (lapEnd - lapStart) : LiveTrack.NoSecondRead;
                int turnAt = splice && trackPos + playCount >= lapEnd ? (int)(lapEnd - trackPos) : -1;
                for (int j = 0; j < list.Length; j++)
                    list[order.Length == list.Length ? order[j] : j].MixInto(trackPos, playCount, anySolo, subMl, subMr, playOffset, second, xfIn.AsSpan(0, playCount), xfOut.AsSpan(0, playCount), turnAt, lapStart, loop && !isRec ? lapEnd : 0, fade);
                if (xf)
                {
                    xfFrom += playCount;
                    if (xfLevel >= 1) xfFrom = -1;
                }

                if (isRec)
                {
                    // the block's samples were captured over the n/SR seconds before this callback
                    if (Interlocked.Read(ref recStartTimestamp) == 0)
                        Interlocked.Exchange(ref recStartTimestamp, Math.Max(1, t0 - (long)((n - playOffset) * (double)Stopwatch.Frequency / SampleRate)));
                    recUsedClick |= ClickOn;
                    for (int i = 0; i < curTaps.Length; i++)
                        curTaps[i].Recorder?.WriteFromCallback(
                            curTaps[i].BlockL.Slice(playOffset, playCount),
                            curTaps[i].BlockR.Slice(playOffset, playCount));
                }
            }
            else
            {
                // the whole block is count-in: the keyboard and the instruments keep playing (a pickup, the first chord)
                var list = tracks;
                bool anySolo = false;
                for (int i = 0; i < list.Length; i++) if (list[i].Soloed) { anySolo = true; break; }
                for (int j = 0; j < list.Length; j++)
                {
                    var t = list[order.Length == list.Length ? order[j] : j];
                    if (t.RunsIdle) t.MixIdle(n, anySolo, ml, mr);
                }
            }
            tracksDone = Stopwatch.GetTimestamp();

            // the folders and buses in order, each into the next node or the master (the export runs the same order)
            for (int i = 0; i < curNodes.Length; i++)
            {
                curNodes[i].Frame = pos - curNodes[i].InputLatency;
                curNodes[i].End(n, ml, mr);
            }
            buses.Slots.Frame = pos - masterArrive;
            buses.Process(ml, mr);
            Loudness.Process(ml, mr);
            TruePeak.Process(ml, mr);
            Correlation.Add(ml, mr, n);
            FoldMono(ref monoLevel, MonoCheck, ml, mr);

            double beatLen = SampleRate * 60.0 / bpm;
            int limLatency = buses.Limiter.Latency + pdcFrames;
            var hi = clickHi;
            var lo = clickLo;
            float ol = outL, or = outR;

            for (int i = 0; i < n; i++)
            {
                long f = pos + i;
                // count-in beats are negative and counted back from the take's start; the rest follow the grid
                bool inCountIn = isRec && nextBeat < 0;
                bool wantClick = inCountIn ? recUsedCountIn : ClickOn;
                long beatFrame = (inCountIn ? recStart : 0) + (long)Math.Round(nextBeat * beatLen) + limLatency;

                if (f >= beatFrame)
                {
                    if (wantClick)
                    {
                        bool accent = inCountIn ? (nextBeat == -recCountInBeats) : (nextBeat % 4 == 0);
                        click = accent ? hi : lo;
                        clickPos = 0;
                    }
                    nextBeat++;
                    if (inCountIn && nextBeat == 0) nextBeat = recFirstBeat;
                }

                float c = 0;
                if (click != null)
                {
                    c = click[clickPos];
                    if (++clickPos >= click.Length) click = null;
                }

                output[0][i] = ml[i] + c;
                output[1][i] = mr[i] + c;
                if (Math.Abs(output[0][i]) > ol) ol = Math.Abs(output[0][i]);
                if (Math.Abs(output[1][i]) > or) or = Math.Abs(output[1][i]);
            }
            outL = ol;
            outR = or;

            long blockStart = pos;
            pos += n;
            if (fadeMode == -2 && fadeLevel <= 0)
            {
                Interlocked.Exchange(ref position, pos);
                if (Interlocked.Exchange(ref cancelPause, 0) != 0) fadeMode = 1; // played again meanwhile
                else
                {
                    // the master fades where it is heard: the playhead stays there, and playing on skips nothing
                    long stopAt = heard.At(pos, pdcFrames);
                    heard.Start(stopAt);
                    Interlocked.Exchange(ref position, stopAt);
                    var faded = tracks;
                    for (int i = 0; i < faded.Length; i++) faded[i].Native?.FadedOut();
                    fadeMode = 0;
                    stopping = false;
                    playing = false;
                }
            }
            else
            {
                if (fadeMode == 1 && fadeLevel >= 1) fadeMode = 0;
                var lr = loopRange;
                long loopStart = lr?.Start ?? 0, loopEnd = lr?.End ?? endFrame;
                bool crossedB = loop & LoopSplice.Turned(blockStart, pos, loopStart, loopEnd, out long lapTo);
                bool atEnd = endFrame > 0 && pos >= endFrame;
                if (!isRec && crossedB)
                {
                    // the block already played the start of the lap (LoopSplice), so A comes in whole on its sample;
                    // the effects keep their state (resetting them here cut the reverb tails and clicked on every lap)
                    // and the beats go on one lap back, so a click that hasn't sounded yet still does
                    if (xfFrom >= 0)
                    {
                        xfFrom = pos;
                        xfLevel = 0;
                    }
                    nextBeat = BeatAt((long)Math.Round(nextBeat * beatLen) - (loopEnd - loopStart));
                    heard.Turn(loopStart, loopEnd - loopStart);
                    Interlocked.Exchange(ref position, lapTo);
                }
                else if (!isRec && atEnd && loop)
                {
                    // past B, at the end of the song: nothing sounds here to join with
                    nextBeat = BeatAt(loopStart);
                    heard.Turn(loopStart, pos - loopStart);
                    Interlocked.Exchange(ref position, loopStart);
                }
                else if (!isRec && atEnd)
                {
                    Interlocked.Exchange(ref position, pos);
                    playing = false;
                }
                else
                {
                    Interlocked.Exchange(ref position, pos);
                }
            }
        }
        else
        {
            // a seek caught by a stop: apply it as is, then any newer one
            rolling = false;
            xfFrom = -1;
            fadeMode = 0;
            fadeLevel = 0; // the next Play fades in from silence
            long req = Interlocked.Exchange(ref pendingSeek, NoSeek);
            if (req != NoSeek) Interlocked.Exchange(ref position, req);
            // instrument plugins keep running while stopped: what they still play (the piano roll's keys) goes through
            // their chain, like when the song plays
            var idle = tracks;
            bool idleSolo = false;
            for (int i = 0; i < idle.Length; i++) if (idle[i].Soloed) { idleSolo = true; break; }
            for (int j = 0; j < idle.Length; j++)
            {
                var t = idle[order.Length == idle.Length ? order[j] : j];
                if (t.RunsIdle) t.MixIdle(n, idleSolo, ml, mr);
            }
            tracksDone = Stopwatch.GetTimestamp();
            // Transport stopped: keep processing the buses with silent input so reverb and delay tails decay
            // naturally in real time (matching MixBus in the web app).
            for (int i = 0; i < curNodes.Length; i++) curNodes[i].End(n, ml, mr);
            buses.Process(ml, mr);
            if (cutLeft == 0 && Interlocked.Exchange(ref pendingCut, 0) != 0) cutLeft = CutFrames;
            if (cutLeft > 0)
            {
                // the cut fades out over CutFrames, whatever the block; the block after it starts with everything cleared
                for (int i = 0; i < n; i++)
                {
                    float g = cutLeft > 0 ? --cutLeft / (float)CutFrames : 0f;
                    ml[i] *= g;
                    mr[i] *= g;
                }
                if (cutLeft == 0) cutReset = true;
            }
            Correlation.Add(ml, mr, n);
            FoldMono(ref monoLevel, MonoCheck, ml, mr);
            float ol = outL, or = outR;
            for (int i = 0; i < n; i++)
            {
                float c = 0;
                if (click != null)
                {
                    c = click[clickPos];
                    if (++clickPos >= click.Length) click = null;
                }
                float l = ml[i] + c;
                float r = mr[i] + c;
                output[0][i] = l;
                output[1][i] = r;
                if (Math.Abs(l) > ol) ol = Math.Abs(l);
                if (Math.Abs(r) > or) or = Math.Abs(r);
            }
            outL = ol;
            outR = or;
        }

        if (Sample.Active) Sample.Mix(output[0], output[1], n);

        if (softwareMonitor)
        {
            // the armed tracks' inputs, each once; with none armed, input 1-2
            if (curTaps.Length == 0) Monitor(output, procL, procR, n);
            for (int k = 0; k < curTaps.Length; k++)
                if (curTaps[k].Monitored) Monitor(output, curTaps[k].BlockL, curTaps[k].BlockR, n);
        }

        OutRms.Add(output[0], output[1], n);
        UpdateLoad(t0, n);
    }

    /// <summary>
    /// The keyboard's messages since the last block, to the tracks that listen to them (their port and channel), at the
    /// start of this one. While recording, each note also goes to the track's take at the frame that was being heard
    /// when it arrived: this block's start, back by the output's latency, and back by how long before the callback it came.
    /// </summary>
    /// <summary>A key that arrives more than half a second before its block is old news: it isn't played.</summary>
    static readonly long StaleKey = Stopwatch.Frequency / 2;

    /// <summary>The scale the MIDI keyboard falls on; null = as played. Immutable, replaced whole by the UI.</summary>
    public volatile MusicScale? KeySnap;

    void DispatchMidi(long t0)
    {
        var list = midiListeners;
        var all = tracks;
        for (int i = 0; i < all.Length; i++) all[i].Keys.BeginBlock();
        if (list != heardListeners)
        {
            var old = heardListeners;
            for (int i = 0; i < old.Length; i++)
            {
                bool still = false;
                for (int k = 0; k < list.Length && !still; k++) still = list[k].Track == old[i].Track;
                if (!still) old[i].Track.Keys.AllOff();
            }
            heardListeners = list;
        }
        var src = midiInputs;
        if (src == null) return;
        bool rec = recording;
        long pos = Interlocked.Read(ref position);
        double framesPerTick = SampleRate / (double)Stopwatch.Frequency;
        while (src.TryRead(out var e))
        {
            // a key that waited (the audio was closed or busy) is not played late: only its end counts
            if (e.Kind == MidiKind.NoteOn && t0 - e.Ticks > StaleKey) continue;
            bool everyone = e.Port == MidiInputs.VirtualPort;
            for (int i = 0; i < list.Length; i++)
            {
                var l = list[i];
                if (!everyone && (l.Port != 0 && l.Port != e.Port || l.Channel != 0 && l.Channel != e.Channel)) continue;
                var keys = l.Track.Keys;
                int before = keys.EventCount;
                keys.Snap = KeySnap;
                keys.Handle(e);
                if (!rec || l.Take is not { } take) continue;
                long frame = pos - midiLatencyFrames + (long)(Math.Min(0, e.Ticks - t0) * framesPerTick);
                var done = keys.Events;
                for (int k = before; k < done.Length; k++) take.Add(frame, done[k]);
            }
        }
    }

    /// <summary>The arpeggiators of the tracks that hear the keyboard, in time with the song; what they play is recorded as it sounds.</summary>
    void RunArps(int n)
    {
        var list = midiListeners;
        bool rec = recording;
        long pos = Interlocked.Read(ref position);
        for (int i = 0; i < list.Length; i++)
        {
            var l = list[i];
            var keys = l.Track.Keys;
            int before = keys.EventCount;
            keys.RunArp(playing ? pos : -1, n, SampleRate * 60 / Math.Max(1, keys.Tempo));
            if (!rec || l.Take is not { } take) continue;
            var done = keys.Events;
            for (int k = before; k < done.Length; k++) take.Add(pos - midiLatencyFrames, done[k]);
        }
    }

    void Monitor(float[][] output, ReadOnlySpan<float> l, ReadOnlySpan<float> r, int n)
    {
        float ol = outL, or = outR;
        for (int i = 0; i < n; i++)
        {
            float a = output[0][i] + l[i];
            float b = output[1][i] + r[i];
            output[0][i] = a;
            output[1][i] = b;
            if (Math.Abs(a) > ol) ol = Math.Abs(a);
            if (Math.Abs(b) > or) or = Math.Abs(b);
        }
        outL = ol;
        outR = or;
    }

    void UpdateLoad(long t0, int n)
    {
        long run = Stopwatch.GetTimestamp() - t0;
        if (run > Interlocked.Read(ref worstRun))
        {
            Interlocked.Exchange(ref worstRun, run);
            Interlocked.Exchange(ref worstInput, inputDone - t0);
            Interlocked.Exchange(ref worstTracks, tracksDone - inputDone);
        }
        double used = run / (double)Stopwatch.Frequency;
        double budget = n / (double)SampleRate;
        LoadPercent = LoadPercent * 0.9 + 0.1 * (100 * used / budget);
    }

    /// <summary>Plays one click of the current sound and volume, playing or not (the web's playClick from the sound menu).</summary>
    public void PreviewClick(bool accent = true) => Volatile.Write(ref previewClick, accent ? clickHi : clickLo);

    public void SetClickSound(ClickSound s, double volumeDb = 0)
    {
        clickSound = s;
        clickVolumeDb = volumeDb;
        clickHi = ClickSynth.Render(s, true, SampleRate, volumeDb);
        clickLo = ClickSynth.Render(s, false, SampleRate, volumeDb);
    }

    public void Dispose()
    {
        StopRecording(discard: false);
        io?.Dispose();
        io = null;
    }
}

/// <summary>
/// A track that hears the keyboard (<see cref="PlaybackEngine.SetMidiListeners"/>): which port it listens to
/// (<see cref="MidiInputs.Port.Id"/>; 0 = all of them, -1 = one that isn't plugged in) and which channel (0 = all), and
/// the take its notes go to while recording (null = it only plays them).
/// </summary>
public sealed record MidiListener(LiveTrack Track, int Port, int Channel, MidiTake? Take);
