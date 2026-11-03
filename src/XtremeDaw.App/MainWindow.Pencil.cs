using Avalonia.Controls;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Model;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The brush of the Lápiz and the Pincel (asked by a collaborator, like FL Studio's): what a click on an empty lane puts
/// there. A click with either on a clip of the timeline takes it; a click on a pattern of the Patrones list or on a sample of
/// the Biblioteca takes that, whatever the tool, so you pick and then draw. Picking the pencil with nothing yet takes
/// the selected clip. A take or a MIDI clip is kept as a snapshot (<see cref="ClipStamp"/>); a sample is imported on its
/// first copy and from then on paints as that take, so the file is copied into the project once.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// One of: a take (<paramref name="Take"/>, with its media's length as heard), a MIDI clip or a pattern
    /// (<paramref name="Notes"/>), or a file of the Biblioteca not imported yet (<paramref name="SamplePath"/>).
    /// </summary>
    sealed record PencilBrush(string Name, Clip? Take = null, double TakeSec = 0, MidiClip? Notes = null, string? SamplePath = null, double SampleSec = 0);

    PencilBrush? pencil;

    void InitPencil()
    {
        Timeline.PencilPicked += (track, id) => PickPencil(track, id);
        Timeline.PencilRefused += RefusePencil;
        Timeline.PencilPlaced += async (track, starts) => await PlacePencil(track, starts);
    }

    /// <summary>Picking the pencil with no brush takes the selected clip.</summary>
    void PencilChosen()
    {
        if (pencil != null || Timeline.Selected < 0 || Timeline.Selected >= tracks.Count) return;
        var track = tracks[Timeline.Selected];
        if ((Timeline.SelectedMidiId ?? Timeline.SelectedClipId) is { } id) PickPencil(track, id, quiet: true);
    }

    /// <summary>A take or a MIDI clip of <paramref name="track"/> becomes the brush.</summary>
    void PickPencil(LiveTrack track, string id, bool quiet = false)
    {
        if (store == null) return;
        if (track.Clips.FirstOrDefault(c => c.Clip.Id == id) is { } take)
            SetPencil(new PencilBrush(take.Media?.Name ?? track.Track.Name, ClipEditing.Copy(take.Clip), take.LengthSec), quiet);
        else if (MidiEditing.TryFind(store.Project, id) is { } notes)
            SetPencil(new PencilBrush(notes.PatternName ?? track.Track.Name, Notes: notes.Copy()), quiet);
    }

    /// <summary>A pattern of the Patrones list becomes the brush: one pass of it.</summary>
    void PickPencilPattern(string patternId)
    {
        if (store == null || ClipStamp.PatternBrush(store.Project, patternId) is not { } brush) return;
        SetPencil(new PencilBrush(brush.PatternName ?? "", Notes: brush), quiet: true);
    }

    /// <summary>A sample of the Biblioteca becomes the brush; until it is in the project its length is what the list read (a bar if not yet).</summary>
    void PickPencilSample(SampleFile f)
    {
        if (f.Broken) return;
        double sec = f.DurationSec > 0 ? f.DurationSec : 4 * 60 / Math.Max(1, Timeline.Bpm);
        SetPencil(new PencilBrush(f.Name, SamplePath: f.Path, SampleSec: sec), quiet: true);
    }

    void SetPencil(PencilBrush? brush, bool quiet)
    {
        pencil = brush;
        if (brush == null) Timeline.SetPencil(0, false);
        else if (brush.Notes != null) Timeline.SetPencil(brush.Notes.LengthBeats, true);
        else if (brush.Take != null && store?.Project.FindMedia(brush.Take.MediaId) is { } media)
        {
            var w = ClipEditing.Window(brush.Take, media, brush.TakeSec);
            Timeline.SetPencil(w.End - w.Start, false);
        }
        else Timeline.SetPencil(brush.SampleSec, false);
        foreach (var (t, key, tipKey) in ToolButtons)
        {
            if (t is not (TimelineTool.Draw or TimelineTool.Paint) || !toolButtons.TryGetValue(t, out var b)) continue;
            string tip = brush == null ? Loc.T(tipKey) : Loc.T("tool.draw.brush", Loc.T(tipKey), brush.Name);
            ToolTip.SetTip(b, Shortcuts.Tip(tip, key));
        }
        if (brush != null && !quiet) Status(Loc.T("pencil.picked", brush.Name));
    }

    void RefusePencil(LiveTrack track)
    {
        if (track.Track.Locked) ToastLocked(track.Track, null);
        else if (pencil == null) Toast(Loc.T("pencil.none"));
        else Toast(Loc.T(pencil.Notes != null ? "pencil.needsMidi" : "pencil.needsAudio", pencil.Name));
    }

    /// <summary>The copies of one press and its drag: one step to undo (a sample's first copy is its import, a step of its own).</summary>
    async Task PlacePencil(LiveTrack track, IReadOnlyList<double> starts)
    {
        if (pencil is not { } brush || store == null || starts.Count == 0) return;
        if (brush.SamplePath is { } path)
        {
            if (!CanImport()) { Status(Loc.T("imp.busy")); return; }
            string trackId = track.Track.Id;
            if (await ImportAudio([path], starts[0], trackId, BringsPicture(path)) is not { } made) return;
            // from here on the sample paints as the take it became
            if (tracks.FirstOrDefault(t => t.Clips.Any(c => c.Clip.Id == made)) is { } on)
            {
                PickPencil(on, made, quiet: true);
                if (starts.Count > 1 && on.Track.Id == trackId) await PlacePencil(on, [.. starts.Skip(1)]);
            }
            return;
        }
        List<string> ids = [];
        string id = track.Track.Id;
        var fit = brush.Notes is { } n ? ClipStamp.Midi(store.Project, n, id, starts, apply: false)
            : brush.Take is { } t ? ClipStamp.Audio(store.Project, t, id, starts, brush.TakeSec, apply: false) : [];
        // nothing fits: no edit, so no empty step to undo
        if (fit.Count > 0 && brush.Notes is { } notes)
            await EditTimeline(p => { ids = ClipStamp.Midi(p, notes, id, starts); return id; },
                notes.PatternId != null ? "midi-pattern-inserted" : "midi-clip-duplicated");
        else if (fit.Count > 0 && brush.Take is { } take)
            await EditTimeline(p => { ids = ClipStamp.Audio(p, take, id, starts, brush.TakeSec); return id; },
                starts.Count > 1 ? "clips-duplicated" : "clip-duplicated");
        if (ids.Count == 0)
        {
            Status(Loc.T(brush.Take != null && store.Project.FindMedia(brush.Take.MediaId) == null ? "pencil.gone" : "pencil.full"));
            return;
        }
        int at = TrackIndex(id);
        if (brush.Notes != null) Timeline.SelectMidiClip(at, ids[^1]);
        else Timeline.SelectClip(at, ids[^1]);
    }
}
