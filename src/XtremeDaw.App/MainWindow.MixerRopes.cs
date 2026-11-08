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
/// tick whether any of it changed, without allocating; only a change rebuilds the ropes. A send at 0 keeps its rope
/// (thin, with its knob): it is still connected (asked by the user, #292).
/// The channel's real output is a rope too (asked by the user, #301), drawn apart from the sends (solid and neutral,
/// without a knob): to its folder, its bus or the master, whose strip the band reaches under. A bus or a folder shows the
/// outputs that arrive at it. With nothing selected the band draws nothing but its hint (#301: too much clutter).
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
    /// <summary>The room an "Enviar" pill needs, with a gap to the next (px); closer buses get the "+" alone, in <see cref="OfferDotW"/>.</summary>
    const double OfferPillW = 92, OfferDotW = 30;

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
        /// <summary>The "Enviar" buttons show only their "+": the buses stand too close for the pill.</summary>
        public bool Compact;
        /// <summary>Each offered bus's dock, visible part (px in the band), while <see cref="Offers"/> places the buttons.</summary>
        public readonly double[] OfferLo = new double[RopesMax], OfferHi = new double[RopesMax];
        /// <summary>The selected channel (a track, a folder or a bus), whose sends the "Enviar" buttons make.</summary>
        public object? Sel;
        public readonly string?[] OfferBus = new string?[RopesMax];
        public readonly double[] OfferX = new double[RopesMax];
        public readonly object?[] Owner = new object?[RopesMax];
        public readonly string?[] OwnerId = new string?[RopesMax], Bus = new string?[RopesMax];
        public readonly double[] Level = new double[RopesMax], FromX = new double[RopesMax], ToX = new double[RopesMax];
        public readonly bool[] Pre = new bool[RopesMax], In = new bool[RopesMax], Off = new bool[RopesMax];
        /// <summary>The rope is the channel's output, not a send: no knob, drawn apart (#301).</summary>
        public readonly bool[] Main = new bool[RopesMax];
        public readonly Color[] Color = new Color[RopesMax];
    }

    readonly RopeSet ropesCur = new() { Count = -1 }, ropesNext = new();
    /// <summary>The master's strip, where the outputs to the master arrive (#301).</summary>
    Control? ropesMasterCard;
    /// <summary>The id an output to the master carries in <see cref="RopeSet.Bus"/>.</summary>
    const string MasterRopeId = "master";
    double ropesW = -1, ropesH = -1;
    readonly List<SendRopes.Rope> ropesList = [];

    bool RopesShown => mixerRopes;

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
        ropesBand.Child = new DockPanel { Children = { grip, new Panel { ClipToBounds = true, Children = { ropes, ropesKnobs } } } };
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
        return card.TranslatePoint(new Point(card.Bounds.Width / 2, 0), ropes) is { } p ? ClampToSide(card, p.X) : double.NaN;
    }

    /// <summary>A rope end's card centre, as <see cref="RopeX(string)"/>; <paramref name="off"/> says it is scrolled out of its dock and was held at the edge (#290).</summary>
    double RopeX(string id, out bool off)
    {
        off = false;
        if (!mixerLayout.Cards.TryGetValue(id, out var card) || !card.IsEffectivelyVisible) return double.NaN;
        if (card.TranslatePoint(new Point(card.Bounds.Width / 2, 0), ropes) is not { } p) return double.NaN;
        double x = ClampToSide(card, p.X);
        off = Math.Abs(x - p.X) > 0.5;
        return x;
    }

    /// <summary>The master strip's centre in the band (px), or NaN while it isn't laid out.</summary>
    double MasterRopeX() =>
        ropesMasterCard is { IsEffectivelyVisible: true } m && m.TranslatePoint(new Point(m.Bounds.Width / 2, 0), ropes) is { } p ? p.X : double.NaN;

    /// <summary>The folder with that id (not a separator), without the closure <see cref="FolderById"/> allocates (this runs on every tick).</summary>
    Folder? FolderAt(string? id)
    {
        if (id == null) return null;
        var folders = Folders;
        for (int i = 0; i < folders.Count; i++) if (folders[i].Id == id && !folders[i].Separator) return folders[i];
        return null;
    }

    /// <summary>Where a channel's sound goes: its folder (a track in one), its bus, or null for the master.</summary>
    string? OutputOf(string? folderId, string? output) =>
        FolderAt(folderId)?.Id ?? (output != null && BusAt(output) != null ? output : null);

    /// <summary>
    /// The channel's output rope (#301): to its folder (a track in one), its bus or the master. Neutral and without a
    /// knob, so it never reads as a send.
    /// </summary>
    void OutputRope(RopeSet set, object owner, string id, string? folderId, string? output, double fromX)
    {
        bool off = false;
        string? to = OutputOf(folderId, output);
        double toX = to != null ? RopeX(to, out off) : MasterRopeX();
        AddRope(set, owner, id, to ?? MasterRopeId, 1, false, fromX, toX, false, OutputColor, off, true);
    }

    /// <summary>The outputs that arrive at <paramref name="target"/> (a bus or folder id, or <see cref="MasterRopeId"/>), at <paramref name="toX"/>.</summary>
    void IncomingOutputs(RopeSet set, string target, double toX)
    {
        string? want = target == MasterRopeId ? null : target;
        var c = OutputColor;
        foreach (var t in tracks)
            if (OutputOf(t.Track.FolderId, t.Channel.Output) == want)
            {
                double x = RopeX(t.Track.Id, out bool off);
                AddRope(set, t, t.Track.Id, target, 1, false, x, toX, true, c, off, true);
            }
        var folders = Folders;
        for (int i = 0; i < folders.Count; i++)
        {
            var f = folders[i];
            if (f.Separator || f.Id == target || OutputOf(null, f.Output) != want) continue;
            double x = RopeX(f.Id, out bool off);
            AddRope(set, f, f.Id, target, 1, false, x, toX, true, c, off, true);
        }
        var buses = Buses;
        for (int i = 0; i < buses.Count; i++)
        {
            var b = buses[i];
            if (b.Id == target || OutputOf(null, b.Output) != want) continue;
            double x = RopeX(b.Id, out bool off);
            AddRope(set, b, b.Id, target, 1, false, x, toX, true, c, off, true);
        }
    }

    /// <summary>The outputs' color: the text's, not a bus's, so they never read as a send.</summary>
    static Color OutputColor => Palette.FgColor;

    /// <summary>Whether a send gets a rope: on and shown, at any level (a send at 0 is still a connection, #292).</summary>
    static bool RopeShows(Send s) => s.On && !s.Hidden;

    /// <summary>The send to <paramref name="bus"/> in a list, without the closure a Find would allocate.</summary>
    static Send? SendIn(List<Send> sends, string bus)
    {
        for (int i = 0; i < sends.Count; i++) if (sends[i].BusId == bus) return sends[i];
        return null;
    }

    static Color BusColor(Bus b) => TrackIcons.ColorOf(b.Color) ?? Palette.AccentColor;

    void AddRope(RopeSet set, object owner, string ownerId, Send s, double fromX, double toX, bool incoming, Color c, bool off) =>
        AddRope(set, owner, ownerId, s.BusId, s.Level, s.Pre, fromX, toX, incoming, c, off, false);

    void AddRope(RopeSet set, object owner, string ownerId, string to, double level, bool pre, double fromX, double toX, bool incoming, Color c, bool off, bool main)
    {
        if (set.Count >= RopesMax || double.IsNaN(fromX) || double.IsNaN(toX)) return;
        int i = set.Count++;
        set.Owner[i] = owner; set.OwnerId[i] = ownerId; set.Bus[i] = to; set.Main[i] = main;
        set.Level[i] = level; set.Pre[i] = pre; set.FromX[i] = fromX; set.ToX[i] = toX; set.In[i] = incoming; set.Color[i] = c; set.Off[i] = off;
    }

    /// <summary>The selected channel's sends; each rope takes the color of the bus it reaches (asked by the user, #264).</summary>
    void Outgoing(RopeSet set, object owner, string id, List<Send> sends, double fromX)
    {
        for (int i = 0; i < sends.Count; i++)
        {
            var s = sends[i];
            if (!RopeShows(s) || BusAt(s.BusId) is not { } to) continue;
            double toX = RopeX(s.BusId, out bool off);
            AddRope(set, owner, id, s, fromX, toX, false, BusColor(to), off);
        }
    }

    /// <summary>The sends that reach the selected bus, all in its color: it is their destination.</summary>
    void Incoming(RopeSet set, Bus target, double toX)
    {
        string bus = target.Id;
        var c = BusColor(target);
        foreach (var t in tracks)
            if (SendIn(t.Channel.Sends, bus) is { } s && RopeShows(s))
            {
                double x = RopeX(t.Track.Id, out bool off);
                AddRope(set, t, t.Track.Id, s, x, toX, true, c, off);
            }
        var folders = Folders;
        for (int i = 0; i < folders.Count; i++)
            if (SendIn(folders[i].Sends, bus) is { } s && RopeShows(s))
            {
                double x = RopeX(folders[i].Id, out bool off);
                AddRope(set, folders[i], folders[i].Id, s, x, toX, true, c, off);
            }
        var buses = Buses;
        for (int i = 0; i < buses.Count; i++)
            if (buses[i].Id != bus && SendIn(buses[i].Sends, bus) is { } s && RopeShows(s))
            {
                double x = RopeX(buses[i].Id, out bool off);
                AddRope(set, buses[i], buses[i].Id, s, x, toX, true, c, off);
            }
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

    /// <summary>
    /// An "Enviar" button under every bus the selected channel (a bus: <paramref name="selBus"/>) can feed and has no rope
    /// to yet. Only a bus whose centre shows in its own dock gets one, held inside that dock; when the buses stand closer
    /// than the pill, every button shrinks to its "+" (<see cref="RopeSet.Compact"/>), and one that would still touch the
    /// one before it is left out: buttons never overlap, however narrow the dock is dragged (#275). The bus the channel's
    /// output already goes to (<paramref name="output"/>) gets none (#301, <see cref="ProjectBuses.CanFeed"/>).
    /// </summary>
    void Offers(RopeSet set, string? selBus, string? output = null)
    {
        var buses = Buses;
        int n = 0;
        for (int i = 0; i < buses.Count && n < RopesMax; i++)
        {
            string id = buses[i].Id;
            if (id == output || selBus != null && BusReaches(id, selBus, buses.Count + 1)) continue;
            bool roped = false;
            for (int r = 0; r < set.Count && !roped; r++) roped = !set.In[r] && !set.Main[r] && set.Bus[r] == id;
            if (roped) continue;
            if (!mixerLayout.Cards.TryGetValue(id, out var card) || !card.IsEffectivelyVisible || !SideSpan(card, out double lo, out double hi)) continue;
            if (card.TranslatePoint(new Point(card.Bounds.Width / 2, 0), ropes) is not { } p || p.X < lo || p.X > hi) continue;
            set.OfferBus[n] = id;
            set.OfferX[n] = p.X;
            set.OfferLo[n] = lo;
            set.OfferHi[n++] = hi;
        }
        bool compact = false;
        for (int i = 0; i < n && !compact; i++)
        {
            compact = set.OfferHi[i] - set.OfferLo[i] < OfferPillW;
            for (int j = i + 1; j < n && !compact; j++) compact = Math.Abs(set.OfferX[i] - set.OfferX[j]) < OfferPillW;
        }
        set.Compact = compact;
        double room = compact ? OfferDotW : OfferPillW, half = room / 2;
        for (int i = 0; i < n; i++)
        {
            double lo = set.OfferLo[i], hi = set.OfferHi[i];
            double x = hi - lo >= room ? Math.Clamp(set.OfferX[i], lo + half, hi - half) : (lo + hi) / 2;
            bool clear = true;
            for (int k = 0; k < set.Offers && clear; k++) clear = Math.Abs(set.OfferX[k] - x) >= room;
            if (!clear) continue;
            set.OfferBus[set.Offers] = set.OfferBus[i];
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
            OutputRope(set, f, f.Id, null, f.Output, x);
            Outgoing(set, f, f.Id, f.Sends, x);
            IncomingOutputs(set, f.Id, x);
            Offers(set, null, f.Output);
        }
        else if (SelectedBus is { } b)
        {
            double x = RopeX(b.Id);
            if (double.IsNaN(x)) return;
            set.Picked = true;
            set.Sel = b;
            OutputRope(set, b, b.Id, null, b.Output, x);
            Outgoing(set, b, b.Id, b.Sends, x);
            IncomingOutputs(set, b.Id, x);
            Incoming(set, b, x);
            Offers(set, b.Id);
        }
        else if (sel != null)
        {
            double x = RopeX(sel.Track.Id);
            if (double.IsNaN(x)) return;
            set.Picked = true;
            set.Sel = sel;
            OutputRope(set, sel, sel.Track.Id, sel.Track.FolderId, sel.Channel.Output, x);
            Outgoing(set, sel, sel.Track.Id, sel.Channel.Sends, x);
            Offers(set, null, sel.Track.FolderId == null ? sel.Channel.Output : null);
        }
        // nothing selected: nothing drawn, only the hint (#301: every output to the master was too much clutter)
    }

    static bool SameX(double a, double b) => double.IsNaN(a) ? double.IsNaN(b) : Math.Abs(a - b) < 0.5;

    static bool SameRopes(RopeSet a, RopeSet b)
    {
        if (a.Count != b.Count || a.Picked != b.Picked || a.Offers != b.Offers || a.Compact != b.Compact || !ReferenceEquals(a.Sel, b.Sel)) return false;
        for (int i = 0; i < a.Offers; i++)
            if (a.OfferBus[i] != b.OfferBus[i] || !SameX(a.OfferX[i], b.OfferX[i])) return false;
        for (int i = 0; i < a.Count; i++)
            if (!ReferenceEquals(a.Owner[i], b.Owner[i]) || a.Bus[i] != b.Bus[i] || a.Level[i] != b.Level[i] || a.Pre[i] != b.Pre[i]
                || a.In[i] != b.In[i] || a.Main[i] != b.Main[i] || a.Off[i] != b.Off[i] || a.Color[i] != b.Color[i] || !SameX(a.FromX[i], b.FromX[i]) || !SameX(a.ToX[i], b.ToX[i]))
                return false;
        return true;
    }

    /// <summary>On the mixer's tick: rebuilds the ropes when the selection, a send or a strip's place changed. Allocates nothing otherwise.</summary>
    void TickRopes()
    {
        bool shown = RopesShown;
        if (ropesBand.IsVisible != shown) ropesBand.IsVisible = shown;
        if (!shown) return;
        GatherRopes(ropesNext);
        if (ropes.Bounds.Width == ropesW && ropes.Bounds.Height == ropesH && SameRopes(ropesNext, ropesCur)) return;
        BuildRopes();
    }

    void BuildRopes()
    {
        var a = ropesNext; var c = ropesCur;
        c.Count = a.Count; c.Picked = a.Picked; c.Offers = a.Offers; c.Sel = a.Sel; c.Compact = a.Compact;
        Array.Copy(a.OfferBus, c.OfferBus, a.Offers); Array.Copy(a.OfferX, c.OfferX, a.Offers);
        Array.Copy(a.Owner, c.Owner, a.Count); Array.Copy(a.OwnerId, c.OwnerId, a.Count); Array.Copy(a.Bus, c.Bus, a.Count);
        Array.Copy(a.Level, c.Level, a.Count); Array.Copy(a.Pre, c.Pre, a.Count); Array.Copy(a.FromX, c.FromX, a.Count);
        Array.Copy(a.ToX, c.ToX, a.Count); Array.Copy(a.In, c.In, a.Count); Array.Copy(a.Color, c.Color, a.Count); Array.Copy(a.Off, c.Off, a.Count); Array.Copy(a.Main, c.Main, a.Count);
        ropesW = ropes.Bounds.Width;
        ropesH = ropes.Bounds.Height;
        ropesList.Clear();
        for (int i = 0; i < c.Count; i++) ropesList.Add(new SendRopes.Rope(c.FromX[i], c.ToX[i], c.Level[i], c.Pre[i], c.In[i], c.Color[i], c.Off[i], c.Main[i]));
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
        // the knobs shrink to fit between their neighbours (#290): the size comes from the layout, not the band alone
        double h = ropesH;
        ropes.Layout(h);
        double size = ropes.KnobSize;
        for (int i = 0; i < ropeKnobPool.Count; i++)
        {
            var k = ropeKnobPool[i];
            // a rope to a channel scrolled out of view has no knob: it stops at its dock's edge
            bool on = i < c.Count && !c.Off[i] && !c.Main[i];
            if (k.IsVisible != on) k.IsVisible = on;
            if (!on) continue;
            var at = ropes.KnobCenter(i);
            // the level beside the knob only shows when it fits; otherwise the knob says it on hover
            ToolTip.SetTip(k, ropes.ShowsLevel(i) ? null : ValueFormat.Percent(c.Level[i]) + (c.Pre[i] ? " PRE" : ""));
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
            if (room.Children[0] is Button { Content: StackPanel { Children: [_, TextBlock label] } } btn && label.IsVisible == c.Compact)
            {
                label.IsVisible = !c.Compact;
                btn.Padding = c.Compact ? new Thickness(3, 0) : new Thickness(4, 0, 11, 0);
            }
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
        if (i >= ropesCur.Count || ropesCur.Main[i] || ropesCur.Bus[i] is not { } bus) return;
        if (RopeRouter(ropesCur.Owner[i]) is not { } r) return;
        r.Edit((p, x) => ProjectBuses.SetSend(p, x, bus, v, v > 0 ? true : null) != null, $"send.{bus}.level");
    }
}
