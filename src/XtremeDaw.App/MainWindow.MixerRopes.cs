using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Model;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The mixer's connections band (asked by the user, #264): under the strips, the selected channel's (track, folder or
/// bus) active sends as ropes to the buses they reach (<see cref="SendRopes"/>), and, when a bus is selected, the
/// sends that reach it, fainter. Each rope carries a knob at the other channel's end that sets that send's level right
/// there. Under every bus the selected channel can send to and has no rope to, an "Enviar" button makes that send at
/// once (on, shown, at the level it kept or <see cref="QuickSendLevel"/>).
/// Shown or hidden with the header's "Conexiones" switch (config.json "mixer.ropes"). It follows everything that moves
/// a rope (the selection, a send's level, the scroll, the zoom, a strip dragged elsewhere) by checking on the mixer's
/// tick whether any of it changed, without allocating; only a change rebuilds the ropes. A send dragged to 0 keeps
/// its rope (and its knob) until the knob is let go.
/// </summary>
public partial class MainWindow
{
    /// <summary>The band's height (px) by default and the least it can be dragged to; the most leaves this much of the mixer above it.</summary>
    const double RopesBandH = 120, RopesMinH = 70, RopesKeepAbove = 160;
    /// <summary>The grip along the band's top that drags its height (px).</summary>
    const double RopesGripH = 6;
    /// <summary>The most ropes drawn (out and in); the check's arrays are this long.</summary>
    const int RopesMax = 128;
    /// <summary>The level a send made with "Enviar" starts at when it had none (the templates' usual reverb send).</summary>
    const double QuickSendLevel = 0.25;
    /// <summary>The room an "Enviar" button is centred in under its bus (px).</summary>
    const double OfferW = 120;

    bool mixerRopes = AppConfig.Get("mixer.ropes") == "on";
    readonly SendRopes ropes = new();
    readonly Canvas ropesKnobs = new();
    readonly List<Knob> ropeKnobPool = [];
    readonly List<Panel> ropeOfferPool = [];
    readonly Border ropesBand = new() { Height = RopesSavedH(), Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 1, 0, 0), IsVisible = false };

    /// <summary>The ropes drawn now (<c>cur</c>) and the ones the tick just gathered (<c>next</c>), to see whether anything moved.</summary>
    sealed class RopeSet
    {
        public int Count, Offers;
        public bool Picked;
        /// <summary>The selected channel (a track, a folder or a bus), whose sends the "Enviar" buttons make.</summary>
        public object? Sel;
        public readonly string?[] OfferBus = new string?[RopesMax];
        public readonly double[] OfferX = new double[RopesMax];
        public readonly object?[] Owner = new object?[RopesMax];
        public readonly string?[] OwnerId = new string?[RopesMax], Bus = new string?[RopesMax];
        public readonly double[] Level = new double[RopesMax], FromX = new double[RopesMax], ToX = new double[RopesMax];
        public readonly bool[] Pre = new bool[RopesMax], In = new bool[RopesMax];
        public readonly Color[] Color = new Color[RopesMax];
    }

    readonly RopeSet ropesCur = new() { Count = -1 }, ropesNext = new();
    double ropesW = -1, ropesH = -1;
    readonly List<SendRopes.Rope> ropesList = [];
    // the send whose knob is being dragged: kept on screen even at 0 until it is let go
    string? ropeHeldOwner, ropeHeldBus;

    bool RopesShown => mixerRopes && Buses.Count > 0;

    /// <summary>The band's height as the user left it (config.json "mixer.ropesH").</summary>
    static double RopesSavedH() =>
        double.TryParse(AppConfig.Get("mixer.ropesH"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h) && double.IsFinite(h)
            ? Math.Max(RopesMinH, h) : RopesBandH;

    /// <summary>The header's "Conexiones" switch: the band under the strips, shown or hidden (kept in config.json).</summary>
    ToggleButton RopesSwitch()
    {
        var grip = new Border { Height = RopesGripH, Background = Brushes.Transparent, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeNorthSouth) };
        DockPanel.SetDock(grip, Dock.Top);
        double startY = 0, startH = 0;
        bool dragging = false;
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            dragging = true;
            startY = e.GetPosition(this).Y;
            startH = ropesBand.Height;
            e.Pointer.Capture(grip);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            double most = Math.Max(RopesMinH, (ropesBand.Parent is Control pc ? pc.Bounds.Height : 400) - RopesKeepAbove);
            ropesBand.Height = Math.Round(Math.Clamp(startH - (e.GetPosition(this).Y - startY), RopesMinH, most));
        };
        grip.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
            SaveViewPref("mixer.ropesH", ropesBand.Height.ToString(System.Globalization.CultureInfo.InvariantCulture));
        };
        grip.PointerCaptureLost += (_, _) => dragging = false;
        ropesBand.Child = new DockPanel { Children = { grip, new Panel { Children = { ropes, ropesKnobs } } } };
        var icon = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var b = new ToggleButton { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), Focusable = false,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { icon,
                new TextBlock { Text = Loc.T("mixer.ropes"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center } } } };
        Avalonia.Automation.AutomationProperties.SetName(b, Loc.T("mixer.ropes"));
        b.Click += (_, _) =>
        {
            mixerRopes = !mixerRopes;
            SaveViewPref("mixer.ropes", mixerRopes ? "on" : "off");
            ropesCur.Count = -1;
            RefreshMixer();
        };
        headerRefresh.Add(() =>
        {
            b.IsChecked = mixerRopes;
            icon.Foreground = mixerRopes ? Palette.Accent : Palette.Faint;
            ToolTip.SetTip(b, Loc.T(mixerRopes ? "mixer.ropes.hide" : "mixer.ropes.show"));
            ropesBand.IsVisible = RopesShown;
        });
        return b;
    }

    /// <summary>A card's centre in the band (px), or NaN when it isn't on screen (a track in a folder folded away).</summary>
    double RopeX(string id)
    {
        if (!mixerLayout.Cards.TryGetValue(id, out var card) || !card.IsEffectivelyVisible) return double.NaN;
        return card.TranslatePoint(new Point(card.Bounds.Width / 2, 0), ropes) is { } p ? p.X : double.NaN;
    }

    /// <summary>Whether a send gets a rope: on, shown and above 0, or the one whose knob is being dragged.</summary>
    bool RopeShows(string ownerId, Send s) =>
        s.On && !s.Hidden && (s.Level > 0 || (s.BusId == ropeHeldBus && ownerId == ropeHeldOwner));

    /// <summary>The send to <paramref name="bus"/> in a list, without the closure a Find would allocate.</summary>
    static Send? SendIn(List<Send> sends, string bus)
    {
        for (int i = 0; i < sends.Count; i++) if (sends[i].BusId == bus) return sends[i];
        return null;
    }

    static Color BusColor(Bus b) => TrackIcons.ColorOf(b.Color) ?? Palette.AccentColor;

    void AddRope(RopeSet set, object owner, string ownerId, Send s, double fromX, double toX, bool incoming, Color c)
    {
        if (set.Count >= RopesMax || double.IsNaN(fromX) || double.IsNaN(toX)) return;
        int i = set.Count++;
        set.Owner[i] = owner; set.OwnerId[i] = ownerId; set.Bus[i] = s.BusId;
        set.Level[i] = s.Level; set.Pre[i] = s.Pre; set.FromX[i] = fromX; set.ToX[i] = toX; set.In[i] = incoming; set.Color[i] = c;
    }

    /// <summary>The selected channel's sends; each rope takes the color of the bus it reaches (asked by the user, #264).</summary>
    void Outgoing(RopeSet set, object owner, string id, List<Send> sends, double fromX)
    {
        for (int i = 0; i < sends.Count; i++)
        {
            var s = sends[i];
            if (!RopeShows(id, s) || BusAt(s.BusId) is not { } to) continue;
            AddRope(set, owner, id, s, fromX, RopeX(s.BusId), false, BusColor(to));
        }
    }

    /// <summary>The sends that reach the selected bus, all in its color: it is their destination.</summary>
    void Incoming(RopeSet set, Bus target, double toX)
    {
        string bus = target.Id;
        var c = BusColor(target);
        foreach (var t in tracks)
            if (SendIn(t.Channel.Sends, bus) is { } s && RopeShows(t.Track.Id, s))
                AddRope(set, t, t.Track.Id, s, RopeX(t.Track.Id), toX, true, c);
        var folders = Folders;
        for (int i = 0; i < folders.Count; i++)
            if (SendIn(folders[i].Sends, bus) is { } s && RopeShows(folders[i].Id, s))
                AddRope(set, folders[i], folders[i].Id, s, RopeX(folders[i].Id), toX, true, c);
        var buses = Buses;
        for (int i = 0; i < buses.Count; i++)
            if (buses[i].Id != bus && SendIn(buses[i].Sends, bus) is { } s && RopeShows(buses[i].Id, s))
                AddRope(set, buses[i], buses[i].Id, s, RopeX(buses[i].Id), toX, true, c);
    }

    /// <summary>The bus with that id, without the closure <see cref="BusById"/> allocates (this runs on every tick).</summary>
    Bus? BusAt(string id)
    {
        var buses = Buses;
        for (int i = 0; i < buses.Count; i++) if (buses[i].Id == id) return buses[i];
        return null;
    }

    /// <summary>
    /// Whether the bus <paramref name="from"/>'s sound reaches the bus <paramref name="to"/> through its output and shown
    /// sends, as <see cref="ProjectBuses.CanFeed"/> checks it, without allocating; <paramref name="depth"/> bounds the walk.
    /// </summary>
    bool BusReaches(string from, string to, int depth)
    {
        if (from == to) return true;
        if (depth <= 0 || BusAt(from) is not { } b) return false;
        if (b.Output is { } o && BusReaches(o, to, depth - 1)) return true;
        var sends = b.Sends;
        for (int i = 0; i < sends.Count; i++)
            if (!sends[i].Hidden && BusReaches(sends[i].BusId, to, depth - 1)) return true;
        return false;
    }

    /// <summary>An "Enviar" button under every bus the selected channel (a bus: <paramref name="selBus"/>) can feed and has no rope to yet.</summary>
    void Offers(RopeSet set, string? selBus)
    {
        var buses = Buses;
        for (int i = 0; i < buses.Count && set.Offers < RopesMax; i++)
        {
            string id = buses[i].Id;
            if (selBus != null && BusReaches(id, selBus, buses.Count + 1)) continue;
            bool roped = false;
            for (int r = 0; r < set.Count && !roped; r++) roped = !set.In[r] && set.Bus[r] == id;
            if (roped) continue;
            double x = RopeX(id);
            if (double.IsNaN(x)) continue;
            set.OfferBus[set.Offers] = id;
            set.OfferX[set.Offers++] = x;
        }
    }

    /// <summary>Gathers what the band would draw now: the selected channel's sends and, for a bus, the ones that reach it.</summary>
    void GatherRopes(RopeSet set)
    {
        set.Count = set.Offers = 0;
        set.Picked = false;
        set.Sel = null;
        if (SelectedFolder is { } f)
        {
            double x = RopeX(f.Id);
            if (double.IsNaN(x)) return;
            set.Picked = true;
            set.Sel = f;
            Outgoing(set, f, f.Id, f.Sends, x);
            Offers(set, null);
        }
        else if (SelectedBus is { } b)
        {
            double x = RopeX(b.Id);
            if (double.IsNaN(x)) return;
            set.Picked = true;
            set.Sel = b;
            Outgoing(set, b, b.Id, b.Sends, x);
            Incoming(set, b, x);
            Offers(set, b.Id);
        }
        else if (sel != null)
        {
            double x = RopeX(sel.Track.Id);
            if (double.IsNaN(x)) return;
            set.Picked = true;
            set.Sel = sel;
            Outgoing(set, sel, sel.Track.Id, sel.Channel.Sends, x);
            Offers(set, null);
        }
    }

    static bool SameX(double a, double b) => double.IsNaN(a) ? double.IsNaN(b) : Math.Abs(a - b) < 0.5;

    static bool SameRopes(RopeSet a, RopeSet b)
    {
        if (a.Count != b.Count || a.Picked != b.Picked || a.Offers != b.Offers || !ReferenceEquals(a.Sel, b.Sel)) return false;
        for (int i = 0; i < a.Offers; i++)
            if (a.OfferBus[i] != b.OfferBus[i] || !SameX(a.OfferX[i], b.OfferX[i])) return false;
        for (int i = 0; i < a.Count; i++)
            if (!ReferenceEquals(a.Owner[i], b.Owner[i]) || a.Bus[i] != b.Bus[i] || a.Level[i] != b.Level[i] || a.Pre[i] != b.Pre[i]
                || a.In[i] != b.In[i] || a.Color[i] != b.Color[i] || !SameX(a.FromX[i], b.FromX[i]) || !SameX(a.ToX[i], b.ToX[i]))
                return false;
        return true;
    }

    bool RopeKnobDragging()
    {
        foreach (var k in ropeKnobPool) if (k.Dragging) return true;
        return false;
    }

    /// <summary>On the mixer's tick: rebuilds the ropes when the selection, a send or a strip's place changed. Allocates nothing otherwise.</summary>
    void TickRopes()
    {
        bool shown = RopesShown;
        if (ropesBand.IsVisible != shown) ropesBand.IsVisible = shown;
        if (!shown) return;
        if (ropeHeldBus != null && !RopeKnobDragging()) ropeHeldBus = ropeHeldOwner = null;
        GatherRopes(ropesNext);
        if (ropes.Bounds.Width == ropesW && ropes.Bounds.Height == ropesH && SameRopes(ropesNext, ropesCur)) return;
        BuildRopes();
    }

    void BuildRopes()
    {
        var a = ropesNext; var c = ropesCur;
        c.Count = a.Count; c.Picked = a.Picked; c.Offers = a.Offers; c.Sel = a.Sel;
        Array.Copy(a.OfferBus, c.OfferBus, a.Offers); Array.Copy(a.OfferX, c.OfferX, a.Offers);
        Array.Copy(a.Owner, c.Owner, a.Count); Array.Copy(a.OwnerId, c.OwnerId, a.Count); Array.Copy(a.Bus, c.Bus, a.Count);
        Array.Copy(a.Level, c.Level, a.Count); Array.Copy(a.Pre, c.Pre, a.Count); Array.Copy(a.FromX, c.FromX, a.Count);
        Array.Copy(a.ToX, c.ToX, a.Count); Array.Copy(a.In, c.In, a.Count); Array.Copy(a.Color, c.Color, a.Count);
        ropesW = ropes.Bounds.Width;
        ropesH = ropes.Bounds.Height;
        ropesList.Clear();
        for (int i = 0; i < c.Count; i++) ropesList.Add(new SendRopes.Rope(c.FromX[i], c.ToX[i], c.Level[i], c.Pre[i], c.In[i], c.Color[i]));
        ropes.Set(ropesList, !c.Picked ? Loc.T("mixer.ropes.pick") : c.Offers > 0 ? "" : Loc.T("mixer.ropes.none"));

        // a knob per rope, at its lowest point; the pool only grows, so a knob being dragged stays the same control
        while (ropeKnobPool.Count < c.Count)
        {
            int index = ropeKnobPool.Count;
            var k = new Knob(SendRopes.KnobSizeFor(ropesH), Loc.T("mix.list.sends")) { Min = 0, Max = 1, ResetTo = 0 };
            k.Changed += v => RopeEdit(index, v);
            ropeKnobPool.Add(k);
            ropesKnobs.Children.Add(k);
        }
        double h = ropesH, size = SendRopes.KnobSizeFor(h);
        ropes.Layout(h);
        for (int i = 0; i < ropeKnobPool.Count; i++)
        {
            var k = ropeKnobPool[i];
            bool on = i < c.Count;
            if (k.IsVisible != on) k.IsVisible = on;
            if (!on) continue;
            var at = ropes.KnobCenter(i);
            k.Resize(size);
            Canvas.SetLeft(k, at.X - size / 2);
            Canvas.SetTop(k, at.Y - size / 2);
            k.Tint = c.Color[i];
            k.Opacity = c.In[i] ? 0.7 : 1;
            k.Value = c.Level[i];
        }

        // the "Enviar" buttons, on the knobs' row under each bus left to send to
        while (ropeOfferPool.Count < c.Offers)
        {
            int index = ropeOfferPool.Count;
            // a pill with the accent "+" in a dot: reads as "plug a send here" next to the knobs, without shouting like an on chip
            var plus = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = Palette.Accent,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 9, Foreground = Palette.App,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            var btn = new Button { Classes = { "chip" }, Height = 24, MinHeight = 24, Padding = new Thickness(4, 0, 11, 0), Focusable = false,
                CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Center,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { plus,
                    new TextBlock { Text = Loc.T("mixer.ropes.send"), FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } } } };
            Avalonia.Automation.AutomationProperties.SetName(btn, Loc.T("mixer.ropes.send"));
            btn.Click += (_, _) => RopeOffer(index);
            var room = new Panel { Width = OfferW, Children = { btn } };
            ropeOfferPool.Add(room);
            ropesKnobs.Children.Add(room);
        }
        double offerY = 4 + size / 2 - 12;
        for (int i = 0; i < ropeOfferPool.Count; i++)
        {
            var room = ropeOfferPool[i];
            bool on = i < c.Offers;
            if (room.IsVisible != on) room.IsVisible = on;
            if (!on) continue;
            Canvas.SetLeft(room, c.OfferX[i] - OfferW / 2);
            Canvas.SetTop(room, offerY);
        }
    }

    /// <summary>Whose sends a rope's knob or an "Enviar" button edits, as it is now.</summary>
    Router? RopeRouter(object? owner) => owner switch
    {
        LiveTrack t => RouterOf(t),
        Folder f => FolderById(f.Id) is { } nf ? RouterOf(nf) : null,
        Bus b => BusById(b.Id) is { } nb ? RouterOf(nb) : null,
        _ => null,
    };

    /// <summary>"Enviar": the selected channel's send to that bus, shown and on, at the level it kept or <see cref="QuickSendLevel"/>.</summary>
    void RopeOffer(int i)
    {
        if (i >= ropesCur.Offers || ropesCur.OfferBus[i] is not { } bus || RopeRouter(ropesCur.Sel) is not { } r) return;
        r.Edit((p, x) =>
        {
            double kept = ProjectBuses.SendOf(x, bus).Level;
            if (ProjectBuses.SetSend(p, x, bus, kept > 0 ? null : QuickSendLevel, on: true) is not { } s) return false;
            s.Hidden = false;
            return true;
        }, $"send.{bus}.add");
    }

    /// <summary>A rope's knob moved: that send's level, as its row in the strip would set it (one undo step per send).</summary>
    void RopeEdit(int i, double v)
    {
        if (i >= ropesCur.Count || ropesCur.Bus[i] is not { } bus || ropesCur.OwnerId[i] is not { } ownerId) return;
        if (RopeRouter(ropesCur.Owner[i]) is not { } r) return;
        ropeHeldOwner = ownerId;
        ropeHeldBus = bus;
        r.Edit((p, x) => ProjectBuses.SetSend(p, x, bus, v, v > 0 ? true : null) != null, $"send.{bus}.level");
    }
}
