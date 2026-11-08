using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Model;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The mixer's channel inspector (asked by the user, #277, like FL Studio's Track inspector and FX panel): a panel at
/// the right of the pinned master that shows the selected channel (a track, a folder or a bus) at full size: its badge
/// and name, where it comes from and where it goes, its whole effects list (add, reorder, power, open), its sends with
/// level, PRE and power, and its pan. It is built from the strips' own pieces (<see cref="SlotList"/>,
/// <see cref="CompactSends"/>, <see cref="OutputSelect"/>) into a layout of its own, rebuilt only when the selection or
/// the strips change, and refreshed with the mixer; a lane moves its controls like the strips' (<see cref="FollowLane"/>).
/// Shown with the header's "Inspector" ("mixer.inspector", off by default) and by the views that take the lists out of
/// the strips (Compacta, Amplia: FL Studio's own). It sits at the master's right or before the strips, like FL Studio's
/// "Track inspector on left side" (its right click, "mixer.inspSide"); its width is dragged from the edge that faces the
/// strips ("mixer.inspectorW"), double click goes back to the default.
/// </summary>
public partial class MainWindow
{
    /// <summary>The inspector's width (px): by default, the least and the most its edge drags to.</summary>
    const double MixerInspDefaultW = 260, MixerInspMinW = 200, MixerInspMaxW = 480;
    /// <summary>The grab room of its left edge (px).</summary>
    const double MixerInspGrab = 6;

    bool mixerInspectorOn = AppConfig.Get("mixer.inspector") == "on";
    /// <summary>The inspector before the strips instead of at the master's right (the default).</summary>
    bool mixerInspLeft = AppConfig.Get("mixer.inspSide") == "left";
    Border mixerInspEdge = null!;
    double mixerInspectorW = double.TryParse(AppConfig.Get("mixer.inspectorW"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double w)
        ? Math.Clamp(w, MixerInspMinW, MixerInspMaxW) : MixerInspDefaultW;
    readonly Border mixerInspPane = new() { Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(1, 0, 0, 0), IsVisible = false };
    readonly StackPanel mixerInspBody = new() { Spacing = 12, Margin = new Thickness(12, 10, 12, 12) };
    readonly MixerLayout mixerInspLayout = new();
    /// <summary>What the inspector was built for: the channel's id and the strips' key (it changes with slots, sends and buses).</summary>
    string? mixerInspId;
    string mixerInspKey = "";

    /// <summary>Puts the inspector in the strips' grid: column 0 (before the strips) or 3 (at the master's right).</summary>
    void InitMixerInspector(Grid strips)
    {
        Grid.SetRowSpan(mixerInspPane, 2);
        var scroll = new ScrollViewer { Content = mixerInspBody, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        // the edge that faces the strips widens or narrows it
        var edge = mixerInspEdge = new Border { Width = MixerInspGrab, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeWestEast), ZIndex = 2 };
        double fromX = 0, fromW = 0;
        bool dragging = false;
        edge.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(edge).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            if (e.ClickCount == 2)
            {
                SetMixerInspectorW(MixerInspDefaultW, true);
                return;
            }
            dragging = true;
            fromX = e.GetPosition(this).X;
            fromW = mixerInspectorW;
            e.Pointer.Capture(edge);
        };
        edge.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            double dx = e.GetPosition(this).X - fromX;
            SetMixerInspectorW(mixerInspLeft ? fromW + dx : fromW - dx, false);
        };
        edge.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            SetMixerInspectorW(mixerInspectorW, true);
        };
        edge.PointerCaptureLost += (_, _) => dragging = false;
        mixerInspPane.Child = new Panel { Children = { scroll, edge } };
        mixerInspPane.Width = mixerInspectorW;
        // its right click moves it to the other side
        mixerInspPane.AddHandler(PointerReleasedEvent, (object? _, PointerReleasedEventArgs e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Right || e.Handled) return;
            e.Handled = true;
            var refresh = new List<Action>();
            OpenAnchoredMenu(Popup(new object[]
            {
                MenuCheck(Loc.T("mixer.insp.left"), () => mixerInspLeft, () => SetMixerInspSide(true), refresh),
                MenuCheck(Loc.T("mixer.insp.right"), () => !mixerInspLeft, () => SetMixerInspSide(false), refresh),
            }, 220), mixerInspPane);
        }, Avalonia.Interactivity.RoutingStrategies.Bubble);
        strips.Children.Add(mixerInspPane);
        PlaceMixerInspector();
    }

    /// <summary>Its column and the edge that faces the strips, as <see cref="mixerInspLeft"/> says.</summary>
    void PlaceMixerInspector()
    {
        Grid.SetColumn(mixerInspPane, mixerInspLeft ? 0 : 3);
        mixerInspPane.BorderThickness = mixerInspLeft ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0);
        mixerInspEdge.HorizontalAlignment = mixerInspLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        mixerInspEdge.Margin = mixerInspLeft ? new Thickness(0, 0, -MixerInspGrab / 2, 0) : new Thickness(-MixerInspGrab / 2, 0, 0, 0);
    }

    /// <summary>Puts the inspector before the strips or at the master's right, and keeps the choice.</summary>
    void SetMixerInspSide(bool left)
    {
        if (left == mixerInspLeft) return;
        mixerInspLeft = left;
        SaveViewPref("mixer.inspSide", left ? "left" : "right");
        PlaceMixerInspector();
        ShowMixerView();
    }

    void SetMixerInspectorW(double w, bool save)
    {
        mixerInspectorW = Math.Round(Math.Clamp(w, MixerInspMinW, MixerInspMaxW));
        mixerInspPane.Width = mixerInspectorW;
        if (save) SaveViewPref("mixer.inspectorW", mixerInspectorW.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The header's "Inspector" switch.</summary>
    ToggleButton MixerInspectorSwitch()
    {
        var icon = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var b = new ToggleButton { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), Focusable = false,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { icon,
                new TextBlock { Text = Loc.T("mixer.inspector"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center } } } };
        Avalonia.Automation.AutomationProperties.SetName(b, Loc.T("mixer.inspector"));
        b.Click += (_, _) =>
        {
            SetMixerInspector(!mixerInspectorOn);
            RefreshMixer();
        };
        headerRefresh.Add(() =>
        {
            b.IsChecked = mixerInspectorOn;
            icon.Foreground = mixerInspectorOn ? Palette.Accent : Palette.Faint;
            ToolTip.SetTip(b, Loc.T(mixerInspectorOn ? "mixer.inspector.hide" : "mixer.inspector.show"));
        });
        return b;
    }

    /// <summary>Shows or hides the inspector and keeps the choice; the next refresh fills it.</summary>
    void SetMixerInspector(bool on)
    {
        if (on == mixerInspectorOn) return;
        mixerInspectorOn = on;
        SaveViewPref("mixer.inspector", on ? "on" : "off");
        mixerInspPane.IsVisible = on;
        if (!on) ClearMixerInspector();
    }

    void ClearMixerInspector()
    {
        ClearStrips(mixerInspLayout);
        mixerInspBody.Children.Clear();
        mixerInspId = null;
        mixerInspKey = "";
    }

    /// <summary>
    /// With the mixer's refresh: rebuilds the inspector when the selected channel or the strips changed (compared
    /// without allocating), then runs its refreshes.
    /// </summary>
    void RefreshMixerInspector()
    {
        mixerInspPane.IsVisible = mixerInspectorOn;
        if (!mixerInspectorOn) return;
        string? id = sel?.Track.Id ?? (SelectedFolder is { Separator: false } f ? f.Id : null) ?? selBusId;
        if (id != mixerInspId || !ReferenceEquals(mixerInspKey, mixerKey) || mixerInspBody.Children.Count == 0)
        {
            mixerInspId = id;
            mixerInspKey = mixerKey;
            BuildMixerInspector();
        }
        mixerInspBody.IsEnabled = store != null && !store.ReadOnly && !restoring;
        bool was = syncing;
        syncing = true;
        try { foreach (var r in mixerInspLayout.Refresh) r(); }
        finally { syncing = was; }
    }

    void BuildMixerInspector()
    {
        ClearStrips(mixerInspLayout);
        mixerInspBody.Children.Clear();
        var saved = mx;
        mx = mixerInspLayout;
        try
        {
            if (sel is { } t) InspectTrack(t);
            else if (SelectedFolder is { Separator: false } folder) InspectFolder(folder);
            else if (SelectedBus is { } bus) InspectBus(bus);
            else mixerInspBody.Children.Add(new TextBlock { Text = Loc.T("mixer.insp.none"), FontSize = 12, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap });
        }
        finally { mx = saved; }
    }

    void InspectTrack(LiveTrack t)
    {
        var badge = new TrackBadge { Width = 28, Height = 28, Radius = 5, VerticalAlignment = VerticalAlignment.Center };
        badge.Clicked += () => ShowTrackLook(t, badge);
        mx.Refresh.Add(() => badge.Set(t.Track, tracks.IndexOf(t) + 1, true, !AudioOnly));
        var name = InspName(() => t.Track.Name, () => RenameTrackIn(t, mixerInspBody));
        InspHead(badge, name, Loc.T(t.Track.IsMidi ? "mixer.insp.midi" : "mixer.insp.audio"));

        // where it records from: the same menu as the timeline header's chip
        var inputText = new TextBlock { Classes = { "mono" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var input = new Button { Classes = { "chip" }, Height = 24, MinHeight = 24, Padding = new Thickness(8, 0), Focusable = false, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Content = inputText };
        input.Click += (_, _) => ShowInputMenu(t, input);
        mx.Refresh.Add(() => inputText.Text = t.Track.IsMidi ? MidiInputShort(t.Track) : InputShort(t.Track));
        InspSection(Loc.T("mixer.insp.input"), input);
        var router = RouterOf(t);
        InspSection(Loc.T("route.output"), OutputSelect(router, mx.Refresh));
        InspChain(SlotsOf(t), router, () => t.Track, () => t.Channel.Pan, v => EditTrack(t, x => x.Channel.Pan = v, "pan"));
    }

    void InspectFolder(Folder folder)
    {
        string id = folder.Id;
        Folder F() => FolderById(id) ?? folder;
        var glyph = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 20, VerticalAlignment = VerticalAlignment.Center };
        mx.Refresh.Add(() => glyph.Foreground = Palette.Alpha(TimelineView.FolderColor(F()), F().Muted ? 0.5 : 1));
        var name = InspName(() => F().Name, null);
        InspHead(glyph, name, Loc.T("mixer.folderTag"));
        var router = RouterOf(folder);
        InspSection(Loc.T("route.output"), OutputSelect(router, mx.Refresh));
        InspChain(SlotsOf(folder), router, () => FolderById(id), () => F().Pan, v => FolderChannelEdit(id, x => x.Pan = v, "pan"));
    }

    void InspectBus(Bus bus)
    {
        string id = bus.Id;
        Bus B() => BusById(id) ?? bus;
        var badge = new TrackBadge { Width = 28, Height = 28, Radius = 5, VerticalAlignment = VerticalAlignment.Center };
        badge.Clicked += () => ShowBusLook(id, badge);
        mx.Refresh.Add(() => badge.SetBus(B().Color, B().Icon, true));
        var name = InspName(() => B().Name, () => RenameBus(id, mixerInspBody));
        InspHead(badge, name, Loc.T("mixer.busTag"));
        // a bus always goes to the master (the user's decision)
        InspSection(Loc.T("route.output"), new TextBlock { Text = Loc.T("route.master"), FontSize = 12, Foreground = Palette.Dim });
        InspChain(SlotsOf(bus), RouterOf(bus), () => BusById(id), () => B().Pan, v => BusEdit(id, b => b.Pan = v, "pan"));
    }

    /// <summary>The channel's name, as the inspector's title (16 SemiBold); a double click renames it when it can be.</summary>
    TextBlock InspName(Func<string> name, Action? rename)
    {
        var text = new TextBlock { FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
        if (rename != null) RenameOnDoubleClick(text, rename);
        mx.Refresh.Add(() => text.Text = name());
        return text;
    }

    /// <summary>The badge, the name and what kind of channel it is.</summary>
    void InspHead(Control badge, TextBlock name, string kind)
    {
        var text = new StackPanel { Spacing = 1, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children =
        {
            name,
            new TextBlock { Text = kind.ToUpperInvariant(), FontSize = 9, LetterSpacing = 0.8, Foreground = Palette.Faint },
        } };
        Grid.SetColumn(text, 1);
        mixerInspBody.Children.Add(new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { badge, text } });
    }

    /// <summary>A section: its label over what it holds.</summary>
    void InspSection(string title, Control body) =>
        mixerInspBody.Children.Add(new StackPanel { Spacing = 6, Children = { new TextBlock { Classes = { "label" }, Text = title.ToUpperInvariant() }, body } });

    /// <summary>The effects, the sends and the pan: the strips' own lists, at the inspector's width and whole.</summary>
    void InspChain(SlotOwner o, Router router, Func<IAutomated?> lanes, Func<double> panOf, Action<double> setPan)
    {
        var (fx, _) = SlotList(o);
        fx.Children.Add(ListAddRow(o.AddTip, CompactRowH, b => ShowAddSlotMenu(o, b)));
        mx.Refresh.Add(() => fx.Opacity = Audio.PlaybackEngine.FxBypass ? 0.4 : 1);
        InspSection(Loc.T("mix.list.fx"), fx);

        var sends = CompactSends(router);
        sends.Children.Add(ListAddRow(Loc.T("send.add.tip"), CompactRowH, b => OpenAnchoredMenu(Popup(SendChoices(router), 220), b)));
        InspSection(Loc.T("mix.list.sends"), sends);

        var pan = new HBar { Bipolar = true, ResetTo = 0, Height = 18 };
        pan.Changed += setPan;
        AutomateOn(pan, lanes, AutoTarget.Pan, k => { pan.Value = k * 2 - 1; pan.Readout = ValueFormat.PanShort(k * 2 - 1); });
        mx.Refresh.Add(() =>
        {
            double v = panOf();
            pan.Value = v;
            pan.Readout = ValueFormat.PanShort(v);
        });
        InspSection(Loc.T("insp.pan"), pan);
    }
}
