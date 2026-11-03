using System.Text.Json;
using System.Text.Json.Serialization;
using XtremeDaw.Audio;
using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.Projects;

/// <summary>
/// The granular of a MIDI track (<see cref="Track.Granular"/>, asked by the user, 2026-11-03: an instrument of its own,
/// not a mode of the sampler): its sample, a <see cref="Media"/> of the project like any import, and every setting of
/// its window. Positions are fractions of the sound, times in seconds. The sound itself is built once per sample: no
/// knob here changes its samples, so every edit only swaps the patch.
/// </summary>
public sealed class GranularSettings
{
    public const string Stretch = "stretch", Scrub = "scrub";
    public const double MaxSpeed = GranularKnobs.MaxSpeed, MinDensity = GranularKnobs.MinDensity, MaxDensity = GranularKnobs.MaxDensity;
    public const double MinSizeSec = GranularKnobs.MinSizeSec, MaxSizeSec = GranularKnobs.MaxSizeSec, MaxSpraySec = GranularKnobs.MaxSpraySec;
    public const double MinGainDb = GranularKnobs.MinGainDb, MaxGainDb = GranularKnobs.MaxGainDb, MaxTune = SamplerSettings.MaxTune;
    public const double MaxEnvSec = SamplerSettings.MaxEnvSec, MinSampleBpm = 20, MaxSampleBpm = 300;
    /// <summary>Beats between grains the Sync menu offers (1/4 to 1/128); 0 = free, in grains per second.</summary>
    public static readonly double[] DensityBeats = [0, 1, 0.5, 0.25, 0.125, 0.0625, 0.03125];

    /// <summary>The sample; null = a granular still waiting for one.</summary>
    public string? MediaId { get; set; }
    /// <summary>"stretch": the playhead runs through the sound. "scrub": it stands at <see cref="Position"/>.</summary>
    public string Mode { get; set; } = Stretch;
    /// <summary>The part of the sound the playhead goes over.</summary>
    public double Start { get; set; }
    public double End { get; set; } = 1;
    /// <summary>0..1 of that part: where the playhead stands (scrub) or starts (stretch).</summary>
    public double Position { get; set; }
    /// <summary>How fast the playhead runs, 1 = the sound's own speed.</summary>
    public double Speed { get; set; } = 1;
    /// <summary>The speed follows the song: at <see cref="SampleBpm"/> it is <see cref="Speed"/>, at double the tempo double.</summary>
    public bool SpeedSync { get; set; }
    /// <summary>The tempo the sample was played at, for <see cref="SpeedSync"/>.</summary>
    public double SampleBpm { get; set; } = 120;
    /// <summary>The playhead runs backwards.</summary>
    public bool Reverse { get; set; }
    /// <summary>Lifts the quietest samples to full level without touching the file.</summary>
    public bool Normalize { get; set; }
    /// <summary>Grains per second.</summary>
    public double Density { get; set; } = 20;
    /// <summary>Beats between grains, following the tempo; 0 = <see cref="Density"/>.</summary>
    public double DensityBeat { get; set; }
    public double SizeSec { get; set; } = 0.08;
    public double Shape { get; set; } = 0.5;
    public double Skew { get; set; }
    public double Jitter { get; set; }
    public double SpraySec { get; set; }
    public double Spread { get; set; }
    /// <summary>The key that plays it as it is (C4 = 60).</summary>
    public int Root { get; set; } = 60;
    public int Transpose { get; set; }
    /// <summary>Cents.</summary>
    public double Fine { get; set; }
    public bool KeyTrack { get; set; } = true;
    public int Voices { get; set; } = 8;
    public double GainDb { get; set; }
    public double Pan { get; set; }
    public double VelAmp { get; set; } = 0.7;
    public double Attack { get; set; } = 0.01;
    public double Decay { get; set; } = 0.1;
    public double Sustain { get; set; } = 1;
    public double Release { get; set; } = 0.3;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    static double Fix(double v, double fallback, double min, double max) => double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

    /// <summary>Every value back in its range, what <see cref="Track"/> does on opening and the window after each edit.</summary>
    public void Normalized()
    {
        Mode = Mode == Scrub ? Scrub : Stretch;
        Start = Fix(Start, 0, 0, 1);
        End = Fix(End, 1, 0, 1);
        if (End <= Start) (Start, End) = (0, 1);
        Position = Fix(Position, 0, 0, 1);
        Speed = Fix(Speed, 1, 0, MaxSpeed);
        SampleBpm = Fix(SampleBpm, 120, MinSampleBpm, MaxSampleBpm);
        Density = Fix(Density, 20, MinDensity, MaxDensity);
        DensityBeat = DensityBeats.Contains(DensityBeat) ? DensityBeat : 0;
        SizeSec = Fix(SizeSec, 0.08, MinSizeSec, MaxSizeSec);
        Shape = Fix(Shape, 0.5, 0, 1);
        Skew = Fix(Skew, 0, -1, 1);
        Jitter = Fix(Jitter, 0, 0, 1);
        SpraySec = Fix(SpraySec, 0, 0, MaxSpraySec);
        Spread = Fix(Spread, 0, 0, 1);
        Root = Math.Clamp(Root, 0, 127);
        Transpose = Math.Clamp(Transpose, -(int)MaxTune, (int)MaxTune);
        Fine = Fix(Fine, 0, -100, 100);
        Voices = Math.Clamp(Voices, 1, Granular.MaxVoices);
        GainDb = Fix(GainDb, 0, MinGainDb, MaxGainDb);
        Pan = Fix(Pan, 0, -1, 1);
        VelAmp = Fix(VelAmp, 0.7, 0, 1);
        Attack = Fix(Attack, 0.01, 0, MaxEnvSec);
        Decay = Fix(Decay, 0.1, 0, MaxEnvSec);
        Sustain = Fix(Sustain, 1, 0, 1);
        Release = Fix(Release, 0.3, 0, MaxEnvSec);
    }

    /// <summary>What the voices read; <paramref name="sound"/> gives Normalizar its gain. A new one on every edit and tempo change.</summary>
    public GranularPatch Patch(double bpm, SamplerSound? sound) => new()
    {
        Mode = Mode == Scrub ? GranularMode.Scrub : GranularMode.Stretch,
        Start = Start,
        End = End,
        Position = Position,
        Speed = Speed,
        SpeedScale = SpeedSync ? Math.Max(1, bpm) / SampleBpm : 1,
        Reverse = Reverse,
        Density = DensityBeat > 0 ? Math.Max(1, bpm) / 60 / DensityBeat : Density,
        DensitySynced = DensityBeat > 0,
        SizeSec = SizeSec,
        Shape = Shape,
        Skew = Skew,
        Jitter = Jitter,
        SpraySec = SpraySec,
        Spread = Spread,
        Root = Root,
        Tune = Transpose + Fine / 100,
        KeyTrack = KeyTrack,
        Voices = Voices,
        GainDb = GainDb,
        Pan = Pan,
        VelAmp = VelAmp,
        Amp = new(Attack, Decay, Sustain, Release),
        Scale = Normalize && sound is { Peak: > 0 } s ? 1 / s.Peak : 1,
    };

    /// <summary>Where a knob is set, what a lane starts from.</summary>
    public double Knob(GranularKnob knob) => knob switch
    {
        GranularKnob.Position => Position,
        GranularKnob.Speed => Speed,
        GranularKnob.Density => Density,
        GranularKnob.Size => SizeSec,
        GranularKnob.Jitter => Jitter,
        GranularKnob.Spray => SpraySec,
        GranularKnob.Spread => Spread,
        GranularKnob.Gain => GainDb,
        _ => Pan,
    };

    /// <summary>The sound the grains read: the sampler's, with no loop and as the file is.</summary>
    public static SamplerSound Sound(float[] left, float[] right, CancellationToken ct = default) =>
        new(left, right, false, SamplerLoop.Off, 0, 1, 0, ct);

    /// <summary>What a new sample starts as: the whole of it, on the key its name or its sound says, in tune.</summary>
    public void Adopt(string name, float[] left, float[] right, int sampleRate)
    {
        (Start, End, Position) = (0, 1, 0);
        int? key = SamplerRoot.FromName(name);
        double cents = 0;
        if (SamplerRoot.FromSound(left, right, sampleRate) is { } heard && (key == null || (key.Value - heard.Key) % 12 == 0))
        {
            key ??= heard.Key;
            cents = heard.Cents;
        }
        Root = key ?? Root;
        Fine = -Math.Round(cents);
    }
}

/// <summary>Reads a track's granular back for the export. Its sample goes in through <see cref="SamplerMedia"/>, like the sampler's.</summary>
public static class GranularMedia
{
    /// <summary>The media a track's granular plays, if it has one.</summary>
    public static Media? Of(Project p, Track t) => t.Granular?.MediaId is { } id ? p.FindMedia(id) : null;

    /// <summary>A file from <see cref="AudioImport"/> as the track's sample: a media like any import, the track's settings kept.</summary>
    public static void Set(Project p, Track t, AudioImport.Imported file, float[] left, float[] right, int sampleRate)
    {
        t.Instrument = null;
        t.Sampler = null;
        var media = SamplerMedia.Add(p, file);
        t.Granular ??= new GranularSettings();
        t.Granular.MediaId = media.Id;
        t.Granular.Adopt(file.Name, left, right, sampleRate);
    }

    /// <summary>The granular of a track as the export plays it, read from disk; null without one. A file that is gone plays silence.</summary>
    public static GranularRig? Rig(ProjectStore store, Track t, double bpm, int sampleRate)
    {
        if (t.Granular is not { } g) return null;
        SamplerSound? sound = null;
        if (store.Resolve(Of(store.Project, t)?.Audio) is { } path && File.Exists(path))
        {
            var audio = AudioLoader.Load(path, sampleRate, null, SamplerSound.MaxSec);
            sound = GranularSettings.Sound(audio.Left, audio.Right);
        }
        return new GranularRig(g.Patch(bpm, sound), sound) { Curves = Automations.GranularCurves(t) };
    }
}
