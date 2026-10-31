using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace WildTrack.App.Controls;

/// <summary>
/// A value as a bar (asked by the user, in the style of a hardware pad): a box that fills with the number on top, from
/// the left or, when zero sits inside its range, from the middle. A click or a drag sets it where the mouse is, the wheel
/// nudges it and the right button puts it back at <see cref="Default"/>. It never takes the focus: the keys have to keep
/// playing.
/// </summary>
public sealed class ValueBar : Control
{
    const double Radius = 6, Inset = 2;

    readonly int min, max;
    /// <summary>What it shows for each value, made once, so a redraw allocates nothing.</summary>
    readonly string[] texts;
    bool dragging;

    public ValueBar(int min, int max, int value, Func<int, string> text)
    {
        this.min = min;
        this.max = max;
        Default = Value = value;
        texts = [.. Enumerable.Range(min, max - min + 1).Select(text)];
        Focusable = false;
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
    }

    /// <summary>The user moved it.</summary>
    public event Action<int>? Moved;

    /// <summary>Where the right button puts it.</summary>
    public int Default { get; }

    /// <summary>What a notch of the wheel moves.</summary>
    public int WheelStep { get; init; } = 1;

    public int Value
    {
        get;
        set
        {
            field = Math.Clamp(value, min, max);
            InvalidateVisual();
        }
    }

    double Across(int value) => (value - min) / (double)(max - min);

    public override void Render(Avalonia.Media.DrawingContext ctx)
    {
        if (Bounds.Width <= 2 * Inset + 2 || Bounds.Height <= 2 * Inset) return; // not laid out yet
        var box = new Rect(Bounds.Size);
        ctx.DrawRectangle(Palette.Raised, Palette.Pen(Palette.Alpha(Palette.AccentColor, 0.45)), box.Deflate(0.5), Radius, Radius);
        double inner = Bounds.Width - 2 * Inset, h = Bounds.Height - 2 * Inset;
        double from = Inset + inner * (min < 0 ? Across(0) : 0), to = Inset + inner * Across(Value);
        if (min >= 0) to = Math.Max(to, from + 2);
        double a = Math.Min(from, to), b = Math.Max(from, to);
        ctx.FillRectangle(Palette.Alpha(Palette.AccentColor, 0.32), new Rect(a, Inset, b - a, h), (float)(Radius - Inset));
        ctx.FillRectangle(Palette.Accent, new Rect(Math.Clamp(to - 1, Inset, Inset + inner - 2), Inset, 2, h));
        var text = Palette.Text(texts[Value - min], 12, Palette.Fg, Palette.Mono);
        ctx.DrawText(text, new Point((Bounds.Width - text.Width) / 2, (Bounds.Height - text.Height) / 2));
    }

    void Set(int value)
    {
        int before = Value;
        Value = value;
        if (Value != before) Moved?.Invoke(Value);
    }

    void FollowMouse(double x) => Set((int)Math.Round(min + (x - Inset) / (Bounds.Width - 2 * Inset) * (max - min)));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        e.Handled = true;
        if (point.Properties.IsRightButtonPressed)
        {
            Set(Default);
            return;
        }
        dragging = true;
        e.Pointer.Capture(this);
        FollowMouse(point.Position.X);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (dragging) FollowMouse(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        dragging = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Set(Value + Math.Sign(e.Delta.Y) * WheelStep);
        e.Handled = true;
    }
}
