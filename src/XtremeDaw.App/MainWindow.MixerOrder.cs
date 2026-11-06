using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using XtremeDaw.App.Controls;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The mixer's order, edited from the mixer itself (asked by the user, to keep big projects tidy): every strip sits on
/// the left or the right side (<see cref="MixerSides"/>), and a strip is dragged by its card to another place, side or
/// folder. Tracks, folders and separators move in the same order the timeline shows (<see cref="TrackFolders"/>, one
/// <see cref="EditTimeline"/> with its undo), so the timeline follows and the other way round; buses move in their own
/// list (<see cref="ProjectBuses"/>), among the buses of either side. Separators (<see cref="Folder.Separator"/>) are
/// narrow named dividers, here and in the timeline unless hidden there (and they start hidden there). While dragging, the
/// others glide out of the way like every reorderable list (<see cref="SlotDrag"/>); nothing changes until it is dropped.
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
        public readonly List<(Control Column, object Item, bool Right)> Top = [];
        /// <summary>The track strips inside open folders, in order.</summary>
        public readonly List<(Control Card, Track Track, string FolderId)> Inner = [];
        /// <summary>Each open folder's own strip, where its first track's place begins.</summary>
        public readonly Dictionary<string, Control> FolderStrips = [];
        public StackPanel? Left, Right;
    }

    /// <summary>A place a dragged strip can land: its x (a boundary between strips, margins included), the side, the folder (null = top level) and the items around it.</summary>
    readonly record struct MixerSlot(double X, bool Right, string? Folder, object? Prev, object? Next);

    /// <summary>
    /// A strip that makes room while another is dragged: its bounds (margins included), its own shift, and the leaf it
    /// sits in (an open folder's column for its strips, -1 at the top), whose shift it already carries.
    /// </summary>
    readonly record struct MixerLeaf(Control Card, double L, double R, bool Right, TranslateTransform Shift, int Parent);

    /// <summary>The drag in progress: the card pressed, what it moves, where it was pressed, and where it would land.</summary>
    Control? mixerDragCard;
    object? mixerDragItem;
    Point mixerDragFrom;
    bool mixerDragging, mixerSettling;
    MixerSlot? mixerDrop;
    /// <summary>What moves with the pointer (the column, the whole folder or a folder's track), its bounds and side when lifted.</summary>
    Control? mixerDragged;
    double mixerDragL, mixerDragW;
    bool mixerDragRight;
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
            AutoScrollMixer(e.GetPosition(mixerScroll).X);
            double dx = e.GetPosition(mx.Strips).X - mixerDragFrom.X;
            mixerDragShift.X = dx;
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
                mixerDragTarget = mixerDrop is { } d ? LandingX(d) - mixerDragL : 0;
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
            mixerDragTarget = 0;
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
        mixerDragRight = top.Column != null ? top.Right : o.Top.FirstOrDefault(t => t.Item is Folder f && f.Id == inner.FolderId).Right;
        var box = OuterInStrips(mixerDragged);
        mixerDragL = box.Left;
        mixerDragW = box.Width;
        MeasureMixerSlots();
        mixerLeaves.Clear();
        int Leaf(Control c, bool right, int parent = -1)
        {
            if (c == mixerDragged) return -1;
            var r = OuterInStrips(c);
            var shift = new TranslateTransform();
            c.RenderTransform = shift;
            mixerLeaves.Add(new MixerLeaf(c, r.Left, r.Right, right, shift, parent));
            return mixerLeaves.Count - 1;
        }
        foreach (var (column, it, right) in o.Top)
        {
            if (column == mixerDragged) continue;
            int at = Leaf(column, right);
            // an open folder moves as one block, and its strips also make room inside it (for a track going in or out)
            if (it is Folder { Separator: false } f && o.FolderStrips.TryGetValue(f.Id, out var own))
            {
                Leaf(own, right, at);
                foreach (var i in o.Inner)
                    if (i.FolderId == f.Id) Leaf(i.Card, right, at);
            }
        }
        mixerTargets = new double[mixerLeaves.Count];
        mixerDrop = null;
        mixerDragShift.X = 0;
        mixerDragTarget = 0;
        mixerDragged.RenderTransform = mixerDragShift;
        mixerDragged.Opacity = 0.85;
        // a separator is see-through: lifted, it gets a card so it doesn't blend into what it passes over
        if (mixerDragItem is Folder { Separator: true } && mixerDragCard is Border sb) sb.Background = Palette.Raised;
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

    /// <summary>Every place the dragged item could land (measured before anything moves).</summary>
    void MeasureMixerSlots()
    {
        var o = mx.Order;
        mixerSlots.Clear();
        bool bus = mixerDragItem is Bus;
        foreach (bool right in new[] { false, true })
        {
            var side = right ? o.Right : o.Left;
            if (side == null) continue;
            var items = o.Top.Where(t => t.Right == right && t.Item is Bus == bus).ToList();
            for (int k = 0; k < items.Count; k++)
                mixerSlots.Add(new MixerSlot(OuterInStrips(items[k].Column).Left, right, null, k > 0 ? items[k - 1].Item : null, items[k].Item));
            if (items.Count > 0)
                mixerSlots.Add(new MixerSlot(OuterInStrips(items[^1].Column).Right, right, null, items[^1].Item, null));
            else
            {
                // an empty side (for this kind): tracks go to its start, buses after its tracks
                var others = o.Top.Where(t => t.Right == right).Select(t => OuterInStrips(t.Column)).ToList();
                double sideL = OuterInStrips(side).Left;
                mixerSlots.Add(new MixerSlot(bus && others.Count > 0 ? others.Max(r => r.Right) : sideL, right, null, null, null));
            }
        }
        // a track can also go into an open folder: between its tracks, or first after the folder's strip
        if (mixerDragItem is Track)
            foreach (var (folderId, strip) in o.FolderStrips)
            {
                bool right = o.Top.FirstOrDefault(t => t.Item is Folder f && f.Id == folderId).Right;
                var inner = o.Inner.Where(i => i.FolderId == folderId).ToList();
                mixerSlots.Add(new MixerSlot(OuterInStrips(strip).Right, right, folderId, null, inner.Count > 0 ? inner[0].Track : null));
                for (int k = 0; k < inner.Count; k++)
                    mixerSlots.Add(new MixerSlot(OuterInStrips(inner[k].Card).Right, right, folderId, inner[k].Track, k + 1 < inner.Count ? inner[k + 1].Track : null));
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
    /// Where the dragged strip's left edge ends in <paramref name="s"/>: the left side grows to the right and the right
    /// side to the left (it sits against the master), so the strips that make room are the ones after the slot on the
    /// left and before it on the right.
    /// </summary>
    double LandingX(MixerSlot s)
    {
        bool after = s.X > mixerDragL + 1;
        return s.Right ? (!mixerDragRight || after ? s.X - mixerDragW : s.X) : (!mixerDragRight && after ? s.X - mixerDragW : s.X);
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
                if (l.Right == mixerDragRight) off += mixerDragRight ? (l.R <= mixerDragL + 1 ? w : 0) : (l.L >= dragR - 1 ? -w : 0);
                if (l.Right == s.Right) off += s.Right ? (l.R <= s.X + 1 ? -w : 0) : (l.L >= s.X - 1 ? w : 0);
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
        if (mixerSettling) still &= Glide(mixerDragShift, mixerDragTarget);
        if (!still) return;
        mixerGlide!.Stop();
        if (mixerSettling) _ = FinishMixerDrag();

        static bool Glide(TranslateTransform t, double target)
        {
            double x = t.X + (target - t.X) * SlotDrag.SlideEase;
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
        if (drop is { } d && what != null && d.Prev != what && d.Next != what) await DropInMixer(what, d);
        foreach (var l in mixerLeaves) l.Card.RenderTransform = null;
        foreach (var c in mixerRaised) c.ZIndex = 0;
        if (mixerDragged != null)
        {
            mixerDragged.RenderTransform = null;
            mixerDragged.Opacity = 1;
        }
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

    /// <summary>Scrolls the mixer while the pointer is near one of its edges.</summary>
    void AutoScrollMixer(double x)
    {
        double w = mixerScroll.Viewport.Width;
        if (w <= 0) return;
        double dx = x < MixerDragEdge ? -MixerDragScroll : x > w - MixerDragEdge ? MixerDragScroll : 0;
        if (dx != 0) mixerScroll.Offset = mixerScroll.Offset.WithX(Math.Clamp(mixerScroll.Offset.X + dx, 0, Math.Max(0, mixerScroll.Extent.Width - w)));
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
                int at = slot.Next is Bus n && list.IndexOf(n) is >= 0 and var ni ? ni
                    : slot.Prev is Bus pv && list.IndexOf(pv) is >= 0 and var pi ? pi + 1 : list.Count;
                list.Insert(at, b);
                b.MixerSide = slot.Right ? null : MixerSides.Left;
                return true;
            });
            return;
        }
        string? keep = sel?.Track.Id;
        await EditTimeline(p =>
        {
            MoveToSlot(p, item, slot);
            return keep;
        }, "tracks-reordered");
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
            f.MixerSide = slot.Right ? MixerSides.Right : null;
        }
        else if (item is Track track && p.Tracks.Find(t => t.Id == track.Id) is { } t)
        {
            var seq = TrackFolders.Sequence(p);
            seq.Remove(t);
            int? at = slot.Folder is { } fid && slot.Next == null && slot.Prev == null
                ? seq.FindIndex(x => x is Folder f && f.Id == fid) + 1
                : Place(p, seq, slot);
            if (at is { } pos) TrackFolders.Move(p, [t.Id], pos, slot.Folder);
            if (slot.Folder == null) t.MixerSide = slot.Right ? MixerSides.Right : null;
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
    /// A separator's strip: a narrow divider with its name written up the middle in its color, and an eye that shows or
    /// hides it in the timeline. A click opens its menu; dragged, it moves like any strip.
    /// </summary>
    Control SeparatorStrip(Folder sep)
    {
        string id = sep.Id;
        Folder S() => FolderById(id) ?? sep;
        var line = new Rectangle { Width = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 40, 0, 8) };
        // the same soft glow as in the timeline (TimelineView.DrawSeparatorLine): stacked faint bands, denser at the line
        var glow = new Rectangle[TimelineView.GlowSteps];
        for (int k = 0; k < glow.Length; k++)
            glow[k] = new Rectangle { Width = 2 + 2 * TimelineView.GlowReach * (k + 1) / TimelineView.GlowSteps, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch, Margin = line.Margin, IsHitTestVisible = false };
        var name = new TextBlock { FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8, TextTrimming = TextTrimming.CharacterEllipsis,
            Background = Palette.App, Padding = new Thickness(6, 0) };
        var rotated = new LayoutTransformControl { LayoutTransform = new RotateTransform(-90), Child = name, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 40, 0, 8) };
        var eye = new Button { Classes = { "ghost" }, Width = 24, Height = 24, MinHeight = 24, Padding = new Thickness(0), Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0),
            Content = new TextBlock { Classes = { "icon" }, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        eye.Click += (_, _) => FolderEdit(S(), x => x.HiddenInTimeline = !x.HiddenInTimeline, "hidden");
        var box = new Border { Width = SeparatorW, Margin = new Thickness(StripGap / 2, StripGap), Background = Brushes.Transparent, CornerRadius = new CornerRadius(8), Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Grid() };
        ((Grid)box.Child).Children.AddRange([.. glow, line, rotated, eye]);
        mx.Refresh.Add(() =>
        {
            var s = S();
            var c = TimelineView.SeparatorColor(s);
            name.Text = s.Name;
            name.Foreground = Palette.Alpha(c, 1);
            line.Fill = Palette.Alpha(c, TimelineView.SeparatorLineAlpha);
            foreach (var g in glow) g.Fill = Palette.Alpha(c, TimelineView.GlowAlpha);
            ((TextBlock)eye.Content!).Text = s.HiddenInTimeline ? "" : "";
            ((TextBlock)eye.Content!).Foreground = s.HiddenInTimeline ? Palette.Faint : Palette.Dim;
            ToolTip.SetTip(eye, Loc.T(s.HiddenInTimeline ? "separator.show.tip" : "separator.hide.tip"));
        });
        void Menu() { if (FolderById(id) is { } s) OpenAnchoredMenu(Popup(SeparatorMenu(s, box, null), 240), box); }
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
            bool right = false;
            if (slot is { } s)
            {
                pos = Place(p, seq, s);
                right = s.Right;
            }
            else if (((object?)p.Folders.Find(f => f.Id == folderId && !f.Separator) ?? p.Tracks.Find(t => t.Id == keep)) is { } picked)
            {
                // a track in a folder puts it after the whole folder (AddSeparator never goes inside one)
                pos = seq.IndexOf(picked) + 1;
                var owner = picked is Track t ? (object?)TrackFolders.Of(p, t) ?? t : picked;
                right = owner is Folder of ? MixerSides.IsRight(of) : MixerSides.IsRight((Track)owner);
            }
            string id = TrackFolders.AddSeparator(p, null, pos);
            if (right && p.Folders.Find(f => f.Id == id) is { } sep) sep.MixerSide = MixerSides.Right;
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
            if (e.Source != mixerStrips || !e.GetCurrentPoint(mixerStrips).Properties.IsLeftButtonPressed || restoring) return;
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
            if (items.Count > 0) items.Add(new Separator());
            items.AddRange(
            [
                MenuAction(Loc.T("mixer.menu.newBus"), () => { BusesEdit(p => ProjectBuses.Add(p) != null); return Task.CompletedTask; }, enabled: enabled, icon: "",
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
