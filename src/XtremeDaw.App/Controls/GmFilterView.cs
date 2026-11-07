using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The GM instrument's filter as it sounds (asked by the user, #212: better panels for the sound): the low pass of the
/// preset with Brillo and Resonancia on it, for a hard note (the line) and for the softest one (dashed, what the
/// velocity's Brillo closes). Read only; the curves are rebuilt only when a value or the size changes.
/// </summary>
public sealed class GmFilterView : Control
{
    const double Pad = 6, Radius = 6;
    /// <summary>The frequencies across, on a log scale.</summary>
    const double LoHz = 20, HiHz = 20000;
    /// <summary>The level range up and down: room for the +20 dB the resonance can add.</summary>
    const double TopDb = 24, BottomDb = -36;
    /// <summary>Points along a curve: enough for a smooth peak at any width the panel takes.</summary>
    const int Steps = 96;

    double hard = HiHz, soft = HiHz, q = 0.707;
    Geometry? hardLine, softLine, fill;

    public GmFilterView()
    {
        Height = 72;
        ClipToBounds = true;
        IsHitTestVisible = false;
    }

    /// <summary>Cutoffs in Hz (≥ the top = open) and the Q of the resonance.</summary>
    public void Set(double hardHz, double softHz, double resonanceQ)
    {
        if (hardHz == hard && softHz == soft && resonanceQ == q) return;
        (hard, soft, q) = (hardHz, softHz, resonanceQ);
        hardLine = softLine = fill = null;
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        hardLine = softLine = fill = null;
    }

    double X(double hz) => Pad + Math.Log(hz / LoHz) / Math.Log(HiHz / LoHz) * Math.Max(1, Bounds.Width - 2 * Pad);
    double Y(double db) => Pad + (TopDb - Math.Clamp(db, BottomDb, TopDb)) / (TopDb - BottomDb) * Math.Max(1, Bounds.Height - 2 * Pad);

    /// <summary>A 2-pole low pass's level at <paramref name="hz"/>; flat when it is open.</summary>
    double Db(double hz, double cutoff)
    {
        if (cutoff >= HiHz) return 0;
        double r = hz / cutoff, re = 1 - r * r, im = r / Math.Max(0.5, q);
        return -10 * Math.Log10(re * re + im * im);
    }

    Geometry Curve(double cutoff, bool closed)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        double bottom = Bounds.Height - Pad;
        c.BeginFigure(closed ? new Point(X(LoHz), bottom) : new Point(X(LoHz), Y(Db(LoHz, cutoff))), closed);
        for (int i = 0; i <= Steps; i++)
        {
            double hz = LoHz * Math.Pow(HiHz / LoHz, i / (double)Steps);
            c.LineTo(new Point(X(hz), Y(Db(hz, cutoff))));
        }
        if (closed) c.LineTo(new Point(X(HiHz), bottom));
        c.EndFigure(closed);
        return g;
    }

    public override void Render(DrawingContext ctx)
    {
        var box = new Rect(Bounds.Size);
        ctx.DrawRectangle(Palette.App, Palette.Pen(Palette.Line), box.Deflate(0.5), Radius, Radius);
        var grid = Palette.Pen(Palette.Alpha(Palette.LineColor, 0.9));
        foreach (double hz in (ReadOnlySpan<double>)[100, 1000, 10000])
        {
            double x = Math.Round(X(hz)) + 0.5;
            ctx.DrawLine(grid, new Point(x, Pad), new Point(x, Bounds.Height - Pad));
        }
        double y0 = Math.Round(Y(0)) + 0.5;
        ctx.DrawLine(grid, new Point(Pad, y0), new Point(Bounds.Width - Pad, y0));
        if (hardLine == null || softLine == null || fill == null)
        {
            hardLine = Curve(hard, false);
            softLine = Curve(soft, false);
            fill = Curve(hard, true);
        }
        bool on = IsEffectivelyEnabled;
        ctx.DrawGeometry(on ? Palette.AccentSoft : Palette.Raised, null, fill);
        if (soft < hard) ctx.DrawGeometry(null, Palette.Dashed(Palette.Dim, 1), softLine);
        ctx.DrawGeometry(null, Palette.Pen(on ? Palette.Accent : Palette.Faint, 1.5, PenLineCap.Round, PenLineJoin.Round), hardLine);
    }
}
