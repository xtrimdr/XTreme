using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The tonal balance's picture (#210): four meters, one per band (Graves, Medios graves, Medios agudos, Agudos), each with
/// the target's range as a box and the mix as a mark inside or out of it, and the low end's punch beside them; in the full
/// view (<see cref="Fine"/>) the long-term spectrum over them, a line against the target's band. It reads the running slot
/// (<see cref="Live"/>) every <see cref="TickMs"/>, which also runs the analysis (<see cref="TonalAnalyzer.Pull"/>), and
/// redraws only when a reading came. Two layers: this control draws what changes with the target or the size (grid,
/// target band, boxes, labels), <see cref="Layer"/> the reading. Nothing in a render allocates: short lines, 1 px columns
/// and cached pens; the punch's text is rebuilt only when its tenth changes. A mark out of its range is in the
/// <see cref="Palette.Mute"/> color ("lo que se pasa de algo").
/// </summary>
public sealed class TonalView : Control
{
    const int TickMs = 50;
    /// <summary>The spectrum's dB axis (a levelled curve: 0 dB is its midrange).</summary>
    const double SpecTop = 15, SpecBottom = -33;
    /// <summary>A band meter shows this much above and under the target's center (dB).</summary>
    const double BandSpan = 12;
    /// <summary>The punch meter's scale: crest factor, dB.</summary>
    const double CrestLo = 0, CrestHi = 20;
    /// <summary>The meters' row: its height, the labels over it and a meter's width.</summary>
    const double RowH = 92, LabelH = 16, MeterW = 16, Gap = 8, Pad = 6;
    const int Cols = TonalScale.Bands + 1;
    static readonly double[] GridHz = [50, 100, 200, 500, 1000, 2000, 5000, 10000];
    static readonly (double Hz, string Text)[] GridText = [(100, "100"), (1000, "1k"), (10000, "10k")];

    static readonly IPen GridPen = Palette.Pen(Palette.Alpha(Colors.White, 0.06));
    static readonly IPen AxisPen = Palette.Pen(Palette.Alpha(Colors.White, 0.14));
    static readonly IBrush Back = Palette.Alpha(Colors.Black, 0.25);
    static readonly IBrush Well = Palette.Alpha(Colors.White, 0.04);

    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(TickMs) };
    readonly Layer layer;
    readonly string[] labels;
    readonly string noSignal;
    readonly double[] curve = new double[TonalScale.Points];
    readonly double[] bands = new double[TonalScale.Bands], tMean = new double[TonalScale.Bands], tMin = new double[TonalScale.Bands], tMax = new double[TonalScale.Bands];
    bool has;
    double crest = double.NaN;
    long version = -1;
    TonalAnalyzer? meter;
    TonalTarget? target;
    TonalMode mode;
    int crestTenths = int.MinValue;
    string crestText = "";

    /// <summary>The running slot; null while it isn't in the engine.</summary>
    public Func<TonalInsert?>? Live { get; init; }
    /// <summary>The full view: the spectrum over the meters.</summary>
    public bool Fine { get; }
    public Color Tint { get; set; }
    /// <summary>How long the reading looks back (the slot's Respuesta), handed to the analysis on every tick.</summary>
    public double TimeSec { get; set; } = TonalAnalyzer.DefaultTimeSec;

    public TonalView(bool fine, double height)
    {
        Fine = fine;
        Height = fine ? Math.Max(height, RowH + 120) : RowH;
        ClipToBounds = true;
        labels = [Loc.T("tonal.band.low"), Loc.T("tonal.band.lowmid"), Loc.T("tonal.band.highmid"), Loc.T("tonal.band.high"), Loc.T("tonal.punch")];
        for (int i = 0; i < labels.Length; i++) labels[i] = labels[i].ToUpperInvariant();
        noSignal = Loc.T("tonal.nosignal");
        layer = new Layer(this) { IsHitTestVisible = false };
        VisualChildren.Add(layer);
        timer.Tick += (_, _) => Tick();
    }

    /// <summary>What the reading is compared with and which mode it shows; the panel sets it on every edit.</summary>
    public void Show(TonalTarget? t, TonalMode m)
    {
        bool modeChanged = m != mode;
        target = t;
        mode = m;
        if (t != null) t.Bands(tMean, tMin, tMax);
        if (modeChanged) version = -1;
        Take();
        InvalidateVisual();
    }

    /// <summary>Starts the reading over.</summary>
    public void Reset()
    {
        Live?.Invoke()?.Meter.Reset();
        version = -1;
        Take();
        layer.InvalidateVisual();
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

    void Tick()
    {
        if (Take()) layer.InvalidateVisual();
    }

    /// <summary>Runs the analysis on what came and reads it; true when the picture changed.</summary>
    bool Take()
    {
        var insert = Live?.Invoke();
        if (insert == null)
        {
            bool was = has || meter != null;
            has = false;
            crest = double.NaN;
            meter = null;
            return was;
        }
        var m = insert.Meter;
        m.TimeSec = TimeSec;
        m.Pull(insert.Tap);
        if (m == meter && m.Version == version) return false;
        meter = m;
        version = m.Version;
        has = m.Read(mode, curve);
        if (has) TonalScale.BandValues(curve, bands);
        crest = m.CrestDb(mode);
        int tenths = double.IsNaN(crest) ? int.MinValue : (int)Math.Round(crest * 10);
        if (tenths != crestTenths)
        {
            crestTenths = tenths;
            crestText = tenths == int.MinValue ? "—" : $"{tenths / 10.0:0.0} dB";
        }
        return true;
    }

    IBrush Ink(double a) => Palette.Alpha(Tint, a);

    Rect SpecBox()
    {
        var r = new Rect(Bounds.Size).Deflate(Pad);
        return new Rect(r.X, r.Y, r.Width, Math.Max(0, r.Height - RowH));
    }

    Rect RowBox()
    {
        var r = new Rect(Bounds.Size).Deflate(Pad);
        double h = Math.Min(r.Height, RowH - Pad);
        return new Rect(r.X, r.Bottom - h, r.Width, h);
    }

    static double SpecX(Rect r, double hz) => r.Left + Math.Log(hz / TonalScale.LowHz) / Math.Log(TonalScale.Freq(TonalScale.Points - 1) / TonalScale.LowHz) * r.Width;
    static double SpecY(Rect r, double db) => r.Top + Math.Clamp((SpecTop - db) / (SpecTop - SpecBottom), 0, 1) * r.Height;

    /// <summary>A target's value between its points, at a fractional point index.</summary>
    static double At(double[] v, double p)
    {
        int i = Math.Clamp((int)Math.Floor(p), 0, v.Length - 2);
        double t = Math.Clamp(p - i, 0, 1);
        return v[i] + t * (v[i + 1] - v[i]);
    }

    /// <summary>The meter of column <paramref name="c"/>: its box (the well) in the row.</summary>
    Rect Meter(Rect row, int c)
    {
        double w = row.Width / Cols;
        double x = row.Left + w * c + (w - MeterW) / 2;
        return new Rect(x, row.Top + LabelH, MeterW, Math.Max(0, row.Height - LabelH - 14));
    }

    static double BandY(Rect m, double center, double db) => m.Top + Math.Clamp((BandSpan - (db - center)) / (2 * BandSpan), 0, 1) * m.Height;
    static double CrestY(Rect m, double db) => m.Top + Math.Clamp((CrestHi - db) / (CrestHi - CrestLo), 0, 1) * m.Height;

    public override void Render(DrawingContext ctx)
    {
        var box = new Rect(Bounds.Size);
        ctx.DrawRectangle(Back, Palette.Pen(Palette.Alpha(Tint, 0.22)), box.Deflate(0.5), 6, 6);
        if (box.Width < 60) return;
        var t = target;
        if (Fine)
        {
            var r = SpecBox();
            if (r.Height > 20)
            {
                foreach (double hz in GridHz)
                {
                    double x = Math.Round(SpecX(r, hz)) + 0.5;
                    ctx.DrawLine(GridPen, new Point(x, r.Top), new Point(x, r.Bottom));
                }
                for (int b = 1; b < TonalScale.Bands; b++)
                {
                    double x = Math.Round(SpecX(r, TonalScale.BandEdges[b])) + 0.5;
                    ctx.DrawLine(AxisPen, new Point(x, r.Top), new Point(x, r.Bottom));
                }
                double zero = Math.Round(SpecY(r, 0)) + 0.5;
                ctx.DrawLine(AxisPen, new Point(r.Left, zero), new Point(r.Right, zero));
                foreach (var (hz, text) in GridText)
                    ctx.DrawText(Palette.Text(text, 9, Palette.Faint), new Point(SpecX(r, hz) + 3, r.Bottom - 12));
                if (t != null)
                {
                    var fill = Ink(0.16);
                    int cols = (int)r.Width;
                    double top = Math.Log(TonalScale.Freq(TonalScale.Points - 1) / TonalScale.LowHz);
                    for (int i = 0; i < cols; i++)
                    {
                        double p = i / (double)cols * top / Math.Log(2) * TonalScale.PerOctave;
                        double y0 = SpecY(r, At(t.Max, p)), y1 = SpecY(r, At(t.Min, p));
                        double x = r.Left + i + 0.5;
                        ctx.DrawLine(Palette.Pen(fill), new Point(x, y0), new Point(x, y1));
                    }
                    var mean = Palette.Pen(Ink(0.55), 1);
                    for (int i = 1; i < TonalScale.Points; i++)
                        ctx.DrawLine(mean, new Point(SpecX(r, TonalScale.Freq(i - 1)), SpecY(r, t.Mean[i - 1])), new Point(SpecX(r, TonalScale.Freq(i)), SpecY(r, t.Mean[i])));
                }
            }
        }
        var row = RowBox();
        for (int c = 0; c < Cols; c++)
        {
            var m = Meter(row, c);
            double w = row.Width / Cols;
            var label = Palette.Text(labels[c], 9, Palette.Faint);
            ctx.DrawText(label, new Point(row.Left + w * c + (w - label.Width) / 2, row.Top));
            ctx.DrawRectangle(Well, null, m, 2, 2);
            if (t == null || m.Height < 4) continue;
            double y0, y1;
            if (c < TonalScale.Bands)
            {
                y0 = BandY(m, tMean[c], tMax[c]);
                y1 = BandY(m, tMean[c], tMin[c]);
            }
            else
            {
                y0 = CrestY(m, t.CrestMax);
                y1 = CrestY(m, t.CrestMin);
            }
            ctx.DrawRectangle(Ink(0.22), Palette.Pen(Ink(0.6)), new Rect(m.Left, y0, m.Width, Math.Max(1, y1 - y0)), 2, 2);
        }
    }

    /// <summary>The reading: the spectrum's line, each meter's mark and the punch's value.</summary>
    void RenderReading(DrawingContext ctx)
    {
        var t = target;
        var row = RowBox();
        if (!has && double.IsNaN(crest))
        {
            var text = Palette.Text(noSignal, 11, Palette.Faint);
            var at = Fine ? SpecBox().Center : row.Center;
            ctx.DrawText(text, new Point(at.X - text.Width / 2, at.Y - text.Height / 2));
        }
        var inPen = Palette.Pen(Palette.Fg, 2, PenLineCap.Round);
        var outPen = Palette.Pen(Palette.Mute, 2, PenLineCap.Round);
        if (Fine && has)
        {
            var r = SpecBox();
            var line = Palette.Pen(Palette.Fg, 1.5, PenLineCap.Round);
            var off = Palette.Pen(Palette.Mute, 1.5, PenLineCap.Round);
            for (int i = 1; i < TonalScale.Points; i++)
            {
                bool outside = t != null && (curve[i] > t.Max[i] || curve[i] < t.Min[i]);
                ctx.DrawLine(outside ? off : line, new Point(SpecX(r, TonalScale.Freq(i - 1)), SpecY(r, curve[i - 1])), new Point(SpecX(r, TonalScale.Freq(i)), SpecY(r, curve[i])));
            }
        }
        for (int c = 0; c < Cols; c++)
        {
            var m = Meter(row, c);
            if (m.Height < 4) continue;
            double y;
            bool outside;
            if (c < TonalScale.Bands)
            {
                if (!has) continue;
                double center = t != null ? tMean[c] : 0;
                y = BandY(m, center, bands[c]);
                outside = t != null && (bands[c] > tMax[c] || bands[c] < tMin[c]);
            }
            else
            {
                if (double.IsNaN(crest)) continue;
                y = CrestY(m, crest);
                outside = t != null && (crest > t.CrestMax || crest < t.CrestMin);
            }
            ctx.DrawLine(outside ? outPen : inPen, new Point(m.Left - 4, y), new Point(m.Right + 4, y));
        }
        var value = Palette.Text(crestText, 10, Palette.Dim, Palette.Mono);
        var last = Meter(row, Cols - 1);
        ctx.DrawText(value, new Point(last.Center.X - value.Width / 2, last.Bottom + 1));
    }

    /// <summary>The reading's layer: it redraws on every new reading, the control under it only with the target or the size.</summary>
    sealed class Layer(TonalView owner) : Control
    {
        public override void Render(DrawingContext ctx) => owner.RenderReading(ctx);
    }
}
