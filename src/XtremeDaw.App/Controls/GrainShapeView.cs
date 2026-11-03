using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The granular's grain as you hear it: a few windows in a row, overlapping, with the shape and skew of its knobs, from
/// the same table the engine reads (<see cref="Granular.MakeWindow"/>). Only drawn again when one of them changes.
/// </summary>
public sealed class GrainShapeView : Control
{
    /// <summary>Grains drawn, and how much of the width each one takes.</summary>
    const int Grains = 4;
    const double Width1 = 0.4;
    const double Pad = 6, Radius = 6;
    /// <summary>Points of the drawn curve per grain.</summary>
    const int Points = 64;

    double shape = -1, skew;
    Geometry? fill, line;

    public GrainShapeView()
    {
        Height = 48;
        ClipToBounds = true;
    }

    public void Set(double grainShape, double grainSkew)
    {
        if (grainShape == shape && grainSkew == skew) return;
        (shape, skew) = (grainShape, grainSkew);
        fill = line = null;
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        fill = line = null;
    }

    void Build()
    {
        var w = Granular.MakeWindow(Math.Max(0, shape), skew);
        double across = Math.Max(1, Bounds.Width - 2 * Pad), up = Math.Max(1, Bounds.Height - 2 * Pad);
        double each = across * Width1, gap = (across - each) / (Grains - 1);
        var f = new StreamGeometry();
        var l = new StreamGeometry();
        using (var cf = f.Open())
        using (var cl = l.Open())
            for (int g = 0; g < Grains; g++)
            {
                double x0 = Pad + g * gap;
                var start = new Point(x0, Pad + up);
                cf.BeginFigure(start, true);
                cl.BeginFigure(start, false);
                for (int i = 0; i <= Points; i++)
                {
                    double y = w[i * Granular.WindowPoints / Points];
                    var p = new Point(x0 + each * i / Points, Pad + up * (1 - y));
                    cf.LineTo(p);
                    cl.LineTo(p);
                }
                cf.EndFigure(true);
                cl.EndFigure(false);
            }
        fill = f;
        line = l;
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawRectangle(Palette.App, Palette.Pen(Palette.Line), new Rect(Bounds.Size).Deflate(0.5), Radius, Radius);
        if (fill == null || line == null) Build();
        bool on = IsEffectivelyEnabled;
        ctx.DrawGeometry(on ? Palette.AccentSoft : Palette.Raised, null, fill!);
        ctx.DrawGeometry(null, Palette.Pen(on ? Palette.Accent : Palette.Faint, 1.5, PenLineCap.Round, PenLineJoin.Round), line!);
    }
}
