using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace XtremeDaw.App.Controls;

/// <summary>
/// Horizontal stereo meter like the web app's StereoMeter: fast attack, 24 dB/s release, 1.5 s peak hold
/// and a clip light that stays on until clicked. A click anywhere else raises <see cref="OpenRequested"/> (the web's
/// meter wrapper opens Configuración → Entradas); with nobody listening it has no tooltip and no hand cursor there.
/// Native: <see cref="ShowScale"/> adds a small dB scale under the bars (the transport bar's input, asked by the user).
/// </summary>
public sealed class StereoMeter : Control
{
    const double MinDb = -60;
    readonly double[] level = [MinDb, MinDb];
    readonly double[] hold = [MinDb, MinDb];
    readonly double[] holdAge = [0, 0];
    bool clip;
    DateTime last = DateTime.UtcNow;

    const double LightW = 7;
    /// <summary>With the scale: the two bars take this much and the ticks and numbers the rest.</summary>
    const double ScaledBarsH = 10, ScaleH = 11;
    static readonly int[] ScaleDb = [-48, -36, -24, -12, -6, 0];
    bool showScale;

    /// <summary>Ticks and numbers in dB under the bars (−60 to 0, the meter's range).</summary>
    public bool ShowScale
    {
        get => showScale;
        set { showScale = value; Height = value ? ScaledBarsH + ScaleH : 14; InvalidateVisual(); }
    }
    readonly string clipTip = Loc.T("meter.clip.tip"), openTip = Loc.T("meter.open.tip");
    static readonly Avalonia.Input.Cursor Hand = new(Avalonia.Input.StandardCursorType.Hand);

    Action? openRequested;
    bool overLight;

    /// <summary>A click outside the clip light.</summary>
    public event Action? OpenRequested
    {
        add { openRequested += value; UpdateHover(); }
        remove { openRequested -= value; UpdateHover(); }
    }

    public StereoMeter()
    {
        Height = 14;
        UpdateHover();
    }

    bool OnLight(Point p) => p.X >= Bounds.Width - LightW - 3 && p.Y <= BarsH;

    double BarsH => showScale ? ScaledBarsH : Bounds.Height;

    void UpdateHover()
    {
        bool open = openRequested != null;
        Cursor = overLight || open ? Hand : null;
        ToolTip.SetTip(this, overLight ? clipTip : open ? openTip : null);
    }

    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        bool on = OnLight(e.GetPosition(this));
        if (on == overLight) return;
        overLight = on;
        UpdateHover();
    }

    public void Push(float peakL, float peakR)
    {
        var now = DateTime.UtcNow;
        double dt = (now - last).TotalSeconds;
        last = now;
        bool was = clip;
        double l0 = level[0], l1 = level[1], h0 = hold[0], h1 = hold[1];
        for (int c = 0; c < 2; c++)
        {
            double db = Math.Max(MinDb, Palette.ToDb(c == 0 ? peakL : peakR));
            level[c] = db > level[c] ? db : Math.Max(db, level[c] - 24 * dt);
            if (db >= hold[c])
            {
                hold[c] = db;
                holdAge[c] = 0;
            }
            else if ((holdAge[c] += dt) > 1.5) hold[c] = Math.Max(level[c], hold[c] - 24 * dt);
            if (db > -0.3) clip = true;
        }
        // repaint only when something moved (resting at the floor, the meter stays as it is)
        if (clip != was || Moved(l0, level[0]) || Moved(l1, level[1]) || Moved(h0, hold[0]) || Moved(h1, hold[1])) InvalidateVisual();
    }

    /// <summary>Changes under this many dB don't move the bar a visible amount.</summary>
    const double RedrawDb = 0.1;
    static bool Moved(double a, double b) => Math.Abs(a - b) >= RedrawDb;

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        if (!OnLight(e.GetPosition(this)))
        {
            openRequested?.Invoke();
            return;
        }
        clip = false;
        InvalidateVisual();
        e.Handled = true;
    }

    string? look;
    /// <summary>This meter's look, whatever the window's is (a sample in the settings); null follows <see cref="MeterLooks.Current"/>.</summary>
    public string? Look
    {
        set { look = value; InvalidateVisual(); }
    }

    /// <summary>The LEDs' clip over both bars, kept until the size changes.</summary>
    StreamGeometry? leds;
    Rect ledsFor;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MeterLooks.Changed += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        MeterLooks.Changed -= InvalidateVisual;
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width - 10, h = (BarsH - 2) / 2;
        string lk = look ?? MeterLooks.Current;
        if (lk != MeterLooks.Solid && w > 0 && h > 0) RenderLook(ctx, lk, w, h);
        else RenderSolid(ctx, w, h);
        ctx.DrawRectangle(clip ? Palette.Rec : Palette.Raised, null, new Rect(w + 3, 0, LightW, BarsH), 2, 2);
        if (!showScale) return;
        var tick = Palette.Pen(Palette.Line);
        foreach (int db in ScaleDb)
        {
            double x = Math.Round(w * (db - MinDb) / -MinDb) - 0.5;
            ctx.DrawLine(tick, new Point(x, BarsH + 1), new Point(x, BarsH + 3));
            var t = Palette.Text(db.ToString(), 8, Palette.Faint, Palette.Mono);
            ctx.DrawText(t, new Point(Math.Clamp(x - t.Width / 2, 0, w - t.Width + 1), BarsH + 2.5));
        }
    }

    /// <summary>The bars as they always were (<see cref="MeterLooks.Solid"/>): accent up to −6 dB, amber above.</summary>
    void RenderSolid(DrawingContext ctx, double w, double h)
    {
        for (int c = 0; c < 2; c++)
        {
            var r = new Rect(0, c * (h + 2), w, h);
            ctx.DrawRectangle(Palette.Raised, null, r, 2, 2);
            double k = (level[c] - MinDb) / -MinDb;
            if (k > 0)
            {
                double split = (-6 - MinDb) / -MinDb;
                ctx.DrawRectangle(MeterColors.StereoLow, null, new Rect(0, r.Y, w * Math.Min(k, split), h), 2, 2);
                if (k > split) ctx.FillRectangle(MeterColors.StereoHot, new Rect(w * split, r.Y, w * (k - split), h));
            }
            double hk = (hold[c] - MinDb) / -MinDb;
            if (hk > 0) ctx.FillRectangle(hold[c] > -1 ? Palette.Rec : Palette.Fg, new Rect(w * hk - 1.5, r.Y, 1.5, h));
        }
    }

    /// <summary>
    /// The bars in another look, with the same colors and thresholds: <see cref="MeterLooks.Led"/> lights whole LEDs,
    /// <see cref="MeterLooks.Gradient"/> paints the colors as one gradient over a faint copy of the whole scale, and
    /// <see cref="MeterLooks.Thin"/> lights a narrow line in the middle of each bar, and <see cref="MeterLooks.Tron"/> lights
    /// LEDs of the gradient on black, the unlit ones faint, each bar edged in the accent. The peak hold keeps the bar's height.
    /// </summary>
    void RenderLook(DrawingContext ctx, string lk, double w, double h)
    {
        double split = (-6 - MinDb) / -MinDb;
        bool led = MeterLooks.Segmented(lk), tron = lk == MeterLooks.Tron;
        if (led)
        {
            var area = new Rect(0, 0, w, BarsH);
            if (leds == null || area != ledsFor) { leds = MeterLooks.Segments(area, false); ledsFor = area; }
        }
        for (int c = 0; c < 2; c++)
        {
            var r = new Rect(0, c * (h + 2), w, h);
            double k = Math.Clamp((level[c] - MinDb) / -MinDb, 0, 1), hk = (hold[c] - MinDb) / -MinDb;
            double lit = led ? MeterLooks.SnapLed(w * k) : w * k;
            using (ctx.PushClip(new RoundedRect(r, 2)))
            using (led ? ctx.PushGeometryClip(leds!) : ctx.PushOpacity(1))
            {
                var bar = r;
                if (lk == MeterLooks.Thin)
                {
                    double th = Math.Max(2, Math.Round(h * MeterLooks.ThinShare));
                    bar = new Rect(0, r.Y + Math.Floor((h - th) / 2), w, th);
                }
                ctx.FillRectangle(tron ? Palette.App : Palette.Raised, bar);
                if (lk == MeterLooks.Gradient || tron)
                {
                    var grad = MeterLooks.StereoGradient(split);
                    using (ctx.PushOpacity(tron ? MeterLooks.TronGhost : MeterLooks.GhostOpacity)) ctx.FillRectangle(grad, r);
                    if (lit > 0)
                        using (ctx.PushClip(new Rect(0, r.Y, lit, h)))
                            ctx.FillRectangle(grad, r);
                }
                else if (lit > 0)
                {
                    ctx.FillRectangle(MeterColors.StereoLow, new Rect(0, bar.Y, Math.Min(lit, w * split), bar.Height));
                    if (lit > w * split) ctx.FillRectangle(MeterColors.StereoHot, new Rect(w * split, bar.Y, lit - w * split, bar.Height));
                }
                if (hk > 0)
                {
                    var brush = hold[c] > -1 ? Palette.Rec : Palette.Fg;
                    if (led)
                    {
                        // the hold's whole LED
                        double at = Math.Max(0, MeterLooks.SnapLed(w * hk) - MeterLooks.SegStep);
                        ctx.FillRectangle(brush, new Rect(at, r.Y, MeterLooks.SegStep, h));
                    }
                    else ctx.FillRectangle(brush, new Rect(w * hk - 1.5, r.Y, 1.5, h));
                }
            }
            if (tron) ctx.DrawRectangle(null, Palette.Pen(Palette.Alpha(Palette.AccentColor, MeterLooks.TronEdge)), new RoundedRect(r.Deflate(0.5), 2));
        }
    }
}
