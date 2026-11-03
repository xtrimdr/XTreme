using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace XtremeDaw.App;

/// <summary>
/// Maximizing a panel (asked by the user, like Enter in FL Studio): the "maximize" shortcut or the transport's button
/// gives the window's whole working area to the panel last clicked (the preview, the timeline, or what shares the
/// mixer's place) and the same key brings everything back as it was. The title and transport bars stay, so the view
/// buttons keep working: maximized, they swap what fills the place. It builds on the stacked arrangement
/// (<see cref="MoveBottomView"/>) that solo audio already uses, so the remembered heights stay untouched. The sidebars
/// can still be brought up (B, P) but that isn't saved. Not remembered between sessions.
/// </summary>
public partial class MainWindow
{
    /// <summary><see cref="Views"/> is the timeline or whatever is in the mixer's place, following the view buttons.</summary>
    enum MaxPanel { None, Stage, Views }

    MaxPanel maxPanel;
    /// <summary>The panel last clicked: what the shortcut maximizes.</summary>
    Control? pressedPanel;
    bool inspectorWasHidden, projectBarWasHidden;
    /// <summary>Stacked, the mixer was open under a maximized timeline: it comes back when the panel is restored.</summary>
    Action? reopenView;

    /// <summary>The preview is maximized, so the buttons of the views on screen mean "show it" rather than "go back".</summary>
    bool ViewsCovered => maxPanel == MaxPanel.Stage;

    void InitMaxPanel()
    {
        MaxPanelButton.Click += (_, _) => ToggleMaxPanel();
        AddHandler(PointerPressedEvent, OnWindowPressPanel, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    void OnWindowPressPanel(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Avalonia.Visual v) return;
        if (StageArea.IsVisualAncestorOf(v)) pressedPanel = StageArea;
        else if (TimelineHost.IsVisualAncestorOf(v)) pressedPanel = TimelineHost;
        else if (MixerHost.IsVisualAncestorOf(v)) pressedPanel = MixerHost;
    }

    void ToggleMaxPanel()
    {
        // the panel under a gesture is about to move
        Timeline.CancelGesture();
        Preview.CancelGesture();
        if (maxPanel != MaxPanel.None)
        {
            SetMaxPanel(MaxPanel.None);
            if (!mixerView) reopenView?.Invoke();
            reopenView = null;
            return;
        }
        if (pressedPanel == StageArea && StageArea.IsVisible)
        {
            SetMaxPanel(MaxPanel.Stage);
            return;
        }
        if (stackedViews && mixerView && pressedPanel == TimelineHost)
        {
            bool piano = pianoView, mastering = masteringView;
            reopenView = piano ? ShowPiano : () => ShowBottomView(mastering);
            SetMixerView(false);
        }
        SetMaxPanel(MaxPanel.Views);
    }

    void SetMaxPanel(MaxPanel panel)
    {
        if (panel == maxPanel) return;
        if (maxPanel == MaxPanel.None) (inspectorWasHidden, projectBarWasHidden) = (!InspectorBar.IsVisible, !ProjectBar.IsVisible);
        maxPanel = panel;
        bool restored = panel == MaxPanel.None;
        SetInspectorHidden(!restored || inspectorWasHidden, save: false);
        SetProjectBarHidden(!restored || projectBarWasHidden, save: false);
        ApplyLayout();
        RefreshMixer();
    }

    /// <summary>A view asked for while the preview covers them: the views take the place, still maximized.</summary>
    void UncoverViews()
    {
        if (ViewsCovered) SetMaxPanel(MaxPanel.Views);
    }

    void ShowMaxPanelState()
    {
        bool on = maxPanel != MaxPanel.None;
        MaxPanelButton.Classes.Set("on", on);
        MaxPanelIcon.Text = on ? "" : "";
    }
}
