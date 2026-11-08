using System.Text.Json.Nodes;
using Avalonia.Controls;
using XtremeDaw.App.Controls;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The mixer's layouts (asked by the user, #277, after FL Studio's mixer layouts), picked from the header's title. They
/// are layouts, not presets (asked by the user): <b>XTreme</b> is the free one, the only one with the zoom, where the
/// width, the sections and their heights, the names' place and the inspector are the user's; the others are FL Studio's
/// and fixed: Compacta and Amplia leave the strips with pan, fader and meter and take each channel's effects, sends and
/// in / out to the inspector, their "alt" puts the names at the strips' foot, and Consola (FL Studio's "Extra large")
/// gives every strip its own chain. In a fixed layout the zoom and the Efectos / Envíos switches are off, and nothing
/// marks it as changed. Leaving XTreme keeps what it had ("mixer.xtreme" in config.json) and coming back puts it back,
/// so whatever a fixed layout sets never reaches it. The filter and Conexiones are not part of any layout.
/// </summary>
public partial class MainWindow
{
    /// <summary>A mixer layout: what it sets (null = leaves as it is).</summary>
    sealed class MixerView
    {
        public string Id = "";
        public double? StripW, FxH, SendsH;
        public bool? Fx, Sends, Inspector, NameFoot, InspLeft;
    }

    /// <summary>The free layout's id: the user's own widths and sections, with the zoom.</summary>
    const string XtremeViewId = "xtreme";

    /// <summary>The strips' width in the Compacta and Amplia layouts (px).</summary>
    const double ViewCompactW = TinyW, ViewWideW = 112;

    /// <summary>FL Studio's layouts (#277), fixed: narrow or wide strips with the chain in the inspector, each with its names on top or at the foot, and the console.</summary>
    static readonly MixerView[] FixedViews =
    [
        new() { Id = "compact", StripW = ViewCompactW, Fx = false, Sends = false, Inspector = true, NameFoot = false },
        new() { Id = "compactAlt", StripW = ViewCompactW, Fx = false, Sends = false, Inspector = true, NameFoot = true },
        new() { Id = "wide", StripW = ViewWideW, Fx = false, Sends = false, Inspector = true, NameFoot = false },
        new() { Id = "wideAlt", StripW = ViewWideW, Fx = false, Sends = false, Inspector = true, NameFoot = true },
        new() { Id = "console", StripW = WideMax, Fx = true, Sends = true, FxH = FxListH, SendsH = SendsListH, Inspector = false, NameFoot = false },
    ];

    /// <summary>Whether the strips on screen were built with their names at the foot (<see cref="MixerOptions.NameAtFoot"/>).</summary>
    bool mixerNameFoot;
    /// <summary>The layout on screen: <see cref="XtremeViewId"/> or one of <see cref="FixedViews"/>.</summary>
    string mixerViewId = AppConfig.Get("mixer.view") is { } v && Array.Exists(FixedViews, f => f.Id == v) ? v : XtremeViewId;
    readonly SelectButton mixerViewPick = new() { Width = 150, Height = 28, MinHeight = 28 };
    /// <summary>The index the select shows now, so it is only set when that changes.</summary>
    int mixerViewShown = -2;

    /// <summary>Whether a fixed layout is on screen: no zoom, and the sections are the layout's.</summary>
    bool MixerLayoutFixed => mixerViewId != XtremeViewId;

    /// <summary>The header's title, which is the layouts' select (asked by the user, #277): <paramref name="lead"/> and the layout, the list dropping from it.</summary>
    Control MixerViewTitle(Control lead)
    {
        mixerViewPick.AsTitle(lead);
        Avalonia.Automation.AutomationProperties.SetName(mixerViewPick, Loc.T("mixer.view"));
        mixerViewPick.Selected += i => SetMixerView(i == 0 ? XtremeViewId : FixedViews[i - 1].Id);
        mixerViewPick.BreaksBefore = [1];
        mixerViewPick.SetItems([Loc.T("mixer.view.xtreme"), .. FixedViews.Select(f => Loc.T("mixer.view." + f.Id))]);
        headerRefresh.Add(ShowMixerView);
        headerRefresh.Add(() =>
        {
            mixerZoomSlider.IsEnabled = !MixerLayoutFixed;
            mixerZoomIcon.Opacity = MixerLayoutFixed ? 0.35 : 1;
        });
        ShowMixerView();
        return mixerViewPick;
    }

    /// <summary>Shows the layout on screen in the select. Runs on every refresh and grip move: sets it only when it changes.</summary>
    void ShowMixerView()
    {
        int index = MixerLayoutFixed ? Array.FindIndex(FixedViews, f => f.Id == mixerViewId) + 1 : 0;
        if (mixerViewShown == index) return;
        mixerViewShown = index;
        mixerViewPick.SelectedIndex = index;
    }

    /// <summary>
    /// Puts a layout on screen. Leaving XTreme keeps what it had; coming back puts that back. The fields first, then one
    /// refresh (widths and heights in place), the names' place last (its own rebuild, through <see cref="MixerOptions.Changed"/>).
    /// </summary>
    void SetMixerView(string id)
    {
        if (!MixerLayoutFixed && id != XtremeViewId) SaveViewPref("mixer.xtreme", XtremeSnapshot().ToJsonString());
        var view = id == XtremeViewId ? LoadXtremeSnapshot() : Array.Find(FixedViews, f => f.Id == id);
        if (view == null && id != XtremeViewId) return;
        if (view != null)
        {
            static string Num(double x) => x.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (view.StripW is { } w)
            {
                mixerStripW = Math.Round(Math.Clamp(w, MinStripW, WideMax));
                SaveViewPref("mixerStripW", Num(mixerStripW));
            }
            if (view.Fx is { } fx) { mixerFx = fx; SaveViewPref("mixer.fx", fx ? "on" : "off"); }
            if (view.Sends is { } sends) { mixerSends = sends; SaveViewPref("mixer.sends", sends ? "on" : "off"); }
            if (view.FxH is { } fh) { mixerFxH = Math.Clamp(fh, ListMinH, ListMaxH); SaveViewPref("mixer.fxH", Num(mixerFxH)); }
            if (view.SendsH is { } sh) { mixerSendsH = Math.Clamp(sh, ListMinH, ListMaxH); SaveViewPref("mixer.sendsH", Num(mixerSendsH)); }
            if (view.Inspector is { } ins) SetMixerInspector(ins);
            if (view.InspLeft is { } il) SetMixerInspSide(il);
        }
        mixerViewId = id;
        SaveViewPref("mixer.view", id);
        ApplyMixerHeights();
        RefreshMixer();
        if (view?.NameFoot is { } nf) MixerOptions.SetNameAtFoot(nf);
        ShowMixerView();
    }

    /// <summary>The free layout as it is on screen now, to put back when it is chosen again.</summary>
    JsonObject XtremeSnapshot() => new()
    {
        ["stripW"] = mixerStripW, ["fx"] = mixerFx, ["sends"] = mixerSends, ["fxH"] = mixerFxH, ["sendsH"] = mixerSendsH,
        ["inspector"] = mixerInspectorOn, ["namePos"] = MixerOptions.NameAtFoot ? "bottom" : "top",
        ["inspSide"] = mixerInspLeft ? "left" : "right",
    };

    /// <summary>What the free layout had when it was left (config.json, "mixer.xtreme"); null if it was never left or doesn't read.</summary>
    static MixerView? LoadXtremeSnapshot()
    {
        try
        {
            if (AppConfig.Get("mixer.xtreme") is not { Length: > 0 } text || JsonNode.Parse(text) is not JsonObject o) return null;
            return new MixerView
            {
                Id = XtremeViewId, StripW = Num(o, "stripW"), FxH = Num(o, "fxH"), SendsH = Num(o, "sendsH"),
                Fx = Flag(o, "fx"), Sends = Flag(o, "sends"), Inspector = Flag(o, "inspector"),
                NameFoot = Str(o, "namePos") is { } np ? np == "bottom" : null,
                InspLeft = Str(o, "inspSide") is { } isd ? isd == "left" : null,
            };
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException) { return null; }

        static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        static bool? Flag(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out bool b) ? b : null;
        static double? Num(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<double>(out double d) && double.IsFinite(d) ? d : null;
    }
}
