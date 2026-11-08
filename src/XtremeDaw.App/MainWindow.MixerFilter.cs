using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Model;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// What the mixer shows (asked by the user, #270: with many channels and buses it got hard to tell what is what): a
/// segmented row in its header turns each kind of strip on or off on its own (audio and MIDI tracks, folders, buses,
/// separators and empty tracks). It only filters the strips that get built; nothing in the project or the sound
/// changes, and the choice is the user's, kept in config.json ("mixer.show.*") like the header's other switches.
/// </summary>
public partial class MainWindow
{
    /// <summary>The kinds the filter knows, in the header's order: config.json key suffix and text key.</summary>
    static readonly string[] MixerKinds = ["audio", "midi", "folders", "buses", "separators", "empty"];

    /// <summary>The kinds turned off (all shown by default).</summary>
    readonly HashSet<string> mixerHidden = [.. MixerKinds.Where(k => AppConfig.Get("mixer.show." + k) == "off")];

    bool MixerShowsKind(string kind) => !mixerHidden.Contains(kind);

    /// <summary>Whether a track's strip passes the filter: its kind (audio or MIDI), and, without content, the empty switch.</summary>
    bool MixerShows(LiveTrack t) =>
        MixerShowsKind(t.Track.IsMidi ? "midi" : "audio") && (t.Track.HasContent || MixerShowsKind("empty"));

    /// <summary>What the filter puts in the strips' key: the hidden kinds and which tracks are empty (a first clip brings one back).</summary>
    string MixerFilterKey() => string.Join("/", mixerHidden) + (MixerShowsKind("empty") ? "" : ":" + string.Join("", tracks.Select(t => t.Track.HasContent ? '1' : '0')));

    /// <summary>Each kind's Fluent glyph, in <see cref="MixerKinds"/>' order: audio, note, folder, routing, divider bar, empty box.</summary>
    static readonly string[] MixerKindGlyphs = ["", "", "", "", "", ""];

    /// <summary>
    /// The header's filter, a console-style "VIEW" panel (asked by the user, #298: the plain segmented row didn't look
    /// like XTreme): a label, then one toggle per kind with a channel LED (lit in the accent while that kind shows), its
    /// glyph and its name; a hidden kind goes dark and faint, so what's filtered out reads at a glance.
    /// </summary>
    Control MixerFilterSwitch()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        row.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("mixer.show.label"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) });
        row.Children.Add(new Border { Width = 1, Height = 16, Background = Palette.Line, Margin = new Thickness(0, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center });
        for (int i = 0; i < MixerKinds.Length; i++)
        {
            string k = MixerKinds[i];
            var led = new Ellipse { Width = 6, Height = 6, StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Center };
            var icon = new TextBlock { Classes = { "icon" }, Text = MixerKindGlyphs[i], FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var text = new TextBlock { Text = Loc.T("mixer.show." + k), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var b = new ToggleButton
            {
                Classes = { "seg" }, Padding = new Thickness(8, 0, 9, 0), Height = 24, Focusable = false,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { led, icon, text } },
            };
            b.Click += (_, _) =>
            {
                if (!mixerHidden.Remove(k)) mixerHidden.Add(k);
                SaveViewPref("mixer.show." + k, MixerShowsKind(k) ? "on" : "off");
                RefreshMixer();
            };
            headerRefresh.Add(() =>
            {
                bool on = MixerShowsKind(k);
                b.IsChecked = on;
                led.Fill = on ? Palette.Accent : Brushes.Transparent;
                led.Stroke = on ? Palette.Accent : Palette.Faint;
                icon.Foreground = on ? Palette.Accent : Palette.Faint;
                text.Foreground = on ? Palette.Fg : Palette.Faint;
            });
            row.Children.Add(b);
        }
        return new Border { Classes = { "seg" }, Height = 30, Child = row, VerticalAlignment = VerticalAlignment.Center };
    }
}
