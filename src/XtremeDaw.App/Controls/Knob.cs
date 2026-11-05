using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace XtremeDaw.App.Controls;

/// <summary>
/// Port of Knob in the web app's ui.tsx (the mixer's knobs): a 270° arc lit from the reset value to the
/// current one, drag up/down (relative, Shift = fine), wheel (50 steps over the range, Shift = fine) and double-click
/// to reset.
/// The cap takes the look chosen in Configuración → Apariencia (<see cref="KnobLooks"/>), or a fixed
/// <see cref="Look"/> (the picker's samples).
/// Avalonia only hit-tests what is painted (the web's svg takes its whole box), so the knob paints a transparent
/// square <see cref="HitPad"/> px bigger than itself to catch the pointer anywhere around it, and its track lights up
/// while hovered or dragged.
/// No tooltip: it would cover the knob while it is dragged; what it is goes on the label beside it.
/// </summary>
public sealed class Knob : Control
{
    /// <summary>Extra px around the knob that still grab it: small knobs are hard to hit with the guitar on.</summary>
    const double HitPad = 6;
    double value, dragValue, dragY;
    IPointer? pointer;

    public double Min { get; init; } = -1;
    public double Max { get; init; } = 1;
    /// <summary>Where a double-click takes it and its lit arc starts; settable for a knob that serves several things (an EQ's selected band).</summary>
    public double ResetTo
    {
        get => resetTo;
        set { if (resetTo == value) return; resetTo = value; InvalidateVisual(); }
    }
    double resetTo;
    /// <summary>Where its lit arc starts if not at <see cref="ResetTo"/>: a threshold lights from the top down, the more it acts (#187).</summary>
    public double? ArcFrom { get; init; }
    public double Size { get; private set; }
    public event Action<double>? Changed;

    /// <summary>A look of its own instead of the chosen one (null = the chosen one).</summary>
    public string? Look { get; init; }

    public Knob(double size = 22, string? name = null)
    {
        Size = size;
        Width = Height = size;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        AccessibleName = name;
    }

    /// <summary>Follows the room it has (the effect panels scale with their width).</summary>
    public void Resize(double size)
    {
        if (size == Size) return;
        Size = size;
        Width = Height = size;
        InvalidateVisual();
    }

    /// <summary>What a screen reader calls it.</summary>
    public string? AccessibleName
    {
        set => AutomationProperties.SetName(this, value);
    }

    public double Value
    {
        get => value;
        set { if (this.value == value) return; this.value = value; InvalidateVisual(); }
    }

    bool Big => Size >= 32;
    double Round(double v) => Math.Round(Math.Clamp(v, Min, Max) * 1000) / 1000;

    public override void Render(DrawingContext ctx)
    {
        double angle = -135 + (value - Min) / (Max - Min) * 270;
        double origin = -135 + ((ArcFrom ?? ResetTo) - Min) / (Max - Min) * 270;
        ctx.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size).Inflate(HitPad));
        KnobLooks.Tint = Tint;
        try { KnobLooks.Draw(Look ?? KnobLooks.Current, ctx, new Point(Size / 2, Size / 2), Size, angle, origin, true, IsPointerOver || pointer != null, false); }
        finally { KnobLooks.Tint = null; }
    }

    /// <summary>Its lit arc in this color instead of the accent (an effect's family, #187); the look stays the chosen one.</summary>
    public Color? Tint { get; set; }

    /// <summary>
    /// Draws a knob of diameter <paramref name="d"/> in the chosen look (every knob in the window, this control's and
    /// the timeline's): the scale lit from <paramref name="origin"/> to <paramref name="angle"/> (degrees, 0 = up), the
    /// cap and a pointer. <paramref name="label"/> keeps the middle free for a value drawn over it. Allocates nothing:
    /// the timeline redraws it on every pointer move.
    /// </summary>
    public static void Draw(DrawingContext ctx, Point c, double d, double angle, double origin, bool on, bool hot, bool label = false) =>
        KnobLooks.Draw(KnobLooks.Current, ctx, c, d, angle, origin, on, hot, label);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        KnobLooks.Changed += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        KnobLooks.Changed -= InvalidateVisual;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount == 2) { Changed?.Invoke(ResetTo); return; }
        Focus();
        pointer = e.Pointer;
        pointer.Capture(this);
        dragY = e.GetPosition(this).Y;
        dragValue = value;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (pointer == null) return;
        double y = e.GetPosition(this).Y;
        // relative steps, so pressing Shift mid-drag doesn't jump
        double next = dragValue + (dragY - y) / (Big ? 160 : 120) * (Max - Min) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.2 : 1);
        dragY = y;
        dragValue = Math.Clamp(next, Min, Max);
        Changed?.Invoke(Round(next));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) { pointer?.Capture(null); pointer = null; e.Handled = true; InvalidateVisual(); }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { pointer = null; InvalidateVisual(); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsPointerOverProperty) InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        double step = (Max - Min) / 50 * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.2 : 1) * (e.Delta.Y > 0 ? 1 : -1);
        Changed?.Invoke(Round(value + step));
        e.Handled = true;
    }
}
