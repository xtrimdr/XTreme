using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The Biblioteca's mini player wave: the take's peaks as thin bars, what was already heard in the accent and the rest
/// faint, plus a hover line; a MIDI file shows its notes instead (<see cref="SetNotes"/>). Click or drag to jump. It only raises <see cref="SeekRequested"/> (a fraction 0..1);
/// the window moves the preview. Without peaks yet (they are read in the background) it shows a flat line.
/// Dragging out of the wave, above or below, raises <see cref="CarryRequested"/> instead, so the window can carry the
/// file to the timeline; sideways it keeps seeking.
/// </summary>
public sealed class TakeScrubber : Control
{
    /// <summary>Bar width and gap in px: thin enough to read as a wave, wide enough to stay crisp.</summary>
    const double BarW = 2, Gap = 1;
    /// <summary>How far above or below the wave the pointer goes before a scrub turns into a drag out.</summary>
    const double CarryOut = 8;
    /// <summary>Thickest a note gets in the MIDI view, in px: few pitches would otherwise draw fat blocks.</summary>
    const double MaxNoteH = 3;
    float[]? peaks, notes;
    float peakMax = 1;
    double progress, hoverX = -1;
    bool dragging;

    public event Action<double>? SeekRequested;
    /// <summary>A scrub left the wave vertically; without a handler the scrub just goes on.</summary>
    public event Action<PointerEventArgs>? CarryRequested;

    public TakeScrubber()
    {
        Height = 32;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>
    /// A MIDI file instead of a wave: triples of start and end (0..1 of its length) and pitch (0..1 of its range), drawn as
    /// a small piano roll. Null goes back to the wave.
    /// </summary>
    public void SetNotes(float[]? n)
    {
        notes = n;
        if (n != null) peaks = null;
        InvalidateVisual();
    }

    public void SetPeaks(float[]? p)
    {
        notes = null;
        peaks = p;
        peakMax = 0;
        if (p != null) foreach (var v in p) peakMax = Math.Max(peakMax, v);
        if (peakMax < 1e-4f) peakMax = 1; // silence stays flat instead of blowing up the noise
        InvalidateVisual();
    }

    /// <summary>0..1 of the take; redraws only when the played edge moves by a pixel.</summary>
    public void SetProgress(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        if (Math.Abs((fraction - progress) * Bounds.Width) < 0.5) return;
        progress = fraction;
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height, mid = h / 2;
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // the whole box takes the pointer
        double playedX = progress * w;
        var played = Palette.Accent;
        var rest = Palette.Alpha(Palette.FaintColor, 0.7);
        if (notes is { Length: >= 3 } n)
        {
            // pitch 1 at the top; one row per pitch as far as the height allows
            double nh = Math.Clamp((h - 2) / 24, 1, MaxNoteH);
            var dim = Palette.Alpha(Palette.AccentColor, 0.75);
            for (int i = 0; i + 2 < n.Length; i += 3)
            {
                double x0 = n[i] * w, x1 = Math.Max(x0 + 1, n[i + 1] * w);
                double y = 1 + (1 - n[i + 2]) * (h - 2 - nh);
                ctx.FillRectangle(x0 <= playedX && playedX > 0 ? played : dim, new Rect(x0, y, x1 - x0, nh));
            }
        }
        else if (peaks is not { Length: > 0 } p)
        {
            ctx.FillRectangle(rest, new Rect(0, mid - 0.5, w, 1));
            ctx.FillRectangle(played, new Rect(0, mid - 0.5, playedX, 1));
        }
        else
        {
            int bars = Math.Max(1, (int)((w + Gap) / (BarW + Gap)));
            for (int i = 0; i < bars; i++)
            {
                int from = i * p.Length / bars, to = Math.Max(from + 1, (i + 1) * p.Length / bars);
                float v = 0;
                for (int k = from; k < to && k < p.Length; k++) v = Math.Max(v, p[k]);
                double bh = Math.Max(1, v / peakMax * (h - 2));
                double x = i * (BarW + Gap);
                ctx.FillRectangle(x + BarW / 2 <= playedX ? played : rest, new Rect(x, mid - bh / 2, BarW, bh));
            }
        }
        if (hoverX >= 0) ctx.FillRectangle(Palette.Fg, new Rect(Math.Round(hoverX) - 0.5, 0, 1, h));
    }

    double FractionAt(Point pt) => Bounds.Width > 0 ? Math.Clamp(pt.X / Bounds.Width, 0, 1) : 0;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        dragging = true;
        e.Pointer.Capture(this);
        Jump(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pt = e.GetPosition(this);
        hoverX = Math.Clamp(pt.X, 0, Bounds.Width);
        if (dragging && CarryRequested != null && (pt.Y < -CarryOut || pt.Y > Bounds.Height + CarryOut))
        {
            dragging = false;
            e.Pointer.Capture(null);
            hoverX = -1;
            InvalidateVisual();
            CarryRequested(e);
        }
        else if (dragging) Jump(pt);
        else InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) => dragging = false;

    protected override void OnPointerExited(PointerEventArgs e)
    {
        hoverX = -1;
        InvalidateVisual();
    }

    void Jump(Point pt)
    {
        progress = FractionAt(pt);
        InvalidateVisual();
        SeekRequested?.Invoke(progress);
    }
}
