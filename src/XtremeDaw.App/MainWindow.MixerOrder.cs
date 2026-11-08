using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using XtremeDaw.App.Controls;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The mixer's order, edited from the mixer itself (asked by the user, to keep big projects tidy): every strip sits on
/// the left dock, the middle or the right dock (<see cref="MixerSides"/>, #277), and a strip is dragged by its card to another place, dock or
/// folder. Tracks, folders and separators move in the same order the timeline shows (<see cref="TrackFolders"/>, one
/// <see cref="EditTimeline"/> with its undo), so the timeline follows and the other way round; buses move in their own
/// list (<see cref="ProjectBuses"/>) and land anywhere, between the channels too, following the strip before them
/// (<see cref="Bus.MixerAfter"/>, #270). Separators (<see cref="Folder.Separator"/>) are
/// narrow named dividers, here and in the timeline unless hidden there (and they start hidden there). While dragging, the
/// others glide out of the way like every reorderable list (<see cref="SlotDrag"/>); nothing changes until it is dropped.
/// What follows the pointer is a picture of the strip on a layer over the docks (#280), so it shows across them.
/// </summary>
public partial class MainWindow
{
    /// <summary>A separator's strip width (px): a divider, not a channel.</summary>
    const double SeparatorW = 30;
    /// <summary>Px the pointer has to move before a press on a card becomes a drag.</summary>
    const double MixerDragStart = 6;
    /// <summary>Px from the mixer's edge where a drag scrolls it, and how far per move.</summary>
    const double MixerDragEdge = 40, MixerDragScroll = 24;

    /// <summary>What a layout knows about its order: the top-level columns per side, the tracks inside open folders, and the marker.</summary>
    sealed class MixerOrder
    {
        /// <summary>Each top-level column with what it holds: a root <see cref="Track"/>, a <see cref="Folder"/> (or separator) or a <see cref="Bus"/>.</summary>
        public readonly List<(Control Column, object Item, int Side)> Top = [];
        /// <summary>The track strips inside open folders, in order.</summary>
        public readonly List<(Control Card, Track Track, string FolderId)> Inner = [];
        /// <summary>Each open folder's own strip, where its first track's place begins.</summary>
        public readonly Dictionary<string, Control> FolderStrips = [];
        /// <summary>Each dock's row of strips, by <see cref="MixerSides"/> index.</summary>
        public readonly StackPanel?[] Sides = new StackPanel?[2];
    }

    /// <summary>
    /// A place a dragged strip can land: its x (a boundary between strips, margins included), the side, the folder (null
    /// = top level), the tracks, folders or separators around it (<paramref name="Prev"/>, <paramref name="Next"/>: buses
    /// and the dragged item skipped), the strips right beside it (<paramref name="Before"/>, <paramref name="After"/>:
    /// buses too), and the buses between it and <paramref name="Next"/>, which follow what lands there (#270).
    /// </summary>
    readonly record struct MixerSlot(double X, int Side, string? Folder, object? Prev, object? Next, object? Before = null, object? After = null,
        Bus[]? Trailing = null);

    /// <summary>
    /// A strip that makes room while another is dragged: its bounds (margins included), its own shift, and the leaf it
    /// sits in (an open folder's column for its strips, -1 at the top), whose shift it already carries.
    /// </summary>
    readonly record struct MixerLeaf(Control Card, double L, double R, int Side, TranslateTransform Shift, int Parent);

    /// <summary>The drag in progress: the card pressed, what it moves, where it was pressed, and where it would land.</summary>
    Control? mixerDragCard;
    object? mixerDragItem;
    Point mixerDragFrom;
    bool mixerDragging, mixerSettling;
    MixerSlot? mixerDrop;
    /// <summary>What moves with the pointer (the column, the whole folder or a folder's track), its bounds and side when lifted.</summary>
    Control? mixerDragged;
    double mixerDragL, mixerDragW;
    int mixerDragSide;
    readonly TranslateTransform mixerDragShift = new();
    double mixerDragTarget;
    /// <summary>The others, the slots and where each one glides to, all measured once when the drag starts.</summary>
    readonly List<MixerLeaf> mixerLeaves = [];
    readonly List<MixerSlot> mixerSlots = [];
    double[] mixerTargets = [];
    /// <summary>The dragged control and its parents, raised over their neighbours while it moves.</summary>
    readonly List<Control> mixerRaised = [];
    DispatcherTimer? mixerGlide;
    /// <summary>
    /// What the pointer carries (#280): a picture of the lifted strip over every dock, since each dock scrolls on its own
    /// and clips what leaves it; the strip itself stays hidden in its place. <see cref="mixerGhostL"/> is where it was
    /// taken (the layout's coordinates, margins included), what <see cref="mixerDragShift"/> moves it from.
    /// </summary>
    Canvas? mixerGhostLayer;
    RenderTargetBitmap? mixerGhostShot;
    double mixerGhostL;
    /// <summary>Share of the remaining distance the dropped strip covers per frame into its room: quicker than making room, so the drop doesn't lag.</summary>
    const double MixerSettleEase = 0.5;

    /// <summary>
    /// Makes <paramref name="card"/> draggable: a press and a move of a few px lifts it, and from there it slides like
    /// every reorderable list in the project (<see cref="SlotDrag"/>, the timeline's tracks): the card follows the pointer,
    /// the others glide out of its way, and on release it glides into its room before the order changes. A click stays a
    /// click, selected or not.
    /// </summary>
    void MixerDraggable(Control card, object item)
    {
        card.PointerPressed += (_, e) =>
        {
            if (store?.ReadOnly != false || restoring || mixerSettling || !e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
            mixerDragCard = card;
            mixerDragItem = item;
            mixerDragFrom = e.GetPosition(mx.Strips);
            mixerDragging = false;
            e.Pointer.Capture(card);
        };
        card.PointerMoved += (_, e) =>
        {
            if (mixerDragCard != card || mixerSettling) return;
            var at = e.GetPosition(mx.Strips);
            if (!mixerDragging)
            {
                if (Math.Abs(at.X - mixerDragFrom.X) < MixerDragStart && Math.Abs(at.Y - mixerDragFrom.Y) < MixerDragStart) return;
                if (!LiftMixerDrag()) { mixerDragCard = null; e.Pointer.Capture(null); return; }
            }
            AutoScrollMixer(e);
            double dx = e.GetPosition(mx.Strips).X - mixerDragFrom.X;
            mixerDragShift.X = mixerDragL + dx - mixerGhostL;
            var slot = MixerDropAt(mixerDragL + dx + mixerDragW / 2);
            if (slot != mixerDrop) MakeMixerRoom(slot);
        };
        card.PointerReleased += (_, e) =>
        {
            if (mixerDragCard != card || mixerSettling) return;
            if (mixerDragging)
            {
                // glide into the room made for it; the capture goes after, so losing it doesn't cancel the drop
                mixerSettling = true;
                mixerDragTarget = (mixerDrop is { } d ? LandingX(d) : mixerDragL) - mixerGhostL;
                mixerGlide!.Start();
            }
            else mixerDragCard = null;
            e.Pointer.Capture(null);
        };
        card.PointerCaptureLost += (_, _) =>
        {
            if (mixerDragCard != card || mixerSettling) return;
            if (!mixerDragging) { mixerDragCard = null; return; }
            // cancelled (Esc, another window): everything glides back
            MakeMixerRoom(null);
            mixerSettling = true;
            mixerDragTarget = mixerDragL - mixerGhostL;
            mixerGlide!.Start();
        };
    }

    /// <summary>The bounds of a control in the layout's coordinates, its margins included.</summary>
    Rect OuterInStrips(Control c)
    {
        if (c.TranslatePoint(new Point(0, 0), mx.Strips) is not { } p) return default;
        var m = c.Margin;
        return new Rect(p.X - m.Left, p.Y - m.Top, c.Bounds.Width + m.Left + m.Right, c.Bounds.Height + m.Top + m.Bottom);
    }

    /// <summary>Measures everything once (nothing has moved yet) and lifts the dragged strip. False if it isn't on screen.</summary>
    bool LiftMixerDrag()
    {
        var o = mx.Order;
        var item = mixerDragItem;
        var top = o.Top.FirstOrDefault(t => t.Item == item);
        var inner = o.Inner.FirstOrDefault(i => i.Track == item);
        mixerDragged = top.Column ?? inner.Card;
        if (mixerDragged == null) return false;
        mixerDragSide = top.Column != null ? top.Side : o.Top.FirstOrDefault(t => t.Item is Folder f && f.Id == inner.FolderId).Side;
        var box = OuterInStrips(mixerDragged);
        mixerDragL = box.Left;
        mixerDragW = box.Width;
        MeasureMixerSlots();
        mixerLeaves.Clear();
        int Leaf(Control c, int side, int parent = -1)
        {
            if (c == mixerDragged) return -1;
            var r = OuterInStrips(c);
            var shift = new TranslateTransform();
            c.RenderTransform = shift;
            mixerLeaves.Add(new MixerLeaf(c, r.Left, r.Right, side, shift, parent));
            return mixerLeaves.Count - 1;
        }
        foreach (var (column, it, side) in o.Top)
        {
            if (column == mixerDragged) continue;
            int at = Leaf(column, side);
            // an open folder moves as one block, and its strips also make room inside it (for a track going in or out)
            if (it is Folder { Separator: false } f && o.FolderStrips.TryGetValue(f.Id, out var own))
            {
                Leaf(own, side, at);
                foreach (var i in o.Inner)
                    if (i.FolderId == f.Id) Leaf(i.Card, side, at);
            }
        }
        mixerTargets = new double[mixerLeaves.Count];
        mixerDrop = null;
        mixerDragShift.X = 0;
        mixerDragTarget = 0;
        // a separator is see-through: lifted, it gets a card so it doesn't blend into what it passes over
        if (mixerDragItem is Folder { Separator: true } && mixerDragCard is Border sb) sb.Background = Palette.Raised;
        LiftMixerGhost(mixerDragged, box);
        // no tooltips while dragging: the card's own would stick to the pointer and the others pop up as it passes
        ToolTip.SetIsOpen(mixerDragCard!, false);
        ToolTip.SetServiceEnabled(mixerStrips, false);
        mixerRaised.Clear();
        for (Control? c = mixerDragged; c != null && c != mx.Strips; c = c.Parent as Control)
        {
            c.ZIndex = 1;
            mixerRaised.Add(c);
        }
        mixerGlide ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => StepMixerGlide());
        mixerDragging = true;
        return true;
    }

    /// <summary>
    /// Takes the lifted strip's picture and puts it on a layer over the three docks (#280): dragged from one dock to
    /// another it stays in sight the whole way, where the strip itself would be clipped by its dock's scroll.
    /// </summary>
    void LiftMixerGhost(Control c, Rect box)
    {
        mixerGhostL = box.Left;
        var top = TopLevel.GetTopLevel(c);
        // the picture at the screen's resolution (the interface's own scale included), so it doesn't blur
        double scale = (top?.RenderScaling ?? 1) * (top != null && c.TransformToVisual(top) is { } m ? Math.Max(0.1, m.M11) : 1);
        double w = c.Bounds.Width, h = c.Bounds.Height;
        mixerGhostShot = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Math.Ceiling(w * scale)), Math.Max(1, (int)Math.Ceiling(h * scale))),
            new Vector(96 * scale, 96 * scale));
        mixerGhostShot.Render(c);
        var shot = new Image { Source = mixerGhostShot, Width = w, Height = h, Opacity = 0.9, RenderTransform = mixerDragShift };
        Canvas.SetLeft(shot, box.Left + c.Margin.Left);
        Canvas.SetTop(shot, box.Top + c.Margin.Top);
        mixerGhostLayer = new Canvas { IsHitTestVisible = false, ZIndex = 10, Children = { shot } };
        Grid.SetColumnSpan(mixerGhostLayer, Math.Max(1, mx.Strips.ColumnDefinitions.Count));
        mx.Strips.Children.Add(mixerGhostLayer);
        // its place stays (the others close it as they glide), empty
        c.Opacity = 0;
    }

    /// <summary>Every place the dragged item could land (measured before anything moves).</summary>
    void MeasureMixerSlots()
    {
        var o = mx.Order;
        mixerSlots.Clear();
        var dragged = mixerDragItem;
        // buses and channels share the gaps (#270: a bus can sit between the channels)
        for (int right = 0; right < o.Sides.Length; right++)
        {
            var side = o.Sides[right];
            if (side == null) continue;
            var items = o.Top.Where(t => t.Side == right).ToList();
            if (items.Count == 0)
            {
                mixerSlots.Add(new MixerSlot(OuterInStrips(side).Left, right, null, null, null));
                continue;
            }
            for (int k = 0; k <= items.Count; k++)
            {
                double x = k < items.Count ? OuterInStrips(items[k].Column).Left : OuterInStrips(items[^1].Column).Right;
                object? prev = null, next = null;
                for (int j = k - 1; j >= 0 && prev == null; j--)
                    if (items[j].Item is not Bus && items[j].Item != dragged) prev = items[j].Item;
                var trailing = new List<Bus>();
                for (int j = k; j < items.Count && next == null; j++)
                    if (items[j].Item is Bus tb) { if (tb != dragged) trailing.Add(tb); }
                    else if (items[j].Item != dragged) next = items[j].Item;
                mixerSlots.Add(new MixerSlot(x, right, null, prev, next, k > 0 ? items[k - 1].Item : null, k < items.Count ? items[k].Item : null,
                    trailing.Count > 0 ? [.. trailing] : null));
            }
        }
        // a track can also go into an open folder: between its tracks, or first after the folder's strip
        if (mixerDragItem is Track)
            foreach (var (folderId, strip) in o.FolderStrips)
            {
                int right = o.Top.FirstOrDefault(t => t.Item is Folder f && f.Id == folderId).Side;
                var inner = o.Inner.Where(i => i.FolderId == folderId).ToList();
                mixerSlots.Add(new MixerSlot(OuterInStrips(strip).Right, right, folderId, null, inner.Count > 0 ? inner[0].Track : null,
                    null, inner.Count > 0 ? inner[0].Track : null));
                for (int k = 0; k < inner.Count; k++)
                {
                    var next = k + 1 < inner.Count ? inner[k + 1].Track : null;
                    mixerSlots.Add(new MixerSlot(OuterInStrips(inner[k].Card).Right, right, folderId, inner[k].Track, next, inner[k].Track, next));
                }
            }
    }

    /// <summary>The slot nearest to the dragged strip's center.</summary>
    MixerSlot? MixerDropAt(double x)
    {
        MixerSlot? best = null;
        double bestD = double.MaxValue;
        foreach (var s in mixerSlots)
        {
            double d = Math.Abs(s.X - x);
            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }

    /// <summary>
    /// Where the dragged strip's left edge ends in <paramref name="s"/>: the left dock and the middle grow to the right and
    /// the right dock to the left (it sits against the master), so the strips that make room are the ones after the slot
    /// on the first two and before it on the right dock.
    /// </summary>
    double LandingX(MixerSlot s)
    {
        bool after = s.X > mixerDragL + 1;
        return s.Side == MixerSides.RightSide ? (mixerDragSide != s.Side || after ? s.X - mixerDragW : s.X) : (mixerDragSide == s.Side && after ? s.X - mixerDragW : s.X);
    }

    /// <summary>The others glide to close the dragged strip's place and open its room at <paramref name="slot"/> (none = back home).</summary>
    void MakeMixerRoom(MixerSlot? slot)
    {
        mixerDrop = slot;
        double w = mixerDragW, dragR = mixerDragL + w;
        for (int i = 0; i < mixerLeaves.Count; i++)
        {
            var l = mixerLeaves[i];
            double off = 0;
            if (slot is { } s)
            {
                if (l.Side == mixerDragSide) off += mixerDragSide == MixerSides.RightSide ? (l.R <= mixerDragL + 1 ? w : 0) : (l.L >= dragR - 1 ? -w : 0);
                if (l.Side == s.Side) off += s.Side == MixerSides.RightSide ? (l.R <= s.X + 1 ? -w : 0) : (l.L >= s.X - 1 ? w : 0);
            }
            // a folder's strip already moves with its folder: only what differs from it is its own
            mixerTargets[i] = l.Parent >= 0 ? off - mixerTargets[l.Parent] : off;
        }
        mixerGlide!.Start();
    }

    void StepMixerGlide()
    {
        bool still = true;
        for (int i = 0; i < mixerLeaves.Count; i++)
            still &= Glide(mixerLeaves[i].Shift, mixerTargets[i]);
        // while dragging, the lifted strip is the pointer's
        if (mixerSettling) still &= Glide(mixerDragShift, mixerDragTarget, MixerSettleEase);
        if (!still) return;
        mixerGlide!.Stop();
        if (mixerSettling) _ = FinishMixerDrag();

        static bool Glide(TranslateTransform t, double target, double ease = SlotDrag.SlideEase)
        {
            double x = t.X + (target - t.X) * ease;
            bool done = Math.Abs(target - x) < 0.5;
            t.X = done ? target : x;
            return done;
        }
    }

    /// <summary>Once everything has settled: the order changes (rebuilding the strips as they look now), then the old cards let go.</summary>
    async Task FinishMixerDrag()
    {
        var drop = mixerDrop;
        var what = mixerDragItem;
        if (drop is { } d && what != null && d.Before != what && d.After != what) await DropInMixer(what, d);
        foreach (var l in mixerLeaves) l.Card.RenderTransform = null;
        foreach (var c in mixerRaised) c.ZIndex = 0;
        if (mixerDragged != null) mixerDragged.Opacity = 1;
        if (mixerGhostLayer != null) mx.Strips.Children.Remove(mixerGhostLayer);
        mixerGhostLayer = null;
        mixerGhostShot?.Dispose();
        mixerGhostShot = null;
        mixerDragShift.X = 0;
        if (mixerDragCard is Border { Background: not null } sb && what is Folder { Separator: true }) sb.Background = Brushes.Transparent;
        mixerStrips.ClearValue(ToolTip.ServiceEnabledProperty);
        mixerLeaves.Clear();
        mixerRaised.Clear();
        mixerDragged = null;
        mixerDragCard = null;
        mixerDragItem = null;
        mixerDrop = null;
        mixerDragging = mixerSettling = false;
    }

    /// <summary>Applies a drop: the item moves to the slot's place, side and folder, in one undo step.</summary>
    async Task DropInMixer(object item, MixerSlot slot)
    {
        if (store == null || store.ReadOnly || restoring) return;
        if (item is Bus bus)
        {
            string id = bus.Id;
            BusesEdit(p =>
            {
                var list = p.Settings.Buses;
                if (list.Find(b => b.Id == id) is not { } b) return false;
                list.Remove(b);
                int at = slot.After is Bus n && list.IndexOf(n) is >= 0 and var ni ? ni
                    : slot.Before is Bus pv && list.IndexOf(pv) is >= 0 and var pi ? pi + 1 : list.Count;
                list.Insert(at, b);
                PlaceBus(p, b, slot);
                return true;
            });
            return;
        }
        // the buses that followed it stay where they are; the ones after the gap follow it there (#270)
        var anchors = new List<(string Bus, string? After, int Side)>();
        var top = mx.Order.Top;
        if (item is Folder or XtremeDaw.Projects.Track { FolderId: null } && top.FindIndex(t => t.Item == item) is >= 0 and var own)
        {
            int right = top[own].Side;
            object? before = null;
            for (int j = own - 1; j >= 0 && before == null; j--)
                if (top[j].Side == right && top[j].Item is not Bus) before = top[j].Item;
            string? movedId = IdOf(item);
            foreach (var b in Buses)
                if (b.MixerAfter == movedId) anchors.Add((b.Id, IdOf(before) ?? MixerSides.First, right));
        }
        if (slot.Folder == null && IdOf(item) is { } landedId)
            foreach (var b in slot.Trailing ?? []) anchors.Add((b.Id, landedId, slot.Side));
        string? keep = sel?.Track.Id;
        await EditTimeline(p =>
        {
            MoveToSlot(p, item, slot);
            Reanchor(p, anchors);
            return keep;
        }, "tracks-reordered");
    }

    /// <summary>
    /// "Dock" in a strip's menu in the mixer (#277, like FL Studio's "Dock to"): the left side or the right dock (#303:
    /// only those two). A track inside a folder goes with its folder, so it has none.
    /// </summary>
    MenuItem DockMenu(object item)
    {
        bool enabled = store is { ReadOnly: false } && !restoring;
        int Now() => item switch
        {
            Bus b => BusById(b.Id) is { } x ? MixerSides.Of(x) : MixerSides.RightSide,
            Folder f => FolderById(f.Id) is { } x ? MixerSides.Of(x) : MixerSides.LeftSide,
            XtremeDaw.Projects.Track t => store?.Project.Tracks.Find(y => y.Id == t.Id) is { } x ? MixerSides.Of(x) : MixerSides.LeftSide,
            _ => MixerSides.LeftSide,
        };
        var refresh = new List<Action>();
        MenuItem Pick(string key, int side) => MenuCheck(Loc.T(key), () => Now() == side, () => _ = MoveToDock(item, side), refresh);
        return new MenuItem
        {
            Header = MenuHeader(Loc.T("mixer.dock"), new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12 }),
            IsEnabled = enabled,
            ItemsSource = new object[]
            {
                Pick("mixer.dock.left", MixerSides.LeftSide),
                Pick("mixer.dock.right", MixerSides.RightSide),
            },
        };
    }

    /// <summary>
    /// Moves a strip to another dock (#277), keeping its place in the order, in one undo step. A bus goes after the
    /// channels of its new dock; the buses that followed a track or folder stay in its old dock, after the strip before it.
    /// </summary>
    async Task MoveToDock(object item, int side)
    {
        if (store == null || store.ReadOnly || restoring || mixerDragging) return;
        if (item is Bus bus)
        {
            string id = bus.Id;
            BusesEdit(p =>
            {
                if (p.Settings.Buses.Find(b => b.Id == id) is not { } b || MixerSides.Of(b) == side) return false;
                b.MixerSide = MixerSides.ForBus(side);
                b.MixerAfter = null;
                return true;
            });
            return;
        }
        var anchors = new List<(string Bus, string? After, int Side)>();
        var top = mixerLayout.Order.Top;
        if (top.FindIndex(t => t.Item == item) is >= 0 and var own)
        {
            int was = top[own].Side;
            if (was == side) return;
            object? before = null;
            for (int j = own - 1; j >= 0 && before == null; j--)
                if (top[j].Side == was && top[j].Item is not Bus) before = top[j].Item;
            string? movedId = IdOf(item);
            foreach (var b in Buses)
                if (b.MixerAfter == movedId) anchors.Add((b.Id, IdOf(before) ?? MixerSides.First, was));
        }
        string? keep = sel?.Track.Id, itemId = IdOf(item);
        await EditTimeline(p =>
        {
            if (p.Folders.Find(f => f.Id == itemId) is { } f) f.MixerSide = MixerSides.ForChannel(side);
            else if (p.Tracks.Find(t => t.Id == itemId) is { FolderId: null } t) t.MixerSide = MixerSides.ForChannel(side);
            Reanchor(p, anchors);
            return keep;
        }, "tracks-reordered");
    }

    /// <summary>
    /// A new empty bus at a mixer gap (#270) on the buses' side (<see cref="MixerSides.BusDefault"/>); a click on the
    /// other side puts it after the channels of the buses' side (#303: a new bus went to the left).
    /// </summary>
    static bool NewBusAt(Project p, MixerSlot? slot)
    {
        if (ProjectBuses.Add(p) is not { } b) return false;
        if (slot is not { Folder: null } s || s.Side != MixerSides.BusDefault) return true;
        var list = p.Settings.Buses;
        // among the buses at the gap: before the one after it, or after the one before it
        int at = s.After is Bus n && list.FindIndex(x => x.Id == n.Id) is >= 0 and var ni ? ni
            : s.Before is Bus pv && list.FindIndex(x => x.Id == pv.Id) is >= 0 and var pi ? pi + 1 : -1;
        if (at >= 0)
        {
            list.Remove(b);
            list.Insert(Math.Min(at, list.Count), b);
        }
        PlaceBus(p, b, s);
        return true;
    }

    /// <summary>The id a bus follows for a track, folder or separator; null for anything else.</summary>
    static string? IdOf(object? item) => item switch { Folder f => f.Id, XtremeDaw.Projects.Track t => t.Id, _ => null };

    /// <summary>Points buses of <paramref name="p"/> at the strips they now follow (an id, or the start of a side).</summary>
    static void Reanchor(Project p, IEnumerable<(string Bus, string? After, int Side)> anchors)
    {
        foreach (var (id, after, side) in anchors)
            if (p.Settings.Buses.Find(b => b.Id == id) is { } b)
            {
                b.MixerAfter = after;
                b.MixerSide = MixerSides.ForBus(side);
            }
    }

    /// <summary>
    /// Puts a bus of <paramref name="p"/> at a mixer gap (#270): after the strip before it, after what the bus before it
    /// follows, or first on the side.
    /// </summary>
    static void PlaceBus(Project p, Bus b, MixerSlot slot)
    {
        string? after = slot.Before switch
        {
            Bus pb => p.Settings.Buses.Find(x => x.Id == pb.Id)?.MixerAfter,
            null => MixerSides.First,
            var o => IdOf(o),
        };
        Reanchor(p, [(b.Id, after, slot.Side)]);
    }

    /// <summary>Moves a track or folder (with its tracks) of <paramref name="p"/> to the slot's place, side and folder.</summary>
    static void MoveToSlot(Project p, object item, MixerSlot slot)
    {
        if (item is Folder folder)
        {
            if (p.Folders.Find(f => f.Id == folder.Id) is not { } f) return;
            var seq = TrackFolders.Sequence(p);
            seq.RemoveAll(x => x == f || x is Track t && t.FolderId == f.Id);
            if (Place(p, seq, slot) is { } at) TrackFolders.MoveFolder(p, f.Id, at);
            f.MixerSide = MixerSides.ForChannel(slot.Side);
        }
        else if (item is Track track && p.Tracks.Find(t => t.Id == track.Id) is { } t)
        {
            var seq = TrackFolders.Sequence(p);
            seq.Remove(t);
            int? at = slot.Folder is { } fid && slot.Next == null && slot.Prev == null
                ? seq.FindIndex(x => x is Folder f && f.Id == fid) + 1
                : Place(p, seq, slot);
            if (at is { } pos) TrackFolders.Move(p, [t.Id], pos, slot.Folder);
            if (slot.Folder == null) t.MixerSide = MixerSides.ForChannel(slot.Side);
        }
    }

    /// <summary>
    /// Where the channels made from the mixer's right-click menu go (asked by the user, #196: like its separators, at the
    /// gap where the click was): the slot, the project, and the ids that were there before, so the next timeline edit
    /// can tell what it made. Set when the menu opens; dropped when it closes without a pick, when another new-track menu
    /// opens, or by the first edit that runs.
    /// </summary>
    sealed record MixerNewAt(Project Project, MixerSlot Slot, HashSet<string> Before);

    MixerNewAt? mixerNewAt;

    /// <summary>Called by <see cref="EditTimeline"/> inside its undo step: what the edit made moves to the pending mixer slot.</summary>
    void PlaceNewInMixer(Project p)
    {
        if (mixerNewAt is not { } at) return;
        mixerNewAt = null;
        if (at.Project != p) return;
        var seq = TrackFolders.Sequence(p);
        // new folders carry their tracks along; a new track goes alone
        var made = seq.Where(x => x is Folder f ? !at.Before.Contains(f.Id)
            : x is Track t && !at.Before.Contains(t.Id) && (t.FolderId == null || at.Before.Contains(t.FolderId))).ToList();
        if (made.Count == 0) return;
        // each one goes before the strip after the slot (keeps their order), or after the one before it (reversed to keep it)
        if (!(at.Slot.Next != null && seq.Contains(at.Slot.Next))) made.Reverse();
        foreach (var x in made) MoveToSlot(p, x, at.Slot);
        // the buses after the gap follow the last one made (#270)
        if (at.Slot.Folder == null && at.Slot.Trailing is { } trailing
            && TrackFolders.Sequence(p).LastOrDefault(x => made.Contains(x) && x is Folder or XtremeDaw.Projects.Track { FolderId: null }) is { } last)
            Reanchor(p, trailing.Select(b => (b.Id, IdOf(last), at.Slot.Side)));
    }

    /// <summary>Every item of a menu with no submenu, at any depth.</summary>
    static IEnumerable<MenuItem> MenuLeaves(IEnumerable<object>? items)
    {
        foreach (var o in items ?? [])
        {
            if (o is not MenuItem m) continue;
            var sub = (m.ItemsSource ?? m.Items).Cast<object>().ToList();
            if (sub.Count == 0) yield return m;
            else foreach (var leaf in MenuLeaves(sub)) yield return leaf;
        }
    }

    /// <summary>
    /// Where the slot is in <paramref name="seq"/> (the sequence without what moves): before the item after it, else after
    /// the item before it (past a folder's tracks), else the end. Null when the slot is the item's own place.
    /// </summary>
    static int? Place(Project p, List<object> seq, MixerSlot slot)
    {
        if (slot.Next != null && seq.IndexOf(slot.Next) is >= 0 and var n) return n;
        if (slot.Prev != null && seq.IndexOf(slot.Prev) is >= 0 and var at)
        {
            if (slot.Prev is Folder f && slot.Folder == null)
                while (at + 1 < seq.Count && seq[at + 1] is Track c && c.FolderId == f.Id) at++;
            return at + 1;
        }
        if (slot.Next != null || slot.Prev != null) return null;
        return seq.Count;
    }

    // --- separators in the mixer ----------------------------------------------------------------------

    /// <summary>
    /// A separator's strip: a narrow divider, a double line with its name small and upper-cased up the middle in its
    /// color (console style, #289), and an eye that shows or
    /// hides it in the timeline. A click opens its menu; dragged, it moves like any strip.
    /// </summary>
    Control SeparatorStrip(Folder sep)
    {
        string id = sep.Id;
        Folder S() => FolderById(id) ?? sep;
        // the same look as in the timeline (SeparatorLooks, picked in Apariencia, #293; the console's double line by default, #289)
        var line = new SeparatorLooks.Rule { HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 40, 0, 8), IsHitTestVisible = false };
        var name = new TextBlock { FontSize = 9, FontWeight = FontWeight.Bold, LetterSpacing = 1.5, TextTrimming = TextTrimming.CharacterEllipsis };
        var tag = new Border { Padding = new Thickness(6, 0), Child = name };
        var rotated = new LayoutTransformControl { LayoutTransform = new RotateTransform(-90), Child = tag, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 40, 0, 8) };
        var eye = new Button { Classes = { "ghost" }, Width = 24, Height = 24, MinHeight = 24, Padding = new Thickness(0), Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0),
            Content = new TextBlock { Classes = { "icon" }, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        eye.Click += (_, _) => FolderEdit(S(), x => x.HiddenInTimeline = !x.HiddenInTimeline, "hidden");
        var box = new Border { Width = SeparatorW, Margin = MixerLooks.Joined ? default : new Thickness(StripGap / 2, StripGap), Background = Brushes.Transparent, CornerRadius = new CornerRadius(8), Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Grid() };
        ((Grid)box.Child).Children.AddRange([line, rotated, eye]);
        mx.Refresh.Add(() =>
        {
            var s = S();
            var c = TimelineView.SeparatorColor(s);
            name.Text = s.Name.ToUpperInvariant();
            (tag.Background, name.Foreground) = SeparatorLooks.NameBrushes(c);
            tag.CornerRadius = SeparatorLooks.NameCorners;
            line.Color = c;
            line.InvalidateVisual();
            ((TextBlock)eye.Content!).Text = s.HiddenInTimeline ? "" : "";
            ((TextBlock)eye.Content!).Foreground = s.HiddenInTimeline ? Palette.Faint : Palette.Dim;
            ToolTip.SetTip(eye, Loc.T(s.HiddenInTimeline ? "separator.show.tip" : "separator.hide.tip"));
        });
        void Menu()
        {
            if (FolderById(id) is not { } s) return;
            var items = SeparatorMenu(s, box, null);
            items.Add(new Separator());
            items.Add(DockMenu(s));
            OpenAnchoredMenu(Popup(items, 240), box);
        }
        // a left click opens the menu once it is clear it isn't the first half of a double click, which goes straight to
        // the color (asked by the user, #194)
        var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SeparatorClickWait) };
        later.Tick += (_, _) => { later.Stop(); Menu(); };
        bool second = false;
        box.PointerPressed += (_, e) =>
        {
            if (e.ClickCount != 2 || !e.GetCurrentPoint(box).Properties.IsLeftButtonPressed) return;
            later.Stop();
            second = true;
            timelineMenu?.Close();
            if (FolderById(id) is not { } s) return;
            // on its name it renames (asked by the user, #194); anywhere else, the color
            var at = e.GetPosition(rotated);
            if (at.X >= 0 && at.Y >= 0 && at.X <= rotated.Bounds.Width && at.Y <= rotated.Bounds.Height && store is { ReadOnly: false } && !restoring)
                AskName(box, null, Loc.T("separator.rename"), s.Name, Loc.T("common.ok"), n => { if (FolderById(id) is { } x) FolderEdit(x, y => y.Name = n.Trim(), "name"); });
            else ShowFolderLook(s, box);
        };
        box.PointerReleased += (_, e) =>
        {
            if (second && e.InitialPressMouseButton == MouseButton.Left) { second = false; e.Handled = true; return; }
            // a click, not the end of a drag (this runs before the drag's own release, which drops it)
            if (e.InitialPressMouseButton == MouseButton.Right) { later.Stop(); Menu(); e.Handled = true; } // not the mixer's own menu too
            else if (e.InitialPressMouseButton == MouseButton.Left && mixerDragCard == box && !mixerDragging) { later.Start(); e.Handled = true; }
        };
        return box;
    }

    /// <summary>Ms a left click on a separator waits for a second one (a double click opens its color) before opening its menu.</summary>
    const int SeparatorClickWait = 280;

    /// <summary>
    /// A new separator in the mixer where it was asked for (asked by the user, #194: it always went to the end): at the gap
    /// between strips nearest <paramref name="x"/> (a right click on the mixer, in the layout's coordinates), else right
    /// after the selected track or folder, on its side; with neither, at the end of the left side. It starts hidden in the
    /// timeline, like every separator made in the mixer.
    /// </summary>
    async Task NewMixerSeparator(double? x)
    {
        if (store == null || store.ReadOnly || restoring || mixerDragging) return;
        MixerSlot? slot = null;
        if (x is { } at)
        {
            MeasureMixerSlots();
            slot = MixerDropAt(at);
            mixerSlots.Clear();
        }
        string? keep = sel?.Track.Id, folderId = SelectedFolder?.Id;
        await EditTimeline(p =>
        {
            var seq = TrackFolders.Sequence(p);
            int? pos = null;
            int right = MixerSides.LeftSide;
            if (slot is { } s)
            {
                pos = Place(p, seq, s);
                right = s.Side;
            }
            else if (((object?)p.Folders.Find(f => f.Id == folderId && !f.Separator) ?? p.Tracks.Find(t => t.Id == keep)) is { } picked)
            {
                // a track in a folder puts it after the whole folder (AddSeparator never goes inside one)
                pos = seq.IndexOf(picked) + 1;
                var owner = picked is Track t ? (object?)TrackFolders.Of(p, t) ?? t : picked;
                right = owner is Folder of ? MixerSides.Of(of) : MixerSides.Of((Track)owner);
            }
            string id = TrackFolders.AddSeparator(p, null, pos);
            if (p.Folders.Find(f => f.Id == id) is { } sep) sep.MixerSide = MixerSides.ForChannel(right);
            // the buses after the gap follow it (#270)
            if (slot is { Trailing: { } trailing }) Reanchor(p, trailing.Select(b => (b.Id, (string?)id, right)));
            return keep;
        }, "separator-added");
    }

    /// <summary>
    /// The mixer's right-click menu (asked by the user, in place of a "+ Bus" column): the new tracks of the timeline's +
    /// (audio, MIDI, instrument, folder, templates, import; #196), a new empty bus or FX channel (its
    /// effect goes in afterwards with "+ Efecto") and a new separator. A right click a strip handles itself doesn't reach it.
    /// The background is transparent, not empty, so the dark space between and around the strips takes the click too, and
    /// a left press there deselects everything (asked by the user).
    /// </summary>
    void InitMixerMenu()
    {
        mixerStrips.Background = Brushes.Transparent;
        mixerStrips.PointerPressed += (_, e) =>
        {
            // the empty space is the place itself or either side's (#275)
            if (e.Source != mixerStrips && e.Source != mixerLeftHost && e.Source != mixerRightHost && e.Source != mx.Strips || !e.GetCurrentPoint(mixerStrips).Properties.IsLeftButtonPressed || restoring) return;
            if (sel != null || SelectedFolder != null || selBusId != null) Timeline.Select(-1);
        };
        mixerStrips.PointerReleased += (_, e) =>
        {
            if (e.Handled || e.InitialPressMouseButton != MouseButton.Right || store == null || restoring) return;
            e.Handled = true;
            bool enabled = !store.ReadOnly;
            double x = e.GetPosition(mx.Strips).X;
            // the new channels of the timeline's + first (asked by the user, #196), then what only the mixer adds
            var items = NewTrackItems() ?? [];
            // and they land where the click was, like the separators (#196)
            if (enabled && !mixerDragging && items.Count > 0)
            {
                MeasureMixerSlots();
                var slot = MixerDropAt(x);
                mixerSlots.Clear();
                if (slot is { } s)
                {
                    var p = store.Project;
                    var made = new MixerNewAt(p, s, [.. p.Tracks.Select(t => t.Id), .. p.Folders.Select(f => f.Id)]);
                    mixerNewAt = made;
                    var picks = MenuLeaves(items).ToList();
                    bool picked = false;
                    foreach (var m in picks) m.Click += (_, _) => picked = true;
                    pendingMixerMenuClosed = () => { if (!picked && mixerNewAt == made) mixerNewAt = null; };
                }
            }
            // a new bus goes there too (#270)
            MixerSlot? busAt = null;
            if (enabled && !mixerDragging)
            {
                MeasureMixerSlots();
                busAt = MixerDropAt(x);
                mixerSlots.Clear();
            }
            if (items.Count > 0) items.Add(new Separator());
            items.AddRange(
            [
                MenuAction(Loc.T("mixer.menu.newBus"), () => { BusesEdit(p => NewBusAt(p, busAt)); return Task.CompletedTask; }, enabled: enabled, icon: "",
                    tip: Loc.T("mixer.menu.newBus.tip")),
                MenuAction(Loc.T("mixer.menu.newSeparator"), () => NewMixerSeparator(x), enabled: enabled, icon: "", tip: Loc.T("separator.add.tip")),
            ]);
            var menu = Popup(items, 240);
            if (pendingMixerMenuClosed is { } closed)
            {
                pendingMixerMenuClosed = null;
                // after the click handlers: a pick raises Click before the menu closes
                menu.Closed += (_, _) => Dispatcher.UIThread.Post(closed);
            }
            OpenAnchoredMenu(menu, mixerStrips);
        };
    }

    /// <summary>Hands the mixer menu its "closed without a pick" check (built with the menu's items, attached once it exists).</summary>
    Action? pendingMixerMenuClosed;

    /// <summary>"+ Separador" in the mixer's header: a new separator right after the selected channel (<see cref="NewMixerSeparator"/>).</summary>
    Button AddSeparatorButton()
    {
        var b = new Button { Classes = { "chip" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children =
            {
                new TextBlock { Classes = { "icon" }, Text = "", FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = Loc.T("separator.add"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
            } } };
        ToolTip.SetTip(b, Loc.T("separator.add.tip"));
        b.Click += async (_, _) => await NewMixerSeparator(null);
        headerRefresh.Add(() => b.IsEnabled = store != null && !store.ReadOnly && !restoring);
        return b;
    }
}
