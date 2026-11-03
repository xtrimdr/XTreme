using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace XtremeDaw.App.Controls;

/// <summary>
/// Port of Fader in the web app's Mixer.tsx: a vertical fader with a square-root travel so unity (100%)
/// sits around 80% of the throw, like a console. Drag (Shift = fine), wheel (Shift = fine), double-click = 0 dB.
/// The cap is a console's (asked by the user: big and easy to grab with the mouse): a grey shaded body with grooves and
/// a white line at the level, the one shading and shadow in the window, made once and shared by every fader.
/// <see cref="Slim"/> narrows the cap for the narrowest mixer strips; it stays as tall, so it is as easy to grab.
/// </summary>
public sealed class Fader : Control
{
    /// <summary>The web's VOL_MAX: +3.5 dB at the top.</summary>
    public const double VolMax = 1.5;
    /// <summary>The cap's size (px): wide and tall enough to grab without aiming.</summary>
    const double CapW = 30, SlimCapW = 18, CapH = 40;
    /// <summary>The horizontal volume's cap (px), for <see cref="DrawSideCap"/>.</summary>
    const double SideCapW = 13, SideCapH = 20;
    /// <summary>Space above and below the travel, so the cap never leaves the control.</summary>
    const double Pad = CapH / 2;
    /// <summary>Between the cap's grooves (px).</summary>
    const double Groove = 3;

    static readonly IBrush CapFill = new ImmutableLinearGradientBrush(
    [
        new ImmutableGradientStop(0, Color.Parse("#c9c9d0")), new ImmutableGradientStop(0.45, Color.Parse("#9b9ba3")),
        new ImmutableGradientStop(0.55, Color.Parse("#8d8d95")), new ImmutableGradientStop(1, Color.Parse("#606067")),
    ], startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(0, 1, RelativeUnit.Relative));
    static readonly IBrush CapShadow = new ImmutableSolidColorBrush(Color.FromArgb(110, 0, 0, 0));
    static readonly IPen CapEdge = new ImmutablePen(Color.Parse("#08080a").ToUInt32(), 1);
    static readonly IPen GrooveDark = new ImmutablePen(Color.FromArgb(120, 20, 20, 24).ToUInt32(), 1);
    static readonly IPen GrooveLight = new ImmutablePen(Color.FromArgb(90, 255, 255, 255).ToUInt32(), 1);
    static readonly IPen LevelLine = new ImmutablePen(Colors.White.ToUInt32(), 2);
    /// <summary>Under the level line: white alone doesn't read on the grey body.</summary>
    static readonly IBrush LevelBand = new ImmutableSolidColorBrush(Color.Parse("#141417"));
    /// <summary>The empty slot's outline: strong enough to read on a selected strip or header too.</summary>
    const double SlotEdge = 0.6;

    double value = 1, dragPos, dragY, capW = CapW;
    IPointer? pointer;
    public event Action<double>? Changed;

    public Fader()
    {
        Width = CapW + 4;
        MinHeight = CapH + 24;
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        ToolTip.SetTip(this, "Arrastrá o usá la rueda (Shift = fino) · doble click: 0 dB");
        AutomationProperties.SetName(this, "Volumen");
    }

    /// <summary>The narrow cap, for a strip with little room.</summary>
    public bool Slim
    {
        set
        {
            double w = value ? SlimCapW : CapW;
            if (w == capW) return;
            capW = w;
            Width = capW + 4;
            InvalidateVisual();
        }
    }

    public static double ToPos(double v) => Math.Sqrt(Math.Max(0, v) / VolMax);
    public static double ToVol(double p) => Math.Round(VolMax * Math.Pow(Math.Clamp(p, 0, 1), 2) * 100) / 100;
    // its readout in dB is ValueFormat.Db / DbShort, shared with every other volume in the window

    public double Value
    {
        get => value;
        set { if (this.value == value) return; this.value = value; InvalidateVisual(); }
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height, cx = Math.Round(w / 2), travel = Math.Max(1, h - 2 * Pad);
        double pos = ToPos(value), unity = ToPos(1);
        // the slot: a dark groove, lit in the accent up to the level
        ctx.DrawRectangle(Palette.App, Palette.Pen(Palette.Alpha(Palette.FaintColor, SlotEdge)), new Rect(cx - 2.5, Pad - 4, 5, travel + 8), 2.5, 2.5);
        ctx.DrawRectangle(Palette.Accent, null, new Rect(cx - 1.5, h - Pad - travel * pos, 3, travel * pos), 1.5, 1.5);
        double uy = Math.Round(h - Pad - travel * unity) - 0.5;
        ctx.DrawLine(Palette.Pen(Palette.Dim), new Point(0, uy), new Point(cx - 5, uy));
        ctx.DrawLine(Palette.Pen(Palette.Dim), new Point(cx + 5, uy), new Point(w, uy));
        // the cap: a shadow under it, the shaded body, grooves across and the white line at the level
        double capY = Math.Round(h - Pad - travel * pos);
        var cap = new Rect(cx - capW / 2, capY - CapH / 2, capW, CapH);
        ctx.DrawRectangle(CapShadow, null, cap.Translate(new Vector(0, 2)), 4, 4);
        ctx.DrawRectangle(CapFill, CapEdge, cap.Deflate(0.5), 4, 4);
        for (double gy = cap.Top + 5; gy < cap.Bottom - 5; gy += Groove)
        {
            if (Math.Abs(gy - capY) < 5) continue;
            ctx.DrawLine(GrooveDark, new Point(cap.Left + 4, gy + 0.5), new Point(cap.Right - 4, gy + 0.5));
            ctx.DrawLine(GrooveLight, new Point(cap.Left + 4, gy + 1.5), new Point(cap.Right - 4, gy + 1.5));
        }
        ctx.FillRectangle(LevelBand, new Rect(cap.Left + 1, capY - 3, cap.Width - 2, 6));
        ctx.DrawLine(LevelLine, new Point(cap.Left + 1, capY), new Point(cap.Right - 1, capY));
    }

    /// <summary>The same cap, lying down, for the timeline's horizontal volume: its level line at <paramref name="x"/>.</summary>
    public static void DrawSideCap(DrawingContext ctx, double x, double cy)
    {
        var cap = new Rect(x - SideCapW / 2, cy - SideCapH / 2, SideCapW, SideCapH);
        ctx.DrawRectangle(CapShadow, null, cap.Translate(new Vector(0, 1.5)), 3, 3);
        ctx.DrawRectangle(CapFill, CapEdge, cap.Deflate(0.5), 3, 3);
        ctx.FillRectangle(LevelBand, new Rect(x - 2.5, cap.Top + 1, 5, SideCapH - 2));
        ctx.DrawLine(LevelLine, new Point(x, cap.Top + 1), new Point(x, cap.Bottom - 1));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount == 2) { Changed?.Invoke(1); return; }
        pointer = e.Pointer;
        pointer.Capture(this);
        dragY = e.GetPosition(this).Y;
        dragPos = ToPos(value);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (pointer == null) return;
        double scale = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.25 : 1;
        Changed?.Invoke(ToVol(dragPos + (dragY - e.GetPosition(this).Y) / Math.Max(1, Bounds.Height) * scale));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) { pointer?.Capture(null); pointer = null; e.Handled = true; }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) => pointer = null;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        Changed?.Invoke(ToVol(ToPos(value) + (e.Delta.Y > 0 ? 1 : -1) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.005 : 0.02)));
        e.Handled = true;
    }
}
