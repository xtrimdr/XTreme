namespace XtremeDaw.Projects;

/// <summary>
/// The song follows its tempo. Two kinds of change:
/// <para>
/// A change of the whole song's speed (<see cref="SetBpm"/>, the transport's tempo; asked by the user: every clip,
/// video included, with no per-clip choice; the window asks each time): every tempo of the map is multiplied by
/// new/old, so every time of the timeline multiplies by old/new and <see cref="Clip.Stretch"/> says what that does to
/// its media (<see cref="XtremeDaw.Audio.Dsp.PitchShifter.Render"/>). What sits on the timeline with the clips goes with
/// them, so everything stays on its bar: automation points, markers, chords and the loop. The crossfades' lengths do not
/// change. It is an undo step (#262): a step keeps <see cref="ProjectSettings.TimeScale"/> and
/// <see cref="ProjectSettings.TempoScale"/>, and undo brings them back with everything they moved.
/// Setting the tempo to what the audio already is (<see cref="SetGrid"/>, "Detectar tempo") is not a change of speed:
/// only the grid moves, and what lives in beats with it (<see cref="Regrid"/>).
/// </para>
/// <para>
/// An edit of the tempo map (<see cref="SetMap"/>, #214: points, ramps, meters) is content and goes in an undo step:
/// each musical thing keeps its beat (its beat under the old map, its seconds under the new one), the rest keeps its
/// seconds. Musical are the MIDI clips, the clips of an audio track that follows the tempo (<see cref="Track.FollowTempo"/>),
/// the lanes of those tracks, markers, chords and the loop. A track in seconds and a folder's lanes don't move.
/// </para>
/// </summary>
public static class TempoFollow
{
    /// <summary>A stretch this close to 1 is the recording itself: going back to its tempo needs no render.</summary>
    const double Exact = 1e-6;

    /// <summary>
    /// A tempo the map can hold: every time in the project is multiplied by old/new, so one that isn't a number would
    /// leave them all infinite or NaN, saved like that.
    /// </summary>
    static double Valid(double bpm) => double.IsFinite(bpm)
        ? Math.Clamp(bpm, TempoMap.MinBpm, TempoMap.MaxBpm)
        : throw new ArgumentException(XtremeDaw.Audio.Texts.T("err.tempo.number", "El tempo tiene que ser un número válido."));

    /// <summary>Puts the project at <paramref name="bpm"/> with everything on its bar: the whole map at that speed.</summary>
    public static void SetBpm(Project p, double bpm)
    {
        var s = p.Settings;
        bpm = Valid(bpm);
        double k = bpm / s.Bpm, f = 1 / k;
        Rescale(p, f);
        s.TimeScale *= f;
        if (s.LoopStartSec is { } a) s.LoopStartSec = a * f;
        if (s.LoopEndSec is { } b) s.LoopEndSec = b * f;
        s.Bpm = bpm;
    }

    /// <summary>The same tempo for the audio, read as <paramref name="bpm"/>: what lives in beats stays on its bar.</summary>
    public static void SetGrid(Project p, double bpm)
    {
        var s = p.Settings;
        bpm = Valid(bpm);
        double k = bpm / s.Bpm;
        Regrid(p, 1 / k);
        s.Bpm = bpm;
    }

    /// <summary>
    /// What a change of grid moves, multiplied by <paramref name="f"/>: the MIDI clips' starts, the clips of the tracks
    /// that follow the tempo (stretched like <see cref="SetBpm"/> does) and the lanes of every musical track.
    /// </summary>
    public static void Regrid(Project p, double f)
    {
        if (f == 1) return;
        foreach (var t in p.Tracks)
        {
            if (!t.Musical) continue;
            foreach (var c in t.MidiClips ?? []) c.AtSec *= f;
            if (t.FollowTempo)
                foreach (var c in t.Clips) Rescale(c, f);
            Rescale(t.Automation, f);
        }
    }

    /// <summary>Every time undo keeps, multiplied by <paramref name="f"/>.</summary>
    public static void Rescale(Project p, double f)
    {
        if (f == 1) return;
        foreach (var t in p.Tracks)
        {
            foreach (var c in t.Clips) Rescale(c, f);
            foreach (var c in t.MidiClips ?? []) c.AtSec *= f;
            Rescale(t.Automation, f);
        }
        foreach (var folder in p.Folders) Rescale(folder.Automation, f);
        foreach (var bus in p.Settings.Buses) Rescale(bus.Automation, f);
        foreach (var m in p.Markers) m.Sec *= f;
        foreach (var m in p.ChordMarkers) { m.Sec *= f; m.EndSec *= f; }
    }

    /// <summary>
    /// Puts <paramref name="map"/> in the project (#214): everything musical keeps its beat and the rest its seconds
    /// (see the class). The caller makes the undo step before. A clip of a follow-tempo track keeps its start's beat and
    /// its end's beat: it is stretched by the ratio of its new length to its old one, uniformly, so inside a ramp its
    /// middle lands only near its beats (the render stretches a clip by one factor). Returns false when the map is the
    /// same and nothing moved.
    /// </summary>
    public static bool SetMap(Project p, TempoMap map)
    {
        var s = p.Settings;
        var old = s.TempoMap;
        map = map.Copy();
        map.Normalize();
        if (map.SameAs(old)) return false;
        double Move(double sec) => map.SecAt(old.BeatAt(sec));
        foreach (var t in p.Tracks)
        {
            if (!t.Musical) continue;
            foreach (var c in t.MidiClips ?? []) c.AtSec = Move(c.AtSec);
            if (t.FollowTempo)
                foreach (var c in t.Clips) Remap(p, c, old, map);
            Remap(t.Automation, Move);
        }
        foreach (var m in p.Markers) m.Sec = Move(m.Sec);
        foreach (var m in p.ChordMarkers) { m.Sec = Move(m.Sec); m.EndSec = Move(m.EndSec); }
        if (s.LoopStartSec is { } a) s.LoopStartSec = Move(a);
        if (s.LoopEndSec is { } b) s.LoopEndSec = Move(b);
        s.TempoMap = map;
        return true;
    }

    /// <summary>A follow-tempo clip under a new map: its visible start and end keep their beats, its media stretches between them.</summary>
    static void Remap(Project p, Clip c, TempoMap old, TempoMap map)
    {
        // the clip's sound starts at AtSec + TrimStartSec (Window): that keeps its beat, and so does its end
        double start = c.AtSec + c.TrimStartSec, end = ClipEditing.Window(c, p.FindMedia(c.MediaId)).End;
        double from = map.SecAt(old.BeatAt(start));
        double f = end > start + Exact
            ? (map.SecAt(old.BeatAt(end)) - from) / (end - start)
            // no length known (the media is missing): the tempos' ratio where it starts
            : old.BpmAtSec(start) / map.BpmAt(old.BeatAt(start));
        Rescale(c, f);
        c.AtSec = from - c.TrimStartSec;
    }

    /// <summary>A track's lanes, every time in them through <paramref name="move"/>.</summary>
    static void Remap(List<AutomationLane>? lanes, Func<double, double> move)
    {
        foreach (var lane in lanes ?? [])
        {
            foreach (var b in lane.Blocks)
            {
                double at = move(b.AtSec), end = move(b.AtSec + b.LengthSec);
                if (b.LoopSec is { } loop) b.LoopSec = move(b.AtSec + loop) - at;
                // a point's time is relative to its block: it moves as the song time it sits at
                foreach (var point in b.Points) point.Sec = move(b.AtSec + point.Sec) - at;
                b.AtSec = at;
                b.LengthSec = end - at;
            }
            lane.Touch();
        }
    }

    /// <summary>A track's, a folder's or a bus's lanes, every time in them multiplied by <paramref name="f"/>.</summary>
    static void Rescale(List<AutomationLane>? lanes, double f)
    {
        foreach (var lane in lanes ?? [])
        {
            foreach (var b in lane.Blocks)
            {
                b.AtSec *= f;
                b.LengthSec *= f;
                if (b.LoopSec is { } loop) b.LoopSec = loop * f;
                foreach (var point in b.Points) point.Sec *= f;
            }
            lane.Touch();
        }
    }

    /// <summary>The clip's own times and its <see cref="Clip.Stretch"/>: also what a stretch by hand does (<see cref="ClipEditing"/>).</summary>
    internal static void Rescale(Clip c, double f)
    {
        c.AtSec *= f;
        c.TrimStartSec *= f;
        c.TrimEndSec *= f;
        c.FadeInSec *= f;
        c.FadeOutSec *= f;
        c.VideoFadeInSec *= f;
        c.VideoFadeOutSec *= f;
        foreach (var g in c.GainCurve) g.Sec *= f;
        c.Stretch *= f;
        if (Math.Abs(c.Stretch - 1) < Exact) c.Stretch = 1;
    }
}
