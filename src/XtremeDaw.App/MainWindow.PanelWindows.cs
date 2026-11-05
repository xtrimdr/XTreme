using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.App.Controls;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// Every panel of the window can go to a window of its own and come back (asked by the user: "Todos los paneles.
/// Todos."), the way the virtual keyboard does (<see cref="OpenVirtualWindow"/>): a button in the panel (and Ver →
/// "Paneles en ventana") moves it out, the same button or the window's X brings it back. The panels are the inspector,
/// the stage (preview and "En vivo"), the timeline, the mixer, the piano roll, Mastering and the project bar: the three
/// views of the mixer's place each go to a window of their own (asked by the user, #139), and the place stays in the main
/// window for those that are still there. The panel itself moves, nothing is rebuilt, so it keeps its state; its window's place and size, and which
/// panels are out, live in config.json ("panelsOut", "panelWindow.&lt;id&gt;") and come back on the next start.
/// <para>
/// What stays in the main window takes the room left (decision of the user): with the stage out the window stacks
/// like solo audio (<see cref="StageHere"/>), with the timeline out the bottom row holds the mixer's place or closes
/// (<see cref="BottomEmpty"/>), and a view in its own window brings that window up from its button. Maximizing (<see cref="ToggleMaxPanel"/>) inside a panel's window maximizes that window (decision of
/// the user); in the main window it works on what is still there. Moving a panel restores a maximized one first.
/// </para>
/// </summary>
public partial class MainWindow
{
    const string FloatInspector = "inspector", FloatStage = "stage", FloatTimeline = "timeline", FloatMixer = "mixer", FloatPiano = "piano", FloatMastering = "mastering", FloatProject = "project";
    static readonly string[] FloatPanels = [FloatInspector, FloatStage, FloatTimeline, FloatMixer, FloatPiano, FloatMastering, FloatProject];

    /// <summary>A panel's window: its first size and smallest, and its place while it isn't maximized (to save it).</summary>
    sealed class PanelWindow
    {
        public required Window Window { get; init; }
        public required Border Frame { get; init; }
        /// <summary>The name in its title bar (the mixer's follows what it shows).</summary>
        public required TextBlock Caption { get; init; }
        public PixelPoint At;
        public Size Size;
        /// <summary>It closes because the main window does: the panel stays out for the next start.</summary>
        public bool Quiet;
        /// <summary>Already closed (its X): nothing left to close.</summary>
        public bool Gone;
    }

    /// <summary>The height of a panel window's own title bar (the test list's).</summary>
    const double PanelTitleH = 36;

    readonly Dictionary<string, PanelWindow> panelWindows = [];
    /// <summary>The main window closed: its panels' windows close with it and stay out for the next start.</summary>
    bool mainClosed;
    /// <summary>The buttons that move each panel out and back, refreshed when it moves.</summary>
    readonly Dictionary<string, List<Button>> floatButtons = [];
    /// <summary>Mastering is rebuilt whenever it opens: its button is one, moved into each new head.</summary>
    Button? masteringFloat;

    bool PanelOut(string id) => panelWindows.ContainsKey(id);
    bool InspectorOut => PanelOut(FloatInspector);
    bool StageOut => PanelOut(FloatStage);
    bool TimelineOut => PanelOut(FloatTimeline);
    /// <summary>The mixer is in its own window (only the mixer: the piano roll and Mastering keep the place).</summary>
    bool MixerOut => PanelOut(FloatMixer);
    bool PianoOut => PanelOut(FloatPiano);
    bool MasteringOut => PanelOut(FloatMastering);
    bool ProjectBarOut => PanelOut(FloatProject);

    /// <summary>The stage is in the main window: a project with a picture whose stage isn't out.</summary>
    bool StageHere => !AudioOnly && !StageOut;

    void InitPanelWindows()
    {
        StageBar.Children.Add(FloatButton(FloatStage, chip: true));
        // every button sits in its panel's top right corner (asked by the user): the timeline's over the ruler's end
        var timeline = FloatButton(FloatTimeline, chip: true);
        timeline.HorizontalAlignment = HorizontalAlignment.Right;
        timeline.VerticalAlignment = VerticalAlignment.Top;
        timeline.Margin = new Thickness(0, 3, 8, 0);
        ((Panel)TimelineToolbar.Parent!).Children.Add(timeline);
        masteringFloat = FloatButton(FloatMastering, chip: true);
        // they aren't owned by the main window: they close with it (and the program wouldn't end while one is open)
        Closed += (_, _) =>
        {
            mainClosed = true;
            foreach (var w in panelWindows.Values.ToList()) w.Window.Close();
        };
        // the panels that were out when the program closed go out again, once the window is there to own them
        // (after its first layout, not in the middle of it)
        Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (string id in (AppConfig.Get("panelsOut") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (FloatPanels.Contains(id)) SetPanelOut(id, true, save: false);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>The button that moves a panel out and back: a chip like the keyboard's, or a ghost one in a sidebar's head.</summary>
    Button FloatButton(string id, bool chip)
    {
        var b = chip
            ? new Button { Classes = { "chip" }, Width = 26, Height = 26, MinHeight = 0, Padding = new Thickness(0), Focusable = false,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Content = new TextBlock { Classes = { "icon" }, FontSize = 13 } }
            : new Button { Classes = { "ghost" }, Width = 32, Height = 32, Padding = new Thickness(0), Focusable = false, Foreground = Palette.Faint,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Content = new TextBlock { Classes = { "icon" }, FontSize = 14 } };
        b.Click += (_, _) => SetPanelOut(id, !PanelOut(id));
        if (!floatButtons.TryGetValue(id, out var list)) floatButtons[id] = list = [];
        list.Add(b);
        ShowFloatButton(b, PanelOut(id));
        return b;
    }

    static void ShowFloatButton(Button b, bool outside)
    {
        ((TextBlock)b.Content!).Text = outside ? "" : "";
        string tip = Loc.T(outside ? "panel.dock.tip" : "panel.float.tip");
        ToolTip.SetTip(b, tip);
        Avalonia.Automation.AutomationProperties.SetName(b, tip);
    }

    /// <summary>The panel's window comes to the front; false when the panel is in the main window.</summary>
    bool ActivatePanel(string id)
    {
        if (!panelWindows.TryGetValue(id, out var w)) return false;
        if (w.Window.WindowState == WindowState.Minimized) w.Window.WindowState = WindowState.Normal;
        w.Window.Activate();
        return true;
    }

    /// <summary>The panel is out and its window has the keys.</summary>
    bool PanelActive(string id) => panelWindows.TryGetValue(id, out var w) && w.Window.IsActive;

    /// <summary>The panel window that has the keys now, if any: Enter maximizes that one.</summary>
    Window? ActivePanelWindow() => panelWindows.Values.Select(w => w.Window).FirstOrDefault(w => w.IsActive);

    string PanelTitle(string id) => id switch
    {
        FloatInspector => Loc.T("insp.title"),
        FloatStage => Loc.T("panel.stage"),
        FloatTimeline => Loc.T("transport.view.timeline"),
        FloatProject => Loc.T("panel.project"),
        FloatPiano => Loc.T("transport.view.piano"),
        FloatMastering => Loc.T("transport.view.mastering"),
        _ => Loc.T("transport.view.mixer"),
    };

    /// <summary>Moves a panel to its window or back, and lays the main window out again for what stays.</summary>
    void SetPanelOut(string id, bool outside, bool save = true)
    {
        if (outside == PanelOut(id))
        {
            if (outside) ActivatePanel(id);
            return;
        }
        // the stage has nothing to show in solo audio: it can come back, not go out
        if (outside && id == FloatStage && AudioOnly) return;
        Timeline.CancelGesture();
        Preview.CancelGesture();
        if (maxPanel != MaxPanel.None) RestoreMaxPanel();
        bool views = id is FloatTimeline;
        if (views && !stackedViews) KeepBottomRow();
        // each view leaves the mixer's place, or comes back to it
        bool own = id is FloatMixer or FloatPiano or FloatMastering,
            inPlace = mixerView && (id == FloatPiano ? pianoView : id == FloatMastering ? masteringView : !pianoView && !masteringView);
        // a window lays out what it holds in its own pass: a pass still pending for the panel would reach it in the other
        // window, and Avalonia throws ("wrong LayoutManager"). So the window it leaves lays out once it is gone, before
        // the panel goes in the other one: a pass skips what isn't in its window
        if (outside)
        {
            var from = TopLevel.GetTopLevel(PanelContent(id));
            var content = TakePanel(id);
            from?.UpdateLayout();
            OpenPanelWindow(id, content);
        }
        else if (panelWindows.Remove(id, out var w))
        {
            var content = w.Frame.Child!;
            w.Frame.Child = null;
            if (!w.Gone)
            {
                w.Window.UpdateLayout();
                w.Window.Close();
            }
            PutPanel(id, content);
        }
        PanelMoved(id, views);
        if (own)
        {
            if (outside)
            {
                if (id == FloatPiano) pianoView = false;
                else if (id == FloatMastering) masteringView = false;
                // what stays takes the room: the place closes
                if (inPlace) SetMixerView(false);
                ApplyMasteringView();
                if (id == FloatMixer) RefreshMixer();
                if (id == FloatPiano)
                {
                    pianoRoll.SetPlayhead(engine.PositionSec);
                    RefreshPiano();
                }
            }
            // back in the mixer's place, and shown there
            else if (id == FloatPiano) OpenPianoView(PianoRollView.Frame.Keep);
            else ShowBottomView(mastering: id == FloatMastering);
        }
        if (save) SaveViewPref("panelsOut", string.Join(",", FloatPanels.Where(PanelOut)));
    }

    /// <summary>What of the panel is on screen now, to know which window it leaves.</summary>
    Control PanelContent(string id) => id switch
    {
        FloatInspector => InspectorBar,
        FloatProject => ProjectBar,
        FloatPiano => pianoHost,
        FloatMastering => masteringScroll,
        FloatMixer => mixerPane,
        _ => PanelHost(id),
    };

    /// <summary>
    /// Takes a panel out of the main window. The sidebars leave their (hidden) bar behind and give their content; the
    /// piano roll and Mastering leave the mixer's place (Mastering with its pinned meter beside it).
    /// </summary>
    Control TakePanel(string id)
    {
        Control c;
        switch (id)
        {
            case FloatMixer:
                mixerViews.Children.Remove(mixerPane);
                return mixerPane;
            case FloatPiano:
                mixerViews.Children.Remove(pianoHost);
                return pianoHost;
            case FloatMastering:
                mixerViews.Children.Remove(masteringScroll);
                mixerPins.Children.Remove(masteringPin);
                Grid.SetColumn(masteringPin, 1);
                // the pinned meter goes along: the view keeps its notips (App.axaml) in its own window
                return new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Classes = { "notips" }, Children = { masteringScroll, masteringPin } };
            case FloatInspector:
                c = InspectorBar.Child!;
                InspectorBar.Child = null;
                return c;
            case FloatProject:
                c = ProjectBar.Child!;
                ProjectBar.Child = null;
                return c;
            default:
                c = PanelHost(id);
                ((Panel)c.Parent!).Children.Remove(c);
                return c;
        }
    }

    Control PanelHost(string id) => id == FloatStage ? StageArea : TimelineHost;

    /// <summary>Puts a panel back where the main window keeps it now (the timeline moves with <see cref="MoveBottomView"/>).</summary>
    void PutPanel(string id, Control c)
    {
        switch (id)
        {
            case FloatInspector:
                InspectorBar.Child = c;
                break;
            case FloatProject:
                ProjectBar.Child = c;
                break;
            case FloatStage:
                Workspace.Children.Insert(Workspace.Children.IndexOf(AudioArea), c);
                break;
            case FloatMixer:
                mixerViews.Children.Insert(0, c);
                break;
            case FloatPiano:
                mixerViews.Children.Add(c);
                break;
            case FloatMastering:
                ((Grid)c).Children.Clear();
                mixerViews.Children.Add(masteringScroll);
                mixerPins.Children.Add(masteringPin);
                break;
            default:
                if (stackedViews)
                {
                    Grid.SetRow(c, 0);
                    AudioArea.Children.Add(c);
                }
                else
                {
                    Grid.SetRow(c, 3);
                    // under the dialogs and toasts (see MoveBottomView)
                    Root.Children.Insert(Root.Children.IndexOf(Projects), c);
                }
                break;
        }
    }

    /// <summary>After a panel moved: what it changes in the main window.</summary>
    void PanelMoved(string id, bool views)
    {
        switch (id)
        {
            case FloatInspector:
                SetInspectorHidden(inspectorHidden, save: false);
                ShowInspectorFold();
                break;
            case FloatProject:
                SetProjectBarHidden(projectBarHidden, save: false);
                ShowProjectBarFold();
                break;
            case FloatStage:
                StageArea.Width = double.NaN;
                break;
        }
        ApplyLayout();
        if (views)
        {
            if (!stackedViews) ApplyBottomRow();
            ApplyViewHosts();
            ApplyMasteringView();
        }
        bool outside = PanelOut(id);
        if (floatButtons.TryGetValue(id, out var list)) foreach (var b in list) ShowFloatButton(b, outside);
    }

    /// <summary>
    /// The panel's window, a window of its own with its button in the taskbar (asked by the user: minimized with the main one,
    /// owned windows went down into a corner of it), so it minimizes, goes behind and comes up on its own. It takes the focus like any other,
    /// so the shortcuts pass through it to <see cref="OnKey"/>; its X brings the panel back.
    /// </summary>
    void OpenPanelWindow(string id, Control content)
    {
        var (width, height, minW, minH) = id switch
        {
            FloatInspector or FloatProject => (SidebarWidth + 40, 720.0, SidebarMinWidth, 300.0),
            FloatStage => (820, 520, 400, 300),
            FloatTimeline => (1100, 420, 500, TimelineMinH + 60),
            FloatMastering => (1100, 440, 500, ViewMinH),
            _ => (1100, 520, 500, ViewMinH + 40),
        };
        var saved = AppConfig.Get("panelWindow." + id)?.Split(',').Select(s => int.TryParse(s, out int n) ? n : int.MinValue).ToArray();
        bool known = saved is { Length: 5 } && !saved.Contains(int.MinValue);
        var at = known ? new PixelPoint(saved![0], saved[1]) : default;
        known = known && Screens.ScreenFromPoint(at) != null;
        // the sizes are the window's, title bar included
        (height, minH) = (height + PanelTitleH, minH + PanelTitleH);
        if (known) (width, height) = (Math.Max(minW, saved![2]), Math.Max(minH, saved[3]));
        var frame = new Border { Child = content };
        // the app's own title bar, like the test list's: its name on the left over Panel, Windows' buttons on the right
        var caption = new TextBlock { Text = PanelTitle(id), FontSize = 12.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var bar = new Border { Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = caption };
        Grid.SetRow(frame, 1);
        var root = new Grid { RowDefinitions = new RowDefinitions($"{PanelTitleH},*"), Children = { bar, frame } };
        var window = new Window
        {
            Title = PanelTitle(id), Width = width, Height = height, MinWidth = minW, MinHeight = minH,
            Background = id is FloatInspector or FloatProject ? Palette.Panel : Palette.App, ShowInTaskbar = true, Icon = Icon,
            WindowStartupLocation = known ? WindowStartupLocation.Manual : WindowStartupLocation.CenterScreen,
            ExtendClientAreaToDecorationsHint = true, ExtendClientAreaTitleBarHeightHint = PanelTitleH,
            Content = root,
        };
        // maximized, Windows pushes the window past the screen's edges (as the main window's Root)
        root.Bind(MarginProperty, window.GetObservable(Window.OffScreenMarginProperty));
        bar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else window.BeginMoveDrag(e);
        };
        if (known) window.Position = at;
        var w = new PanelWindow { Window = window, Frame = frame, Caption = caption, At = at, Size = new Size(width, height) };
        // the place to save is the one it has while not maximized
        window.PositionChanged += (_, e) => { if (window.WindowState == WindowState.Normal) w.At = e.Point; };
        window.SizeChanged += (_, e) =>
        {
            if (window.WindowState == WindowState.Normal) w.Size = e.NewSize;
            if (id == FloatStage) ArrangeStage(e.NewSize.Height > e.NewSize.Width);
        };
        window.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        window.AddHandler(KeyUpEvent, OnVirtualKeyUp, RoutingStrategies.Tunnel);
        window.AddHandler(KeyUpEvent, OnTransportKeyUp, RoutingStrategies.Tunnel);
        window.AddHandler(PointerPressedEvent, OnWindowPressCloseMenu, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.Closing += (_, e) =>
        {
            SaveViewPref("panelWindow." + id, $"{w.At.X},{w.At.Y},{(int)w.Size.Width},{(int)w.Size.Height},{(window.WindowState == WindowState.Maximized ? 1 : 0)}");
            w.Quiet = mainClosed || e.CloseReason != WindowCloseReason.WindowClosing;
        };
        // its X (not the main window closing, nor the button, which took the panel already): the panel comes back
        window.Closed += (_, _) =>
        {
            w.Gone = true;
            if (w.Quiet || !panelWindows.TryGetValue(id, out var now) || now != w) return;
            SetPanelOut(id, false);
        };
        panelWindows[id] = w;
        if (id == FloatStage) ArrangeStage(height > width);
        window.Show();
        if (known && saved![4] == 1) window.WindowState = WindowState.Maximized;
    }

    /// <summary>Solo audio has no picture: the stage's window hides, and shows again back in video.</summary>
    void ShowStageWindow()
    {
        if (!panelWindows.TryGetValue(FloatStage, out var w)) return;
        bool show = !AudioOnly;
        if (w.Window.IsVisible == show) return;
        if (show) w.Window.Show();
        else w.Window.Hide();
    }

    /// <summary>Ver → "Paneles en ventana": each panel, checked while it is out, and every one back at once.</summary>
    MenuItem PanelsOutItem()
    {
        var items = new List<object>();
        foreach (string id in FloatPanels)
        {
            string label = PanelTitle(id);
            bool enabled = PanelOut(id) || id != FloatStage || !AudioOnly;
            items.Add(Check(label, PanelOut(id), () => SetPanelOut(id, !PanelOut(id)), enabled: enabled));
        }
        items.Add(new Separator());
        items.Add(Entry(Loc.T("menubar.dockAll"), () => { foreach (string id in FloatPanels) SetPanelOut(id, false); }, enabled: panelWindows.Count > 0));
        return new MenuItem { Header = new TextBlock { Text = Loc.T("menubar.panelsOut"), FontSize = 12 }, ItemsSource = items };
    }

    /// <summary>Puts the shared button in the Mastering view's corner (its meter's title row), which is built anew each time it opens.</summary>
    Control MasteringFloatButton()
    {
        var b = masteringFloat!;
        (b.Parent as Panel)?.Children.Remove(b);
        b.HorizontalAlignment = HorizontalAlignment.Right;
        b.VerticalAlignment = VerticalAlignment.Top;
        return b;
    }
}
