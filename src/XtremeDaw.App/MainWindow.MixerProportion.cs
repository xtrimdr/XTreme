using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.App.Controls;

namespace XtremeDaw.App;

/// <summary>
/// The grip over every fader that trades height between the lists and the fader (asked by the user, #277, like the
/// three dots of FL Studio's mixer): dragged, all the shown lists grow or shrink together, keeping their proportion,
/// and the fader takes the rest. Like the lists' own grips it never makes the mixer taller (it stops at
/// <see cref="ListRoom"/>, which keeps <see cref="FaderRoomH"/> for the fader) nor a list shorter than
/// <see cref="ListMinH"/>; only row heights change (<see cref="ApplyMixerHeights"/>). The result is the lists' heights
/// themselves ("mixer.fxH", "mixer.sendsH"), so a mixer view keeps it with them. A double click puts the default back.
/// </summary>
public partial class MainWindow
{
    /// <summary>The grip's height (px): the room over the fader (<see cref="StripLayout"/> leaves it 6).</summary>
    const double ProportionGripH = 6;

    /// <summary>A strip's grip over its fader, registered so it shows only while a list does.</summary>
    Control ProportionGrip()
    {
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false };
        for (int i = 0; i < 3; i++) dots.Children.Add(new Ellipse { Width = 3, Height = 3, Fill = Palette.Hover });
        var grip = new Border { Height = ProportionGripH, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Top, Child = dots,
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth), IsVisible = mixerFx || SendsRow, ZIndex = 1 };
        void Paint(IBrush b) { foreach (var d in dots.Children) ((Ellipse)d).Fill = b; }
        grip.PointerEntered += (_, _) => Paint(Palette.Accent);
        grip.PointerExited += (_, _) => Paint(Palette.Hover);
        double fromY = 0, fromFx = 0, fromSends = 0;
        bool dragging = false;
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            // not the card's: it would select or drag the strip
            e.Handled = true;
            if (e.ClickCount == 2)
            {
                mixerFxH = FxListH;
                mixerSendsH = SendsListH;
                ApplyMixerHeights();
                SaveListHeights();
                return;
            }
            dragging = true;
            fromY = e.GetPosition(this).Y;
            (fromFx, fromSends) = ShownLists();
            e.Pointer.Capture(grip);
        };
        grip.PointerMoved += (_, e) => { if (dragging) ScaleLists(fromFx, fromSends, e.GetPosition(this).Y - fromY); };
        grip.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            SaveListHeights();
        };
        grip.PointerCaptureLost += (_, _) => dragging = false;
        mx.Grips.Add(grip);
        return grip;
    }

    /// <summary>
    /// The shown lists, as they were at <paramref name="fx"/> and <paramref name="sends"/>, scaled together by
    /// <paramref name="dy"/> px of their sum, within what the mixer's height leaves them.
    /// </summary>
    void ScaleLists(double fx, double sends, double dy)
    {
        bool showFx = mixerFx, showSends = SendsRow;
        double from = (showFx ? fx : 0) + (showSends ? sends : 0);
        if (from <= 0) return;
        double min = (showFx ? ListMinH : 0) + (showSends ? ListMinH : 0);
        double max = Math.Min(ListRoom, (showFx ? ListMaxH : 0) + (showSends ? ListMaxH : 0));
        double k = Math.Clamp(from + dy, min, Math.Max(min, max)) / from;
        if (showFx) mixerFxH = Math.Round(Math.Clamp(fx * k, ListMinH, ListMaxH));
        if (showSends) mixerSendsH = Math.Round(Math.Clamp(sends * k, ListMinH, ListMaxH));
        ApplyMixerHeights();
    }

    void SaveListHeights()
    {
        SaveViewPref("mixer.fxH", mixerFxH.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SaveViewPref("mixer.sendsH", mixerSendsH.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
