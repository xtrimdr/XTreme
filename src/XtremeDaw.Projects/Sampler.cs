using System.Text.Json;
using System.Text.Json.Serialization;
using XtremeDaw.Audio;
using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.Projects;

/// <summary>
/// The native sampler of a MIDI track (<see cref="Track.Sampler"/>, asked by the user, 2026-11-02: one sample for now):
/// its sample, a <see cref="Media"/> of the project like any import, and every setting of its window. Positions are
/// fractions of the sound as it plays (after Reverse), so they survive anything but a new sample; times in seconds.
/// </summary>
public sealed class SamplerSettings
{
    public const string Gate = "gate", OneShot = "one-shot";
    public const string LoopOff = "off", LoopForward = "forward", LoopPingPong = "ping-pong";
    public const string FilterOff = "off";
    public static readonly string[] Filters = [FilterOff, "lp12", "lp24", "hp12", "hp24", "bp", "notch"];
    public static readonly string[] LfoShapes = ["sine", "triangle", "saw", "square", "random"];
    /// <summary>The longest attack, decay and release.</summary>
    public const double MaxEnvSec = 20;
    public const double MinCutoff = SamplerKnobs.MinCutoff, MaxCutoff = SamplerKnobs.MaxCutoff;
    public const double MinLfoHz = 0.05, MaxLfoHz = 30;
    public const double MaxTune = 48, MaxPitchEnv = 48, MaxLfoPitch = SamplerKnobs.MaxLfoPitch, MaxLfoFilter = SamplerKnobs.MaxLfoFilter;
    public const double MaxGlideSec = 2, MaxLfoDelay = 5, MinPitchDecay = 0.005, MaxPitchDecay = 2;
    public const double MinGainDb = SamplerKnobs.MinGainDb, MaxGainDb = SamplerKnobs.MaxGainDb;
    /// <summary>Beats of one LFO lap the Sync menu offers; 0 = free, in Hz.</summary>
    public static readonly double[] LfoBeats = [0, 8, 4, 2, 1, 0.5, 0.25];

    /// <summary>The sample; null = a sampler still waiting for one.</summary>
    public string? MediaId { get; set; }
    public double Start { get; set; }
    public double End { get; set; } = 1;
    public bool Reverse { get; set; }
    /// <summary>Lifts the quietest samples to full level without touching the file.</summary>
    public bool Normalize { get; set; }
    /// <summary>"gate": it follows the note. "one-shot": it plays to its end.</summary>
    public string Mode { get; set; } = Gate;
    public string Loop { get; set; } = LoopOff;
    public double LoopStart { get; set; }
    public double LoopEnd { get; set; } = 1;
    /// <summary>How much of the loop the seam crossfades over, 0..1.</summary>
    public double LoopFade { get; set; }
    /// <summary>The key that plays it as it is (C4 = 60).</summary>
    public int Root { get; set; } = 60;
    public int Transpose { get; set; }
    /// <summary>Cents.</summary>
    public double Fine { get; set; }
    public bool KeyTrack { get; set; } = true;
    public int Voices { get; set; } = 16;
    public bool Legato { get; set; }
    public double GlideSec { get; set; }
    public double GainDb { get; set; }
    public double Pan { get; set; }
    public double VelAmp { get; set; } = 0.7;
    public double Attack { get; set; }
    public double Decay { get; set; } = 0.1;
    public double Sustain { get; set; } = 1;
    public double Release { get; set; } = 0.15;
    public string Filter { get; set; } = FilterOff;
    public double Cutoff { get; set; } = 2000;
    public double Resonance { get; set; }
    public double Drive { get; set; }
    public double FilterKey { get; set; }
    public double FilterVel { get; set; }
    public double FilterEnv { get; set; }
    public double FilterAttack { get; set; }
    public double FilterDecay { get; set; } = 0.3;
    public double FilterSustain { get; set; }
    public double FilterRelease { get; set; } = 0.2;
    public string LfoShape { get; set; } = "sine";
    public double LfoHz { get; set; } = 5;
    /// <summary>Beats of a lap, following the tempo; 0 = <see cref="LfoHz"/>.</summary>
    public double LfoBeat { get; set; }
    public double LfoDelay { get; set; }
    public double LfoPitch { get; set; }
    public double LfoFilter { get; set; }
    public double LfoAmp { get; set; }
    public double PitchEnv { get; set; }
    public double PitchDecay { get; set; } = 0.05;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    static double Fix(double v, double fallback, double min, double max) => double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

    /// <summary>Every value back in its range, what <see cref="Track"/> does on opening and the window after each edit.</summary>
    public void Normalized()
    {
        Start = Fix(Start, 0, 0, 1);
        End = Fix(End, 1, 0, 1);
        if (End <= Start) (Start, End) = (0, 1);
        Mode = Mode == OneShot ? OneShot : Gate;
        Loop = Loop is LoopForward or LoopPingPong ? Loop : LoopOff;
        LoopStart = Fix(LoopStart, 0, 0, 1);
        LoopEnd = Fix(LoopEnd, 1, 0, 1);
        if (LoopEnd <= LoopStart) (LoopStart, LoopEnd) = (0, 1);
        LoopFade = Fix(LoopFade, 0, 0, 1);
        Root = Math.Clamp(Root, 0, 127);
        Transpose = Math.Clamp(Transpose, -(int)MaxTune, (int)MaxTune);
        Fine = Fix(Fine, 0, -100, 100);
        Voices = Math.Clamp(Voices, 1, Sampler.MaxVoices);
        GlideSec = Fix(GlideSec, 0, 0, MaxGlideSec);
        GainDb = Fix(GainDb, 0, MinGainDb, MaxGainDb);
        Pan = Fix(Pan, 0, -1, 1);
        VelAmp = Fix(VelAmp, 0.7, 0, 1);
        Attack = Fix(Attack, 0, 0, MaxEnvSec);
        Decay = Fix(Decay, 0.1, 0, MaxEnvSec);
        Sustain = Fix(Sustain, 1, 0, 1);
        Release = Fix(Release, 0.15, 0, MaxEnvSec);
        Filter = Filters.Contains(Filter) ? Filter : FilterOff;
        Cutoff = Fix(Cutoff, 2000, MinCutoff, MaxCutoff);
        Resonance = Fix(Resonance, 0, 0, 1);
        Drive = Fix(Drive, 0, 0, 1);
        FilterKey = Fix(FilterKey, 0, 0, 1);
        FilterVel = Fix(FilterVel, 0, 0, 1);
        FilterEnv = Fix(FilterEnv, 0, -1, 1);
        FilterAttack = Fix(FilterAttack, 0, 0, MaxEnvSec);
        FilterDecay = Fix(FilterDecay, 0.3, 0, MaxEnvSec);
        FilterSustain = Fix(FilterSustain, 0, 0, 1);
        FilterRelease = Fix(FilterRelease, 0.2, 0, MaxEnvSec);
        LfoShape = LfoShapes.Contains(LfoShape) ? LfoShape : LfoShapes[0];
        LfoHz = Fix(LfoHz, 5, MinLfoHz, MaxLfoHz);
        LfoBeat = LfoBeats.Contains(LfoBeat) ? LfoBeat : 0;
        LfoDelay = Fix(LfoDelay, 0, 0, MaxLfoDelay);
        LfoPitch = Fix(LfoPitch, 0, 0, MaxLfoPitch);
        LfoFilter = Fix(LfoFilter, 0, 0, MaxLfoFilter);
        LfoAmp = Fix(LfoAmp, 0, 0, 1);
        PitchEnv = Fix(PitchEnv, 0, -MaxPitchEnv, MaxPitchEnv);
        PitchDecay = Fix(PitchDecay, 0.05, MinPitchDecay, MaxPitchDecay);
    }

    /// <summary>What the voices read; <paramref name="sound"/> gives Normalizar its gain. A new one on every edit and tempo change.</summary>
    public SamplerPatch Patch(double bpm, SamplerSound? sound) => new()
    {
        Start = Start,
        End = End,
        OneShot = Mode == OneShot,
        Root = Root,
        Tune = Transpose + Fine / 100,
        KeyTrack = KeyTrack,
        Voices = Voices,
        Legato = Legato,
        GlideSec = GlideSec,
        GainDb = GainDb,
        Pan = Pan,
        VelAmp = VelAmp,
        Amp = new(Attack, Decay, Sustain, Release),
        Filter = (SamplerFilter)Array.IndexOf(Filters, Filter),
        Cutoff = Cutoff,
        Resonance = Resonance,
        Drive = Drive,
        FilterKey = FilterKey,
        FilterVel = FilterVel,
        FilterEnv = FilterEnv,
        FilterShape = new(FilterAttack, FilterDecay, FilterSustain, FilterRelease),
        LfoShape = (SamplerLfoShape)Array.IndexOf(LfoShapes, LfoShape),
        LfoHz = LfoBeat > 0 ? Math.Max(1, bpm) / 60 / LfoBeat : LfoHz,
        LfoDelay = LfoDelay,
        LfoPitch = LfoPitch,
        LfoFilter = LfoFilter,
        LfoAmp = LfoAmp,
        PitchEnv = PitchEnv,
        PitchDecay = PitchDecay,
        Scale = Normalize && sound is { Peak: > 0 } s ? 1 / s.Peak : 1,
    };

    /// <summary>Where a knob is set, what a lane starts from.</summary>
    public double Knob(SamplerKnob knob) => knob switch
    {
        SamplerKnob.Cutoff => Cutoff,
        SamplerKnob.Resonance => Resonance,
        SamplerKnob.Drive => Drive,
        SamplerKnob.FilterEnv => FilterEnv,
        SamplerKnob.Gain => GainDb,
        SamplerKnob.Pan => Pan,
        SamplerKnob.LfoPitch => LfoPitch,
        SamplerKnob.LfoFilter => LfoFilter,
        _ => LfoAmp,
    };

    /// <summary>The part of it that changes the samples themselves: a new sound is built only when this does.</summary>
    public (bool, string, double, double, double) SoundKey => (Reverse, Loop, Loop == LoopOff ? 0 : LoopStart, Loop == LoopOff ? 1 : LoopEnd, Loop == LoopForward ? LoopFade : 0);

    public SamplerSound Sound(float[] left, float[] right, CancellationToken ct = default)
    {
        var loop = Loop == LoopForward ? SamplerLoop.Forward : Loop == LoopPingPong ? SamplerLoop.PingPong : SamplerLoop.Off;
        return new SamplerSound(left, right, Reverse, loop, LoopStart, LoopEnd, LoopFade, ct);
    }

    /// <summary>
    /// What a new sample starts as: on the key its name or its sound says, in tune. Without any pitch, a short sound is a hit:
    /// it plays whole, whatever the length of the note.
    /// </summary>
    public void Adopt(string name, float[] left, float[] right, int sampleRate)
    {
        (Start, End, LoopStart, LoopEnd) = (0, 1, 0, 1);
        int? key = SamplerRoot.FromName(name);
        double cents = 0;
        // the name says the note; the sound says how far off it is, unless they don't even agree on the note
        if (SamplerRoot.FromSound(left, right, sampleRate) is { } heard && (key == null || (key.Value - heard.Key) % 12 == 0))
        {
            key ??= heard.Key;
            cents = heard.Cents;
        }
        Root = key ?? Root;
        Fine = -Math.Round(cents);
        Mode = key == null && left.Length < HitSec * sampleRate ? OneShot : Gate;
    }

    /// <summary>Under this long and without a pitch, a sound counts as a hit.</summary>
    const double HitSec = 3;
}

/// <summary>Puts samples in the project and reads them back. Every path to the sound of a track's sampler goes through here.</summary>
public static class SamplerMedia
{
    /// <summary>The media a track's sampler plays, if it has one.</summary>
    public static Media? Of(Project p, Track t) => t.Sampler?.MediaId is { } id ? p.FindMedia(id) : null;

    /// <summary>The media the samplers and granulars keep in use, which isn't anyone's to delete.</summary>
    public static IEnumerable<string> Used(Project p) => p.Tracks.Select(t => t.Sampler?.MediaId ?? t.Granular?.MediaId).OfType<string>();

    /// <summary>A file from <see cref="AudioImport"/> as the track's sample: a media like any import, the track's settings kept.</summary>
    public static void Set(Project p, Track t, AudioImport.Imported file, float[] left, float[] right, int sampleRate)
    {
        t.Instrument = null;
        t.Granular = null;
        var media = Add(p, file);
        t.Sampler ??= new SamplerSettings();
        t.Sampler.MediaId = media.Id;
        t.Sampler.Adopt(file.Name, left, right, sampleRate);
    }

    /// <summary>A file from <see cref="AudioImport"/> as a media of the project, an import like any other.</summary>
    public static Media Add(Project p, AudioImport.Imported file)
    {
        var media = new Media
        {
            Name = file.Name,
            Kind = "import",
            Audio = file.RelativePath,
            AudioBytes = file.Bytes,
            DurationSec = file.DurationSec,
            SampleRate = file.SampleRate,
            Channels = file.Channels,
            SyncMs = 0,
            VideoOffsetMs = 0,
        };
        p.Media.Add(media);
        return media;
    }

    /// <summary>The sampler of a track as the export plays it, read from disk; null without one. A file that is gone plays silence.</summary>
    public static SamplerRig? Rig(ProjectStore store, Track t, double bpm, int sampleRate)
    {
        if (t.Sampler is not { } s) return null;
        SamplerSound? sound = null;
        if (store.Resolve(Of(store.Project, t)?.Audio) is { } path && File.Exists(path))
        {
            var audio = AudioLoader.Load(path, sampleRate, null, SamplerSound.MaxSec);
            sound = s.Sound(audio.Left, audio.Right);
        }
        return new SamplerRig(s.Patch(bpm, sound), sound) { Curves = Automations.SamplerCurves(t) };
    }
}
