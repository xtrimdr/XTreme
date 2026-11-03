using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// One pass of a MIDI pattern in small, for its row in the Patrones tab: a box like the clip on the timeline with the
/// notes of its instruments inside, each in its track's color, stretched to the width it gets. Without a track color
/// it takes the accent, read while drawing.
/// </summary>
public sealed class PatternPreview : Control
{
    const double Inset = 4, Radius = 5;
    /// <summary>A range of at least an octave, so a single note doesn't fill the box from top to bottom.</summary>
    const int MinSpan = 12;

    List<(List<MidiNote> Notes, Color? Tint)> parts = [];
    double beats = 1;
    int lo, span = MinSpan;

    public PatternPreview() => Height = 30;

    public void Set(List<(List<MidiNote> Notes, Color? Tint)> pass, double passBeats)
    {
        (parts, beats) = (pass, Math.Max(MidiClip.MinBeats, passBeats));
        int low = 127, high = 0;
        foreach (var (notes, _) in pass)
            foreach (var n in notes)
            {
                if (n.Beat >= beats) continue;
                low = Math.Min(low, n.Pitch);
                high = Math.Max(high, n.Pitch);
            }
        span = Math.Max(MinSpan, high - low + 1);
        lo = low - (span - (high - low + 1)) / 2;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var color = (parts.Count > 0 ? parts[0].Tint : null) ?? Palette.AccentColor;
        var box = new Rect(Bounds.Size);
        ctx.DrawRectangle(Palette.Alpha(color, 0.14), Palette.Pen(Palette.Alpha(color, 0.45)), box.Deflate(0.5), Radius, Radius);
        double w = box.Width - 2 * Inset, h = box.Height - 2 * Inset;
        if (w <= 0 || h <= 0) return;
        double rowH = Math.Max(1, Math.Min(4, h / span));
        foreach (var (notes, tint) in parts)
        {
            var fill = Palette.Alpha(tint ?? Palette.AccentColor, 0.85);
            foreach (var n in notes)
            {
                if (!MidiClip.Slice(n, 0, beats, beats, out double from, out double to)) continue;
                double y = Inset + (span - 1 - (n.Pitch - lo)) / (double)span * h;
                ctx.FillRectangle(fill, new Rect(Inset + from / beats * w, y, Math.Max(1.5, (to - from) / beats * w - 1), rowH));
            }
        }
    }
}
