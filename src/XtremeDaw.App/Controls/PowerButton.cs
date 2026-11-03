using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The one ON / OFF of an effect, the same in every place that switches one (inspector card, Color, the mixer's effect
/// panel, mastering, an EQ band): the power glyph and ON / OFF, solid accent while on, a faint outline while off that
/// lights up like an active chip under the pointer. Its colors are App.axaml's <c>Button.chip.power</c> (with
/// <c>on</c>), so the hover never hides the state. The children are made once and <see cref="Refresh"/> only touches
/// the text, the class and the tooltip when the state changed, so it can run on every refresh without allocating.
/// </summary>
public sealed class PowerButton : Button
{
    /// <summary>Power glyph of Segoe Fluent Icons.</summary>
    const string Glyph = "";
    readonly TextBlock state = new() { FontSize = 10.5, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
    readonly Func<bool> isOn;
    readonly Func<bool, string>? tip;
    /// <summary>The state shown, null before the first <see cref="Refresh"/>.</summary>
    bool? shown;

    /// <param name="isOn">Whether the effect is on now.</param>
    /// <param name="toggle">Switches it.</param>
    /// <param name="tip">The tooltip for a state (what a click does); by default "Prender" / "Apagar".</param>
    public PowerButton(Func<bool> isOn, Action toggle, Func<bool, string>? tip = null)
    {
        this.isOn = isOn;
        this.tip = tip;
        Classes.Add("chip");
        Classes.Add("power");
        Height = MinHeight = 24;
        Padding = new Thickness(6, 0);
        Focusable = false;
        Cursor = new Cursor(StandardCursorType.Hand);
        var glyph = new TextBlock { Classes = { "icon" }, Text = Glyph, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { glyph, state } };
        Click += (_, _) => toggle();
    }

    /// <summary>Shows ON / OFF beside the glyph (off, the glyph alone, for narrow places).</summary>
    public bool ShowLabel
    {
        get => state.IsVisible;
        set => state.IsVisible = value;
    }

    /// <summary>Reads the state and shows it; nothing changes (or is allocated) while it stays the same.</summary>
    public void Refresh()
    {
        bool on = isOn();
        if (shown == on) return;
        shown = on;
        state.Text = Loc.T(on ? "fx.power.on" : "fx.power.off");
        Classes.Set("on", on);
        ToolTip.SetTip(this, tip != null ? tip(on) : Loc.T(on ? "mix.list.off" : "mix.list.on"));
    }
}
