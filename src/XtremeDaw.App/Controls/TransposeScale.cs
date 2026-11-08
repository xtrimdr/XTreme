using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The track's Transponer in the inspector (asked by the user, #316: something worthy of XTreme that shows exactly how
/// far it is moved): the name and the interval it makes ("5ª justa arriba") on the left, the semitones in large type on
/// the right, and under them a ruler from <see cref="Min"/> to <see cref="Max"/> with a tick per semitone, the octaves
/// marked and shaded, and a bar lit from 0 to the value. Rendering a transpose takes seconds, so a gesture only shows
/// where it goes (the old value stays as a ghost mark) and <see cref="Changed"/> fires once: when the drag ends, the
/// wheel or the arrows rest. Drag or click on the ruler, wheel (Shift: an octave), arrows (Page: an octave), double
/// click or Alt+click: back to 0. Draws from cached texts and pens only (nothing allocated per frame).
/// </summary>
public sealed class TransposeScale : Control
{
    public const int Min = -24, Max = 24;
    /// <summary>The ruler's side margins: the thumb and the end labels fit inside the control.</summary>
    const double Side = 12;
    /// <summary>Where the ruler's line sits, from the top, and its thickness.</summary>
    const double RulerY = 52, RulerH = 4;
    /// <summary>The thumb's radius, and its halo's while hovered or dragged.</summary>
    const double ThumbR = 7, HaloR = 12;
    /// <summary>How long the wheel or the arrows rest before the value is set.</summary>
    static readonly TimeSpan Rest = TimeSpan.FromMilliseconds(400);

    /// <summary>"+7", "-12", "0": made once, so painting allocates nothing.</summary>
    static readonly string[] Tags = [.. Enumerable.Range(Min, Max - Min + 1).Select(v => v.ToString("+0;-0;0"))];

    readonly string label;
    /// <summary>The interval of every value, from <see cref="Min"/> to <see cref="Max"/>.</summary>
    readonly string[] intervals;
    readonly DispatcherTimer rest = new() { Interval = Rest };
    int value;
    /// <summary>Where a gesture is taking it, before it is set; null when nothing is pending.</summary>
    int? pending;
    bool mixed, hover, dragging;
    string mixedText = "";

    /// <summary>The value the user chose (once per gesture).</summary>
    public event Action<int>? Changed;

    public TransposeScale(string label, Func<int, string> interval)
    {
        this.label = label;
        intervals = [.. Enumerable.Range(Min, Max - Min + 1).Select(interval)];
        Height = 76;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        AutomationProperties.SetName(this, label);
        rest.Tick += (_, _) => Commit();
    }

    /// <summary>What the model says; a value waiting to be set keeps showing. <paramref name="mixedText"/>: the
    /// tracks edited together don't share a value (the number dims and says so).</summary>
    public void Set(double v, string? mixedText = null)
    {
        if (pending != null) return;
        int n = Math.Clamp((int)Math.Round(v), Min, Max);
        bool m = mixedText != null;
        if (n == value && m == mixed) return;
        value = n;
        mixed = m;
        this.mixedText = mixedText ?? "";
        InvalidateVisual();
    }

    int Shown => pending ?? value;

    double X(double v) => Side + (v - Min) / (Max - Min) * Math.Max(1, Bounds.Width - 2 * Side);
    int ValueAt(double x) => Math.Clamp((int)Math.Round(Min + (x - Side) / Math.Max(1, Bounds.Width - 2 * Side) * (Max - Min)), Min, Max);

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width;
        int v = Shown;
        bool lit = v != 0 && !(mixed && pending == null);
        var accent = Palette.Accent;

        // the background catches the pointer anywhere
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        // name and interval on the left
        ctx.DrawText(Palette.Text(label, 12, Palette.Dim), new Point(0, 2));
        var desc = mixed && pending == null ? mixedText : intervals[v - Min];
        ctx.DrawText(Palette.Text(desc, 11, lit ? Palette.Fg : Palette.Faint), new Point(0, 21));

        // the semitones, large, on the right: "+7 st"
        var unit = Palette.Text("st", 11, Palette.Dim, Palette.Mono);
        var number = mixed && pending == null
            ? Palette.Text("—", 19, Palette.Faint, Palette.MonoBold)
            : Palette.Text(Tags[v - Min], 19, lit ? accent : Palette.Faint, Palette.MonoBold);
        double right = w;
        ctx.DrawText(unit, new Point(right - unit.WidthIncludingTrailingWhitespace, 30 - unit.Height));
        ctx.DrawText(number, new Point(right - unit.WidthIncludingTrailingWhitespace - 4 - number.WidthIncludingTrailingWhitespace, 33 - number.Height));

        // the second octave on each side, shaded
        double top = RulerY - 8, bottom = RulerY + RulerH + 8;
        for (int o = Min; o < Max; o += 12)
            if (o < -12 || o >= 12)
                ctx.FillRectangle(Palette.Alpha(Palette.FgColor, 0.035), new Rect(X(o), top, X(o + 12) - X(o), bottom - top));

        // the line, and the bar lit from 0 to the value
        double y = RulerY;
        ctx.DrawRectangle(Palette.Hover, null, new RoundedRect(new Rect(X(Min), y, X(Max) - X(Min), RulerH), RulerH / 2));
        if (lit)
        {
            double a = X(Math.Min(0, v)), b = X(Math.Max(0, v));
            ctx.DrawRectangle(Palette.Alpha(Palette.AccentColor, 0.25), null, new RoundedRect(new Rect(a, y - 2, b - a, RulerH + 4), (RulerH + 4) / 2));
            ctx.DrawRectangle(accent, null, new RoundedRect(new Rect(a, y, b - a, RulerH), RulerH / 2));
        }

        // a tick per semitone; half octaves longer, octaves longest and labelled
        double tickY = y + RulerH + 3;
        for (int s = Min; s <= Max; s++)
        {
            bool octave = s % 12 == 0, half = s % 6 == 0;
            double len = octave ? 7 : half ? 4.5 : 2.5;
            bool inside = lit && (v > 0 ? s > 0 && s <= v : s < 0 && s >= v);
            var brush = inside ? accent : octave ? Palette.Dim : Palette.Faint;
            double x = Math.Round(X(s)) + 0.5;
            ctx.DrawLine(Palette.Pen(brush, octave ? 1.5 : 1), new Point(x, tickY), new Point(x, tickY + len));
            if (!octave) continue;
            var t = Palette.Text(Tags[s - Min], 10.5, s == v && lit ? accent : Palette.Dim, Palette.Mono);
            // before the first layout the control can be narrower than a label: Clamp would throw with max < min
            double tx = Math.Max(0, Math.Min(x - t.WidthIncludingTrailingWhitespace / 2, w - t.WidthIncludingTrailingWhitespace));
            ctx.DrawText(t, new Point(tx, tickY + 8));
        }

        // the value it had while a gesture moves it
        if (pending != null && pending != value)
        {
            double gx = X(value);
            ctx.DrawEllipse(null, Palette.Pen(Palette.Dim, 1), new Point(gx, y + RulerH / 2), ThumbR - 2, ThumbR - 2);
        }

        // the thumb
        if (!(mixed && pending == null))
        {
            var c = new Point(X(v), y + RulerH / 2);
            if (hover || dragging || IsFocused) ctx.DrawEllipse(Palette.Alpha(Palette.AccentColor, dragging ? 0.3 : 0.18), null, c, HaloR, HaloR);
            ctx.DrawEllipse(lit ? accent : Palette.Raised, Palette.Pen(lit ? Palette.AccentFg : Palette.Dim, 1.5), c, ThumbR, ThumbR);
        }
    }

    /// <summary>Shows where a gesture is taking it; <see cref="Commit"/> sets it.</summary>
    void Move(int v)
    {
        v = Math.Clamp(v, Min, Max);
        if (v == Shown && pending != null) return;
        pending = v;
        InvalidateVisual();
    }

    void Commit()
    {
        rest.Stop();
        if (pending is not { } v) return;
        pending = null;
        bool changed = v != value || mixed;
        value = v;
        InvalidateVisual();
        if (changed) Changed?.Invoke(v);
    }

    /// <summary>Back to 0, set at once.</summary>
    void Reset()
    {
        pending = 0;
        Commit();
    }

    /// <summary>A step from the keyboard or the wheel: set when they rest.</summary>
    void Step(int by)
    {
        Move(Shown + by);
        rest.Stop();
        rest.Start();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetCurrentPoint(this);
        if (!p.Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        Focus();
        if ((e.KeyModifiers & KeyModifiers.Alt) != 0 || e.ClickCount == 2)
        {
            dragging = false;
            e.Pointer.Capture(null);
            Reset();
            return;
        }
        dragging = true;
        e.Pointer.Capture(this);
        Move(ValueAt(p.Position.X));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (dragging) Move(ValueAt(e.GetPosition(this).X));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!dragging) return;
        dragging = false;
        e.Pointer.Capture(null);
        Commit();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!dragging) return;
        dragging = false;
        Commit();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0) return;
        e.Handled = true;
        Step(Math.Sign(e.Delta.Y) * ((e.KeyModifiers & KeyModifiers.Shift) != 0 ? 12 : 1));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int by = e.Key switch
        {
            Key.Right or Key.Up => 1,
            Key.Left or Key.Down => -1,
            Key.PageUp => 12,
            Key.PageDown => -12,
            _ => 0,
        };
        if (by != 0) { e.Handled = true; Step(by); return; }
        if (e.Key is Key.Home or Key.Delete or Key.Back) { e.Handled = true; Reset(); }
        else if (e.Key == Key.Enter) { e.Handled = true; Commit(); }
    }

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); hover = true; InvalidateVisual(); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); hover = false; InvalidateVisual(); }
    protected override void OnGotFocus(GotFocusEventArgs e) { base.OnGotFocus(e); InvalidateVisual(); }
    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e) { base.OnLostFocus(e); Commit(); InvalidateVisual(); }
}
