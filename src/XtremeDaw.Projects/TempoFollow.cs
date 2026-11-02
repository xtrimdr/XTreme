namespace XtremeDaw.Projects;

/// <summary>
/// The whole song can follow the tempo (asked by the user: every clip, video included, with no per-clip choice; the window asks each time): a clip's
/// numbers are seconds of the timeline, so a new tempo multiplies them all by old/new and <see cref="Clip.Stretch"/>
/// says what that does to its media (<see cref="XtremeDaw.Audio.Dsp.PitchShifter.Render"/>). What sits on the timeline
/// with the clips goes with them, so everything stays on its bar: automation points, markers, chords and the loop. The
/// crossfades' lengths do not change. Undo keeps the tempo out of its steps, so a step remembers
/// <see cref="ProjectSettings.TimeScale"/> and the tempo it was made at, and comes back through <see cref="Rescale"/>
/// and <see cref="Regrid"/>. Setting the tempo to what the audio already is (<see cref="SetGrid"/>, "Detectar tempo")
/// is not a change of speed: only the grid moves, and the MIDI clips with it.
/// </summary>
public static class TempoFollow
{
    /// <summary>A stretch this close to 1 is the recording itself: going back to its tempo needs no render.</summary>
    const double Exact = 1e-6;

    /// <summary>Puts the project at <paramref name="bpm"/> with everything on its bar.</summary>
    public static void SetBpm(Project p, double bpm)
    {
        double f = p.Settings.Bpm / bpm;
        Rescale(p, f);
        p.Settings.TimeScale *= f;
        if (p.Settings.LoopStartSec is { } a) p.Settings.LoopStartSec = a * f;
        if (p.Settings.LoopEndSec is { } b) p.Settings.LoopEndSec = b * f;
        p.Settings.Bpm = bpm;
    }

    /// <summary>The same tempo for the audio, read as <paramref name="bpm"/>: the MIDI clips, which live in beats, stay on their bars.</summary>
    public static void SetGrid(Project p, double bpm)
    {
        Regrid(p, p.Settings.Bpm / bpm);
        p.Settings.Bpm = bpm;
    }

    /// <summary>The MIDI clips' starts, multiplied by <paramref name="f"/>: what a change of grid moves.</summary>
    public static void Regrid(Project p, double f)
    {
        foreach (var t in p.Tracks)
            foreach (var c in t.MidiClips ?? []) c.AtSec *= f;
    }

    /// <summary>Every time undo keeps, multiplied by <paramref name="f"/>.</summary>
    public static void Rescale(Project p, double f)
    {
        if (f == 1) return;
        foreach (var t in p.Tracks)
        {
            foreach (var c in t.Clips) Rescale(c, f);
            foreach (var c in t.MidiClips ?? []) c.AtSec *= f;
            foreach (var lane in t.Automation ?? [])
            {
                foreach (var b in lane.Blocks)
                {
                    b.AtSec *= f;
                    b.LengthSec *= f;
                    foreach (var point in b.Points) point.Sec *= f;
                }
                lane.Touch();
            }
        }
        foreach (var m in p.Markers) m.Sec *= f;
        foreach (var m in p.ChordMarkers) { m.Sec *= f; m.EndSec *= f; }
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
