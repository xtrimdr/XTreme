using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The mixer's connections band (asked by the user, #264): under the strips, the selected channel's active sends drawn
/// as ropes that hang from its strip to each bus it sends to, and the sends that reach it (when it is a bus) fainter,
/// from each channel that sends to it. Each rope ends on a knob for the send's level at the other channel's end: under
/// the bus a send arrives at (the arrow goes into it), under the channel an incoming send leaves from (the rope starts
/// there); the window puts real knobs where <see cref="KnobCenter"/> says, sized by the band's height
/// (<see cref="KnobSizeFor"/>), and the level is written beside each. Ropes whose spans overlap hang at different depths
/// (<see cref="Layout"/>: the shorter ones higher), so they don't run on top of each other: each one drops straight,
/// runs along its lane and rises straight into its end, with round corners, edged in the panel's color so a crossing
/// reads, and the ropes sharing a port fan out so their drops don't cross another's run. A rope takes the color of the
/// bus it reaches. The window gives it the
/// strips' centres in its own coordinates (<see cref="Set"/>) each time the selection, the sends or the strips' places
/// change: it only draws them. Geometries are built once per <see cref="Set"/> (or a new height) and texts come from
/// <see cref="Palette.Text"/>, so a redraw allocates nothing new. At the selected channel's end the ropes split into
/// two ports, labelled (asked by the user, #264): the sends leave from OUT, to the right of the strip's centre, and the
/// ones that reach it arrive at IN, to the left. The other end is labelled too, beside the rope under its knob: IN
/// where a send arrives, OUT where an incoming one leaves. Every rope leaves and arrives vertically, so its arrow
/// points straight up into the knob or the port.
/// A channel's real output (<see cref="Rope.Main"/>, asked by the user, #301) is drawn apart from the sends: solid,
/// thicker, in the text's neutral color, without a knob or a level (its end is just labelled), and under the sends.
/// </summary>
public sealed class SendRopes : Control
{
    /// <summary>Where a rope's plain end (the selected channel's) leaves or arrives (px from the top), and the room under the knobs before the first depth.</summary>
    const double AnchorY = 18, Clear = 10;
    /// <summary>How far the IN and OUT ports sit from the selected strip's centre (px), and where their labels go (px from the top).</summary>
    const double PortGap = 14, PortLabelY = 2;
    /// <summary>A rope's width at full level and at none (px), and its arrow's length and half width.</summary>
    const double MaxThick = 3, MinThick = 1.25, ArrowL = 7, ArrowW = 4.5;
    /// <summary>The knobs' size as a share of the band's height, and the least and most they get (px).</summary>
    const double KnobShare = 0.3, KnobMin = 22, KnobMax = 56;
    /// <summary>The least room between two knobs (px), the least a knob shrinks to so it never covers the next one (#290), and how far apart the ends of ropes to channels scrolled out of view land at their dock's edge.</summary>
    const double KnobGap = 4, KnobFloor = 14, OffStep = 6;
    /// <summary>An output rope's width (px) and how strong its neutral color goes (#301).</summary>
    const double MainThick = 3.5, MainAlpha = 0.55;
    /// <summary>How much of an outgoing rope's look an incoming one keeps: they are the faint ones.</summary>
    const double InFade = 0.45;
    /// <summary>The radius of a rope's corners, the dark edge around it so crossings stay readable, and the most the ropes fan out at a port (px).</summary>
    const double Corner = 10, HaloW = 3, FanMax = 4, FanRoom = 10;

    /// <summary>How far apart the ropes sharing a port drop (px), for that many lanes.</summary>
    static double FanStep(int lanes) => lanes <= 1 ? 0 : Math.Min(FanMax, FanRoom / (lanes - 1));

    /// <summary>A rope from <paramref name="FromX"/> to <paramref name="ToX"/> (px), its level (0..1), whether it is pre-fader, whether it comes into the selected channel, the color of the bus it reaches (asked by the user, #264: a rope always takes its destination's color) whether its knob's end is scrolled out of its dock (<paramref name="Off"/>: the rope stops at the dock's edge, without a knob, #290) and whether it is the channel's output rather than a send (<paramref name="Main"/>: no knob, drawn apart, #301).</summary>
    public readonly record struct Rope(double FromX, double ToX, double Level, bool Pre, bool In, Color Color, bool Off = false, bool Main = false);

    /// <summary>The rope ends without a knob: scrolled out of view or an output.</summary>
    static bool Knobless(in Rope r) => r.Off || r.Main;

    sealed class Built
    {
        public required StreamGeometry Line, Arrow;
        public required IPen Pen, Halo;
        public required IBrush Fill;
        public required TextLayout Text;
        public required TextLayout End;
        public required Point TextAt, Knot, EndAt;
        public required Rect Plate;
        /// <summary>Whether the level and the end's label are drawn (the knob is on screen and there is room).</summary>
        public required bool Labeled, Ended;
    }

    Rope[] ropes = [];
    string[] labels = [];
    Built[] built = [];
    Point[] knobs = [];
    double[] depth = [];
    int[] lane = [];
    bool[] roomy = [];
    int lanes;
    double knobSize = KnobMin;
    double builtH = -1, laidH = -1;
    string emptyText = "";
    TextLayout? inLabel, outLabel;
    double portX = double.NaN;

    /// <summary>The knobs' size (px) in a band <paramref name="h"/> px high.</summary>
    public static double KnobSizeFor(double h) => Math.Clamp(Math.Round(h * KnobShare), KnobMin, KnobMax);

    /// <summary>Where rope <paramref name="i"/>'s knob goes, after <see cref="Layout"/>.</summary>
    public Point KnobCenter(int i) => i < knobs.Length ? knobs[i] : default;

    /// <summary>The knobs' size after <see cref="Layout"/>: the band's (<see cref="KnobSizeFor"/>), shrunk so neighbouring knobs never overlap (#290).</summary>
    public double KnobSize => knobSize;

    /// <summary>Whether rope <paramref name="i"/>'s level is written beside its knob: only when it fits before the next knob (#290).</summary>
    public bool ShowsLevel(int i) => i < roomy.Length && roomy[i];

    /// <summary>The ropes to draw; <paramref name="none"/> is shown when there are none.</summary>
    public void Set(IReadOnlyList<Rope> list, string none)
    {
        ropes = [.. list];
        labels = new string[ropes.Length];
        for (int i = 0; i < ropes.Length; i++)
            labels[i] = ValueFormat.Percent(ropes[i].Level) + (ropes[i].Pre ? " PRE" : "");
        emptyText = none;
        inLabel = outLabel = null;
        portX = double.NaN;
        foreach (var r in ropes)
        {
            portX = r.In ? r.ToX : r.FromX;
            if (r.In) inLabel ??= Palette.Text("IN", 8, Palette.Faint, Palette.Mono);
            else outLabel ??= Palette.Text("OUT", 8, Palette.Dim, Palette.Mono);
        }
        builtH = laidH = -1;
        InvalidateVisual();
    }

    /// <summary>
    /// Places the knobs and each rope's depth for a band <paramref name="h"/> px high: every rope gets the first lane
    /// (from the top) where no rope already there overlaps it, the shorter ones first, and the lanes share the room
    /// under the knobs.
    /// </summary>
    public void Layout(double h)
    {
        if (laidH == h && knobs.Length == ropes.Length) return;
        laidH = h;
        int n = ropes.Length;
        knobs = new Point[n];
        depth = new double[n];
        lane = new int[n];
        roomy = new bool[n];
        lanes = 0;
        if (n == 0) return;
        // the knobs' ends: the ones scrolled out of view fan out from their dock's edge, towards the selected strip
        var xs = new double[n];
        for (int i = 0; i < n; i++)
        {
            var r = ropes[i];
            xs[i] = r.In ? r.FromX : r.ToX;
            if (!r.Off) continue;
            int same = 0;
            for (int j = 0; j < i; j++)
                if (ropes[j].Off && Math.Abs((ropes[j].In ? ropes[j].FromX : ropes[j].ToX) - xs[i]) < 0.5) same++;
            double port = r.In ? r.ToX : r.FromX;
            xs[i] += (port >= xs[i] ? 1 : -1) * OffStep * same;
        }
        // as big as the band asks, but never over the next knob: narrow strips get smaller knobs
        double k = KnobSizeFor(h), gap = double.MaxValue;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (!Knobless(ropes[i]) && !Knobless(ropes[j])) gap = Math.Min(gap, Math.Abs(xs[i] - xs[j]));
        if (gap < double.MaxValue) k = Math.Max(KnobFloor, Math.Min(k, Math.Floor(gap - KnobGap)));
        knobSize = k;
        double half = k / 2 + 4;
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) => Math.Abs(ropes[a].ToX - ropes[a].FromX).CompareTo(Math.Abs(ropes[b].ToX - ropes[b].FromX)));
        var taken = new List<List<(double L, double R)>>();
        foreach (int i in order)
        {
            var r = ropes[i];
            double l = Math.Min(r.FromX, r.ToX) - half, rr = Math.Max(r.FromX, r.ToX) + half;
            int li = 0;
            for (; li < taken.Count; li++)
            {
                bool free = true;
                foreach (var (a, b) in taken[li]) if (l < b && rr > a) { free = false; break; }
                if (free) break;
            }
            if (li == taken.Count) taken.Add([]);
            taken[li].Add((l, rr));
            lane[i] = li;
        }
        lanes = taken.Count;
        double kc = 4 + k / 2, top = 4 + k + Clear, bottom = Math.Max(top + 4, h - 6);
        for (int i = 0; i < n; i++)
        {
            var r = ropes[i];
            knobs[i] = new Point(xs[i], kc);
            depth[i] = top + (bottom - top) * (lane[i] + 1) / (lanes + 0.35);
        }
        // a level is written beside its knob only when it fits before the next one to the right
        for (int i = 0; i < n; i++)
        {
            if (Knobless(ropes[i])) continue;
            double next = double.MaxValue;
            for (int j = 0; j < n; j++)
                if (j != i && !Knobless(ropes[j]) && xs[j] > xs[i]) next = Math.Min(next, xs[j]);
            double w = Palette.Text(labels[i], 9, Palette.Dim, Palette.Mono).Width + 6;
            roomy[i] = next - xs[i] >= k + 4 + w + 2;
        }
    }

    void Build(double h)
    {
        Layout(h);
        builtH = h;
        built = new Built[ropes.Length];
        double k = knobSize;
        for (int i = 0; i < ropes.Length; i++)
        {
            var r = ropes[i];
            double kx = knobs[i].X;
            var color = Color.FromRgb(r.Color.R, r.Color.G, r.Color.B);
            double level = Math.Clamp(r.Level, 0, 1), fade = r.In ? InFade : 1;
            // the knob's end leaves from under the knob; the selected channel's end, from the top
            // (a rope to a channel out of view has no knob: it stops at its dock's edge, at the knobs' row)
            double knobY = Knobless(r) ? knobs[i].Y : knobs[i].Y + k / 2 + 2;
            // at the selected strip's port the ropes fan out by lane, so their drops never cross another's run: leaving,
            // the deeper ones stay behind (against the way they go); arriving, the deeper ones land further along
            double dir = r.ToX >= r.FromX ? 1 : -1;
            double fan = (lane[i] - (lanes - 1) / 2.0) * FanStep(lanes);
            var a = new Point(r.In ? kx : r.FromX + PortGap - dir * fan, r.In ? knobY : AnchorY);
            var b = new Point(r.In ? r.ToX - PortGap + dir * fan : kx, r.In ? AnchorY : knobY);
            double d = Math.Max(depth[i], Math.Max(a.Y, b.Y) + 4);
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(a, false);
                // straight down, along its lane and straight up into the end, with round corners: the lanes keep the runs
                // apart and the straight drops read at a glance where each rope goes
                double run = Math.Abs(b.X - a.X);
                if (run < 1) g.LineTo(b);
                else
                {
                    double rad = Math.Min(Corner, Math.Min(run / 2, Math.Min(d - a.Y, d - b.Y)));
                    g.LineTo(new Point(a.X, d - rad));
                    g.QuadraticBezierTo(new Point(a.X, d), new Point(a.X + dir * rad, d));
                    g.LineTo(new Point(b.X - dir * rad, d));
                    g.QuadraticBezierTo(new Point(b.X, d), new Point(b.X, d - rad));
                    g.LineTo(b);
                }
                g.EndFigure(false);
            }
            // the arrow points straight up into the end
            var arrow = new StreamGeometry();
            using (var g = arrow.Open())
            {
                g.BeginFigure(b, true);
                g.LineTo(new Point(b.X - ArrowW, b.Y + ArrowL));
                g.LineTo(new Point(b.X, b.Y + ArrowL * 0.7));
                g.LineTo(new Point(b.X + ArrowW, b.Y + ArrowL));
                g.EndFigure(true);
            }
            double thick = r.Main ? MainThick : (MinThick + (MaxThick - MinThick) * level) * (r.In ? 0.8 : 1);
            double alpha = r.Main ? MainAlpha * (r.In ? 0.8 : 1) : (0.55 + 0.45 * level) * fade;
            var text = Palette.Text(labels[i], 9, r.In ? Palette.Faint : Palette.Dim, Palette.Mono);
            var kp = knobs[i];
            double tx = kp.X + k / 2 + 4, ty = kp.Y - text.Height / 2;
            // the knob's end of the rope: IN where a send arrives, OUT where an incoming one leaves
            var end = r.In ? Palette.Text("OUT", 8, Palette.Faint, Palette.Mono) : Palette.Text("IN", 8, Palette.Dim, Palette.Mono);
            built[i] = new Built
            {
                Line = line, Arrow = arrow, Fill = Palette.Alpha(color, r.Main ? Math.Min(1, alpha + 0.2) : 0.95 * fade),
                Pen = Palette.Pen(Palette.Alpha(color, alpha), thick, PenLineCap.Round, PenLineJoin.Round),
                Halo = Palette.Pen(Palette.Panel, thick + HaloW, PenLineCap.Round, PenLineJoin.Round),
                Text = text, TextAt = new Point(tx, ty), Knot = a,
                End = end, EndAt = new Point(kp.X - ArrowW - 3 - end.Width, knobY + 1),
                Plate = new Rect(tx - 3, ty - 1, text.Width + 6, text.Height + 2),
                Labeled = !Knobless(r) && roomy[i], Ended = !r.Off,
            };
        }
    }

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        if (ropes.Length == 0)
        {
            if (emptyText.Length > 0)
            {
                var t = Palette.Text(emptyText, 11, Palette.Faint);
                ctx.DrawText(t, new Point(12, (size.Height - t.Height) / 2));
            }
            return;
        }
        if (builtH != size.Height) Build(size.Height);
        using (ctx.PushClip(new Rect(size)))
        {
            // the outputs first, then the incoming sends, under the outgoing ones
            for (int pass = 0; pass < 3; pass++)
                for (int i = 0; i < built.Length; i++)
                {
                    var rp = ropes[i];
                    if ((rp.Main ? 0 : rp.In ? 1 : 2) != pass) continue;
                    var r = built[i];
                    ctx.DrawGeometry(null, r.Halo, r.Line);
                    ctx.DrawGeometry(null, r.Pen, r.Line);
                    ctx.DrawGeometry(r.Fill, null, r.Arrow);
                    ctx.DrawEllipse(r.Fill, null, r.Knot, 3.5, 3.5);
                }
            // the selected channel's ports, labelled
            if (inLabel != null) Port(ctx, inLabel, portX - PortGap);
            if (outLabel != null) Port(ctx, outLabel, portX + PortGap);
            // the levels beside the knobs, on a plate so no rope crosses a text
            foreach (var r in built)
            {
                if (r.Labeled)
                {
                    ctx.DrawRectangle(Palette.Panel, Palette.Pen(Palette.Line), r.Plate, 3, 3);
                    ctx.DrawText(r.Text, r.TextAt);
                }
                if (r.Ended) ctx.DrawText(r.End, r.EndAt);
            }
        }
    }

    /// <summary>A port's label, centred over where its ropes meet the selected strip.</summary>
    static void Port(DrawingContext ctx, TextLayout label, double x) =>
        ctx.DrawText(label, new Point(x - label.Width / 2, PortLabelY));
}
