using Avalonia;
using XtremeDaw.App.Controls;

namespace XtremeDaw.App;

/// <summary>
/// Paints the window's panels (inspector, project bar, timeline, mixer) in the look picked in Apariencia
/// (<see cref="PanelLooks"/>, #191): their corners, edge, shadows, background and the gap between them. Done from code
/// rather than styles because the shadows and the gradient are built from the theme's colors, and again on every
/// theme change. The timeline and the mixer clip their content, so their frame goes on the edge
/// drawn over them (<c>card-edge</c>).
/// </summary>
public partial class MainWindow
{
    void InitPanelLook()
    {
        ApplyPanelLook();
        PanelLooks.Changed += ApplyPanelLook;
        UiTheme.Changed += ApplyPanelLook;
    }

    void ApplyPanelLook()
    {
        double m = PanelLooks.Margin;
        foreach (var card in new[] { InspectorBar, ProjectBar })
        {
            PanelLooks.Frame(card);
            PanelLooks.Fill(card, Palette.Panel, Palette.PanelColor);
        }
        PanelLooks.Frame(TimelineEdge);
        PanelLooks.Frame(MixerEdge);
        PanelLooks.Round(TimelineClip);
        PanelLooks.Round(MixerClip);
        PanelLooks.Fill(MixerClip, Palette.App, Palette.AppColor);
        ProjectBar.Margin = TimelineHost.Margin = MixerHost.Margin = new Thickness(m);
        // the same gap again around the area the cards live in, so every gap is 2m: between two cards, against the
        // window's sides and against the bars, which take it back to stay edge to edge (#193)
        Root.Margin = new Thickness(m, 0);
        TitleBar.Margin = new Thickness(-m, 0, -m, m);
        TransportBar.Margin = new Thickness(-m, m, -m, 0);
        VirtualKeysHost.Margin = new Thickness(-m, 0);
        SpaceForPointer();
        // the inspector's colored stripe ends before the corners curve
        double inset = PanelLooks.Radius - 1;
        inspectorEdge.Margin = new Thickness(InspectorEdgeInsetX, inset, 0, inset);
    }
}
