using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace XtremeDaw.App.Controls;

/// <summary>
/// TakeInspector's ui.tsx Slider: label, numeric value, stepped range and double-click reset. <c>compact</c> makes it one
/// row, like a console's inspector (asked by the user, after Fender Studio Pro's): the label on the left and the value
/// on the right in a box that is dragged up and down (Shift: fine) or turned with the wheel; a double click types the
/// number and Alt+click puts it back.
/// </summary>
public sealed class InspectorSlider : StackPanel
{
    readonly TextBlock value;
    readonly Func<double, string> format;
    readonly Action<double> change;
    readonly Border? box;
    bool filling;
    public Slider Range { get; }
    public TextBlock Label { get; }
    public double ResetValue { get; set; }
    public StackPanel Trailing { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    /// <summary>A compact row that only sets its value when the drag ends (or the wheel stops): while it moves it shows
    /// the number it will set. For what is costly to hear (Transponer renders the track again).</summary>
    public bool OnRelease { get; set; }
    /// <summary>Waits for the wheel to stop on an <see cref="OnRelease"/> row.</summary>
    Avalonia.Threading.DispatcherTimer? wheelWait;
    double pending = double.NaN;

    /// <summary>Pixels of an upward drag that cross the whole range of a compact row (ten times that with Shift).</summary>
    const double DragSpan = 240;
    /// <summary>How long the wheel rests before an <see cref="OnRelease"/> row sets its value.</summary>
    static readonly TimeSpan WheelRest = TimeSpan.FromMilliseconds(400);
    /// <summary>Wheel notches that cross the whole range of a compact row (Shift: one step a notch).</summary>
    const double WheelNotches = 100;
    /// <summary>A compact row's value box (px).</summary>
    const double BoxW = 88, BoxH = 24;

    public InspectorSlider(string label, double min, double max, double step, double reset,
        Func<double, string> format, Action<double> change, string? hint = null, bool compact = false)
    {
        this.format = format;
        this.change = change;
        ResetValue = reset;
        Spacing = 4;
        var name = new TextBlock { Text = label, Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        Label = name;
        ToolTip.SetTip(name, hint);
        value = new TextBlock { Classes = { "mono" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Range = new InspectorRange { Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true,
            SmallChange = step };
        AutomationProperties.SetName(Range, label);
        Range.ValueChanged += (_, e) => { if (!filling) this.change(e.NewValue); };
        if (compact)
        {
            value.HorizontalAlignment = HorizontalAlignment.Right;
            box = new Border
            {
                Width = BoxW, Height = BoxH, CornerRadius = new CornerRadius(4), Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 0), Child = value, Cursor = new Cursor(StandardCursorType.SizeNorthSouth), Margin = new Thickness(6, 0, 0, 0),
            };
            AutomationProperties.SetName(box, label);
            Drag(box, step);
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), MinHeight = 28 };
            line.Children.Add(name);
            Grid.SetColumn(Trailing, 1); line.Children.Add(Trailing);
            Grid.SetColumn(box, 2); line.Children.Add(box);
            Trailing.VerticalAlignment = VerticalAlignment.Center;
            Children.Add(line);
            return;
        }
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(name);
        Grid.SetColumn(value, 1);
        value.PointerPressed += (_, e) => { if (e.ClickCount == 2) change(ResetValue); };
        row.Children.Add(value);
        Children.Add(row);
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        bottom.Children.Add(Range);
        Grid.SetColumn(Trailing, 1);
        bottom.Children.Add(Trailing);
        Children.Add(bottom);
    }

    /// <summary>The compact box's gestures: they move <see cref="Range"/>, which snaps to the step and reports the change.</summary>
    void Drag(Border b, double step)
    {
        double startY = 0, startV = 0;
        bool dragging = false;
        b.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(b).Properties.IsLeftButtonPressed || b.Child is TextBox) return;
            e.Handled = true;
            if ((e.KeyModifiers & KeyModifiers.Alt) != 0) { change(ResetValue); return; }
            if (e.ClickCount == 2) { dragging = false; e.Pointer.Capture(null); Type(b); return; }
            dragging = true;
            startY = e.GetPosition(b).Y;
            startV = double.IsNaN(pending) ? Range.Value : pending;
            e.Pointer.Capture(b);
        };
        b.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            double span = Range.Maximum - Range.Minimum;
            bool fine = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            double dy = startY - e.GetPosition(b).Y;
            Move(startV + dy / (fine ? DragSpan * 10 : DragSpan) * span, step);
        };
        b.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            Commit();
        };
        b.PointerWheelChanged += (_, e) =>
        {
            if (b.Child is TextBox) return;
            e.Handled = true;
            bool fine = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            double notch = fine ? step : Math.Max(step, Math.Round((Range.Maximum - Range.Minimum) / WheelNotches / step) * step);
            Move((double.IsNaN(pending) ? Range.Value : pending) + Math.Sign(e.Delta.Y) * notch, step);
            if (!OnRelease) return;
            wheelWait ??= new Avalonia.Threading.DispatcherTimer { Interval = WheelRest };
            wheelWait.Tick -= WheelRested;
            wheelWait.Tick += WheelRested;
            wheelWait.Stop();
            wheelWait.Start();
        };
    }

    void WheelRested(object? sender, EventArgs e)
    {
        wheelWait?.Stop();
        Commit();
    }

    /// <summary>Where a gesture takes the value: set at once, or, on an <see cref="OnRelease"/> row, only shown until <see cref="Commit"/>.</summary>
    void Move(double v, double step)
    {
        v = Math.Clamp(Range.Minimum + Math.Round((v - Range.Minimum) / step) * step, Range.Minimum, Range.Maximum);
        if (!OnRelease) { Range.Value = v; return; }
        pending = v;
        value.Text = format(v);
    }

    void Commit()
    {
        if (double.IsNaN(pending)) return;
        double v = pending;
        pending = double.NaN;
        if (v != Range.Value) Range.Value = v;
        else value.Text = format(v);
    }

    /// <summary>The number typed in the box: Enter or leaving it sets it (clamped, snapped), Esc leaves it as it was.</summary>
    void Type(Border b)
    {
        var field = new TextBox
        {
            Text = Range.Value.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture), FontSize = 12, Classes = { "mono" }, MinHeight = 0, Height = BoxH - 2,
            Padding = new Thickness(4, 0), BorderThickness = new Thickness(0), Background = Palette.App, HorizontalContentAlignment = HorizontalAlignment.Right,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var padding = b.Padding;
        b.Padding = new Thickness(0);
        b.Child = field;
        bool done = false;
        void Close(bool keep)
        {
            if (done) return;
            done = true;
            if (keep && Parse(field.Text) is { } v) Range.Value = Math.Clamp(v, Range.Minimum, Range.Maximum);
            b.Padding = padding;
            b.Child = value;
        }
        field.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape) { e.Handled = true; Close(e.Key == Key.Enter); }
        };
        field.LostFocus += (_, _) => Close(true);
        field.AttachedToVisualTree += (_, _) => { field.Focus(); field.SelectAll(); };
    }

    /// <summary>The number in what was typed, with its unit or without, in the system's culture or with a point.</summary>
    static double? Parse(string? text)
    {
        var digits = new string((text ?? "").Where(c => char.IsDigit(c) || c is '-' or '+' or ',' or '.' or '−').ToArray()).Replace('−', '-');
        if (double.TryParse(digits, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out double v)) return v;
        if (double.TryParse(digits.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return v;
        return null;
    }

    /// <param name="mixed">What it reads while the tracks edited together don't share a value: the handle dims, it says no number.</param>
    public void Set(double v, double? max = null, string? mixed = null)
    {
        // a value waiting for its release keeps showing until it is set
        if (!double.IsNaN(pending)) return;
        filling = true;
        if (max is { } m) Range.Maximum = Math.Max(Range.Minimum + 0.01, m);
        Range.Value = v;
        Range.Opacity = mixed == null ? 1 : MixedOpacity;
        if (box != null) box.Opacity = mixed == null ? 1 : MixedOpacity;
        Range.InvalidateVisual();
        value.Text = mixed ?? format(v);
        filling = false;
    }

    /// <summary>The slider while its handle stands for no value in particular.</summary>
    const double MixedOpacity = 0.45;
}
