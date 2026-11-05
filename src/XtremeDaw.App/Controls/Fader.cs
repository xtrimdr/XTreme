using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace XtremeDaw.App.Controls;

/// <summary>
/// Port of Fader in the web app's Mixer.tsx: a vertical fader with a square-root travel so unity (100%)
/// sits around 80% of the throw, like a console. Drag (Shift = fine), wheel (Shift = fine), double-click = 0 dB.
/// The cap is big and easy to grab with the mouse (asked by the user); how the rail and the cap look is chosen in
/// Configuración → Apariencia (<see cref="FaderLooks"/>, the console's by default), or fixed with <see cref="Look"/>
/// for the picker's samples. The look only paints: every cap fits in the same box, so the layout never changes.
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

    double value = 1, dragPos, dragY, capW = CapW;
    IPointer? pointer;
    public event Action<double>? Changed;

    public Fader()
    {
        Width = CapW + 4;
        MinHeight = CapH + 24;
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        AutomationProperties.SetName(this, Loc.T("fader.name"));
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

    /// <summary>A fixed look (the picker's samples); null = the one chosen in Configuración.</summary>
    public string? Look { get; init; }

    public override void Render(DrawingContext ctx) =>
        FaderLooks.Draw(Look ?? FaderLooks.Current, ctx, Bounds.Width, Bounds.Height, Pad, capW, CapH, ToPos(value), ToPos(1));

    /// <summary>The same cap, lying down, for the timeline's horizontal volume: its level line at <paramref name="x"/>.</summary>
    public static void DrawSideCap(DrawingContext ctx, double x, double cy) =>
        FaderLooks.DrawSide(FaderLooks.Current, ctx, x, cy, SideCapW, SideCapH);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        FaderLooks.Changed += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        FaderLooks.Changed -= InvalidateVisual;
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
