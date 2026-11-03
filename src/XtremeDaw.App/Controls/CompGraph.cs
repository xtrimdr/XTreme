using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.App.Controls;

/// <summary>
/// A compressor's graph (asked by the user: to see how it is compressing), the same for every compressor. On the left its
/// curve, input against output with its gain, and a dot where the signal is now; on the right the last seconds scrolling:
/// what came in and went out as bars from the bottom and the reduction from the top, in the accent. It reads the running
/// effect (<see cref="Live"/>) a few times a second and redraws only when a new point arrived; without one (the slot isn't
/// in the engine) it draws the curve from <see cref="Offline"/>. Nothing in <see cref="Render"/> allocates: segments with
/// cached pens, the reduction's text rebuilt only when its tenth changes.
/// </summary>
public sealed class CompGraph : Control
{
    const int TickMs = 33;
    /// <summary>The levels both axes of the curve and the history's bars cover, dBFS.</summary>
    const double Lo = -60, Hi = 0;
    /// <summary>The deepest reduction the history shows, dB.</summary>
    const double GrRange = 24;
    /// <summary>How much the history shows, in scope points (about 4 s).</summary>
    const int Span = 768;
    /// <summary>The dot fades out after this long without signal, in points (about 0.3 s).</summary>
    const int DotHold = 60;
    const double Gap = 8, ColW = 2;

    static readonly IBrush InFill = Palette.Alpha(Colors.White, 0.08);
    static readonly IBrush OutFill = Palette.Alpha(Colors.White, 0.16);
    static readonly IPen Grid = Palette.Pen(Palette.Alpha(Colors.White, 0.06));
    static readonly IPen Unity = Palette.Pen(Palette.Alpha(Colors.White, 0.14));

    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(TickMs) };
    int lastWritten = -1;
    int grTenths = int.MinValue;
    string grText = "";

    /// <summary>The running effect; null while the slot isn't in the engine.</summary>
    public Func<ICompGraph?>? Live { get; init; }
    /// <summary>The same settings without the engine, for the curve alone; the panel replaces it on every edit.</summary>
    public ICompGraph? Offline { get; set; }

    public CompGraph()
    {
        Height = 128;
        ClipToBounds = true;
        ToolTip.SetTip(this, Loc.T("fx.comp.graph.tip"));
        timer.Tick += (_, _) =>
        {
            int w = (Live?.Invoke())?.Scope.Written ?? -1;
            if (w == lastWritten) return;
            lastWritten = w;
            InvalidateVisual();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Live != null) timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        timer.Stop();
    }

    static double Unit(double db, double lo, double hi) => Math.Clamp((db - lo) / (hi - lo), 0, 1);

    public override void Render(DrawingContext ctx)
    {
        double h = Bounds.Height, w = Bounds.Width;
        double side = Math.Min(h, Math.Max(60, w * 0.45));
        var curveBox = new Rect(0, 0, side, h);
        var historyBox = new Rect(side + Gap, 0, Math.Max(0, w - side - Gap), h);
        ctx.DrawRectangle(Palette.App, Palette.Pen(Palette.Line), curveBox, 6, 6);
        ctx.DrawRectangle(Palette.App, Palette.Pen(Palette.Line), historyBox, 6, 6);

        var live = Live?.Invoke();
        var curve = live ?? Offline;
        double X(double db) => curveBox.X + Unit(db, Lo, Hi) * curveBox.Width;
        double Y(double db) => curveBox.Bottom - Unit(db, Lo, Hi) * curveBox.Height;
        for (double db = Lo + 12; db < Hi; db += 12)
        {
            ctx.DrawLine(Grid, new Point(X(db), curveBox.Y), new Point(X(db), curveBox.Bottom));
            ctx.DrawLine(Grid, new Point(curveBox.X, Y(db)), new Point(curveBox.Right, Y(db)));
        }
        ctx.DrawLine(Unity, new Point(X(Lo), Y(Lo)), new Point(X(Hi), Y(Hi)));
        if (curve != null)
        {
            var pen = Palette.Pen(Palette.Accent, 2, PenLineCap.Round, PenLineJoin.Round);
            var prev = new Point(X(Lo), Y(curve.CurveOutDb(Lo)));
            for (double db = Lo + 1; db <= Hi; db++)
            {
                var p = new Point(X(db), Y(curve.CurveOutDb(db)));
                ctx.DrawLine(pen, prev, p);
                prev = p;
            }
        }
        if (live == null || historyBox.Width < 8) return;

        var scope = live.Scope;
        int written = scope.Written;
        if (written <= 0) return;
        int first = Math.Max(0, written - Span);

        // where the signal is now: the newest point that wasn't silence, if it is recent
        for (int i = written - 1; i >= Math.Max(first, written - DotHold); i--)
        {
            scope.Read(i, out float inDb, out float outDb, out _);
            if (inDb <= Lo) continue;
            ctx.DrawEllipse(Palette.Fg, null, new Point(X(inDb), Y(outDb)), 3.5, 3.5);
            break;
        }

        int cols = Math.Max(1, (int)(historyBox.Width / ColW));
        double per = Span / (double)cols;
        double grY0 = historyBox.Y;
        var grFill = Palette.Alpha(Palette.AccentColor, 0.18);
        var grPen = Palette.Pen(Palette.Accent, 1.5);
        for (double db = 6; db < GrRange; db += 6)
        {
            double y = grY0 + db / GrRange * historyBox.Height;
            ctx.DrawLine(Grid, new Point(historyBox.X, y), new Point(historyBox.Right, y));
        }
        Point? last = null;
        for (int c = 0; c < cols; c++)
        {
            // column c covers the points that are this far back from the newest
            int to = written - (int)Math.Round((cols - 1 - c) * per);
            int from = Math.Max(first, to - Math.Max(1, (int)Math.Round(per)));
            if (to <= from) { last = null; continue; }
            float inMax = CompScope.Floor, outMax = CompScope.Floor, grMin = 0;
            for (int i = from; i < to; i++)
            {
                scope.Read(i, out float a, out float b, out float g);
                if (a > inMax) inMax = a;
                if (b > outMax) outMax = b;
                if (g < grMin) grMin = g;
            }
            double x = historyBox.X + c * ColW;
            double hIn = Unit(inMax, Lo, Hi) * historyBox.Height, hOut = Unit(outMax, Lo, Hi) * historyBox.Height;
            if (hIn > 0) ctx.DrawRectangle(InFill, null, new Rect(x, historyBox.Bottom - hIn, ColW, hIn));
            if (hOut > 0) ctx.DrawRectangle(OutFill, null, new Rect(x, historyBox.Bottom - hOut, ColW, hOut));
            double yGr = grY0 + Math.Min(-grMin, GrRange) / GrRange * historyBox.Height;
            if (yGr > grY0 + 0.5) ctx.DrawRectangle(grFill, null, new Rect(x, grY0, ColW, yGr - grY0));
            var pt = new Point(x + ColW / 2, yGr);
            if (last is { } lp) ctx.DrawLine(grPen, lp, pt);
            last = pt;
        }

        // the reduction now, the deepest of the last few points, so a hit can be read
        float now = 0;
        for (int i = Math.Max(first, written - 8); i < written; i++)
        {
            scope.Read(i, out _, out _, out float g);
            if (g < now) now = g;
        }
        int tenths = (int)Math.Round(now * 10);
        if (tenths != grTenths)
        {
            grTenths = tenths;
            grText = tenths == 0 ? "0 dB" : $"{tenths / 10.0:0.0} dB";
        }
        var text = Palette.Text(grText, 11, Palette.Dim, Palette.Mono);
        ctx.DrawText(text, new Point(historyBox.Right - text.Width - 6, historyBox.Bottom - text.Height - 4));
    }
}
