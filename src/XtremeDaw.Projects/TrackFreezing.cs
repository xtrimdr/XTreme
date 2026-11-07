using System.Text.Json;
using System.Text.Json.Serialization;
using XtremeDaw.Audio;
using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.Projects;

/// <summary>
/// A frozen track's render (<see cref="Track.Freeze"/>, #240, the user's call): what the track plays through its insert
/// slots, with its instrument and every lane of its slots, written once to a WAV in <c>freeze/</c>. Its fader, pan,
/// mute, solo and sends stay live, so the render is taken before them. <see cref="AtSec"/> is where its first sample
/// sounds. A file that isn't there leaves the track playing live (the window says so), never silent.
/// </summary>
public sealed class TrackFreeze
{
    /// <summary>Relative to the project folder: <c>freeze/&lt;file&gt;.wav</c>.</summary>
    public string File { get; set; } = "";
    /// <summary>The song time of the render's first sample.</summary>
    public double AtSec { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>False when it is unusable (no file, or one outside <c>freeze/</c>): the track is then not frozen.</summary>
    internal bool Normalize()
    {
        File = (File ?? "").Replace('\\', '/').Trim();
        if (!File.StartsWith(Freezing.Dir + "/", StringComparison.OrdinalIgnoreCase) || File.Contains("..")) return false;
        AtSec = double.IsFinite(AtSec) ? Math.Max(0, AtSec) : 0;
        return true;
    }
}

/// <summary>
/// Freezing a track (#240, the user's call, like Studio Pro's Track Freeze): its instrument and slots are rendered
/// offline by the export's own chain (<see cref="MixRenderer"/>, the track alone: <see cref="Isolated"/>), so the live
/// engine plays a file instead of running plugins. While frozen its takes, notes and slots can't be edited
/// (<see cref="Track.EditLocked"/>); unfreezing brings everything back as it was, since nothing of the track changes.
/// </summary>
public static class Freezing
{
    /// <summary>The folder of the renders, inside the project.</summary>
    public const string Dir = "freeze";
    /// <summary>What the slots may still ring after the last clip or note (the longest reverb is 14 s): rendered and then trimmed.</summary>
    public const double TailSec = 15;
    /// <summary>The render's end is trimmed where it stays below this (-100 dB), like a track's tail in the live engine.</summary>
    const float Silence = 1e-5f;
    /// <summary>Room rendered before the first clip or note: a transition plays a clip ahead of its start.</summary>
    const double LeadSec = 0.5;

    /// <summary>
    /// The track as the render takes it: its sound through its slots and their lanes, with the fader at 0 dB, the pan in
    /// the middle, no mute, solo, sends or fader lanes, and out to the master (which a lone track skips). What the live
    /// engine and the export put back on top of the render.
    /// </summary>
    public static MixTrackSpec Isolated(MixTrackSpec t) => new()
    {
        Name = t.Name,
        Id = t.Id,
        Clips = t.Clips,
        Notes = t.Notes,
        Instrument = t.Instrument,
        Sampler = t.Sampler,
        Granular = t.Granular,
        Gm = t.Gm,
        Tempo = t.Tempo,
        Sat = t.Sat,
        Comp = t.Comp,
        EqOn = t.EqOn,
        EqBands = t.EqBands,
        Inserts = t.Inserts,
        InsertsAt = t.InsertsAt,
        InstrumentCurves = t.InstrumentCurves,
    };

    /// <summary>Where the track first sounds (its first clip or note), in seconds; infinity when it has nothing.</summary>
    static double FirstSec(MixTrackSpec t, int sampleRate)
    {
        double first = double.PositiveInfinity;
        foreach (var c in t.Clips) first = Math.Min(first, c.GetWindow(sampleRate).Start);
        if (t.Notes is { Notes.Length: > 0 } seq) first = Math.Min(first, seq.Notes.Min(n => n.Start) / (double)sampleRate);
        return first;
    }

    /// <summary>
    /// Renders track <paramref name="k"/> of the mix <paramref name="tracks"/> (the others only run when a slot of it
    /// listens to them), aligned to the song like the export (its slots' latency taken off). Null when it sounds nothing.
    /// </summary>
    public static (float[] Left, float[] Right, double AtSec)? Render(IReadOnlyList<MixTrackSpec> tracks, int k, double bpm,
        int sampleRate = 48000, Action<double>? onProgress = null, CancellationToken ct = default)
    {
        var t = tracks[k];
        double first = FirstSec(t, sampleRate);
        if (!double.IsFinite(first)) return null;
        double end = MixRenderer.MixDurationSec([t], sampleRate) + TailSec;
        long from = (long)Math.Floor(Math.Max(0, first - LeadSec) * sampleRate);
        int frames = (int)Math.Max(1, (long)Math.Ceiling(end * sampleRate) - from);
        var l = new float[frames];
        var r = new float[frames];
        var list = tracks.ToArray();
        list[k] = Isolated(t);
        MixRenderer.RenderTo(list, new MixBusSpec { Bpm = bpm }, end, (at, a, b) =>
        {
            long skip = Math.Max(0, from - at);
            if (skip >= a.Length) return;
            int dst = (int)(at + skip - from), n = Math.Min(a.Length - (int)skip, frames - dst);
            if (n <= 0) return;
            a.Slice((int)skip, n).CopyTo(l.AsSpan(dst));
            b.Slice((int)skip, n).CopyTo(r.AsSpan(dst));
        }, sampleRate, onProgress, ct, only: k, limit: false, master: false);
        // silence at the ends goes: exact zeros before it starts, the tail once it is inaudible
        int head = 0;
        while (head < frames && l[head] == 0 && r[head] == 0) head++;
        if (head == frames) return null;
        int last = frames - 1;
        while (last > head && Math.Abs(l[last]) < Silence && Math.Abs(r[last]) < Silence) last--;
        return (l[head..(last + 1)], r[head..(last + 1)], (from + head) / (double)sampleRate);
    }

    /// <summary>
    /// Writes a render into <c>freeze/</c> of <paramref name="projectDir"/> (a new file each time, through a temporary
    /// name: a cut never leaves half a render) and returns the track's <see cref="TrackFreeze"/>.
    /// </summary>
    public static TrackFreeze Write(string projectDir, Track track, float[] left, float[] right, double atSec, int sampleRate)
    {
        string stem = $"{track.Id}-{Guid.NewGuid():N}"[..Math.Min(48, track.Id.Length + 33)];
        string path = AudioImport.WriteWav(Path.Combine(projectDir, Dir), stem, left, right, sampleRate);
        return new TrackFreeze { File = $"{Dir}/{Path.GetFileName(path)}", AtSec = atSec };
    }

    /// <summary>
    /// Deletes the renders of <paramref name="p"/>'s folder that no track points at (a track frozen again, or unfrozen),
    /// on opening it: during a session they stay, so an undo finds them. One brought back from an older version plays
    /// the track live until it is frozen again.
    /// </summary>
    public static void Prune(string projectDir, Project p) => Prune(projectDir, [.. p.Tracks.Select(t => t.Freeze?.File).OfType<string>()]);

    /// <inheritdoc cref="Prune(string, Project)"/>
    public static void Prune(string projectDir, IReadOnlyList<string> keep)
    {
        string dir = Path.Combine(projectDir, Dir);
        if (!Directory.Exists(dir)) return;
        var used = keep.Select(f => Path.GetFileName(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(dir, "*.wav"))
            if (!used.Contains(Path.GetFileName(f)))
                try { System.IO.File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The render's file, or null when it isn't there.</summary>
    public static string? PathOf(string projectDir, TrackFreeze f)
    {
        string path = Path.Combine(projectDir, f.File.Replace('/', Path.DirectorySeparatorChar));
        return System.IO.File.Exists(path) ? path : null;
    }

    /// <summary>Reads a render at <paramref name="sampleRate"/>; null when its file is missing or unreadable (the track plays live).</summary>
    public static (float[] Left, float[] Right)? Load(string projectDir, TrackFreeze f, int sampleRate)
    {
        if (PathOf(projectDir, f) is not { } path) return null;
        try
        {
            var a = AudioLoader.Load(path, sampleRate);
            return (a.Left, a.Right);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A render as a clip of the mix (the export and xtlab).</summary>
    public static MixClipSpec Clip(float[] left, float[] right, double atSec, int sampleRate) =>
        new() { Left = left, Right = right, DurationSec = left.Length / (double)sampleRate, AtSec = atSec };

    /// <summary>
    /// Freezes <paramref name="t"/> in <paramref name="p"/> to <paramref name="freeze"/>: its clips, notes and slots stay as
    /// they are, under the lock. Unfreezing passes null.
    /// </summary>
    public static void Set(Project p, string trackId, TrackFreeze? freeze)
    {
        if (p.Tracks.Find(t => t.Id == trackId) is { } t) t.Freeze = freeze;
    }

    /// <summary>A track can be frozen: it has something to play. Folders and buses aren't tracks.</summary>
    public static bool CanFreeze(Track t) => t.HasContent;
}
