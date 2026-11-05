using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The custom color of the track color picker (#145, asked by the user): a saturation/brightness square over a hue
/// strip, drawn by hand (Avalonia's ColorPicker is a separate package). Dragging either one raises
/// <see cref="Changed"/> with the color under the pointer, so the track follows live; <see cref="Committed"/> comes
/// when the button is let go. The square's brush is rebuilt only when the hue moves.
/// </summary>
public sealed class ColorField : Control
{
    /// <summary>Height of the hue strip and the gap above it.</summary>
    const double HueH = 12, Gap = 8;
    /// <summary>Radius of the marker on the square.</summary>
    const double Marker = 5;

    static readonly IBrush Whites = new ImmutableLinearGradientBrush(
        [new ImmutableGradientStop(0, Colors.White), new ImmutableGradientStop(1, Color.FromArgb(0, 255, 255, 255))],
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(1, 0, RelativeUnit.Relative));
    static readonly IBrush Blacks = new ImmutableLinearGradientBrush(
        [new ImmutableGradientStop(0, Color.FromArgb(0, 0, 0, 0)), new ImmutableGradientStop(1, Colors.Black)],
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(0, 1, RelativeUnit.Relative));
    static readonly IBrush Hues = new ImmutableLinearGradientBrush(
        [.. Enumerable.Range(0, 7).Select(i => new ImmutableGradientStop(i / 6.0, new HsvColor(1, i * 60 % 360, 1, 1).ToRgb()))],
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(1, 0, RelativeUnit.Relative));

    double hue, sat = 0.5, val = 0.8;
    IBrush hueBrush = Brushes.Red;
    /// <summary>What the pointer is dragging: 1 the square, 2 the hue strip, 0 nothing.</summary>
    int dragging;

    public event Action<Color>? Changed;
    public event Action<Color>? Committed;

    public Color Color => new HsvColor(1, hue, sat, val).ToRgb();

    /// <summary>Shows <paramref name="c"/> without raising anything. A grey keeps the hue it had.</summary>
    public void Set(Color c)
    {
        var hsv = c.ToHsv();
        if (hsv.S > 0.001 && hsv.V > 0.001) SetHue(hsv.H);
        sat = hsv.S; val = hsv.V;
        InvalidateVisual();
    }

    void SetHue(double h)
    {
        h = Math.Clamp(h, 0, 359.999);
        if (h == hue) return; // hue 0 starts with its brush, red
        hue = h;
        hueBrush = new ImmutableSolidColorBrush(new HsvColor(1, hue, 1, 1).ToRgb());
    }

    Rect Square => new(0, 0, Bounds.Width, Math.Max(0, Bounds.Height - HueH - Gap));
    Rect Strip => new(0, Bounds.Height - HueH, Bounds.Width, HueH);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        dragging = p.Y >= Square.Bottom + Gap / 2 ? 2 : 1;
        e.Pointer.Capture(this);
        Drag(p);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (dragging != 0) Drag(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (dragging == 0) return;
        dragging = 0;
        e.Pointer.Capture(null);
        Committed?.Invoke(Color);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (dragging != 0) { dragging = 0; Committed?.Invoke(Color); }
    }

    void Drag(Point p)
    {
        var w = Math.Max(1, Bounds.Width);
        if (dragging == 2) SetHue(Math.Clamp(p.X / w, 0, 1) * 360);
        else
        {
            var sq = Square;
            sat = Math.Clamp(p.X / w, 0, 1);
            val = 1 - Math.Clamp(p.Y / Math.Max(1, sq.Height), 0, 1);
        }
        InvalidateVisual();
        Changed?.Invoke(Color);
    }

    public override void Render(DrawingContext ctx)
    {
        var sq = Square;
        if (sq.Width <= 0 || sq.Height <= 0) return;
        using (ctx.PushClip(new RoundedRect(sq, 5)))
        {
            ctx.FillRectangle(hueBrush, sq);
            ctx.FillRectangle(Whites, sq);
            ctx.FillRectangle(Blacks, sq);
        }
        var at = new Point(sq.X + sat * sq.Width, sq.Y + (1 - val) * sq.Height);
        ctx.DrawEllipse(null, Palette.Pen(Brushes.Black, 3), at, Marker, Marker);
        ctx.DrawEllipse(null, Palette.Pen(Brushes.White, 1.5), at, Marker, Marker);

        var st = Strip;
        ctx.DrawRectangle(Hues, null, new RoundedRect(st, 4));
        double x = Math.Round(st.X + hue / 360 * st.Width);
        var knob = new Rect(Math.Clamp(x - 2, st.X, st.Right - 4), st.Y - 2, 4, st.Height + 4);
        ctx.DrawRectangle(Brushes.White, Palette.Pen(Brushes.Black, 1), new RoundedRect(knob, 2));
    }
}
