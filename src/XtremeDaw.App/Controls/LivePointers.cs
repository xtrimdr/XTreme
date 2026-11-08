using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The others' mice in a live session (#319, asked by the user: "cursores como de mouse, con el color del colaborador,
/// que muestren específicamente lo que están tocando"): an arrow in each person's color with their name, over the
/// whole window and under the dialogs, never taking a click. The window tells it where each one is on this copy's
/// screen (<see cref="Put"/>) a few times a second; between two of those the arrow glides there on every
/// <see cref="Step"/>, so it reads smooth. While someone holds a control a ring pulses at the tip. One that stops being
/// anywhere fades out, one that shows up fades in where it is. Nothing is allocated while drawing.
/// </summary>
public sealed class LivePointers : Control
{
    /// <summary>Arrows drawn at most.</summary>
    const int Max = 16;
    /// <summary>How much of the way to its target an arrow goes on each step (~30 a second).</summary>
    const double Glide = 0.45;
    /// <summary>How much an arrow fades in or out on each step.</summary>
    const double FadeStep = 0.2;
    /// <summary>The name tag's text size, its padding and where it hangs from the arrow's tip (px).</summary>
    const double TagFont = 10, TagPad = 5, TagX = 11, TagY = 15;

    struct Arrow
    {
        public string Who;
        public Color Color;
        public Point At, Target;
        public double Alpha;
        public bool Shown, Holding, Seen;
    }

    readonly Arrow[] arrows = new Arrow[Max];
    int count;
    static StreamGeometry? shape;

    public LivePointers()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    /// <summary>The arrow, with its tip at (0, 0): built once.</summary>
    static StreamGeometry Shape()
    {
        if (shape != null) return shape;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0, 0), true);
            c.LineTo(new Point(0, 15));
            c.LineTo(new Point(4, 11.5));
            c.LineTo(new Point(7, 17.5));
            c.LineTo(new Point(9.5, 16.3));
            c.LineTo(new Point(6.6, 10.6));
            c.LineTo(new Point(11.5, 10.6));
            c.EndFigure(true);
        }
        return shape = g;
    }

    /// <summary>Starts a round of <see cref="Put"/>: whoever isn't put again before <see cref="End"/> fades out.</summary>
    public void Begin()
    {
        for (int i = 0; i < count; i++) arrows[i].Seen = false;
    }

    /// <summary>Where someone's arrow goes on this screen (in this control's coordinates); null = nowhere in sight.</summary>
    public void Put(string who, Color color, Point? at, bool holding)
    {
        int i = Find(who);
        if (i < 0)
        {
            if (at == null || count == Max) return;
            i = count++;
            arrows[i] = new Arrow { Who = who };
        }
        ref var a = ref arrows[i];
        a.Seen = true;
        a.Color = color;
        a.Holding = holding && at != null;
        if (at is not { } p) { a.Shown = false; return; }
        // one that was out of sight appears where it is, it doesn't fly in from where it left
        if (!a.Shown && a.Alpha <= 0) a.At = p;
        a.Target = p;
        a.Shown = true;
    }

    /// <summary>Ends the round: those not put fade out.</summary>
    public void End()
    {
        for (int i = 0; i < count; i++)
            if (!arrows[i].Seen) { arrows[i].Shown = false; arrows[i].Holding = false; }
    }

    /// <summary>Everyone out at once (the session ended).</summary>
    public void Clear()
    {
        for (int i = 0; i < count; i++) arrows[i] = default;
        count = 0;
        InvalidateVisual();
    }

    /// <summary>Called by the session's timer: the arrows glide toward where they go, fade in or out, and redraw.</summary>
    public void Step()
    {
        if (count == 0) return;
        int w = 0;
        bool moving = false;
        for (int i = 0; i < count; i++)
        {
            ref var a = ref arrows[i];
            double was = a.Alpha;
            var from = a.At;
            a.Alpha = Math.Clamp(a.Alpha + (a.Shown ? FadeStep : -FadeStep), 0, 1);
            a.At = new Point(a.At.X + (a.Target.X - a.At.X) * Glide, a.At.Y + (a.Target.Y - a.At.Y) * Glide);
            if (Math.Abs(a.At.X - a.Target.X) < 0.3 && Math.Abs(a.At.Y - a.Target.Y) < 0.3) a.At = a.Target;
            moving |= was != a.Alpha || from != a.At || a.Holding;
            if (a.Shown || a.Alpha > 0) arrows[w++] = a;
        }
        for (int i = w; i < count; i++) arrows[i] = default;
        count = w;
        if (moving) InvalidateVisual();
    }

    int Find(string who)
    {
        for (int i = 0; i < count; i++) if (arrows[i].Who == who) return i;
        return -1;
    }

    public override void Render(DrawingContext ctx)
    {
        if (count == 0) return;
        var geo = Shape();
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        var edge = Palette.Pen(Palette.App, 1);
        for (int i = 0; i < count; i++)
        {
            ref var a = ref arrows[i];
            if (a.Alpha <= 0) continue;
            var fill = Palette.Alpha(a.Color, 1);
            using (ctx.PushOpacity(a.Alpha))
            {
                if (a.Holding)
                {
                    // what they hold: a ring that keeps opening at the tip
                    double k = now * 1.4 - Math.Floor(now * 1.4);
                    using (ctx.PushOpacity(0.9 * (1 - k)))
                        ctx.DrawEllipse(null, Palette.Pen(fill, 1.5), a.At, 4 + 9 * k, 4 + 9 * k);
                    ctx.DrawEllipse(Palette.Alpha(a.Color, 0.35), null, a.At, 4, 4);
                }
                using (ctx.PushTransform(Matrix.CreateTranslation(Math.Round(a.At.X) + 0.5, Math.Round(a.At.Y) + 0.5)))
                    ctx.DrawGeometry(fill, edge, geo);
                var text = Palette.Text(a.Who, TagFont, Palette.App, Palette.UiBold);
                var r = new Rect(Math.Round(a.At.X + TagX), Math.Round(a.At.Y + TagY), text.Width + 2 * TagPad, text.Height + 2);
                ctx.DrawRectangle(fill, null, r, 4, 4);
                ctx.DrawText(text, new Point(r.X + TagPad, r.Y + 1));
            }
        }
    }
}
