using XtremeDaw.Audio;
using Dsp = XtremeDaw.Audio.Dsp;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XtremeDaw.Projects;

/// <summary>
/// A project, as saved in its .xtp. Three layers:
/// <see cref="Media"/> (recordings and imported files, never modified), <see cref="Track"/> (the channel
/// strip and whether it gets a video panel) and <see cref="Clip"/> (a piece of a media placed on a track).
/// Timeline times are in seconds; latency-type corrections stay in ms, like in the web app.
/// Fields the app doesn't know go to <c>Extra</c> and are written back untouched.
/// </summary>
public sealed class Project
{
    public const string FormatId = "xtreme-project";
    /// <summary>
    /// The format of WildTrack (and GuitarCam Studio Native) projects, guitarcam.json: the same fields under another id.
    /// They open as they are and save as XTreme (asked by the user, 2026-11-03).
    /// </summary>
    public const string OldFormatId = "guitarcam-project";
    /// <summary>
    /// 2: the channel's effects are insert slots (<see cref="Channel.Inserts"/>) instead of fixed sat/comp/eq.
    /// 3: the fixed reverb and delay and the extra returns became buses (<see cref="ProjectSettings.Buses"/>), every
    /// channel, folder and bus picks its output and sends to buses, and folders only hold tracks. The older fields are
    /// read once and turned into the new ones when the project is normalized (<see cref="ProjectSettings"/>,
    /// <see cref="Channel"/>), so even a project opened read-only sounds as it did.
    /// </summary>
    public const int CurrentVersion = 3;

    public string Format { get; set; } = FormatId;
    public int Version { get; set; } = CurrentVersion;
    public string Id { get; set; } = Ids.New("prj");
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ProjectSettings Settings { get; set; } = new();
    public List<Media> Media { get; set; } = [];
    public List<Track> Tracks { get; set; } = [];
    /// <summary>Native only: folders that group tracks (<see cref="Folder"/>, <see cref="TrackFolders"/>).</summary>
    public List<Folder> Folders { get; set; } = [];
    /// <summary>Native only: named points of the song (Intro, Solo…), in time order (<see cref="ProjectMarkers"/>).</summary>
    public List<Marker> Markers { get; set; } = [];
    /// <summary>Native only: the chords "Detectar escala y acordes" found, apart from the user's markers, in time order
    /// (<see cref="ProjectMarkers"/>).</summary>
    public List<ChordMarker> ChordMarkers { get; set; } = [];
    public List<ExportRecord> Exports { get; set; } = [];
    public List<LogEntry> Log { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public Media? FindMedia(string id) => Media.Find(m => m.Id == id);

    public void AddLog(string type, string? name = null) => Log.Add(new LogEntry { Type = type, Name = name });

    /// <summary>Fills what a hand-edited or older file left out, so the rest of the app never sees nulls.</summary>
    public void Normalize()
    {
        if (Format == OldFormatId) Format = FormatId;
        Settings ??= new();
        Settings.Normalize();
        Media ??= [];
        Tracks ??= [];
        Exports ??= [];
        Log ??= [];
        Exports.RemoveAll(e => e == null);
        foreach (var e in Exports) e.TakeIds ??= [];
        Media.RemoveAll(m => m == null);
        foreach (var m in Media) m.AddedBy = string.IsNullOrWhiteSpace(m.AddedBy) ? null : m.AddedBy.Trim();
        Tracks.RemoveAll(t => t == null);
        foreach (var t in Tracks) t.Normalize();
        // a shared session's stand-in renders were dropped (#291, the user's call): their entry and files go
        Extra?.Remove("renders");
        TrackFolders.Normalize(this);
        ProjectBuses.Normalize(this);
        ProjectMarkers.Normalize(this);
    }
}

public sealed class ProjectSettings
{
    /// <summary>
    /// The initial tempo, 110 like a new project in the web app: the first point of <see cref="TempoMap"/>. Setting it
    /// is a change of the whole song's speed, as the transport's tempo has always been: every tempo of the map is
    /// multiplied by new/old (<see cref="TempoScale"/>), an undo step of its own (#262); nothing on the timeline moves (that is
    /// <see cref="TempoFollow.SetBpm"/> and <see cref="TempoFollow.SetGrid"/>). A point of the map, the first one too,
    /// is edited through <see cref="TempoFollow.SetMap"/>, which is content and goes in an undo step.
    /// </summary>
    [JsonIgnore]
    public double Bpm
    {
        get => TempoMap.Bpm;
        set
        {
            double k = value / TempoMap.Bpm;
            if (k == 1) return;
            TempoMap = TempoMap.Scaled(k).WithInitial(value);
            if (double.IsFinite(k) && k > 0) TempoScale *= k;
        }
    }
    /// <summary>
    /// "bpm" in the file: the initial tempo, kept for older versions; a project without a map gets a one-point 4/4 map
    /// from it. Reading it only sets the first point (the map that comes after it in the file wins).
    /// </summary>
    [JsonInclude, JsonPropertyName("bpm")]
    double SavedBpm
    {
        get => TempoMap.Bpm;
        set => TempoMap = TempoMap.WithInitial(value);
    }
    /// <summary>
    /// The song's tempo and meter (#214, <see cref="Projects.TempoMap"/>). Replaced whole, never edited in place:
    /// <see cref="TempoFollow.SetMap"/> moves what is musical with it and goes in an undo step.
    /// </summary>
    public TempoMap TempoMap { get; set; } = TempoMap.Constant(TempoMap.DefaultBpm);
    /// <summary>
    /// What the changes of the whole song's speed (<see cref="TempoFollow.SetBpm"/>, <see cref="TempoFollow.SetGrid"/>)
    /// have multiplied the map's tempos by so far. Native only: like <see cref="TimeScale"/>, an undo step keeps it and
    /// brings it back.
    /// </summary>
    public double TempoScale { get; set; } = 1;
    /// <summary>
    /// What the tempo changes have multiplied the timeline's times by so far (<see cref="TempoFollow"/>). Native only:
    /// an undo step keeps it and brings it back with the times it scaled.
    /// </summary>
    public double TimeScale { get; set; } = 1;
    /// <summary>
    /// The song's scale, which the piano roll and the virtual keyboard follow: an id of <see cref="XtremeDaw.Audio.Midi.MusicScale.Kinds"/>
    /// (null = none) and its root, 0 = C. It goes with the project, as the tempo does (decision of the user's request:
    /// a scale is of the song, not of the machine).
    /// </summary>
    public string? Scale { get; set; }
    public int ScaleRoot { get; set; }
    /// <summary>
    /// video | audio (native only). Audio hides the stage and the camera and exports the mix instead of an MP4;
    /// nothing is deleted, so switching back to video shows everything again.
    /// </summary>
    public string Mode { get; set; } = "video";
    public bool AudioOnly => Mode == "audio";
    /// <summary>side-by-side | top-to-bottom</summary>
    public string Layout { get; set; } = "side-by-side";
    /// <summary>"16:9", "9:16", "1:1", "4:5"…</summary>
    public string Aspect { get; set; } = "16:9";
    /// <summary>Native only: the master's EQ and multiband (<see cref="Dsp.MasterChain"/>), off by default.</summary>
    public MasterSettings Master { get; set; } = new();
    public ColorGrade Color { get; set; } = new();
    public VideoFx VideoFx { get; set; } = new();
    public ExportSettings Export { get; set; } = new();
    /// <summary>
    /// Native only: the A–B range that playback repeats while the loop is on (seconds); both null = the whole song.
    /// Transport state, outside undo (a step that brings back another tempo rescales it). Set through <see cref="ProjectMarkers.SetLoop"/>.
    /// </summary>
    public double? LoopStartSec { get; set; }
    public double? LoopEndSec { get; set; }
    /// <summary>
    /// Native only: the buses (<see cref="Bus"/>, edited through <see cref="ProjectBuses"/>), in the mixer's order. Starts
    /// with "Reverb" and "Delay" (<see cref="ProjectBuses.Defaults"/>), what a template without buses gets; an empty new
    /// project clears them (<see cref="ProjectLibrary.Create"/>).
    /// </summary>
    public List<Bus> Buses { get; set; } = ProjectBuses.Defaults();
    /// <summary>
    /// Native only: the buses' rows at the end of the timeline (#267, with their automation lanes) are hidden. A view
    /// setting, outside undo; shown by default (the user's call), old projects too. Their lanes play either way.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool BusRowsHidden { get; set; }
    /// <summary>Native only: the mix's A/B (<see cref="MixCompare"/>); null = one mix, nothing to compare.</summary>
    public MixCompareState? Compare { get; set; }

    // version 2's fixed reverb and delay and its extra returns, read only to turn them into buses (never written)
    [JsonPropertyName("reverb")] public ReverbSettings? OldReverb { get; set; }
    [JsonPropertyName("delay")] public DelaySettings? OldDelay { get; set; }
    [JsonPropertyName("returns")] public List<Bus>? OldReturns { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public (double W, double H) AspectWH()
    {
        var p = (Aspect ?? "").Split(':');
        return p.Length == 2 && double.TryParse(p[0], System.Globalization.CultureInfo.InvariantCulture, out var w)
            && double.TryParse(p[1], System.Globalization.CultureInfo.InvariantCulture, out var h) && w > 0 && h > 0
            ? (w, h)
            : (16, 9);
    }

    /// <summary>App.tsx changeAspectRatio: vertical stacks the panels, horizontal puts them side by side (square keeps the layout).</summary>
    public void SetAspect(string aspect)
    {
        if (aspect is not ("16:9" or "9:16" or "1:1")) throw new ArgumentException(Texts.T("err.aspect", "La relación tiene que ser 16:9, 9:16 o 1:1."));
        Aspect = aspect;
        if (aspect == "9:16") Layout = "top-to-bottom";
        else if (aspect == "16:9") Layout = "side-by-side";
    }

    /// <summary>App.tsx changeLayout.</summary>
    public void SetLayout(string layout)
    {
        if (layout is not ("side-by-side" or "top-to-bottom")) throw new ArgumentException(Texts.T("err.layout", "La disposición tiene que ser side-by-side o top-to-bottom."));
        Layout = layout;
    }

    public void SetMode(string mode)
    {
        if (mode is not ("video" or "audio")) throw new ArgumentException("El modo tiene que ser video o audio.");
        Mode = mode;
    }

    internal void Normalize()
    {
        TempoMap ??= TempoMap.Constant(TempoMap.DefaultBpm);
        TempoMap.Normalize();
        if (!double.IsFinite(TimeScale) || TimeScale <= 0) TimeScale = 1;
        if (!double.IsFinite(TempoScale) || TempoScale <= 0) TempoScale = 1;
        ScaleRoot = (ScaleRoot % 12 + 12) % 12;
        if (XtremeDaw.Audio.Midi.MusicScale.Find(ScaleRoot, Scale) == null) Scale = null;
        Mode = Mode == "audio" ? Mode : "video";
        Layout = Layout == "top-to-bottom" ? Layout : "side-by-side";
        Aspect ??= "16:9";
        if (OldReverb != null || OldDelay != null || OldReturns != null)
        {
            Buses = ProjectBuses.FromVersion2(OldReverb, OldDelay, OldReturns);
            OldReverb = null;
            OldDelay = null;
            OldReturns = null;
        }
        (Master ??= new()).Normalize();
        (Color ??= new()).Normalize();
        (VideoFx ??= new()).Normalize();
        (Export ??= new()).Normalize();
        Buses ??= [];
        Buses.RemoveAll(x => x == null);
        foreach (var x in Buses) x.Normalize();
        Compare?.Normalize();
        if (LoopStartSec is not { } a || LoopEndSec is not { } b || !double.IsFinite(a) || !double.IsFinite(b)
            || a < 0 || b - a < ProjectMarkers.MinLoopSec)
            LoopStartSec = LoopEndSec = null;
    }
}

/// <summary>
/// A reverb slot's settings (<see cref="InsertSlot.Reverb"/>, in a bus's chain): <see cref="Muted"/> means the slot is
/// off (bypassed) and <see cref="ReturnGain"/> is its own level. Version 2 also used it for the fixed Reverb return.
/// </summary>
public sealed class ReverbSettings
{
    public double Decay { get; set; } = 1.8;
    public double PreDelayMs { get; set; } = 20;
    public double Tone { get; set; } = 6000;
    public double LowCut { get; set; } = 150;
    public double Width { get; set; } = 1;
    public double ReturnGain { get; set; } = 0.8;
    public bool Muted { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// The master (native only): one EQ (low cut and mud go before the multiband, the rest after it) and a three-band
/// compressor where each band only asks how much (it sets its threshold from its own level as it plays), and the
/// limiter with its own ON. <see cref="On"/> is the whole chain's (asked by the user: one button for all of it): off,
/// nothing processes, Salida included (the Empuje, the soft clip and the limiter); only the Destino's gain stays, so the
/// export still lands where it was going.
/// </summary>
public sealed class MasterSettings
{
    /// <summary>Off by default, as in a file from before it existed; a new project turns it on (<see cref="ProjectLibrary.Create"/>).</summary>
    public bool On { get; set; }
    /// <summary>The cleanup before the multiband: low cut and mud.</summary>
    public bool EqOn { get; set; } = true;
    /// <summary>The colour after the multiband: lows, presence, air and tilt.</summary>
    public bool ColorOn { get; set; } = true;
    /// <summary>Hz; 0 = no low cut.</summary>
    public double LowCutHz { get; set; }
    public double MudDb { get; set; }
    public double LowsDb { get; set; }
    public double PresenceDb { get; set; }
    public double AirDb { get; set; }
    /// <summary>+ brighter, − darker, pivot at 1 kHz.</summary>
    public double TiltDb { get; set; }
    public bool BandsOn { get; set; } = true;
    public double LowXHz { get; set; } = Dsp.MasterBands.DefaultLowXHz;
    public double HighXHz { get; set; } = Dsp.MasterBands.DefaultHighXHz;
    /// <summary>Always three: lows, mids, highs.</summary>
    public List<MasterBand> Bands { get; set; } = Defaults();
    /// <summary>The limiter, under the master's ON: its own ON, Empuje into it (dB), Techo (dBFS) and Release (ms).</summary>
    public bool LimOn { get; set; } = true;
    /// <summary>Cinta: tape saturation before the limiter, 0 to 1 (0 = nothing).</summary>
    public bool TapeOn { get; set; } = true;
    public double TapeAmount { get; set; }
    /// <summary>Ancho: stereo width (1 = as it is, 0 = mono, 2 = twice) and the lows in mono under this (Hz; 0 = off).</summary>
    public bool WidthOn { get; set; } = true;
    public double Width { get; set; } = 1;
    public double MonoBassHz { get; set; }
    /// <summary>Soft clip before the limiter: how many dB of the peaks it takes (0 = off).</summary>
    public bool ClipOn { get; set; } = true;
    public double ClipDb { get; set; }
    public double LimDriveDb { get; set; }
    /// <summary>
    /// dBTP. Saved under a new key because the old one (limCeilingDb, −0,3 per sample) meant something else: an older
    /// project keeps it in <see cref="Extra"/> and starts from −1 dBTP.
    /// </summary>
    [JsonPropertyName("limCeilingDbtp")] public double LimCeilingDb { get; set; } = Dsp.MasterLimiter.CeilingDb;
    public double LimReleaseMs { get; set; } = Dsp.MasterLimiter.ReleaseMs;
    /// <summary>Destino: where the mix is going (<see cref="Dsp.MasterLoudness.All"/>, "" = as it is), so its loudness is heard while mixing.</summary>
    public string Target { get; set; } = Dsp.MasterLoudness.AsIs;
    /// <summary>The gain (dB) last measured to land the mix on <see cref="Target"/>: kept, so reopening plays at that level at once.</summary>
    public double TargetGainDb { get; set; }
    /// <summary>The look the knobs came from (<see cref="Dsp.MasterLooks.All"/>, "" = by hand) and its "Cuánto" (0 to 1).</summary>
    public string Look { get; set; } = Dsp.MasterLooks.Custom;
    public double LookAmount { get; set; } = Dsp.MasterLooks.DefaultAmount;
    /// <summary>The multiband's fourth band, Púa: how hard it holds 2–5 kHz down when the pick hits (0 = off).</summary>
    public double PickAmount { get; set; }
    /// <summary>The master's chain, a list like a bus's: what its strip and the Mastering view show. <see cref="On"/> turns it all on or off.</summary>
    public List<InsertSlot> Slots { get; set; } = [];
    /// <summary>
    /// The chain lives in <see cref="Slots"/>; the knobs' fields above it (EQ, bands, Cinta, Ancho) only pass values to a
    /// look or a suggestion. Off, a project from before: <see cref="Normalize"/> moves them, once, so it sounds the same.
    /// The soft clip and the limiter stay here, in Salida.
    /// </summary>
    public bool ChainInSlots { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static List<MasterBand> Defaults() => [new(), new(), new()];

    /// <summary>Puts a look at that "Cuánto" into the knobs it sets (<see cref="Dsp.MasterLooks.At"/>); the rest stays.</summary>
    public void ApplyLook(Dsp.MasterLook look, double amount)
    {
        var v = Dsp.MasterLooks.At(look, amount);
        Look = look.Id;
        LookAmount = Math.Clamp(amount, 0, 1);
        LowCutHz = v.LowCutHz; MudDb = v.MudDb; LowsDb = v.LowsDb; PresenceDb = v.PresenceDb; AirDb = v.AirDb; TiltDb = v.TiltDb;
        Bands[0].Amount = v.Low; Bands[1].Amount = v.Mid; Bands[2].Amount = v.High;
        TapeAmount = v.Tape; Width = v.Width; MonoBassHz = v.MonoBassHz; PickAmount = v.Pick;
        if (ChainInSlots) FxDefs.SyncStages(this);
    }

    /// <summary>Takes what "Sugerime" proposed (<see cref="Dsp.MasterSuggest"/>): the EQ, the bands and the Púa; by hand from then on.</summary>
    public void ApplySuggestion(Dsp.MasterParams p)
    {
        Look = Dsp.MasterLooks.Custom;
        On = p.On;
        LowCutHz = p.LowCutHz; MudDb = p.MudDb; LowsDb = p.LowsDb; PresenceDb = p.PresenceDb; AirDb = p.AirDb; TiltDb = p.TiltDb;
        Bands[0].Amount = p.Low.Amount; Bands[1].Amount = p.Mid.Amount; Bands[2].Amount = p.High.Amount;
        PickAmount = p.PickAmount;
        if (ChainInSlots) FxDefs.SyncStages(this);
    }

    public Dsp.MasterParams ToParams()
    {
        Dsp.MasterBandParams B(int i) => new(Bands[i].Amount);
        // the master's ON reaches the limiter too (the soft clip reads it already)
        return new(On, EqOn, LowCutHz, MudDb, LowsDb, PresenceDb, AirDb, TiltDb, BandsOn, LowXHz, HighXHz, B(0), B(1), B(2), ColorOn, LimDriveDb, LimCeilingDb, LimReleaseMs, On && LimOn,
            TapeOn, TapeAmount, WidthOn, Width, MonoBassHz, ClipOn, ClipDb, Dsp.MasterLoudness.Get(Target).Lufs, TargetGainDb, PickAmount, ChainInSlots);
    }

    internal void Normalize()
    {
        static double Fin(double v, double lo, double hi, double fallback) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;
        LowCutHz = LowCutHz < Dsp.MasterEq.LowCutMin || !double.IsFinite(LowCutHz) ? 0 : Math.Min(LowCutHz, Dsp.MasterEq.LowCutMax);
        MudDb = Fin(MudDb, -Dsp.MasterEq.GainMax, Dsp.MasterEq.GainMax, 0);
        LowsDb = Fin(LowsDb, -Dsp.MasterEq.GainMax, Dsp.MasterEq.GainMax, 0);
        PresenceDb = Fin(PresenceDb, -Dsp.MasterEq.GainMax, Dsp.MasterEq.GainMax, 0);
        AirDb = Fin(AirDb, -Dsp.MasterEq.GainMax, Dsp.MasterEq.GainMax, 0);
        TiltDb = Fin(TiltDb, -Dsp.MasterEq.TiltMax, Dsp.MasterEq.TiltMax, 0);
        LowXHz = Fin(LowXHz, Dsp.MasterBands.LowXMin, Dsp.MasterBands.LowXMax, Dsp.MasterBands.DefaultLowXHz);
        HighXHz = Fin(HighXHz, Dsp.MasterBands.HighXMin, Dsp.MasterBands.HighXMax, Dsp.MasterBands.DefaultHighXHz);
        LimDriveDb = Fin(LimDriveDb, 0, Dsp.MasterParams.DriveMaxDb, 0);
        TapeAmount = Fin(TapeAmount, 0, 1, 0);
        Width = Fin(Width, 0, Dsp.StereoWidth.MaxWidth, 1);
        MonoBassHz = MonoBassHz < 20 || !double.IsFinite(MonoBassHz) ? 0 : Math.Min(MonoBassHz, Dsp.StereoWidth.MonoBassMaxHz);
        ClipDb = Fin(ClipDb, 0, Dsp.SoftClipper.MaxDb, 0);
        LimCeilingDb = Fin(LimCeilingDb, Dsp.MasterLimiter.CeilingMinDb, 0, Dsp.MasterLimiter.CeilingDb);
        LimReleaseMs = Fin(LimReleaseMs, Dsp.MasterLimiter.ReleaseMinMs, Dsp.MasterLimiter.ReleaseMaxMs, Dsp.MasterLimiter.ReleaseMs);
        Target = Dsp.MasterLoudness.Get(Target).Id;
        TargetGainDb = Fin(TargetGainDb, -Dsp.MixMaster.MaxGainDb, Dsp.MixMaster.MaxGainDb, 0);
        Look = Dsp.MasterLooks.Get(Look)?.Id ?? Dsp.MasterLooks.Custom;
        LookAmount = Fin(LookAmount, 0, 1, Dsp.MasterLooks.DefaultAmount);
        PickAmount = Fin(PickAmount, 0, 1, 0);
        Bands ??= [];
        Bands.RemoveAll(b => b == null);
        while (Bands.Count < Dsp.MasterBands.Count) Bands.Add(new());
        if (Bands.Count > Dsp.MasterBands.Count) Bands.RemoveRange(Dsp.MasterBands.Count, Bands.Count - Dsp.MasterBands.Count);
        foreach (var b in Bands) b.Normalize();
        Slots ??= [];
        InsertSlot.NormalizeList(Slots);
        if (!ChainInSlots) MoveChainToSlots();
    }

    /// <summary>A project from before the chain was its slots: what was in use becomes slots, in the order it sounded.</summary>
    void MoveChainToSlots()
    {
        var fresh = new MasterSettings();
        bool touched = LowCutHz != 0 || MudDb != 0 || LowsDb != 0 || PresenceDb != 0 || AirDb != 0 || TiltDb != 0 || TapeAmount != 0 || Width != 1 || MonoBassHz != 0
            || PickAmount != 0 || Bands.Exists(b => b.Amount != Dsp.MasterBands.DefaultAmount);
        // slots from the short while they sounded ahead of the box, which "Cadena completa" left off: they stay, and sound
        if (Slots.Count > 0) On = true;
        else if (On || touched) Slots.AddRange(Dsp.TrackInserts.MasterOrder.Select(k => FxDefs.FromMaster(k, this)));
        ChainInSlots = true;
    }
}

/// <summary>One band of the master's multiband: "Cuánto" (0 = off); the band sets its threshold as it plays.</summary>
public sealed class MasterBand
{
    public double Amount { get; set; } = Dsp.MasterBands.DefaultAmount;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    internal void Normalize()
    {
        Amount = double.IsFinite(Amount) ? Math.Clamp(Amount, 0, 1) : Dsp.MasterBands.DefaultAmount;
    }
}

/// <summary>A delay slot's settings (<see cref="InsertSlot.Delay"/>), like <see cref="ReverbSettings"/>.</summary>
public sealed class DelaySettings
{
    /// <summary>stereo | pingpong</summary>
    public string Mode { get; set; } = "stereo";
    public bool Sync { get; set; } = true;
    public string Division { get; set; } = "1/8.";
    public double TimeMs { get; set; } = 375;
    public double Feedback { get; set; } = 0.35;
    public double Tone { get; set; } = 5000;
    public double LowCut { get; set; } = 250;
    public double Width { get; set; } = 1;
    public double Duck { get; set; } = 0.35;
    public double ReturnGain { get; set; } = 0.7;
    public bool Muted { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ColorGrade
{
    public bool On { get; set; } = true;
    public string Look { get; set; } = "none";
    public double Amount { get; set; } = 100;
    public double Exposure { get; set; }
    public double Contrast { get; set; }
    public double Highlights { get; set; }
    public double Shadows { get; set; }
    public double Saturation { get; set; }
    public double Vibrance { get; set; }
    public double Temperature { get; set; }
    public double Tint { get; set; }
    public double Fade { get; set; }
    /// <summary>The white point (-100..100), like CapCut's "Blancos".</summary>
    public double Whites { get; set; }
    /// <summary>The black point (-100..100): deeper or more open blacks.</summary>
    public double Blacks { get; set; }
    /// <summary>Opens or closes the mid-tones keeping blacks and whites (-100..100), like CapCut's "Sensación de luz".</summary>
    public double Light { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Same ranges as normalizeGrade (colorGrade.js): unknown looks become "none".</summary>
    public void Normalize()
    {
        static double N(double v, double lo, double d) => double.IsFinite(v) ? Math.Clamp(v, lo, 100) : d;
        Look = XtremeDaw.Audio.VideoColorProcessor.IsKnownLook(Look) ? Look : "none";
        Amount = N(Amount, 0, 100);
        Exposure = N(Exposure, -100, 0); Contrast = N(Contrast, -100, 0);
        Highlights = N(Highlights, -100, 0); Shadows = N(Shadows, -100, 0);
        Saturation = N(Saturation, -100, 0); Vibrance = N(Vibrance, -100, 0);
        Temperature = N(Temperature, -100, 0); Tint = N(Tint, -100, 0);
        Fade = N(Fade, 0, 0);
        Whites = N(Whites, -100, 0); Blacks = N(Blacks, -100, 0); Light = N(Light, -100, 0);
    }
}

public sealed class VideoFx
{
    public bool On { get; set; } = true;
    public double Vignette { get; set; }
    public double VignetteSize { get; set; } = 50;
    public double Grain { get; set; }
    public string Letterbox { get; set; } = "off";
    /// <summary>Height of each bar with the "custom" letterbox, in % of the panel height.</summary>
    public double LetterboxHeight { get; set; } = XtremeDaw.Audio.VideoColorProcessor.LetterboxHeightDefault;
    /// <summary>Edge sharpening (0..100, 0 = off), like CapCut's "Nitidez" (<see cref="XtremeDaw.Audio.VideoDetail"/>).</summary>
    public double Sharpness { get; set; }
    /// <summary>Mid-tone local contrast with a wide radius (0..100, 0 = off), like CapCut's "Claridad".</summary>
    public double Clarity { get; set; }
    /// <summary>Soft halo around the bright parts (0..100, 0 = off), like CapCut's "Glow".</summary>
    public double Glow { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Same ranges as normalizeVideoFx (videoFx.js).</summary>
    public void Normalize()
    {
        static double N(double v, double d) => double.IsFinite(v) ? Math.Clamp(v, 0, 100) : d;
        Vignette = N(Vignette, 0); VignetteSize = N(VignetteSize, 50); Grain = N(Grain, 0);
        Sharpness = N(Sharpness, 0); Clarity = N(Clarity, 0); Glow = N(Glow, 0);
        if (!XtremeDaw.Audio.VideoColorProcessor.LetterboxOptions.Any(l => l.Id == Letterbox)) Letterbox = "off";
        LetterboxHeight = double.IsFinite(LetterboxHeight)
            ? Math.Clamp(LetterboxHeight, XtremeDaw.Audio.VideoColorProcessor.LetterboxHeightMin, XtremeDaw.Audio.VideoColorProcessor.LetterboxHeightMax)
            : XtremeDaw.Audio.VideoColorProcessor.LetterboxHeightDefault;
    }
}

public sealed class ExportSettings
{
    /// <summary>Last export folder; null = the project's exports folder.</summary>
    public string? Dir { get; set; }
    public string Quality { get; set; } = "alta";
    /// <summary>wav | mp3 | flac | aiff | ogg: the last "Exportar audio" format (native only; the last three, "Avanzado").</summary>
    public string AudioFormat { get; set; } = "wav";
    /// <summary>Native only: each track that sounds also goes to its own WAV next to the export ("stems").</summary>
    public bool Stems { get; set; }
    /// <summary>"Exportar audio" → "Avanzado" (#208): 16, 24 or 32 (float, WAV only).</summary>
    public int AudioBits { get; set; } = XtremeDaw.Audio.AudioExportSettings.DefaultDepth;
    /// <summary>"Avanzado": 44100, 48000 or 96000.</summary>
    public int AudioRate { get; set; } = XtremeDaw.Audio.AudioExportSettings.DefaultRate;
    /// <summary>"Avanzado": TPDF dither at 16 bits.</summary>
    public bool AudioDither { get; set; } = true;
    /// <summary>"Avanzado": MP3 and OGG bitrate (128, 192, 256, 320); null = variable.</summary>
    public int? AudioKbps { get; set; } = XtremeDaw.Audio.AudioExportSettings.DefaultKbps;
    /// <summary>"Avanzado": a fixed tail in seconds (0 = none); null = automatic, until it falls quiet.</summary>
    public double? AudioTailSec { get; set; }
    /// <summary>"Avanzado": normalize the loudest true peak to this many dBTP; null = the level as the master leaves it.</summary>
    public double? AudioNormalizeDbtp { get; set; }
    /// <summary>"Avanzado": the export in mono.</summary>
    public bool AudioMono { get; set; }
    /// <summary>"Avanzado": the tracks left out of the stems (ids); the rest that sound go.</summary>
    public List<string> StemsSkip { get; set; } = [];
    /// <summary>"Avanzado": the folders and buses that get a stem of their own (ids).</summary>
    public List<string> StemNodes { get; set; } = [];
    /// <summary>Native only: the last social preset of the MP4 (<c>ExportTargets</c> id); "project" = the project's frame, mix as it is.</summary>
    public string Target { get; set; } = XtremeDaw.Audio.ExportTargets.Project;
    /// <summary>Native only: "Exportar audio" masters the mix to the streaming loudness (−14 LUFS).</summary>
    public bool AudioLufs { get; set; }
    /// <summary>Native only, "Avanzado": the MP4's short side (720, 1080, 1440 or 2160).</summary>
    public int Resolution { get; set; } = XtremeDaw.Audio.Mp4Exporter.DefaultResolution;
    /// <summary>Native only, "Avanzado": the MP4's video bitrate in Mbps; null = the quality's.</summary>
    public double? BitrateMbps { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    internal void Normalize()
    {
        if (!XtremeDaw.Audio.ExportTargets.All.Any(t => t.Id == Target)) Target = XtremeDaw.Audio.ExportTargets.Project;
        Resolution = XtremeDaw.Audio.Mp4Exporter.NormalizeResolution(Resolution);
        BitrateMbps = XtremeDaw.Audio.Mp4Exporter.NormalizeBitrate(BitrateMbps);
        var a = AudioSettings();
        AudioFormat = XtremeDaw.Audio.AudioExporter.FormatId(a.Format);
        (AudioBits, AudioRate, AudioKbps, AudioTailSec, AudioNormalizeDbtp) = (a.BitDepth, a.SampleRate, a.Kbps, a.TailSec, a.NormalizeDbtp);
        StemsSkip = [.. (StemsSkip ?? []).Where(id => !string.IsNullOrEmpty(id)).Distinct()];
        StemNodes = [.. (StemNodes ?? []).Where(id => !string.IsNullOrEmpty(id)).Distinct()];
    }

    /// <summary>The last "Exportar audio" as the exporter takes it, made valid for its format.</summary>
    public XtremeDaw.Audio.AudioExportSettings AudioSettings() => new XtremeDaw.Audio.AudioExportSettings
    {
        Format = XtremeDaw.Audio.AudioExporter.ParseFormat(AudioFormat),
        BitDepth = AudioBits,
        SampleRate = AudioRate,
        Dither = AudioDither,
        Kbps = AudioKbps,
        TailSec = AudioTailSec,
        NormalizeDbtp = AudioNormalizeDbtp,
        Mono = AudioMono,
    }.Normalized();
}

/// <summary>A recording or an imported file. Its files are never rewritten; edits live in the clips.</summary>
public sealed class Media
{
    public string Id { get; set; } = Ids.New("med");
    public string Name { get; set; } = "";
    /// <summary>recording | import</summary>
    public string Kind { get; set; } = "recording";
    /// <summary>Relative to the project folder (media/…), or absolute for files left where they were.</summary>
    public string? Audio { get; set; }
    public string? Video { get; set; }
    public double DurationSec { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Latency correction applied when it was recorded: the recording's start is at clip.AtSec - SyncMs.</summary>
    public double SyncMs { get; set; }
    /// <summary>Camera latency: positive trims the start of the video.</summary>
    public double VideoOffsetMs { get; set; } = 80;
    /// <summary>Linked on import: a file of it was left where it was, so it needs that place and isn't the project's to delete.</summary>
    [JsonIgnore] public bool Linked => Path.IsPathRooted(Audio) || Path.IsPathRooted(Video);
    /// <summary>Sizes of its files when they were linked or found again, how <see cref="LinkedMedia.Search"/> tells them from others of the same name; 0 = not known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long AudioBytes { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long VideoBytes { get; set; }
    /// <summary>
    /// Who brought it in a shared session (#291: the person's name there, shown in Biblioteca); null = not known (it came
    /// before a session, or outside one). Set once, by the session, and never changed after.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AddedBy { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class Track : IAutomated
{
    public string Id { get; set; } = Ids.New("trk");
    public string Name { get; set; } = "";
    /// <summary>Takes or notes (<see cref="TrackKinds"/>): "audio" or "midi". Native only.</summary>
    public string Kind { get; set; } = TrackKinds.Audio;
    [JsonIgnore] public bool IsMidi => Kind == TrackKinds.Midi;
    /// <summary>
    /// An audio track whose clips follow the tempo map (#214, asked by the user): they live in beats like a MIDI
    /// track's, so a tempo change moves and stretches them. False (the default) = seconds: they stay where they are.
    /// Native only; a MIDI track always follows (<see cref="Musical"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool FollowTempo { get; set; }
    /// <summary>Its clips and lanes are in beats: a MIDI track, or an audio one that follows the tempo.</summary>
    [JsonIgnore] public bool Musical => IsMidi || FollowTempo;
    /// <summary>It has something to play: takes or MIDI clips.</summary>
    [JsonIgnore] public bool HasContent => Clips.Count > 0 || MidiClips is { Count: > 0 };
    /// <summary>False = audio only: the track gets no panel in the video. Always false on a MIDI track.</summary>
    public bool Video { get; set; } = true;
    /// <summary>
    /// The driver inputs it records from when armed, "3" or "3-4" (<see cref="XtremeDaw.Audio.InputRoute"/>);
    /// null = the input of Configuración. Native only.
    /// </summary>
    public string? Input { get; set; }
    /// <summary>The track's color as "#rrggbb", tinting its badge and clips; null = no color (the accent). Native only.</summary>
    public string? Color { get; set; }
    /// <summary>The track's instrument icon id ("electric-guitar", "drum-kit"…); null = its number. Native only.</summary>
    public string? Icon { get; set; }
    /// <summary>The <see cref="Folder"/> the track is in; null = at the root. Native only.</summary>
    public string? FolderId { get; set; }
    /// <summary>
    /// Its takes can't be moved, trimmed, split, deleted or replaced by a new take (<see cref="ClipEditing.IsLocked"/>);
    /// volume, fades and framing still change. Native only.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Locked { get; set; }
    /// <summary>Frozen (#240): it plays this render of its instrument and slots instead (<see cref="Freezing"/>); null = live. Native only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public TrackFreeze? Freeze { get; set; }
    [JsonIgnore] public bool Frozen => Freeze != null;
    /// <summary>Its takes, notes and slots can't change: locked, or frozen (the render would no longer be what it is).</summary>
    [JsonIgnore] public bool EditLocked => Locked || Freeze != null;
    /// <summary>The mixer side its strip sits on (<see cref="MixerSides"/>); null = the left. A track in a folder goes with it. Native only.</summary>
    public string? MixerSide { get; set; }
    /// <summary>Its own lane height as a multiple of the height every track shares (the timeline's slider), so the slider
    /// still scales a track made taller or shorter; null = 1. The lane stays within <see cref="MinHeight"/>..<see cref="MaxHeight"/> px. Native only.</summary>
    public double? HeightScale { get; set; }
    /// <summary>Its stereo clips draw each side's wave, left over right, instead of one wave of both (asked by the user).
    /// A view setting like its height: saved, without undo. Native only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SplitWave { get; set; }
    public const double MinHeight = 24, MaxHeight = 320;
    /// <summary>The shared height a new timeline starts at; a folder's heights are its px at this zoom.</summary>
    public const double DefaultHeight = 64;
    /// <summary>
    /// Semitones its audio is moved by without changing its length, so it stays in sync with the video
    /// (<see cref="XtremeDaw.Audio.Dsp.PitchShifter"/>, cached by <see cref="ProjectStore.TrackAudio"/>); fractions are
    /// cents, 0 = as it was recorded. Native only.
    /// </summary>
    public double Transpose { get; set; }
    public Channel Channel { get; set; } = new();
    public List<Clip> Clips { get; set; } = [];
    /// <summary>The notes of a MIDI track (<see cref="MidiClip"/>); null on an audio track, so its file doesn't change. Native only.</summary>
    public List<MidiClip>? MidiClips { get; set; }
    /// <summary>The instrument plugin a MIDI track plays its notes with (<see cref="PluginRef"/>); null = the test tone. Native only.</summary>
    public PluginRef? Instrument { get; set; }
    /// <summary>The native sampler a MIDI track plays with (<see cref="SamplerSettings"/>), never along with <see cref="Instrument"/>; null = none. Native only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SamplerSettings? Sampler { get; set; }
    /// <summary>The native granular a MIDI track plays with (<see cref="GranularSettings"/>), never along with <see cref="Instrument"/> or <see cref="Sampler"/>; null = none. Native only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public GranularSettings? Granular { get; set; }
    /// <summary>The native General MIDI instrument a MIDI track plays with (<see cref="GmSettings"/>), never along with the others; null = none. Native only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public GmSettings? Gm { get; set; }
    /// <summary>
    /// The MIDI input a MIDI track plays and records from: a device's key from Configuración → MIDI; null = all of
    /// them. Native only.
    /// </summary>
    public string? MidiInput { get; set; }
    /// <summary>The channel it takes from that input, 1..16; 0 = all (not written, so other tracks' files don't change). Native only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int MidiChannel { get; set; }
    /// <summary>Its arpeggiator for what the keyboard plays; null = off. Native only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ArpSettings? Arp { get; set; }
    /// <summary>Names of its keys on the piano roll ("Bombo", "Caja"), by pitch; null = none. Native only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<int, string>? KeyNames { get; set; }
    /// <summary>Its automated parameters (<see cref="Automations"/>), each a lane under it in the timeline; null = none. Native only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<AutomationLane>? Automation { get; set; }
    /// <summary>Its lanes are folded under it (they keep playing). A view setting, like a folder's fold.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool AutomationFolded { get; set; }
    IReadOnlyList<InsertSlot> IAutomated.AutoSlots => Channel.Slots;
    IRouted IAutomated.AutoRouted => Channel;
    double IAutomated.AutoVolume => Channel.Volume;
    double IAutomated.AutoPan => Channel.Pan;
    bool IAutomated.AutoMuted => Channel.Muted;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    internal void Normalize()
    {
        if (Automation != null)
        {
            Automation.RemoveAll(l => l == null);
            foreach (var l in Automation) l.Normalize();
            if (Automation.Count == 0) Automation = null;
        }
        Kind = TrackKinds.Normalize(Kind);
        if (Freeze != null && !Freeze.Normalize()) Freeze = null;
        if (IsMidi)
        {
            Video = false;
            Input = null;
            MidiClips ??= [];
            MidiClips.RemoveAll(c => c == null);
            foreach (var c in MidiClips) c.Normalize();
            if (Instrument != null && !Instrument.Normalize()) Instrument = null;
            Sampler?.Normalized();
            Granular?.Normalized();
            if (Instrument != null) Sampler = null;
            if (Instrument != null || Sampler != null) Granular = null;
            Gm?.Normalize();
            if (Instrument != null || Sampler != null || Granular != null) Gm = null;
            MidiInput = string.IsNullOrWhiteSpace(MidiInput) ? null : MidiInput.Trim();
            MidiChannel = Math.Clamp(MidiChannel, 0, 16);
            Arp?.Normalize();
            KeyNames = KeyNames?.Where(k => k.Key is >= MidiNote.MinPitch and <= MidiNote.MaxPitch && !string.IsNullOrWhiteSpace(k.Value)).ToDictionary(k => k.Key, k => k.Value.Trim());
            if (KeyNames?.Count == 0) KeyNames = null;
        }
        else
        {
            MidiClips = null;
            Instrument = null;
            Sampler = null;
            Granular = null;
            Gm = null;
            MidiInput = null;
            MidiChannel = 0;
            Arp = null;
            KeyNames = null;
        }
        Input = XtremeDaw.Audio.InputRoute.Parse(Input)?.Key;
        Icon = string.IsNullOrWhiteSpace(Icon) || Icon.Length > 64 ? null : Icon.Trim();
        Color = NormalizeColor(Color);
        HeightScale = NormalizeHeight(HeightScale, 0.1, 8);
        MixerSide = MixerSides.Normalize(MixerSide, MixerSides.Left);
        Transpose = double.IsFinite(Transpose)
            ? Math.Round(Math.Clamp(Transpose, -XtremeDaw.Audio.Dsp.PitchShifter.MaxSemitones, XtremeDaw.Audio.Dsp.PitchShifter.MaxSemitones), 2)
            : 0;
        Channel ??= new();
        Channel.Normalize();
        Clips ??= [];
        Clips.RemoveAll(c => c == null);
        foreach (var c in Clips) c.Normalize();
        ClipLoops.Normalize(this);
    }

    /// <summary>A row height (or height scale) inside its limits, or null when it is missing or broken.</summary>
    internal static double? NormalizeHeight(double? h, double min, double max) => h is { } v && double.IsFinite(v) ? Math.Clamp(v, min, max) : null;

    /// <summary>"#rrggbb" in lower case, or null for anything else.</summary>
    internal static string? NormalizeColor(string? color) =>
        color is { Length: 7 } hex && hex[0] == '#' && hex.Skip(1).All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
}

/// <summary>
/// A folder of tracks (native only, asked by the user): its children are the tracks with its <see cref="Track.FolderId"/>,
/// always together in <see cref="Project.Tracks"/>, and the folder sits right before them. It folds in the timeline and
/// in the mixer. It is a channel of its own (the user's call, 2026-11-04): its children's outputs add up there, go
/// through its insert slots (<see cref="Inserts"/>), pan, fader and mute, and on to its <see cref="Output"/> (the master
/// or a bus) and its sends to buses (<see cref="Sends"/>, pre-fader ones taken after the slots and the pan, like a
/// track's). Its <see cref="Solo"/> solos its children. A separator carries none of this. Version 2 saved the fader,
/// mute, solo and slots under the same names, so they come back as they were.
/// </summary>
public sealed class Folder : IRouted, IAutomated
{
    public string Id { get; set; } = Ids.New("fld");
    public string Name { get; set; } = "";
    /// <summary>"#rrggbb" or null (a neutral gray), like <see cref="Track.Color"/>.</summary>
    public string? Color { get; set; }
    /// <summary>Folded in the timeline: only its own row shows.</summary>
    public bool Collapsed { get; set; }
    /// <summary>Folded in the mixer: only its own strip shows.</summary>
    public bool MixerCollapsed { get; set; }
    /// <summary>Its own row height in the timeline when open: px at the tracks' <see cref="Track.DefaultHeight"/>, scaled
    /// with the vertical zoom and shown within <see cref="MinHeight"/>..<see cref="MaxHeight"/>; null = the usual one.</summary>
    public double? Height { get; set; }
    /// <summary><see cref="MinHeight"/> is also the height of a folder's row without send knobs (the timeline's FolderH).</summary>
    public const double MinHeight = 34, MaxHeight = 160;
    /// <summary>Its folded row height (its mini map) as a multiple of the tracks' shared height, so the vertical zoom
    /// still scales it, like <see cref="Track.HeightScale"/>; null = the usual one, as open. Native only.</summary>
    public double? HeightScale { get; set; }
    /// <summary>
    /// How many tracks come before it in <see cref="Project.Tracks"/>. With children it is the first child's index
    /// (<see cref="TrackFolders.Normalize"/> keeps it so); an empty folder needs it to keep its place.
    /// </summary>
    public int Index { get; set; }
    /// <inheritdoc/>
    public string? Output { get; set; }
    /// <inheritdoc/>
    public List<Send> Sends { get; set; } = [];
    /// <summary>
    /// A separator (asked by the user): a named divider between the strips of the mixer and the rows of the timeline,
    /// for keeping order in big projects. It goes where a folder goes but holds no tracks and makes no sound.
    /// </summary>
    public bool Separator { get; set; }
    /// <summary>A separator left out of the timeline (it still shows in the mixer, where it is switched).</summary>
    public bool HiddenInTimeline { get; set; }
    /// <summary>The mixer side its strip (with its tracks) sits on (<see cref="MixerSides"/>); null = the left.</summary>
    public string? MixerSide { get; set; }
    /// <summary>Its fader, 0..1.5 like a track's.</summary>
    public double Volume { get; set; } = 1;
    /// <summary>-1 (left) .. 1 (right), like a track's.</summary>
    public double Pan { get; set; }
    /// <summary>Silences its node (its children's sum), not its children's own sends.</summary>
    public bool Muted { get; set; }
    /// <summary>Solos every child track (the user's call): the folder's node is heard with them.</summary>
    public bool Solo { get; set; }
    /// <summary>Its effects, run over its children's sum, like a bus's (<see cref="Bus.Inserts"/>).</summary>
    public List<InsertSlot> Inserts { get; set; } = [];
    /// <summary>Its automated parameters (<see cref="Automations"/>), lanes under its row in the timeline, like a track's; null = none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<AutomationLane>? Automation { get; set; }
    /// <summary>Its lanes are folded under it (they keep playing), like <see cref="Track.AutomationFolded"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool AutomationFolded { get; set; }
    IReadOnlyList<InsertSlot> IAutomated.AutoSlots => Inserts;
    IRouted IAutomated.AutoRouted => this;
    double IAutomated.AutoVolume => Volume;
    double IAutomated.AutoPan => Pan;
    bool IAutomated.AutoMuted => Muted;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    internal void Normalize()
    {
        Sends ??= [];
        if (Automation != null)
        {
            Automation.RemoveAll(l => l == null);
            foreach (var l in Automation) l.Normalize();
            if (Automation.Count == 0) Automation = null;
        }
        MixerSide = MixerSides.Normalize(MixerSide, MixerSides.Left);
        Volume = double.IsFinite(Volume) ? Math.Clamp(Volume, 0, 1.5) : 1;
        Pan = double.IsFinite(Pan) ? Math.Clamp(Pan, -1, 1) : 0;
        Inserts ??= [];
        InsertSlot.NormalizeList(Inserts);
        // a separator routes nothing and carries no channel; HiddenInTimeline is a separator's alone
        if (Separator)
        {
            Output = null;
            Sends.Clear();
            Collapsed = false;
            Volume = 1;
            Pan = 0;
            Muted = Solo = false;
            Inserts.Clear();
            Automation = null;
        }
        else HiddenInTimeline = false;
    }
}

/// <summary>
/// What sends its sound somewhere (native only): a track's <see cref="Channel"/>, a <see cref="Folder"/> or a
/// <see cref="Bus"/>. Its whole output goes to <see cref="Output"/> and, at each send's level, to the buses in
/// <see cref="Sends"/>. Edited through <see cref="ProjectBuses"/>, which never lets a bus feed itself.
/// </summary>
public interface IRouted
{
    /// <summary>The bus its output goes to (an id from <see cref="ProjectSettings.Buses"/>); null = the master.</summary>
    string? Output { get; set; }
    /// <summary>Its sends to buses, one per bus at most.</summary>
    List<Send> Sends { get; set; }
}

/// <summary>The channel strip of a track: stays with the track when its clips change.</summary>
/// <summary>
/// A track's mixer channel. Its effects are insert slots (<see cref="Inserts"/>, native only) that can be added,
/// removed, repeated and reordered; a new channel starts with none (asked by the user, 2026-10-29: the three slots it
/// brought, all off, filled the inspector), and a factory template's tracks bring Vida → compressor → EQ
/// (<see cref="DefaultInserts"/>), the chain that was fixed before.
/// <see cref="Sat"/>, <see cref="Comp"/> and <see cref="Eq"/> are the first slot of each kind (what the inspector, the
/// timeline knobs and the compact strip edit). A kind with no slot gives a spare that isn't heard; turning it on
/// (<see cref="AdoptSpares"/>) puts it back in the chain where it went by default.
/// Projects of version 1 saved <c>sat</c>, <c>comp</c> and <c>eq</c> as fields: they come in through the
/// <c>Old*</c> properties and become the three slots in that order, so they sound the same.
/// </summary>
public sealed class Channel : IRouted
{
    public double Volume { get; set; } = 1;
    public double Pan { get; set; }
    public bool Muted { get; set; }
    public bool Solo { get; set; }
    /// <summary>The insert slots, in the order the sound goes through them.</summary>
    public List<InsertSlot>? Inserts { get; set; } = [];
    /// <summary>Native only: the bus its output goes to; null = the master. A track in a folder goes through the folder instead.</summary>
    public string? Output { get; set; }
    /// <summary>Native only: its sends to buses (<see cref="ProjectSettings.Buses"/>), one per bus at most.</summary>
    public List<Send> Sends { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Its send to the bus <paramref name="busId"/>, or null (no send: nothing goes there).</summary>
    public Send? SendTo(string busId) => Sends.Find(s => s.BusId == busId);

    // version 2's sends to the fixed reverb and delay, read only to turn them into sends to their buses (never written)
    [JsonPropertyName("reverbOn")] public bool? OldReverbOn { get; set; }
    [JsonPropertyName("reverbSend")] public double? OldReverbSend { get; set; }
    [JsonPropertyName("reverbPre")] public bool? OldReverbPre { get; set; }
    [JsonPropertyName("delayOn")] public bool? OldDelayOn { get; set; }
    [JsonPropertyName("delaySend")] public double? OldDelaySend { get; set; }
    [JsonPropertyName("delayPre")] public bool? OldDelayPre { get; set; }

    // version 1's fixed effects, read only to turn them into slots (never written: null after Upgrade)
    [JsonPropertyName("sat")] public SatSettings? OldSat { get; set; }
    [JsonPropertyName("comp")] public CompSettings? OldComp { get; set; }
    [JsonPropertyName("eq")] public EqSettings? OldEq { get; set; }

    SatSettings? spareSat;
    CompSettings? spareComp;
    EqSettings? spareEq;

    /// <summary>The first Vida slot (or the spare, when the channel has none).</summary>
    [JsonIgnore]
    public SatSettings Sat
    {
        get => First(InsertSlot.SatKind)?.Sat ?? (spareSat ??= new());
        set => Put(InsertSlot.SatKind, s => s.Sat = value);
    }

    /// <summary>The first compressor slot (or the spare).</summary>
    [JsonIgnore]
    public CompSettings Comp
    {
        get => First(InsertSlot.CompKind)?.Comp ?? (spareComp ??= new());
        set => Put(InsertSlot.CompKind, s => s.Comp = value);
    }

    /// <summary>The first EQ slot (or the spare).</summary>
    [JsonIgnore]
    public EqSettings Eq
    {
        get => First(InsertSlot.EqKind)?.Eq ?? (spareEq ??= new());
        set => Put(InsertSlot.EqKind, s => s.Eq = value);
    }

    /// <summary>The slots, with a version 1 channel turned into slots first.</summary>
    [JsonIgnore]
    public List<InsertSlot> Slots
    {
        get
        {
            Upgrade();
            return Inserts!;
        }
    }

    public static List<InsertSlot> DefaultInserts() =>
        [InsertSlot.New(InsertSlot.SatKind), InsertSlot.New(InsertSlot.CompKind), InsertSlot.New(InsertSlot.EqKind)];

    public InsertSlot? Slot(string id) => Slots.Find(s => s.Id == id);

    /// <summary>True when <paramref name="slot"/> is the first of its kind: the one the inspector and the timeline edit.</summary>
    public bool IsMain(InsertSlot slot) => First(slot.Type) == slot;

    InsertSlot? First(string kind) => Slots.Find(s => s.Type == kind);

    void Put(string kind, Action<InsertSlot> set)
    {
        var slot = First(kind);
        if (slot == null)
        {
            slot = InsertSlot.New(kind);
            Slots.Insert(DefaultPlace(kind), slot);
        }
        set(slot);
        slot.Normalize();
    }

    /// <summary>Where a slot of <paramref name="kind"/> goes back by default: after the slots that came before it in the old fixed chain.</summary>
    public int DefaultPlace(string kind)
    {
        int rank = Array.IndexOf(Dsp.TrackInserts.DefaultOrder, kind);
        var slots = Slots;
        int at = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            int r = Array.IndexOf(Dsp.TrackInserts.DefaultOrder, slots[i].Type);
            if (r >= 0 && r <= rank) at = i + 1;
        }
        return at;
    }

    /// <summary>
    /// A spare that was turned on (the inspector or a knob edited a kind the channel had no slot for) becomes a slot
    /// again, at its default place. Returns true when the slots changed. UI thread, after an edit.
    /// </summary>
    public bool AdoptSpares()
    {
        bool changed = false;
        if (spareSat is { On: true } s) { spareSat = null; Put(InsertSlot.SatKind, x => x.Sat = s); changed = true; }
        if (spareComp is { On: true } c) { spareComp = null; Put(InsertSlot.CompKind, x => x.Comp = c); changed = true; }
        if (spareEq is { On: true } e) { spareEq = null; Put(InsertSlot.EqKind, x => x.Eq = e); changed = true; }
        return changed;
    }

    void Upgrade()
    {
        if (OldSat == null && OldComp == null && OldEq == null)
        {
            Inserts ??= [];
            return;
        }
        Inserts =
        [
            InsertSlot.New(InsertSlot.SatKind, sat: OldSat ?? new()),
            InsertSlot.New(InsertSlot.CompKind, comp: OldComp ?? new()),
            InsertSlot.New(InsertSlot.EqKind, eq: OldEq ?? new()),
        ];
        OldSat = null;
        OldComp = null;
        OldEq = null;
    }

    internal void Normalize()
    {
        Upgrade();
        InsertSlot.NormalizeList(Inserts!);
        AdoptSpares();
        Sends ??= [];
        OldSend(ProjectBuses.ReverbId, OldReverbSend, OldReverbOn, OldReverbPre);
        OldSend(ProjectBuses.DelayId, OldDelaySend, OldDelayOn, OldDelayPre);
        OldReverbOn = OldReverbPre = OldDelayOn = OldDelayPre = null;
        OldReverbSend = OldDelaySend = null;
    }

    /// <summary>A version 2 send to the fixed reverb or delay, as a send to the bus that took its place.</summary>
    void OldSend(string busId, double? level, bool? on, bool? pre)
    {
        if (level is not { } v || !(v > 0) || !double.IsFinite(v) || SendTo(busId) != null) return;
        Sends.Add(new Send { BusId = busId, Level = Math.Min(v, 1), On = on ?? true, Pre = pre ?? false });
    }
}

/// <summary>
/// A send to a bus (native only), from a track, a folder or another bus: how much of it goes there (0..1), its ON, and
/// whether it is taken before the fader (<see cref="Pre"/>). A send to a bus that no
/// longer exists is dropped when the project is normalized (<see cref="ProjectBuses.Normalize"/>).
/// </summary>
public sealed class Send
{
    public string BusId { get; set; } = "";
    /// <summary>Version 2 called it the return's id: read only (never written), it becomes <see cref="BusId"/>.</summary>
    [JsonPropertyName("returnId")]
    public string? OldReturnId { get => null; set { if (!string.IsNullOrEmpty(value) && BusId.Length == 0) BusId = value; } }
    public double Level { get; set; }
    public bool On { get; set; } = true;
    public bool Pre { get; set; }
    /// <summary>
    /// Taken out of the track's (or folder's) list of buses (asked by the user: not every track wants every bus): off and
    /// not shown, its level kept for when it is added back. A bus without a send isn't in the list either: a new track
    /// or bus starts with none, the user adds them (<see cref="ProjectBuses.Shown"/>).
    /// </summary>
    public bool Hidden { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// A bus (native only): it gathers what is routed to it (the <see cref="IRouted.Output"/> of tracks, folders and other
/// buses) and what is sent to it (<see cref="IRouted.Sends"/>), runs the sum through its insert slots (a reverb or a delay
/// is one more slot: <see cref="InsertSlot.Reverb"/>, <see cref="InsertSlot.Delay"/>), its pan, fader and mute, and passes
/// it on to its own <see cref="Output"/> and sends. One idea for what other mixers split into buses and FX returns (the
/// user's call); version 2's fixed Reverb and Delay and its extra returns became buses. Its parameters carry lanes like a
/// folder's (asked by the user, #267: its rows go at the end of the timeline, like aux tracks), read by its node.
/// </summary>
public sealed class Bus : IRouted, IAutomated
{
    public string Id { get; set; } = Ids.New("bus");
    public string Name { get; set; } = "";
    /// <summary>Its fader, 0..1.5 like a track's.</summary>
    public double Volume { get; set; } = 1;
    /// <summary>-1 (left) .. 1 (right), like a track's.</summary>
    public double Pan { get; set; }
    public bool Muted { get; set; }
    /// <summary>"#rrggbb" or null (no color), like <see cref="Track.Color"/>.</summary>
    public string? Color { get; set; }
    /// <summary>An icon id from the tracks' set (<see cref="Track.Icon"/>); null = the bus icon every bus without one shows.</summary>
    public string? Icon { get; set; }
    public List<InsertSlot> Inserts { get; set; } = [];
    /// <inheritdoc/>
    public string? Output { get; set; }
    /// <inheritdoc/>
    public List<Send> Sends { get; set; } = [];
    /// <summary>The mixer side its strip sits on (<see cref="MixerSides"/>); null = the right, where buses go by default.</summary>
    public string? MixerSide { get; set; }
    /// <summary>
    /// The mixer strip it follows (asked by the user, #270: buses go between the channels too): the id of a top-level
    /// track, folder or separator, <see cref="MixerSides.First"/> for the start of its side, or null (and an id that is
    /// gone) for after the channels of its side, as before.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MixerAfter { get; set; }
    /// <summary>Its automated parameters (<see cref="Automations"/>), lanes under its row at the end of the timeline; null = none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<AutomationLane>? Automation { get; set; }
    /// <summary>Its lanes are folded under its row (they keep playing), like <see cref="Track.AutomationFolded"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool AutomationFolded { get; set; }
    IReadOnlyList<InsertSlot> IAutomated.AutoSlots => Inserts;
    IRouted IAutomated.AutoRouted => this;
    double IAutomated.AutoVolume => Volume;
    double IAutomated.AutoPan => Pan;
    bool IAutomated.AutoMuted => Muted;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    internal void Normalize()
    {
        if (Automation != null)
        {
            Automation.RemoveAll(l => l == null);
            foreach (var l in Automation) l.Normalize();
            if (Automation.Count == 0) Automation = null;
        }
        // "right" is kept: with another default dock (#301) it is a place the user chose
        MixerSide = MixerSide == MixerSides.LeftDock ? MixerSides.Left : MixerSide is MixerSides.Left or MixerSides.Right ? MixerSide : null;
        MixerAfter = string.IsNullOrWhiteSpace(MixerAfter) || MixerAfter.Length > 64 ? null : MixerAfter.Trim();
        if (string.IsNullOrEmpty(Id)) Id = Ids.New("bus");
        Name ??= "";
        Icon = string.IsNullOrWhiteSpace(Icon) || Icon.Length > 64 ? null : Icon.Trim();
        Color = Track.NormalizeColor(Color);
        Volume = double.IsFinite(Volume) ? Math.Clamp(Volume, 0, 1.5) : 1;
        Pan = double.IsFinite(Pan) ? Math.Clamp(Pan, -1, 1) : 0;
        Inserts ??= [];
        InsertSlot.NormalizeList(Inserts);
        Sends ??= [];
    }
}

/// <summary>
/// One insert slot of a <see cref="Channel"/>: its kind and the settings of that kind (the others stay null). A kind
/// this version doesn't know is kept as it came, through <see cref="Extra"/>, and not heard; so is a VST3 slot that
/// doesn't say which plugin it is.
/// </summary>
public sealed class InsertSlot
{
    public const string SatKind = Dsp.TrackInserts.Sat, CompKind = Dsp.TrackInserts.Comp, EqKind = Dsp.TrackInserts.Eq,
        ReverbKind = Dsp.TrackInserts.Reverb, DelayKind = Dsp.TrackInserts.Delay, GateKind = Dsp.TrackInserts.Gate,
        VstKind = Dsp.TrackInserts.Vst;

    public string Id { get; set; } = Ids.New("fx");
    /// <summary>sat | comp | eq | gate | reverb | delay | vst | tonal | a master stage (<see cref="Dsp.TrackInserts.IsStage"/>)…</summary>
    public string Type { get; set; } = EqKind;
    public SatSettings? Sat { get; set; }
    public CompSettings? Comp { get; set; }
    public EqSettings? Eq { get; set; }
    /// <summary>A reverb or delay slot's settings; their <c>Muted</c> is the slot's OFF.</summary>
    public ReverbSettings? Reverb { get; set; }
    public DelaySettings? Delay { get; set; }
    public GateSettings? Gate { get; set; }
    /// <summary>A VST3 effect slot's plugin and its ON.</summary>
    public VstSlotSettings? Vst { get; set; }
    /// <summary>A master effect's ON and its values (<see cref="FxDefs"/>).</summary>
    public StageSettings? Stage { get; set; }
    /// <summary>A tonal balance's own target, made from the user's references (#210); null = it has none.</summary>
    public TonalCurveSettings? Tonal { get; set; }
    /// <summary>The effect's level, 0 to 1 (<see cref="Dsp.FxShell"/>).</summary>
    public double Wet { get; set; } = 1;
    /// <summary>The dry signal's level, 0 to 1, on its own knob: both at 100 % is a parallel effect.</summary>
    public double Dry { get; set; }
    /// <summary>Its output, dB.</summary>
    public double OutputDb { get; set; }
    /// <summary>
    /// The track it listens to instead of its own input (a sidechain: a compressor ducked by the kick), by its id; null = its
    /// own. Only the kinds that take one (<see cref="Dsp.TrackInserts.TakesKey"/>). A track that no longer exists leaves it
    /// listening to its own input, and the id stays so undoing the removal brings it back.
    /// </summary>
    public string? Sidechain { get; set; }
    /// <summary>
    /// Folded in the mixer (asked by the user): only its title row, with a summary, so a long chain fits. A view choice
    /// kept with the project, like a folder's fold; it changes nothing in the sound.
    /// </summary>
    public bool Collapsed { get; set; }
    /// <summary>
    /// Its window opens in the full view, with every control in inner panels (asked by the user, #140), instead of the
    /// compact one; only an effect with both (<see cref="FxDefs.Layout"/>). A view choice kept with the project, per slot.
    /// </summary>
    public bool Full { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public bool Known => Dsp.TrackInserts.IsKind(Type) && (Type != VstKind || Vst?.Plugin is { Path.Length: > 0, ClassId.Length: > 0 });

    /// <summary>Whether it sounds: its kind's own ON.</summary>
    [JsonIgnore]
    public bool On
    {
        get => Type switch
        {
            SatKind => Sat?.On == true, CompKind => Comp?.On == true, EqKind => Eq?.On == true,
            ReverbKind => Reverb?.Muted == false, DelayKind => Delay?.Muted == false, GateKind => Gate?.On == true, VstKind => Vst?.On == true, _ => Stage?.On == true,
        };
        set
        {
            Normalize();
            switch (Type)
            {
                case SatKind: Sat!.On = value; break;
                case CompKind: Comp!.On = value; break;
                case EqKind: Eq!.On = value; break;
                case ReverbKind: Reverb!.Muted = !value; break;
                case DelayKind: Delay!.Muted = !value; break;
                case GateKind: Gate!.On = value; break;
                case VstKind: Vst!.On = value; break;
                default: if (Stage != null) Stage.On = value; break;
            }
        }
    }

    public static InsertSlot New(string kind, SatSettings? sat = null, CompSettings? comp = null, EqSettings? eq = null)
    {
        var s = new InsertSlot { Type = kind, Sat = sat, Comp = comp, Eq = eq };
        s.Normalize();
        return s;
    }

    /// <summary>A list of slots (a channel's or a folder's) without nulls, each normalized, no two with one id.</summary>
    internal static void NormalizeList(List<InsertSlot> slots)
    {
        slots.RemoveAll(s => s == null);
        foreach (var s in slots) s.Normalize();
        // two slots with one id would share one effect in the engine
        var seen = new HashSet<string>();
        foreach (var s in slots)
            if (!seen.Add(s.Id)) { s.Id = Ids.New("fx"); seen.Add(s.Id); }
    }

    internal void Normalize()
    {
        if (string.IsNullOrEmpty(Id)) Id = Ids.New("fx");
        if (string.IsNullOrWhiteSpace(Sidechain) || !Dsp.TrackInserts.TakesKey(Type)) Sidechain = null;
        // a slot saved with the single Mix knob they replaced: the same balance on the two
        if (Extra?.Remove("mix", out var mix) == true && mix.TryGetDouble(out double m) && double.IsFinite(m))
        {
            Wet = Math.Clamp(m, 0, 1);
            Dry = 1 - Wet;
        }
        Wet = double.IsFinite(Wet) ? Math.Clamp(Wet, 0, 1) : 1;
        Dry = double.IsFinite(Dry) ? Math.Clamp(Dry, 0, 1) : 0;
        OutputDb = double.IsFinite(OutputDb) ? Math.Clamp(OutputDb, FxDefs.Output.Min, FxDefs.Output.Max) : 0;
        if (Type != Dsp.TrackInserts.Tonal || Tonal?.Normalize() == false) Tonal = null;
        if (Dsp.TrackInserts.IsMeter(Type))
        {
            // an analyzer never changes the sound: no mix, no output
            (Wet, Dry, OutputDb) = (1, 0, 0);
        }
        if (Dsp.TrackInserts.IsStage(Type) || Dsp.TrackInserts.IsMod(Type) || Dsp.TrackInserts.IsModel(Type) || Dsp.TrackInserts.IsMeter(Type))
        {
            Stage ??= new();
            Stage.Normalize(Type);
            if (Type == Dsp.TrackInserts.Tonal && Tonal == null && FxDefs.TonalIdOf(this) == FxDefs.TonalOwnId) Stage.Values["target"] = 0;
            Sat = null; Comp = null; Eq = null; Reverb = null; Delay = null; Gate = null; Vst = null;
            return;
        }
        Stage = null;
        switch (Type)
        {
            case SatKind: Sat ??= new(); Comp = null; Eq = null; Reverb = null; Delay = null; Gate = null; Vst = null; break;
            case CompKind: Comp ??= new(); Comp.Eq ??= new(); Sat = null; Eq = null; Reverb = null; Delay = null; Gate = null; Vst = null; break;
            case EqKind: Eq ??= new(); Eq.Normalize(); Sat = null; Comp = null; Reverb = null; Delay = null; Gate = null; Vst = null; break;
            case ReverbKind: Reverb ??= new(); Sat = null; Comp = null; Eq = null; Delay = null; Gate = null; Vst = null; break;
            case DelayKind: Delay ??= new(); Delay.Mode ??= "stereo"; Delay.Division ??= "1/8."; Sat = null; Comp = null; Eq = null; Reverb = null; Gate = null; Vst = null; break;
            case GateKind: Gate ??= new(); Gate.Normalize(); Sat = null; Comp = null; Eq = null; Reverb = null; Delay = null; Vst = null; break;
            case VstKind: Vst ??= new(); Vst.Plugin ??= new(); Vst.Plugin.Normalize(); Sat = null; Comp = null; Eq = null; Reverb = null; Delay = null; Gate = null; break;
        }
    }
}

/// <summary>A master effect in a slot: its ON and its knobs by their <see cref="FxParam.Id"/>; what is missing starts at its default.</summary>
public sealed class StageSettings
{
    public bool On { get; set; }
    public Dictionary<string, double> Values { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public double Get(string id, double fallback) => Values.TryGetValue(id, out double v) ? v : fallback;

    internal void Normalize(string kind)
    {
        Values ??= [];
        foreach (var p in FxDefs.Params(kind))
        {
            double v = Get(p.Id, p.Default);
            bool off = p.OffAtZero && v <= 0;
            Values[p.Id] = off ? 0 : double.IsFinite(v) ? p.Snap(Math.Clamp(v, p.Min, p.Max)) : p.Default;
        }
    }
}

/// <summary>
/// A VST3 effect in a slot (native only): which plugin, with its sound as the user left it in its window
/// (<see cref="PluginRef.State"/>, saved outside the undo like an instrument's), and whether it is on. It runs in its own
/// process (<c>VstBridge</c>), opened by the window for playback and by the export for its own.
/// </summary>
public sealed class VstSlotSettings
{
    public bool On { get; set; } = true;
    public PluginRef Plugin { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// The noise gate: how much it closes (<see cref="Mode"/>: soft | normal | hard, <see cref="Dsp.GateParams"/>) and where
/// it opens (dBFS, absolute: the noise it removes doesn't depend on the take's level the way Vida does).
/// </summary>
public sealed class GateSettings
{
    public bool On { get; set; }
    public string Mode { get; set; } = Dsp.GateParams.Normal;
    public double Threshold { get; set; } = Dsp.GateParams.DefaultThresholdDb;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    internal void Normalize()
    {
        Mode = Dsp.GateParams.ParseMode(Mode);
        Threshold = double.IsFinite(Threshold) ? Math.Clamp(Threshold, Dsp.GateParams.MinThresholdDb, Dsp.GateParams.MaxThresholdDb) : Dsp.GateParams.DefaultThresholdDb;
    }
}

public sealed class SatSettings
{
    public bool On { get; set; }
    /// <summary>tube | tape</summary>
    public string Mode { get; set; } = "tube";
    public double Amount { get; set; } = 0.3;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class CompSettings
{
    public bool On { get; set; }
    /// <summary>A preset id of the web app (rhythm, lead…) or "custom".</summary>
    public string Preset { get; set; } = Dsp.CompPresets.DefaultId;
    public double Amount { get; set; } = 0.5;
    /// <summary>The preset it was on before a knob turned it "custom": moving Amount goes back to it.</summary>
    public string? ManualFrom { get; set; }
    /// <summary>The compressor's analyze button was used: the trims hold what it found.</summary>
    public bool Analyzed { get; set; }
    /// <summary>How far the take's measured threshold and make-up are from the preset's fixed ones (dB).</summary>
    public double ThresholdTrim { get; set; }
    public double MakeupTrim { get; set; }
    public bool Limit { get; set; } = true;
    public double Threshold { get; set; } = -18;
    public double Ratio { get; set; } = 4;
    public double Attack { get; set; } = 0.01;
    public double Release { get; set; } = 0.15;
    public double Knee { get; set; } = 6;
    public double Makeup { get; set; }
    public CompEq Eq { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The small EQ around the compressor that comes with its presets.</summary>
public sealed class CompEq
{
    public double HpfHz { get; set; } = 10;
    public double MudDb { get; set; }
    public double PresenceDb { get; set; }
    public double PresenceHz { get; set; } = 2500;
    public double AirDb { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Track EQ: always 7 bands (low cut, 5 bells, high cut), any band can change type.</summary>
public sealed class EqSettings
{
    public bool On { get; set; }
    public List<EqBand> Bands { get; set; } = Defaults();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static List<EqBand> Defaults() =>
    [
        new() { Type = "highpass", Freq = 80, Q = 0.7 },
        new() { Type = "peaking", Freq = 250 },
        new() { Type = "peaking", Freq = 800 },
        new() { Type = "peaking", Freq = 2000 },
        new() { Type = "peaking", Freq = 4500 },
        new() { Type = "peaking", Freq = 8000 },
        new() { Type = "lowpass", Freq = 12000, Q = 0.7 },
    ];

    /// <summary>The bands an EQ had before the seventh: it goes in, off, before the last one, so the high cut stays last.</summary>
    const int OldBandCount = 6;

    internal void Normalize()
    {
        Bands ??= [];
        Bands.RemoveAll(b => b == null);
        var d = Defaults();
        if (Bands.Count == OldBandCount) Bands.Insert(OldBandCount - 1, d[OldBandCount - 1]);
        while (Bands.Count < d.Count) Bands.Add(d[Bands.Count]);
        if (Bands.Count > d.Count) Bands.RemoveRange(d.Count, Bands.Count - d.Count);
        foreach (var b in Bands) b.Normalize();
    }
}

public sealed class EqBand
{
    public bool On { get; set; }
    /// <summary>highpass | lowshelf | peaking | highshelf | lowpass</summary>
    public string Type { get; set; } = "peaking";
    public double Freq { get; set; } = 1000;
    public double Gain { get; set; }
    public double Q { get; set; } = 1;
    /// <summary>12 or 24 dB/oct, cuts only.</summary>
    public int Slope { get; set; } = 12;
    /// <summary>A bell or shelf that also moves with the signal (<see cref="Dsp.EqDynamics"/>): where it starts, how far it goes and how fast.</summary>
    public bool Dyn { get; set; }
    public double DynThreshold { get; set; } = Dsp.EqDynamics.DefaultThresholdDb;
    public double DynRange { get; set; } = Dsp.EqDynamics.DefaultRangeDb;
    public double DynAttackMs { get; set; } = Dsp.EqDynamics.DefaultAttackMs;
    public double DynReleaseMs { get; set; } = Dsp.EqDynamics.DefaultReleaseMs;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    internal void Normalize()
    {
        static double Fin(double v, double lo, double hi, double fallback) => double.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;
        DynThreshold = Fin(DynThreshold, Dsp.EqDynamics.ThresholdMinDb, 0, Dsp.EqDynamics.DefaultThresholdDb);
        DynRange = Fin(DynRange, -Dsp.EqDynamics.RangeMaxDb, Dsp.EqDynamics.RangeMaxDb, Dsp.EqDynamics.DefaultRangeDb);
        DynAttackMs = Fin(DynAttackMs, Dsp.EqDynamics.AttackMinMs, Dsp.EqDynamics.AttackMaxMs, Dsp.EqDynamics.DefaultAttackMs);
        DynReleaseMs = Fin(DynReleaseMs, Dsp.EqDynamics.ReleaseMinMs, Dsp.EqDynamics.ReleaseMaxMs, Dsp.EqDynamics.DefaultReleaseMs);
    }
}

/// <summary>A piece of a media on a track.</summary>
public sealed class Clip
{
    public string Id { get; set; } = Ids.New("clp");
    public string MediaId { get; set; } = "";
    /// <summary>Timeline time of the media's sample 0 (includes the sync correction).</summary>
    public double AtSec { get; set; }
    /// <summary>
    /// How many times longer than its media it plays: where the tempo (<see cref="TempoFollow"/>) and a stretch by hand
    /// (<see cref="ClipEditMode.StretchEnd"/>) left it, 1 = as recorded. Every time of the clip is a time of the timeline, so they already count it; what reads the media
    /// goes through <see cref="MediaSec"/>.
    /// </summary>
    public double Stretch { get; set; } = 1;
    /// <summary>Optional per-clip correction; null inherits the immutable recording correction.</summary>
    public double? SyncMs { get; set; }
    /// <summary>Optional camera latency; null inherits the recording's camera latency.</summary>
    public double? VideoOffsetMs { get; set; }
    /// <summary>Its place and length are fixed, like every clip of a locked track (<see cref="ClipEditing.IsLocked"/>). Native only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Locked { get; set; }
    /// <summary>
    /// Native only: it plays its media backward (<see cref="ClipEditing.Reverse"/>); the file stays as it is. Its trims,
    /// <see cref="AtSec"/> and curve count in the direction it is heard, so sample 0 is the media's last one.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Reversed { get; set; }
    /// <summary>
    /// Native only: semitones of its own, on top of its track's <see cref="Track.Transpose"/> (asked by the user), so one
    /// clip can change key without the rest of the track. Whole semitones; the length stays (<see cref="SemitonesOn"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Pitch { get; set; }
    /// <summary>
    /// Native only: this clip is a repeat of the clip with this id on its track, made by looping it (<see cref="ClipLoops"/>,
    /// asked by the user, like GarageBand). It is a whole clip, so playback, the video and the export need nothing else;
    /// the loop remakes its repeats from the source whenever the source changes. Null on every other clip.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LoopOf { get; set; }
    /// <summary>
    /// Native only: a name of its own (asked by the user), drawn on the clip; null = none, and the clip goes by its
    /// media's name (<see cref="ClipEditing.Rename"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
    /// <summary>How far it sounds moved on <paramref name="track"/>: the track's and its own, up to what the shifter does.</summary>
    public double SemitonesOn(Track track) => Math.Clamp(track.Transpose + Pitch, -Dsp.PitchShifter.MaxSemitones, Dsp.PitchShifter.MaxSemitones);
    /// <summary>What it plays, for the transitions: two pieces of one take only join as one going the same way, at the same stretch and pitch.</summary>
    public string TakeKey() => MediaId + (Reversed ? "<" : "") + (Stretch == 1 ? "" : "x" + Stretch.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
        + (Pitch == 0 ? "" : "p" + Pitch.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

    public double EffectiveSyncMs(Media? media) => SyncMs ?? media?.SyncMs ?? 0;
    public double EffectiveVideoOffsetMs(Media? media) => VideoOffsetMs ?? media?.VideoOffsetMs ?? 80;
    /// <summary>A length on the timeline as a length of the media.</summary>
    public double MediaSec(double sec) => sec / Stretch;

    internal void Normalize()
    {
        Framing ??= new();
        if (!double.IsFinite(Stretch) || Stretch <= 0) Stretch = 1;
        Pitch = double.IsFinite(Pitch) ? Math.Clamp(Pitch, -Dsp.PitchShifter.MaxSemitones, Dsp.PitchShifter.MaxSemitones) : 0;
        if (SyncMs is { } sync && !double.IsFinite(sync)) SyncMs = null;
        if (VideoOffsetMs is { } video && !double.IsFinite(video)) VideoOffsetMs = null;
        if (!double.IsFinite(GainDb)) GainDb = 0;
        GainDb = Math.Clamp(GainDb, MinGainDb, MaxGainDb);
        GainCurve = (GainCurve ?? []).Where(g => g != null && double.IsFinite(g.Sec) && double.IsFinite(g.Db))
            .OrderBy(g => g.Sec).ToList();
        foreach (var g in GainCurve) g.Db = Math.Clamp(g.Db, Dsp.ClipGainCurve.MinDb, Dsp.ClipGainCurve.MaxDb);
        Transition = ClipTransitions.Id(ClipTransitions.Parse(Transition));
        TransitionSec = ClipTransitions.ClampSec(TransitionSec);
        Name = ClipEditing.CleanName(Name);
    }

    /// <summary>Range of <see cref="GainDb"/> (the inspector's slider).</summary>
    public const double MinGainDb = -24, MaxGainDb = 12;

    /// <summary>The curve as the audio thread reads it: a new array, never changed afterwards.</summary>
    public Dsp.ClipGainCurve.Point[] CurvePoints() => GainCurve.Select(g => new Dsp.ClipGainCurve.Point(g.Sec, g.Db)).ToArray();
    /// <summary>Hidden from the start / end of the media, without moving the rest.</summary>
    public double TrimStartSec { get; set; }
    public double TrimEndSec { get; set; }
    public double FadeInSec { get; set; }
    public double FadeOutSec { get; set; }
    /// <summary>The video fades with the audio fades; otherwise with its own times.</summary>
    public bool LinkVideoFades { get; set; } = true;
    public double VideoFadeInSec { get; set; }
    public double VideoFadeOutSec { get; set; }
    /// <summary>
    /// Native only: how this clip comes in from the one that ends where it starts on its track ("cut", "crossfade",
    /// "dip"; <see cref="ClipTransitions"/>): the picture and, crossfaded, the sound; the clip's own fades stay as they are.
    /// Ignored while nothing touches its start.
    /// </summary>
    public string Transition { get; set; } = ClipTransitions.CutId;
    /// <summary>Length of <see cref="Transition"/>, centered on the join.</summary>
    public double TransitionSec { get; set; } = ClipTransitions.DefaultSec;
    /// <summary>Clip gain, before the track's channel strip.</summary>
    public double GainDb { get; set; }
    /// <summary>
    /// Native only: the clip's volume curve on top of <see cref="GainDb"/> (<see cref="Dsp.ClipGainCurve"/>), in
    /// seconds from the media's sample 0. Empty = flat. Edits assign a new list (the audio thread reads a copy).
    /// </summary>
    public List<GainPoint> GainCurve { get; set; } = [];
    public Framing Framing { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A point of a clip's volume curve: seconds from the media's sample 0 and dB (<see cref="Dsp.ClipGainCurve"/>).</summary>
public sealed class GainPoint
{
    public double Sec { get; set; }
    public double Db { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>How the video sits in its panel: zoom around a pan point (-1..1), optional mirror.</summary>
public sealed class Framing
{
    public double Zoom { get; set; } = 1;
    public double PanX { get; set; }
    public double PanY { get; set; }
    public bool Mirror { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ExportRecord
{
    public string File { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? Layout { get; set; }
    public string? Aspect { get; set; }
    public string? Quality { get; set; }
    public double DurationSec { get; set; }
    public long Bytes { get; set; }
    /// <summary>Native only: integrated loudness of the exported mix; null = not measured (older exports, silence).</summary>
    public double? Lufs { get; set; }
    /// <summary>Native only: the social preset it was exported with; null = the project's frame.</summary>
    public string? Target { get; set; }
    /// <summary>The takes in the video (media ids), shown as "n canales" in Historial.</summary>
    public List<string> TakeIds { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A named point of the song on the timeline (native only): to jump there and to know where the solo starts.</summary>
public sealed class Marker
{
    public string Id { get; set; } = Ids.New("mrk");
    public string Name { get; set; } = "";
    public double Sec { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A detected chord: its own kind of mark, a block in time on its own strip, so a re-detection can replace
/// them and they can go all at once without touching what the user wrote.</summary>
public sealed class ChordMarker
{
    public string Id { get; set; } = Ids.New("chd");
    public string Name { get; set; } = "";
    public double Sec { get; set; }
    public double EndSec { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One line of the project's activity log.</summary>
public sealed class LogEntry
{
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public static class Ids
{
    public static string New(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];
}

/// <summary>
/// The mixer's two sides (asked by the user, #275 and #277, down to two in #303): every strip sits on the left side (the
/// one that scrolls and takes the rest) or the right dock, and the user moves it from one to the other. Tracks and
/// folders go on the left and buses on the right unless they say otherwise; the default is saved as null, so a project
/// only writes the strips that moved. "leftDock" (#277's third dock, gone in #303) reads as the left side.
/// </summary>
public static class MixerSides
{
    public const string Left = "left", Right = "right", LeftDock = "leftDock";
    /// <summary>A <see cref="Bus.MixerAfter"/> that puts the bus first on its side.</summary>
    public const string First = "first";
    /// <summary>The sides by index, from left to right.</summary>
    public const int LeftSide = 0, RightSide = 1;

    /// <summary>A known side, with the default (and anything else) as null; the old left dock is the left side.</summary>
    public static string? Normalize(string? side, string defaultSide)
    {
        if (side == LeftDock) side = Left;
        return side is Left or Right && side != defaultSide ? side : null;
    }

    public static bool IsRight(Track t) => t.MixerSide == Right;
    public static bool IsRight(Folder f) => f.MixerSide == Right;
    public static bool IsRight(Bus b) => Of(b) == RightSide;

    /// <summary>
    /// The side of a bus left in its default place (null): the right dock unless the user picks the left side in the
    /// settings (asked by the user, #301). A machine setting, not the project's: the window sets it from config.json.
    /// </summary>
    public static int BusDefault { get; set; } = RightSide;

    /// <summary>The side a strip sits in (<see cref="LeftSide"/> or <see cref="RightSide"/>).</summary>
    public static int Of(Track t) => ChannelIndex(t.MixerSide);
    public static int Of(Folder f) => ChannelIndex(f.MixerSide);
    public static int Of(Bus b) => b.MixerSide switch { Left or LeftDock => LeftSide, Right => RightSide, _ => BusDefault };

    /// <summary>What a track or folder saves for side <paramref name="side"/>; a bus, <see cref="ForBus"/>.</summary>
    public static string? ForChannel(int side) => side == RightSide ? Right : null;
    /// <summary>What a bus saves for side <paramref name="side"/>: null in the <see cref="BusDefault"/> one, so it follows the setting.</summary>
    public static string? ForBus(int side) => side == BusDefault ? null : side == RightSide ? Right : Left;

    static int ChannelIndex(string? side) => side == Right ? RightSide : LeftSide;
}
