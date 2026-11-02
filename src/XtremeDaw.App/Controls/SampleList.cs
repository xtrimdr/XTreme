using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The Biblioteca's sample list: the folders as a tree, or the files a search found. Drawn by hand and only what is in
/// view, because a library holds a hundred thousand rows and neither a control per row nor a virtualized list keeps up
/// with holding an arrow key. It sits in a <see cref="ScrollViewer"/> at the full height of its rows and paints the
/// stretch the viewer shows. It raises events; the window plays, inserts and remembers.
/// </summary>
public sealed class SampleList : Control
{
    const double RowH = 26, Indent = 14, TextY = 5.5;
    /// <summary>Right side of a row: the wave, then the length or the count, each a fixed column so they line up.</summary>
    const double WaveW = 52, TimeW = 42, Pad = 8;
    /// <summary>Below this width the row's wave gives its room to the name.</summary>
    const double WaveFrom = 250;
    /// <summary>How far the pointer travels before a press on a file becomes a drag.</summary>
    const double DragStart = 6;
    /// <summary>Rows PgUp and PgDn move, when the viewer's height isn't known yet.</summary>
    const int PageRows = 12;
    const string Open = "", Closed = "", FolderGlyph = "", AudioGlyph = "", VideoGlyph = "", StarGlyph = "";
    static readonly Typeface Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>Text per whole second: a row's length is drawn again on every scroll.</summary>
    static readonly Dictionary<int, string> times = [];
    ScrollViewer host = new();
    IReadOnlyList<SampleRow> rows = [];
    int selected = -1, hover = -1, pressed = -1;
    Point pressAt;
    string? playing;

    /// <summary>A row was picked by click or by keyboard.</summary>
    public event Action<SampleRow>? Picked;
    /// <summary>Double click or Enter on a file.</summary>
    public event Action<SampleFile>? Activated;
    /// <summary>A folder wants to open or close.</summary>
    public event Action<SampleNode>? Toggled;
    /// <summary>Space on the list.</summary>
    public event Action? ListenPressed;
    /// <summary>F on a file.</summary>
    public event Action<SampleFile>? FavoritePressed;
    public event Action<SampleRow, Point>? MenuRequested;
    public event Action<SampleFile, PointerEventArgs>? DragRequested;
    /// <summary>A row on screen doesn't know its length yet.</summary>
    public Action<SampleFile>? NeedsInfo { get; set; }
    public Func<SampleFile, bool>? IsFavorite { get; set; }
    /// <summary>Whether a folder of the tree is open: its arrow shows it.</summary>
    public Func<SampleNode, bool>? IsOpen { get; set; }
    public string OfflineText { get; set; } = "";

    public SampleRow? Selected => selected >= 0 && selected < rows.Count ? rows[selected] : null;

    public SampleList()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>Puts the list in its viewer, whose offset says what is on screen.</summary>
    public void Attach(ScrollViewer viewer)
    {
        host = viewer;
        host.Content = this;
        host.ScrollChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>New rows; the selection follows its file or folder when it is still among them.</summary>
    public void SetRows(IReadOnlyList<SampleRow> list)
    {
        var was = Selected;
        rows = list;
        selected = -1;
        hover = -1;
        if (was is { } w)
            for (int i = 0; i < list.Count; i++)
                if (list[i].File == w.File && (w.File != null || list[i].Folder?.Path == w.Folder?.Path)) { selected = i; break; }
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void SetPlaying(string? path)
    {
        if (path == playing) return;
        playing = path;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 0 : available.Width, rows.Count * RowH);

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, top = host.Offset.Y;
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // the whole box takes the pointer
        int from = Math.Max(0, (int)(top / RowH)), to = Math.Min(rows.Count, from + (int)(host.Viewport.Height / RowH) + 2);
        for (int i = from; i < to; i++) DrawRow(ctx, i, i * RowH, w);
    }

    void DrawRow(DrawingContext ctx, int i, double y, double w)
    {
        var row = rows[i];
        if (i == selected) ctx.FillRectangle(Palette.Raised, new Rect(0, y, w, RowH));
        else if (i == hover) ctx.FillRectangle(Palette.Alpha(Palette.RaisedColor, 0.6), new Rect(0, y, w, RowH));
        double x = Pad + row.Depth * Indent;
        double right = w - Pad;
        if (row.Folder is { } folder)
        {
            ctx.DrawText(IsOpen?.Invoke(folder) == true ? Open : Closed, 9, Palette.Faint, new Point(x, y + 8.5), Icons);
            ctx.DrawText(FolderGlyph, 12, folder.Offline ? Palette.Faint : Palette.Dim, new Point(x + 14, y + 7), Icons);
            string info = folder.Offline ? OfflineText : folder.Count.ToString();
            var tail = Palette.Text(info, folder.Offline ? 11 : 10, Palette.Faint, folder.Offline ? Palette.Ui : Palette.Mono);
            ctx.DrawText(tail, new Point(right - tail.Width, y + 7));
            right -= tail.Width + Pad;
            ctx.DrawText(folder.Name, 12, folder.Offline ? Palette.Faint : Palette.Fg, new Point(x + 32, y + TextY), null, Math.Max(10, right - x - 32));
            return;
        }
        var f = row.File!;
        if (f.DurationSecValue == 0) NeedsInfo?.Invoke(f);
        bool on = f.Path == playing;
        ctx.DrawText(f.IsVideo ? VideoGlyph : AudioGlyph, 12, on ? Palette.Accent : Palette.Faint, new Point(x + 14, y + 7), Icons);

        if (f.Broken) ctx.DrawText("—", 10, Palette.Faint, new Point(right - 10, y + 7), Palette.Mono);
        else if (f.DurationSec > 0)
        {
            var t = Palette.Text(Time(f.DurationSec), 10, Palette.Faint, Palette.Mono);
            ctx.DrawText(t, new Point(right - t.Width, y + 7));
        }
        right -= TimeW;
        if (w >= WaveFrom)
        {
            right -= WaveW + Pad;
            if (f.Peaks is { } p) DrawWave(ctx, p, right + Pad, y, on);
        }
        if (IsFavorite?.Invoke(f) == true)
        {
            right -= 16;
            ctx.DrawText(StarGlyph, 11, Palette.Accent, new Point(right + 2, y + 7.5), Icons);
        }
        ctx.DrawText(f.Name, 12, f.Broken ? Palette.Faint : on ? Palette.Accent : Palette.Fg, new Point(x + 32, y + TextY), null, Math.Max(10, right - x - 32));
    }

    static void DrawWave(DrawingContext ctx, byte[] peaks, double x, double y, bool on)
    {
        var brush = on ? Palette.Accent : Palette.Alpha(Palette.FaintColor, 0.8);
        double mid = y + RowH / 2, step = WaveW / peaks.Length;
        for (int i = 0; i < peaks.Length; i++)
        {
            double h = Math.Max(1, peaks[i] / 255.0 * (RowH - 10));
            ctx.FillRectangle(brush, new Rect(x + i * step, mid - h / 2, Math.Max(0.75, step - 0.25), h));
        }
    }

    static string Time(double sec)
    {
        int s = (int)Math.Ceiling(sec);
        if (times.TryGetValue(s, out var t)) return t;
        return times[s] = s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    int RowAt(Point p)
    {
        int i = (int)(p.Y / RowH);
        return p.Y >= 0 && i < rows.Count ? i : -1;
    }

    void Select(int i, bool scroll = true)
    {
        selected = i;
        InvalidateVisual();
        if (i < 0) return;
        if (scroll)
        {
            double y = i * RowH, top = host.Offset.Y, h = host.Viewport.Height;
            if (y < top) host.Offset = host.Offset.WithY(y);
            else if (h > 0 && y + RowH > top + h) host.Offset = host.Offset.WithY(y + RowH - h);
        }
        Picked?.Invoke(rows[i]);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        int i = RowAt(point.Position);
        Focus();
        if (i < 0) return;
        e.Handled = true;
        if (point.Properties.IsRightButtonPressed)
        {
            Select(i, false);
            MenuRequested?.Invoke(rows[i], point.Position);
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        if (rows[i].Folder is { } folder)
        {
            Select(i, false);
            Toggled?.Invoke(folder);
            return;
        }
        if (e.ClickCount == 2)
        {
            Activated?.Invoke(rows[i].File!);
            return;
        }
        pressed = i;
        pressAt = point.Position;
        Select(i, false);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (pressed >= 0 && pressed < rows.Count && rows[pressed].File is { } f && Point.Distance(p, pressAt) >= DragStart)
        {
            pressed = -1;
            DragRequested?.Invoke(f, e);
            return;
        }
        int i = RowAt(p);
        if (i == hover) return;
        hover = i;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) => pressed = -1;

    protected override void OnPointerExited(PointerEventArgs e)
    {
        hover = -1;
        InvalidateVisual();
    }

    /// <summary>The list's own keys. The window asks first, because Space and the arrows are also shortcuts of the project.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None) return false;
        int page = host.Viewport.Height > 0 ? (int)(host.Viewport.Height / RowH) : PageRows;
        switch (e.Key)
        {
            case Key.Down: Move(selected + 1); return true;
            case Key.Up: Move(selected < 0 ? 0 : selected - 1); return true;
            case Key.PageDown: Move(selected + page); return true;
            case Key.PageUp: Move(selected - page); return true;
            case Key.Home: Move(0); return true;
            case Key.End: Move(rows.Count - 1); return true;
            case Key.Space: ListenPressed?.Invoke(); return true;
            case Key.Right:
                if (Selected?.Folder is { } closed && IsOpen?.Invoke(closed) == false) Toggled?.Invoke(closed);
                else Move(selected + 1);
                return true;
            case Key.Left:
                if (Selected?.Folder is { } shown && IsOpen?.Invoke(shown) == true) Toggled?.Invoke(shown);
                else GoToParent();
                return true;
            case Key.Enter:
                if (Selected?.Folder is { } folder) Toggled?.Invoke(folder);
                else if (Selected?.File is { } file) Activated?.Invoke(file);
                return Selected != null;
            case Key.F:
                if (Selected?.File is { } star) FavoritePressed?.Invoke(star);
                return Selected?.File != null;
            default: return false;
        }
    }

    void Move(int i)
    {
        if (rows.Count > 0) Select(Math.Clamp(i, 0, rows.Count - 1));
    }

    void GoToParent()
    {
        if (Selected is not { } row) return;
        for (int i = selected - 1; i >= 0; i--)
            if (rows[i].Folder != null && rows[i].Depth < row.Depth) { Move(i); return; }
    }
}
