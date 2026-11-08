using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using XtremeDaw.App.Controls;

namespace XtremeDaw.App.Terminal;

/// <summary>
/// Draws a <see cref="TerminalScreen"/> and sends the keys to its program (#278): the console of a Claude session inside
/// the bench. Cells in Cascadia Mono on the app's background, the cursor in the accent. The wheel goes through the
/// lines that left the screen; dragging selects, right click copies the selection (or pastes, with none), Ctrl+C copies
/// when something is selected and otherwise interrupts, Ctrl+V pastes. Redraws at most <see cref="Frame"/> apart.
/// </summary>
sealed class TerminalView : Control
{
    const double FontSize = 13;
    /// <summary>Space around the cells.</summary>
    const double Pad = 8;
    /// <summary>Lines per wheel step.</summary>
    const int WheelLines = 3;
    static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    readonly TerminalScreen screen;
    readonly Action<string> send;
    readonly Typeface face = new(Palette.Code), bold = new(Palette.Code, FontStyle.Normal, FontWeight.Bold),
        italic = new(Palette.Code, FontStyle.Italic), boldItalic = new(Palette.Code, FontStyle.Italic, FontWeight.Bold);
    readonly double cw, ch;
    readonly Dictionary<int, IBrush> brushes = [];
    /// <summary>Lines above the bottom the view is scrolled to (0 = following the program).</summary>
    int scroll;
    long drawn = -1;
    readonly DispatcherTimer poll;
    /// <summary>Selection in absolute lines (scrollback + screen), anchor and end; null = none.</summary>
    (int L, int C)? selA, selB;
    bool selecting;
    long selDropped;

    /// <summary>The size in cells changed: the program's console follows.</summary>
    public event Action<int, int>? CellsChanged;

    public TerminalView(TerminalScreen screen, Action<string> send)
    {
        this.screen = screen;
        this.send = send;
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        var probe = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, FontSize, Brushes.White);
        cw = probe.WidthIncludingTrailingWhitespace / 10;
        ch = Math.Ceiling(probe.Height);
        // the output comes on another thread: the screen's version says when there is something new to draw
        poll = new DispatcherTimer(Frame, DispatcherPriority.Render, (_, _) => Poll());
        AttachedToVisualTree += (_, _) => poll.Start();
        DetachedFromVisualTree += (_, _) => poll.Stop();
        GotFocus += (_, _) => InvalidateVisual();
        LostFocus += (_, _) => InvalidateVisual();
    }

    void Poll()
    {
        long v;
        lock (screen.Sync) v = screen.Version;
        if (v != drawn) InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        int cols = Math.Max(20, (int)((e.NewSize.Width - Pad * 2) / cw));
        int rows = Math.Max(4, (int)((e.NewSize.Height - Pad * 2) / ch));
        lock (screen.Sync)
        {
            if (cols == screen.Cols && rows == screen.Rows) return;
            screen.Resize(cols, rows);
        }
        CellsChanged?.Invoke(cols, rows);
    }

    public override void Render(DrawingContext ctx)
    {
        var bg = Palette.App;
        ctx.FillRectangle(bg, new Rect(Bounds.Size));
        lock (screen.Sync)
        {
            drawn = screen.Version;
            scroll = Math.Clamp(scroll, 0, screen.ScrollbackCount);
            int rows = screen.Rows, cols = screen.Cols;
            int first = screen.ScrollbackCount - scroll;
            var (sa, sb) = Selection();
            for (int r = 0; r < rows; r++)
            {
                int abs = first + r;
                if (abs < 0 || abs >= screen.TotalLines) continue;
                DrawRow(ctx, screen.Line(abs), abs, Pad + r * ch, cols, sa, sb);
            }
            if (scroll == 0 && screen.CursorVisible)
            {
                var at = new Rect(Pad + screen.X * cw, Pad + screen.Y * ch, cw, ch);
                if (IsFocused) ctx.FillRectangle(Palette.Alpha(Palette.AccentColor, 0.7), at);
                else ctx.DrawRectangle(Palette.Pen(Palette.Accent), at.Deflate(0.5));
            }
            if (scroll > 0)
            {
                // how far up the view is
                var t = new FormattedText(Loc.T("test.consoles.scrolled", scroll), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    face, 11, Palette.Dim);
                var box = new Rect(Bounds.Width - t.Width - 20, 6, t.Width + 12, t.Height + 6);
                ctx.FillRectangle(Palette.Raised, box);
                ctx.DrawText(t, new Point(box.X + 6, box.Y + 3));
            }
        }
    }

    void DrawRow(DrawingContext ctx, Cell[] line, int abs, double y, int cols, (int L, int C) sa, (int L, int C) sb)
    {
        int n = Math.Min(cols, line.Length);
        // backgrounds first, in runs of the same color, then the text, so a wide glyph is never painted over
        int c = 0;
        while (c < n)
        {
            var (_, b0) = Colors(line[c]);
            bool s0 = Selected(abs, c, sa, sb);
            int e = c + 1;
            while (e < n && Colors(line[e]).Bg == b0 && Selected(abs, e, sa, sb) == s0) e++;
            var rect = new Rect(Pad + c * cw, y, (e - c) * cw, ch);
            if (b0 != -1) ctx.FillRectangle(Brush(b0, false), rect);
            if (s0) ctx.FillRectangle(Palette.Alpha(Palette.AccentColor, 0.35), rect);
            c = e;
        }
        c = 0;
        while (c < n)
        {
            var cell = line[c];
            if (cell.Ch == 0 || cell.Ch == ' ' || cell.Style.HasFlag(CellStyle.WideTail) || cell.Style.HasFlag(CellStyle.Hidden))
            {
                c++;
                continue;
            }
            var (f0, _) = Colors(cell);
            var st = cell.Style & (CellStyle.Bold | CellStyle.Italic | CellStyle.Underline | CellStyle.Strike | CellStyle.Dim);
            bool wide = c + 1 < n && line[c + 1].Style.HasFlag(CellStyle.WideTail);
            var run = new System.Text.StringBuilder();
            int e = c;
            if (wide || !Grid(cell.Ch))
            {
                run.Append(char.ConvertFromUtf32(cell.Ch));
                e = wide ? c + 2 : c + 1;
            }
            else
            {
                // narrow characters with the same look go together; a wide one or a blank ends the run
                while (e < n)
                {
                    var x = line[e];
                    if (x.Style.HasFlag(CellStyle.WideTail) || x.Style.HasFlag(CellStyle.Hidden)) break;
                    if (e + 1 < n && line[e + 1].Style.HasFlag(CellStyle.WideTail)) break;
                    if (x.Ch != 0 && !Grid(x.Ch)) break;
                    if (x.Ch != 0 && x.Ch != ' ' && (Colors(x).Fg != f0 || (x.Style & (CellStyle.Bold | CellStyle.Italic | CellStyle.Underline | CellStyle.Strike | CellStyle.Dim)) != st)) break;
                    run.Append(x.Ch == 0 ? " " : char.ConvertFromUtf32(x.Ch));
                    e++;
                }
            }
            var tf = st.HasFlag(CellStyle.Bold) ? (st.HasFlag(CellStyle.Italic) ? boldItalic : bold) : st.HasFlag(CellStyle.Italic) ? italic : face;
            var brush = Brush(f0, true, st.HasFlag(CellStyle.Dim));
            var text = new FormattedText(run.ToString().TrimEnd(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, FontSize, brush);
            if (st.HasFlag(CellStyle.Underline)) text.SetTextDecorations(TextDecorations.Underline);
            if (st.HasFlag(CellStyle.Strike)) text.SetTextDecorations(TextDecorations.Strikethrough);
            ctx.DrawText(text, new Point(Pad + c * cw, y));
            c = e;
        }
    }

    /// <summary>
    /// Characters the mono font draws one cell wide for sure (ASCII, Latin-1 letters, box drawing): they go together in
    /// a run. Any other one may come from a fallback font of another width, so it is drawn alone at its own cell.
    /// </summary>
    static bool Grid(int cp) => cp is (>= 0x20 and < 0x7f) or (>= 0xa0 and <= 0x17f) or (>= 0x2500 and <= 0x259f);

    /// <summary>Foreground and background of a cell, inverse applied (-1 = default).</summary>
    static (int Fg, int Bg) Colors(Cell c)
    {
        int f = c.Fg, b = c.Bg;
        if (c.Style.HasFlag(CellStyle.Inverse))
        {
            // the defaults swap too: inverse on default is the text color as background
            (f, b) = (b == -1 ? -2 : b, f == -1 ? -3 : f);
        }
        if (c.Style.HasFlag(CellStyle.Bold) && f is >= 0 and < 8) f += 8;
        return (f, b);
    }

    IBrush Brush(int color, bool fg, bool dim = false)
    {
        // -1 default, -2 = the background as text, -3 = the text color as background
        if (color == -1) return dim ? Palette.Dim : fg ? Palette.Fg : Palette.App;
        if (color == -2) return Palette.App;
        if (color == -3) return Palette.Fg;
        int key = dim ? color | 0x2000000 : color;
        if (brushes.TryGetValue(key, out var b)) return b;
        var c = ToColor(color);
        if (dim) c = Color.FromRgb((byte)(c.R * 0.6), (byte)(c.G * 0.6), (byte)(c.B * 0.6));
        b = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(c);
        brushes[key] = b;
        return b;
    }

    /// <summary>The 16 colors tuned for the dark background, then the xterm cube and grays.</summary>
    static readonly uint[] Base16 =
    [
        0x1c1c20, 0xef4444, 0x22c55e, 0xeab308, 0x3b82f6, 0xa855f7, 0x22d3ee, 0xd4d4d8,
        0x6b6b74, 0xf87171, 0x4ade80, 0xfacc15, 0x60a5fa, 0xc084fc, 0x67e8f9, 0xfafafa,
    ];

    static Color ToColor(int v)
    {
        if ((v & TerminalScreen.Rgb) != 0) return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        if (v < 16) return Color.FromUInt32(0xff000000 | Base16[v]);
        if (v < 232)
        {
            int i = v - 16;
            static byte L(int x) => (byte)(x == 0 ? 0 : 55 + x * 40);
            return Color.FromRgb(L(i / 36), L(i / 6 % 6), L(i % 6));
        }
        byte g = (byte)(8 + (v - 232) * 10);
        return Color.FromRgb(g, g, g);
    }

    // ---- selection ----

    ((int L, int C) A, (int L, int C) B) Selection()
    {
        if (selA is not { } a || selB is not { } b) return ((-1, 0), (-2, 0));
        // lines that left the scrollback moved the selection up
        int d = (int)(screen.Dropped - selDropped);
        a.L -= d;
        b.L -= d;
        return a.L < b.L || (a.L == b.L && a.C <= b.C) ? (a, b) : (b, a);
    }

    static bool Selected(int l, int c, (int L, int C) a, (int L, int C) b) =>
        (l > a.L || (l == a.L && c >= a.C)) && (l < b.L || (l == b.L && c <= b.C));

    (int L, int C) CellAt(Point p)
    {
        int r = (int)Math.Floor((p.Y - Pad) / ch), c = (int)Math.Floor((p.X - Pad) / cw);
        r = Math.Clamp(r, 0, screen.Rows - 1);
        c = Math.Clamp(c, 0, screen.Cols - 1);
        return (screen.ScrollbackCount - scroll + r, c);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsRightButtonPressed)
        {
            if (selA != null) Copy();
            else Paste();
            e.Handled = true;
            return;
        }
        if (!pt.Properties.IsLeftButtonPressed) return;
        lock (screen.Sync)
        {
            var at = CellAt(e.GetPosition(this));
            selDropped = screen.Dropped;
            if (e.ClickCount == 2) SelectWord(at);
            else
            {
                selA = at;
                selB = null;
                selecting = true;
            }
        }
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    void SelectWord((int L, int C) at)
    {
        var line = screen.Line(at.L);
        bool Word(int c) => c >= 0 && c < line.Length && line[c].Ch != 0 && line[c].Ch != ' ';
        if (!Word(at.C)) return;
        int a = at.C, b = at.C;
        while (Word(a - 1)) a--;
        while (Word(b + 1)) b++;
        selA = (at.L, a);
        selB = (at.L, b);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!selecting) return;
        lock (screen.Sync)
        {
            var at = CellAt(e.GetPosition(this));
            int d = (int)(screen.Dropped - selDropped);
            selB = (at.L + d, at.C);
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!selecting) return;
        selecting = false;
        e.Pointer.Capture(null);
        // a click without a drag selects nothing
        if (selB == null || selB == selA) selA = selB = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        lock (screen.Sync) scroll = Math.Clamp(scroll + (int)Math.Round(e.Delta.Y * WheelLines), 0, screen.ScrollbackCount);
        InvalidateVisual();
        e.Handled = true;
    }

    async void Copy()
    {
        string text;
        lock (screen.Sync)
        {
            var (a, b) = Selection();
            if (a.L < 0 && b.L < 0) return;
            text = screen.Text(a.L, a.C, b.L, b.C);
        }
        selA = selB = null;
        InvalidateVisual();
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clip && text != "") await clip.SetTextAsync(text);
    }

    async void Paste()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clip) return;
        string? text = await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clip);
        if (string.IsNullOrEmpty(text)) return;
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        bool bracketed;
        lock (screen.Sync) bracketed = screen.BracketedPaste;
        Type(bracketed ? "\x1b[200~" + text + "\x1b[201~" : text);
    }

    /// <summary>Sends to the program and goes back to the bottom, where the answer will show.</summary>
    void Type(string s)
    {
        if (scroll != 0)
        {
            scroll = 0;
            InvalidateVisual();
        }
        send(s);
    }

    // ---- keys ----

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (string.IsNullOrEmpty(e.Text) || e.Text.Any(c => c < 0x20 || c == 0x7f)) return;
        Type(e.Text);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var m = e.KeyModifiers;
        bool ctrl = m.HasFlag(KeyModifiers.Control), shift = m.HasFlag(KeyModifiers.Shift), alt = m.HasFlag(KeyModifiers.Alt);
        bool app;
        lock (screen.Sync) app = screen.AppCursor;
        string? s = null;
        if (ctrl && e.Key == Key.C && (shift || selA != null)) { Copy(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.V || shift && e.Key == Key.Insert) { Paste(); e.Handled = true; return; }
        // xterm's modifier number for keys with a modifier: 1 + shift + 2·alt + 4·ctrl
        int mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        string Csi(char final) => mod > 1 ? $"\x1b[1;{mod}{final}" : app ? $"\x1bO{final}" : $"\x1b[{final}";
        string Tilde(int n) => mod > 1 ? $"\x1b[{n};{mod}~" : $"\x1b[{n}~";
        switch (e.Key)
        {
            // Shift+Enter and Alt+Enter: a new line in Claude's prompt (meta-Enter), not sending it
            case Key.Enter: s = shift || alt ? "\x1b\r" : "\r"; break;
            case Key.Back: s = ctrl ? "\x08" : alt ? "\x1b\x7f" : "\x7f"; break;
            case Key.Tab: s = shift ? "\x1b[Z" : "\t"; break;
            case Key.Escape: s = "\x1b"; break;
            case Key.Up: s = Csi('A'); break;
            case Key.Down: s = Csi('B'); break;
            case Key.Right: s = Csi('C'); break;
            case Key.Left: s = Csi('D'); break;
            case Key.Home: s = Csi('H'); break;
            case Key.End: s = Csi('F'); break;
            case Key.PageUp:
                if (shift) { ScrollPage(1); e.Handled = true; return; }
                s = Tilde(5);
                break;
            case Key.PageDown:
                if (shift) { ScrollPage(-1); e.Handled = true; return; }
                s = Tilde(6);
                break;
            case Key.Insert: s = Tilde(2); break;
            case Key.Delete: s = Tilde(3); break;
            case >= Key.F1 and <= Key.F4: s = mod > 1 ? $"\x1b[1;{mod}{(char)('P' + (e.Key - Key.F1))}" : $"\x1bO{(char)('P' + (e.Key - Key.F1))}"; break;
            case >= Key.F5 and <= Key.F12:
                {
                    int[] codes = [15, 17, 18, 19, 20, 21, 23, 24];
                    s = Tilde(codes[e.Key - Key.F5]);
                    break;
                }
            case Key.Space when ctrl: s = "\0"; break;
            case >= Key.A and <= Key.Z when ctrl && !alt:
                s = ((char)(e.Key - Key.A + 1)).ToString();
                break;
            case >= Key.A and <= Key.Z when alt && !ctrl:
                s = "\x1b" + (char)((shift ? 'A' : 'a') + (e.Key - Key.A));
                break;
            case Key.OemOpenBrackets when ctrl: s = "\x1b"; break;
        }
        if (s == null) return;
        Type(s);
        e.Handled = true;
    }

    void ScrollPage(int dir)
    {
        lock (screen.Sync) scroll = Math.Clamp(scroll + dir * Math.Max(1, screen.Rows - 1), 0, screen.ScrollbackCount);
        InvalidateVisual();
    }
}
