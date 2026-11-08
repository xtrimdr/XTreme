using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The live session's animation on its button (#319, asked by the user: "a cool animation that reacts to what happens"):
/// one orb per person, in their color, joined by a thin line where a glint runs while the session is on. Each orb
/// breathes; someone joining pops in with a burst, someone leaving shrinks away with a ring that closes on it; while
/// someone plays or edits, ripples leave their orb (slow while only their transport runs, red and faster while they
/// record), and every change that arrives (or that this copy sends) flashes a ring on its author. Everything is read
/// from the clock in <see cref="Render"/>, so <see cref="Step"/> only redraws: no state moves per frame, and nothing is
/// allocated while drawing (brushes and pens come from <see cref="Palette"/>'s caches).
/// </summary>
public sealed class LivePulse : Control
{
    /// <summary>Orbs drawn at most; the button's text says how many are in.</summary>
    const int MaxOrbs = 8;
    /// <summary>Distance between two orbs' centers, and the room on each side for their rings (px).</summary>
    const double Spacing = 12, Side = 7, Tall = 20;
    /// <summary>An orb's radius at rest, and how much it breathes.</summary>
    const double Radius = 3.2, Breath = 0.5;
    /// <summary>How long an arrival, a departure and a flash last (s).</summary>
    const double BornSec = 0.6, GoneSec = 0.55, SparkSec = 0.75;
    /// <summary>The intro: each orb a little after the one before when the session starts.</summary>
    const double Stagger = 0.09;

    /// <summary>What someone is doing, from the least to the most.</summary>
    public enum Doing { Connected, Rolling, Active, Recording }

    struct Orb
    {
        public string Who;
        public Color Color;
        public double Born, Gone, Spark, Phase;
        public Doing Doing;
    }

    readonly Orb[] orbs = new Orb[MaxOrbs * 2];
    int count;

    public LivePulse()
    {
        Height = Tall;
        ClipToBounds = false;
        IsHitTestVisible = false;
    }

    static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Who is in now, in order: newcomers pop in, those gone shrink away (they stay until they finish).</summary>
    public void SetPeople(IReadOnlyList<(string Who, Color Color)> people)
    {
        double now = Now;
        bool first = count == 0;
        // gone: those not in the list any more
        for (int i = 0; i < count; i++)
        {
            if (orbs[i].Gone > 0) continue;
            bool still = false;
            foreach (var p in people) if (p.Who == orbs[i].Who) { still = true; break; }
            if (!still) orbs[i].Gone = now;
        }
        int n = 0;
        foreach (var (who, color) in people)
        {
            int at = Find(who);
            if (at >= 0) { orbs[at].Color = color; continue; }
            if (count >= orbs.Length || Live() >= MaxOrbs) break;
            orbs[count++] = new Orb { Who = who, Color = color, Born = now + (first ? n * Stagger : 0), Phase = (who.GetHashCode() & 255) / 255.0 * Math.Tau };
            n++;
        }
        Resize();
        InvalidateVisual();
    }

    /// <summary>Leaves no one, at once (the session ended).</summary>
    public void Clear()
    {
        count = 0;
        Resize();
        InvalidateVisual();
    }

    /// <summary>What someone is doing now: ripples while it isn't only being connected.</summary>
    public void SetDoing(string who, Doing doing)
    {
        int i = Find(who);
        if (i >= 0) orbs[i].Doing = doing;
    }

    /// <summary>Something by this person just happened (a change arrived or went, something picked): a ring flashes.</summary>
    public void Spark(string who)
    {
        int i = Find(who);
        if (i >= 0) orbs[i].Spark = Now;
    }

    /// <summary>Called by the session's timer: drops the orbs that finished leaving and redraws.</summary>
    public void Step()
    {
        if (count == 0) return;
        double now = Now;
        int w = 0;
        for (int i = 0; i < count; i++)
            if (orbs[i].Gone <= 0 || now - orbs[i].Gone < GoneSec) orbs[w++] = orbs[i];
        if (w != count)
        {
            for (int i = w; i < count; i++) orbs[i] = default;
            count = w;
            Resize();
        }
        InvalidateVisual();
    }

    int Find(string who)
    {
        for (int i = 0; i < count; i++)
            if (orbs[i].Gone <= 0 && orbs[i].Who == who) return i;
        return -1;
    }

    int Live()
    {
        int n = 0;
        for (int i = 0; i < count; i++) if (orbs[i].Gone <= 0) n++;
        return n;
    }

    void Resize() => Width = count == 0 ? 0 : Side * 2 + (count - 1) * Spacing;

    /// <summary>A color's brush and pens, from <see cref="Palette"/>'s caches.</summary>
    static (IBrush Brush, IPen Pen, IPen Thin) Paint(Color c)
    {
        var b = Palette.Alpha(c, 1);
        return (b, Palette.Pen(b, 1.3), Palette.Pen(b, 1));
    }

    static double Frac(double x) => x - Math.Floor(x);
    static double EaseOut(double k) => 1 - (1 - k) * (1 - k) * (1 - k);
    /// <summary>Overshoots a little before settling: the pop of someone joining.</summary>
    static double EaseOutBack(double k)
    {
        const double c = 1.9;
        double t = k - 1;
        return 1 + (c + 1) * t * t * t + c * t * t;
    }

    public override void Render(DrawingContext ctx)
    {
        if (count == 0) return;
        double now = Now, cy = Bounds.Height / 2;
        var accent = Paint(Palette.AccentColor);

        // the link between everyone, with a glint running along it
        if (count > 1)
        {
            double x0 = Side, x1 = Side + (count - 1) * Spacing;
            using (ctx.PushOpacity(0.3)) ctx.DrawLine(accent.Thin, new Point(x0, cy), new Point(x1, cy));
            double g = x0 + (x1 - x0) * Frac(now / 2.4);
            using (ctx.PushOpacity(0.7)) ctx.DrawEllipse(accent.Brush, null, new Point(g, cy), 1.3, 1.3);
        }

        for (int i = 0; i < count; i++)
        {
            ref var o = ref orbs[i];
            var paint = Paint(o.Color);
            var at = new Point(Side + i * Spacing, cy);
            double life = now - o.Born;
            if (life < 0) continue;
            double scale = life < BornSec ? Math.Max(0, EaseOutBack(life / BornSec)) : 1, alpha = 1;
            if (o.Gone > 0)
            {
                double k = Math.Clamp((now - o.Gone) / GoneSec, 0, 1);
                scale *= 1 - EaseOut(k);
                alpha = 1 - k;
                // a ring that closes on the one leaving
                using (ctx.PushOpacity(0.8 * (1 - k))) ctx.DrawEllipse(null, paint.Pen, at, Radius + 8 * (1 - k), Radius + 8 * (1 - k));
            }
            double r = (Radius + Breath * Math.Sin(now * Math.Tau / 1.8 + o.Phase)) * scale;

            // ripples while doing something: slow while only rolling, red and quick while recording
            if (o.Gone <= 0 && o.Doing != Doing.Connected)
            {
                double period = o.Doing switch { Doing.Recording => 0.6, Doing.Active => 0.85, _ => 1.6 };
                var pen = o.Doing == Doing.Recording ? Palette.Pen(Palette.Rec, 1.3) : paint.Thin;
                for (int w = 0; w < 2; w++)
                {
                    double k = Frac(now / period + o.Phase / Math.Tau + w * 0.5);
                    double rr = r + 1 + k * (o.Doing == Doing.Rolling ? 5 : 7);
                    using (ctx.PushOpacity((1 - k) * (o.Doing == Doing.Rolling ? 0.35 : 0.75))) ctx.DrawEllipse(null, pen, at, rr, rr);
                }
            }

            // a flash: someone joining, or a change of theirs
            double since = Math.Min(life, o.Spark > 0 ? now - o.Spark : double.MaxValue);
            if (o.Gone <= 0 && since < SparkSec)
            {
                double k = since / SparkSec, rr = r + 1 + EaseOut(k) * 9;
                using (ctx.PushOpacity(1 - k)) ctx.DrawEllipse(null, paint.Pen, at, rr, rr);
                using (ctx.PushOpacity(0.35 * (1 - k))) ctx.DrawEllipse(paint.Brush, null, at, r + 3, r + 3);
            }

            if (r <= 0.05) continue;
            using (ctx.PushOpacity(0.2 * alpha)) ctx.DrawEllipse(paint.Brush, null, at, r + 2.2, r + 2.2);
            using (ctx.PushOpacity(alpha)) ctx.DrawEllipse(paint.Brush, null, at, r, r);
            if (o.Doing == Doing.Recording && o.Gone <= 0) ctx.DrawEllipse(Palette.Rec, null, at, r * 0.45, r * 0.45);
        }
    }
}
