using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The buses' rows (asked by the user, #267: "Filas de bus al final", like Cubase's FX channels or Logic's aux tracks):
/// one row per bus after every track and folder, without clips, carrying the bus's automation lanes under it the way a
/// folder's row does. The window hands the list in (<see cref="SetBusRows"/>: empty while the project hides them,
/// <see cref="ProjectSettings.BusRowsHidden"/>). A click selects the bus (the window's, like the mixer's), M mutes it, a
/// right click opens its menu (on M, its lane); its chevron folds its lanes. Like the rest of the control it only raises events.
/// </summary>
public sealed partial class TimelineView
{
    IReadOnlyList<Bus> busRows = [];

    /// <summary>A bus row was clicked: the window selects that bus.</summary>
    public event Action<string>? BusRowSelected;
    /// <summary>Right click on a bus row: the window shows its menu there.</summary>
    public event Action<Bus, Point>? BusRowContextRequested;
    /// <summary>A bus row's M was clicked.</summary>
    public event Action<string>? BusMuteRequested;
    /// <summary>The bus the window has selected (null = none), read while drawing.</summary>
    public Func<string?>? SelectedBusId { get; set; }

    /// <summary>The buses shown at the end, in the mixer's order (empty = no bus rows). Lays the rows out again.</summary>
    public void SetBusRows(IReadOnlyList<Bus> list)
    {
        busRows = list;
        Relayout();
        InvalidateVisual();
    }

    /// <summary>A bus's color, or a neutral gray without one, like a folder's.</summary>
    public static Color BusColor(Bus b) => TrackIcons.ColorOf(b.Color) ?? Palette.DimColor;

    /// <summary>The bus rows' tops after the rest, which ends at <paramref name="y"/>; returns the new height.</summary>
    double StackBuses(double y)
    {
        for (int k = 0; k < busRows.Count; k++)
        {
            var b = busRows[k];
            targetTop[b.Id] = y;
            y += FolderH;
            foreach (var lane in LanesOf(b))
            {
                targetTop[lane.Id] = y;
                y += AutoH(lane);
            }
        }
        return y;
    }

    void AddBusRows()
    {
        foreach (var b in busRows)
        {
            rows.Add(new Row(-1, null, targetTop[b.Id], FolderH, Bus: b));
            foreach (var lane in LanesOf(b)) rows.Add(new Row(-1, null, targetTop[lane.Id], AutoH(lane), lane, b));
        }
    }

    double BusY(Bus b) => RulerH + Top(b.Id) - scrollY;

    /// <summary>The chevron that folds its lanes; none without lanes.</summary>
    Rect BusAutoFoldRect(Bus b) => b.Automation is { Count: > 0 } ? new(4, BusY(b) + (FolderH - AutoCaretH) / 2, AutoFoldW, AutoCaretH) : NoRect;

    Rect BusBadgeRect(Bus b) => new(24, BusY(b) + (FolderH - 22) / 2, 22, 22);

    Rect BusMuteRect(Bus b) => Compact ? NoRect : new(HeaderRight - 34, BusY(b) + 5, 24, 24);

    bool BusSelected(Bus b) => SelectedBusId?.Invoke() == b.Id;

    /// <summary>The bus rows' lanes: a tint in the bus's color, nothing to edit (a bus has no clips).</summary>
    void DrawBusLanes(DrawingContext ctx, double W)
    {
        for (int k = 0; k < busRows.Count; k++)
        {
            var b = busRows[k];
            double y = BusY(b);
            if (y + FolderH < RulerH || y > Bounds.Height) continue;
            ctx.FillRectangle(Palette.Alpha(BusColor(b), 0.05), new Rect(HeaderW, y, Math.Max(0, W - HeaderW), FolderH));
            DrawRowBottom(ctx, b.Id, HeaderW, W, y + FolderH);
        }
    }

    /// <summary>A bus row's header: its badge, name, "BUS" and M.</summary>
    void DrawBusHeaders(DrawingContext ctx)
    {
        for (int k = 0; k < busRows.Count; k++)
        {
            var b = busRows[k];
            double y = BusY(b);
            if (y + FolderH < RulerH || y > Bounds.Height) continue;
            var c = BusColor(b);
            bool sel = BusSelected(b);
            var row = new Rect(0, y, HeaderW, FolderH);
            ctx.FillRectangle(sel ? Palette.Raised : Palette.Panel, row);
            ctx.FillRectangle(Palette.Alpha(c, sel ? 0.18 : 0.12), row);
            if (sel) DrawSelectedFrame(ctx, y, FolderH);
            TrackIcons.DrawBusBadge(ctx, BusBadgeRect(b), b.Color, b.Icon, sel, 4);
            var mute = BusMuteRect(b);
            double x = 54, maxW = Math.Max(0, (Compact ? HeaderRight : mute.X) - 8 - x);
            var name = Palette.Text(b.Name, 12.5, b.Muted ? Palette.Faint : Palette.Fg, Palette.UiBold);
            ctx.DrawText(b.Name, 12.5, b.Muted ? Palette.Faint : Palette.Fg, new Point(x, y + 9), Palette.UiBold, maxW);
            var tag = Palette.Text(Loc.T("mixer.busTag"), 9.5, Palette.Faint, Palette.UiBold);
            double tx = x + Math.Min(name.Width, maxW) + 8;
            if (!Compact && tx + tag.Width <= mute.X - 6) ctx.DrawText(tag, new Point(tx, y + 12));
            if (!Compact) DrawToggle(ctx, mute, "M", b.Muted, Palette.Mute, Hot(mute));
            DrawRowBottom(ctx, b.Id, 0, HeaderW, y + FolderH);
        }
    }

    /// <summary>Left press on a bus row: it selects the bus; its chevron folds its lanes, its M mutes it.</summary>
    void BusRowPressed(Bus b, Point p, PointerPressedEventArgs e)
    {
        e.Handled = true;
        BusRowSelected?.Invoke(b.Id);
        if (p.X >= HeaderW) return;
        if (BusAutoFoldRect(b).Contains(p)) AutomationFoldRequested?.Invoke(b);
        else if (BusMuteRect(b).Contains(p)) BusMuteRequested?.Invoke(b.Id);
    }

    /// <summary>Right press on a bus row: its M asks for its lane, anywhere else its menu.</summary>
    void BusRowRightPressed(Bus b, Point p)
    {
        BusRowSelected?.Invoke(b.Id);
        if (p.X < HeaderW && BusMuteRect(b).Contains(p)) AutomationAsked?.Invoke(b, AutoTarget.Mute, p);
        else BusRowContextRequested?.Invoke(b, p);
    }
}
