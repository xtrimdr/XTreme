using System.Globalization;
using System.Text;

namespace XtremeDaw.App.Terminal;

/// <summary>One character cell: its code point (0 = blank), colors and style. A wide character's second cell is <see cref="Cell.Wide"/>.</summary>
struct Cell
{
    public int Ch;
    /// <summary>-1 = default; 0–255 = palette; <see cref="TerminalScreen.Rgb"/> | 0xRRGGBB = true color.</summary>
    public int Fg, Bg;
    public CellStyle Style;

    public const CellStyle Wide = CellStyle.WideTail;
    public static readonly Cell Blank = new() { Fg = -1, Bg = -1 };
}

[Flags]
enum CellStyle : byte
{
    None = 0, Bold = 1, Dim = 2, Italic = 4, Underline = 8, Inverse = 16, Strike = 32, Hidden = 64, WideTail = 128,
}

/// <summary>
/// The screen of a VT terminal (#278): what a pseudo console writes, as rows of cells with colors, plus the lines that
/// went off the top. It understands what ConPTY and Claude Code send (cursor moves, erasing, scrolling regions, colors
/// up to true color, the alternate screen, bracketed paste and the queries a terminal answers), not every VT feature:
/// the rest is read and dropped. Fed on the reader thread and drawn on the UI thread, both under <see cref="Sync"/>.
/// </summary>
sealed class TerminalScreen
{
    /// <summary>Lines kept above the screen.</summary>
    const int MaxScrollback = 5000;
    public const int Rgb = 0x1000000;

    public readonly object Sync = new();
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    Cell[][] main, alt;
    Cell[][] lines;
    readonly List<Cell[]> scrollback = [];
    /// <summary>Lines that left the scrollback at the top: keeps a selection on its text while new lines come.</summary>
    public long Dropped { get; private set; }

    public int X { get; private set; }
    public int Y { get; private set; }
    bool pendingWrap;
    int top, bottom;
    Cell attr = Cell.Blank;
    (int X, int Y, Cell Attr) saved;
    bool[] tabs = [];

    public bool CursorVisible { get; private set; } = true;
    public bool AppCursor { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool AltScreen => lines == alt;
    /// <summary>Changes on every write, so the view knows when to draw.</summary>
    public long Version { get; private set; }
    /// <summary>What the terminal answers (cursor position, device attributes), to write back to the program.</summary>
    public event Action<string>? Reply;

    public TerminalScreen(int cols, int rows)
    {
        Cols = Math.Max(2, cols);
        Rows = Math.Max(1, rows);
        main = NewLines(Rows, Cols);
        alt = NewLines(Rows, Cols);
        lines = main;
        bottom = Rows - 1;
        ResetTabs();
    }

    static Cell[][] NewLines(int rows, int cols)
    {
        var l = new Cell[rows][];
        for (int i = 0; i < rows; i++) l[i] = NewLine(cols, Cell.Blank);
        return l;
    }

    static Cell[] NewLine(int cols, Cell fill)
    {
        var l = new Cell[cols];
        Array.Fill(l, fill);
        return l;
    }

    void ResetTabs()
    {
        tabs = new bool[Cols];
        for (int i = 8; i < Cols; i += 8) tabs[i] = true;
    }

    /// <summary>Scrollback plus screen: the lines a view can show, oldest first.</summary>
    public int TotalLines => scrollback.Count + Rows;
    public int ScrollbackCount => scrollback.Count;
    public Cell[] Line(int i) => i < scrollback.Count ? scrollback[i] : lines[i - scrollback.Count];

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(2, cols);
        rows = Math.Max(1, rows);
        if (cols == Cols && rows == Rows) return;
        // on the main screen the lines above the cursor that no longer fit go to the scrollback
        int shift = Math.Max(0, Y - (rows - 1));
        main = Fit(main, cols, rows, shift, true);
        alt = Fit(alt, cols, rows, shift, false);
        lines = AltScreen ? alt : main;
        // the field above was rebuilt: which one is in use follows the mode
        Cols = cols;
        Rows = rows;
        Y = Math.Max(0, Y - shift);
        X = Math.Min(X, cols - 1);
        pendingWrap = false;
        top = 0;
        bottom = rows - 1;
        ResetTabs();
        Version++;
    }

    Cell[][] Fit(Cell[][] old, int cols, int rows, int shift, bool keep)
    {
        if (keep && old == main)
            for (int i = 0; i < shift && i < old.Length; i++) Push(old[i]);
        var l = new Cell[rows][];
        for (int i = 0; i < rows; i++)
        {
            l[i] = NewLine(cols, Cell.Blank);
            int from = i + shift;
            if (from < old.Length) Array.Copy(old[from], l[i], Math.Min(cols, old[from].Length));
        }
        return l;
    }

    void Push(Cell[] line)
    {
        scrollback.Add(line);
        if (scrollback.Count > MaxScrollback)
        {
            scrollback.RemoveAt(0);
            Dropped++;
        }
    }

    // ---- parser ----

    enum State { Ground, Escape, EscapeSkip, Csi, Osc, OscEscape, Dcs, DcsEscape }
    State state;
    readonly StringBuilder csi = new();
    char highSurrogate;

    public void Feed(string text)
    {
        foreach (char c in text) Step(c);
        Version++;
    }

    void Step(char c)
    {
        switch (state)
        {
            case State.Ground:
                Ground(c);
                break;
            case State.Escape:
                Escape(c);
                break;
            case State.EscapeSkip:
                state = State.Ground;
                break;
            case State.Csi:
                if (c >= 0x40 && c <= 0x7e)
                {
                    state = State.Ground;
                    Csi(csi.ToString(), c);
                }
                else if (c == 0x1b) state = State.Escape;
                else if (c >= 0x20) csi.Append(c);
                else Control(c);
                break;
            case State.Osc:
                // the title and links: read and dropped
                if (c == 0x07) state = State.Ground;
                else if (c == 0x1b) state = State.OscEscape;
                break;
            case State.OscEscape:
                state = c == '\\' ? State.Ground : State.Osc;
                break;
            case State.Dcs:
                if (c == 0x1b) state = State.DcsEscape;
                break;
            case State.DcsEscape:
                state = c == '\\' ? State.Ground : State.Dcs;
                break;
        }
    }

    void Ground(char c)
    {
        if (c < 0x20 || c == 0x7f)
        {
            Control(c);
            return;
        }
        if (char.IsHighSurrogate(c))
        {
            highSurrogate = c;
            return;
        }
        int cp = c;
        if (char.IsLowSurrogate(c))
        {
            if (highSurrogate == 0) return;
            cp = char.ConvertToUtf32(highSurrogate, c);
            highSurrogate = '\0';
        }
        Print(cp);
    }

    void Control(char c)
    {
        switch (c)
        {
            case '\x1b':
                state = State.Escape;
                break;
            case '\b':
                if (X > 0) X--;
                pendingWrap = false;
                break;
            case '\t':
                pendingWrap = false;
                do X++; while (X < Cols - 1 && !tabs[X]);
                X = Math.Min(X, Cols - 1);
                break;
            case '\n':
            case '\v':
            case '\f':
                LineFeed();
                break;
            case '\r':
                X = 0;
                pendingWrap = false;
                break;
        }
    }

    void Escape(char c)
    {
        state = State.Ground;
        switch (c)
        {
            case '[':
                csi.Clear();
                state = State.Csi;
                break;
            case ']':
                state = State.Osc;
                break;
            case 'P':
            case 'X':
            case '^':
            case '_':
                state = State.Dcs;
                break;
            case '(':
            case ')':
            case '*':
            case '+':
            case '#':
            case '%':
            case ' ':
                state = State.EscapeSkip;
                break;
            case '7':
                Save();
                break;
            case '8':
                Restore();
                break;
            case 'D':
                LineFeed();
                break;
            case 'E':
                X = 0;
                LineFeed();
                break;
            case 'M':
                ReverseIndex();
                break;
            case 'H':
                if (X < Cols) tabs[X] = true;
                break;
            case 'c':
                Reset();
                break;
        }
    }

    void Reset()
    {
        main = NewLines(Rows, Cols);
        alt = NewLines(Rows, Cols);
        lines = main;
        X = Y = 0;
        pendingWrap = false;
        top = 0;
        bottom = Rows - 1;
        attr = Cell.Blank;
        CursorVisible = true;
        AppCursor = BracketedPaste = false;
        ResetTabs();
    }

    void Save() => saved = (X, Y, attr);

    void Restore()
    {
        X = Math.Min(saved.X, Cols - 1);
        Y = Math.Min(saved.Y, Rows - 1);
        attr = saved.Attr;
        pendingWrap = false;
    }

    void Print(int cp)
    {
        int w = Width(cp);
        if (w == 0)
        {
            // a joiner or a variation selector: the previous character already holds the place
            return;
        }
        if (pendingWrap)
        {
            X = 0;
            LineFeed();
        }
        if (X + w > Cols)
        {
            X = 0;
            LineFeed();
        }
        var line = lines[Y];
        // writing over half of a wide character clears the other half
        if (line[X].Style.HasFlag(CellStyle.WideTail) && X > 0) line[X - 1] = Blank();
        if (X + w < Cols && line[X + w].Style.HasFlag(CellStyle.WideTail)) line[X + w] = Blank();
        var cell = attr;
        cell.Ch = cp;
        cell.Style &= ~CellStyle.WideTail;
        line[X] = cell;
        if (w == 2)
        {
            var tail = attr;
            tail.Ch = 0;
            tail.Style |= CellStyle.WideTail;
            line[X + 1] = tail;
        }
        X += w;
        if (X >= Cols)
        {
            X = Cols - 1;
            pendingWrap = true;
        }
    }

    /// <summary>A blank cell in the current background (what erasing leaves).</summary>
    Cell Blank() => new() { Fg = attr.Fg, Bg = attr.Bg };

    void LineFeed()
    {
        pendingWrap = false;
        if (Y == bottom) ScrollUp(1);
        else if (Y < Rows - 1) Y++;
    }

    void ReverseIndex()
    {
        pendingWrap = false;
        if (Y == top) ScrollDown(1);
        else if (Y > 0) Y--;
    }

    void ScrollUp(int n)
    {
        n = Math.Min(n, bottom - top + 1);
        for (int k = 0; k < n; k++)
        {
            var gone = lines[top];
            if (top == 0 && !AltScreen) Push(gone);
            for (int i = top; i < bottom; i++) lines[i] = lines[i + 1];
            lines[bottom] = NewLine(Cols, Blank());
        }
    }

    void ScrollDown(int n)
    {
        n = Math.Min(n, bottom - top + 1);
        for (int k = 0; k < n; k++)
        {
            for (int i = bottom; i > top; i--) lines[i] = lines[i - 1];
            lines[top] = NewLine(Cols, Blank());
        }
    }

    void Csi(string body, char final)
    {
        char prefix = body.Length > 0 && body[0] is '?' or '>' or '<' or '=' ? body[0] : '\0';
        if (prefix != '\0') body = body[1..];
        // intermediates (space, !, ", $, ') come before the final: those sequences are not ours
        int inter = body.IndexOfAny([' ', '!', '"', '$', '\'', '#', '*', '+']);
        string im = inter >= 0 ? body[inter..] : "";
        if (inter >= 0) body = body[..inter];
        var groups = Params(body);
        int P(int i, int def = 1) => i < groups.Count && groups[i][0] > 0 ? groups[i][0] : def;
        int P0(int i) => i < groups.Count ? groups[i][0] : 0;
        if (im != "")
        {
            // DECSTR (CSI ! p): soft reset
            if (im == "!" && final == 'p') { attr = Cell.Blank; CursorVisible = true; AppCursor = BracketedPaste = false; top = 0; bottom = Rows - 1; }
            return;
        }
        switch (final)
        {
            case '@': InsertChars(P(0)); break;
            case 'A': Y = Math.Max(Y < top ? 0 : top, Y - P(0)); pendingWrap = false; break;
            case 'B': Y = Math.Min(Y > bottom ? Rows - 1 : bottom, Y + P(0)); pendingWrap = false; break;
            case 'C': X = Math.Min(Cols - 1, X + P(0)); pendingWrap = false; break;
            case 'D': X = Math.Max(0, X - P(0)); pendingWrap = false; break;
            case 'E': Y = Math.Min(Rows - 1, Y + P(0)); X = 0; pendingWrap = false; break;
            case 'F': Y = Math.Max(0, Y - P(0)); X = 0; pendingWrap = false; break;
            case 'G':
            case '`': X = Math.Clamp(P(0) - 1, 0, Cols - 1); pendingWrap = false; break;
            case 'a': X = Math.Min(Cols - 1, X + P(0)); pendingWrap = false; break;
            case 'H':
            case 'f':
                Y = Math.Clamp(P(0) - 1, 0, Rows - 1);
                X = Math.Clamp(P(1) - 1, 0, Cols - 1);
                pendingWrap = false;
                break;
            case 'd': Y = Math.Clamp(P(0) - 1, 0, Rows - 1); pendingWrap = false; break;
            case 'e': Y = Math.Min(Rows - 1, Y + P(0)); pendingWrap = false; break;
            case 'I': for (int n = P(0); n > 0; n--) Control('\t'); break;
            case 'Z':
                for (int n = P(0); n > 0 && X > 0; n--)
                    do X--; while (X > 0 && !tabs[X]);
                break;
            case 'g':
                if (P0(0) == 3) Array.Clear(tabs);
                else if (X < Cols) tabs[X] = false;
                break;
            case 'J': EraseDisplay(P0(0)); break;
            case 'K': EraseLine(P0(0)); break;
            case 'L': InsertLines(P(0)); break;
            case 'M': DeleteLines(P(0)); break;
            case 'P': DeleteChars(P(0)); break;
            case 'S': if (prefix == '\0') ScrollUp(P(0)); break;
            case 'T': if (prefix == '\0') ScrollDown(P(0)); break;
            case 'X':
                {
                    var line = lines[Y];
                    for (int i = X, end = Math.Min(Cols, X + P(0)); i < end; i++) line[i] = Blank();
                    break;
                }
            case 'b':
                {
                    // repeat the last character: rare, left blank-safe
                    break;
                }
            case 'm':
                if (prefix == '\0') Sgr(groups);
                break;
            case 'h':
            case 'l':
                if (prefix == '?') foreach (var g in groups) DecMode(g[0], final == 'h');
                break;
            case 'r':
                if (prefix == '\0')
                {
                    int t = P(0) - 1, b = P(1, Rows) - 1;
                    if (t < b && b < Rows)
                    {
                        top = t;
                        bottom = b;
                        X = Y = 0;
                        pendingWrap = false;
                    }
                }
                break;
            case 's':
                if (prefix == '\0') Save();
                break;
            case 'u':
                if (prefix == '\0') Restore();
                break;
            case 'n':
                if (prefix == '\0' && P0(0) == 5) Reply?.Invoke("\x1b[0n");
                else if (P0(0) == 6) Reply?.Invoke(string.Create(CultureInfo.InvariantCulture, $"\x1b[{(prefix == '?' ? "?" : "")}{Y + 1};{X + 1}R"));
                break;
            case 'c':
                if (prefix == '\0') Reply?.Invoke("\x1b[?1;2c");
                else if (prefix == '>') Reply?.Invoke("\x1b[>0;10;1c");
                break;
        }
    }

    /// <summary>The parameters: one group per <c>;</c>, each with its <c>:</c> sub-parameters (-1 = left empty).</summary>
    static List<int[]> Params(string body)
    {
        var groups = new List<int[]>();
        if (body.Length == 0) return groups;
        foreach (var g in body.Split(';'))
        {
            var parts = g.Split(':');
            var v = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                v[i] = int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? Math.Min(n, 65535) : (parts[i].Length == 0 && parts.Length > 1 ? -1 : 0);
            groups.Add(v);
        }
        return groups;
    }

    void Sgr(List<int[]> groups)
    {
        if (groups.Count == 0)
        {
            attr = Cell.Blank;
            return;
        }
        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            int p = g[0];
            switch (p)
            {
                case 0: attr = Cell.Blank; break;
                case 1: attr.Style |= CellStyle.Bold; break;
                case 2: attr.Style |= CellStyle.Dim; break;
                case 3: attr.Style |= CellStyle.Italic; break;
                case 4:
                    if (g.Length > 1 && g[1] == 0) attr.Style &= ~CellStyle.Underline;
                    else attr.Style |= CellStyle.Underline;
                    break;
                case 7: attr.Style |= CellStyle.Inverse; break;
                case 8: attr.Style |= CellStyle.Hidden; break;
                case 9: attr.Style |= CellStyle.Strike; break;
                case 21: attr.Style |= CellStyle.Underline; break;
                case 22: attr.Style &= ~(CellStyle.Bold | CellStyle.Dim); break;
                case 23: attr.Style &= ~CellStyle.Italic; break;
                case 24: attr.Style &= ~CellStyle.Underline; break;
                case 27: attr.Style &= ~CellStyle.Inverse; break;
                case 28: attr.Style &= ~CellStyle.Hidden; break;
                case 29: attr.Style &= ~CellStyle.Strike; break;
                case >= 30 and <= 37: attr.Fg = p - 30; break;
                case 39: attr.Fg = -1; break;
                case >= 40 and <= 47: attr.Bg = p - 40; break;
                case 49: attr.Bg = -1; break;
                case >= 90 and <= 97: attr.Fg = p - 90 + 8; break;
                case >= 100 and <= 107: attr.Bg = p - 100 + 8; break;
                case 38:
                case 48:
                case 58:
                    {
                        int color;
                        if (g.Length > 1) color = ExtColor(g, 1);
                        else
                        {
                            // the ;-separated form takes the next groups
                            var flat = new List<int> { 0 };
                            for (int k = i + 1; k < groups.Count && k <= i + 4; k++) flat.Add(groups[k][0]);
                            int mode = flat.Count > 1 ? flat[1] : 0;
                            color = ExtColor([.. flat], 1);
                            i += mode == 2 ? 4 : mode == 5 ? 2 : 1;
                        }
                        if (color == int.MinValue) break;
                        if (p == 38) attr.Fg = color;
                        else if (p == 48) attr.Bg = color;
                        break;
                    }
            }
        }
    }

    /// <summary>A 38/48 color from <paramref name="v"/>[at]: 5;n (palette) or 2;[space;]r;g;b. MinValue = not understood.</summary>
    static int ExtColor(int[] v, int at)
    {
        if (at >= v.Length) return int.MinValue;
        if (v[at] == 5) return at + 1 < v.Length ? Math.Clamp(v[at + 1], 0, 255) : int.MinValue;
        if (v[at] != 2) return int.MinValue;
        // the colon form may carry a color space before r:g:b
        int n = v.Length - at - 1;
        int from = n >= 4 ? at + 2 : at + 1;
        if (from + 2 >= v.Length) return int.MinValue;
        int r = Math.Clamp(v[from], 0, 255), gr = Math.Clamp(v[from + 1], 0, 255), b = Math.Clamp(v[from + 2], 0, 255);
        return Rgb | (r << 16) | (gr << 8) | b;
    }

    void DecMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1: AppCursor = on; break;
            case 25: CursorVisible = on; break;
            case 2004: BracketedPaste = on; break;
            case 47:
            case 1047:
            case 1049:
                if (on == AltScreen) break;
                if (on)
                {
                    if (mode == 1049) Save();
                    alt = NewLines(Rows, Cols);
                    lines = alt;
                }
                else
                {
                    lines = main;
                    if (mode == 1049) Restore();
                }
                top = 0;
                bottom = Rows - 1;
                break;
        }
    }

    void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (int i = Y + 1; i < Rows; i++) lines[i] = NewLine(Cols, Blank());
                break;
            case 1:
                EraseLine(1);
                for (int i = 0; i < Y; i++) lines[i] = NewLine(Cols, Blank());
                break;
            case 2:
                for (int i = 0; i < Rows; i++) lines[i] = NewLine(Cols, Blank());
                break;
            case 3:
                Dropped += scrollback.Count;
                scrollback.Clear();
                break;
        }
    }

    void EraseLine(int mode)
    {
        var line = lines[Y];
        int from = mode == 0 ? X : 0, to = mode == 1 ? X + 1 : Cols;
        for (int i = from; i < Math.Min(to, Cols); i++) line[i] = Blank();
        pendingWrap = false;
    }

    void InsertChars(int n)
    {
        var line = lines[Y];
        n = Math.Min(n, Cols - X);
        Array.Copy(line, X, line, X + n, Cols - X - n);
        for (int i = X; i < X + n; i++) line[i] = Blank();
    }

    void DeleteChars(int n)
    {
        var line = lines[Y];
        n = Math.Min(n, Cols - X);
        Array.Copy(line, X + n, line, X, Cols - X - n);
        for (int i = Cols - n; i < Cols; i++) line[i] = Blank();
    }

    void InsertLines(int n)
    {
        if (Y < top || Y > bottom) return;
        int keep = top;
        top = Y;
        ScrollDown(n);
        top = keep;
        X = 0;
    }

    void DeleteLines(int n)
    {
        if (Y < top || Y > bottom) return;
        int keep = top;
        top = Y;
        // deleted lines inside the screen are not history
        n = Math.Min(n, bottom - top + 1);
        for (int k = 0; k < n; k++)
        {
            for (int i = top; i < bottom; i++) lines[i] = lines[i + 1];
            lines[bottom] = NewLine(Cols, Blank());
        }
        top = keep;
        X = 0;
    }

    /// <summary>
    /// Cells a character takes: 2 for East Asian wide and emoji, 0 for joiners, variation selectors and combining
    /// marks, 1 for the rest. Close to what ConPTY counts, so the columns line up.
    /// </summary>
    public static int Width(int cp)
    {
        if (cp is 0x200B or 0x200C or 0x200D or 0x2060 or (>= 0xFE00 and <= 0xFE0F) or (>= 0x0300 and <= 0x036F)
            or (>= 0x1AB0 and <= 0x1AFF) or (>= 0x20D0 and <= 0x20FF) or (>= 0xE0100 and <= 0xE01EF)) return 0;
        if (cp < 0x1100) return 1;
        return cp switch
        {
            <= 0x115F => 2,
            0x231A or 0x231B or 0x23E9 or 0x23EA or 0x23EB or 0x23EC or 0x23F0 or 0x23F3 or 0x25FD or 0x25FE => 2,
            0x2614 or 0x2615 or (>= 0x2648 and <= 0x2653) or 0x267F or 0x2693 or 0x26A1 or 0x26AA or 0x26AB or 0x26BD or 0x26BE
                or 0x26C4 or 0x26C5 or 0x26CE or 0x26D4 or 0x26EA or 0x26F2 or 0x26F3 or 0x26F5 or 0x26FA or 0x26FD
                or 0x2705 or 0x270A or 0x270B or 0x2728 or 0x274C or 0x274E or (>= 0x2753 and <= 0x2755) or 0x2757
                or (>= 0x2795 and <= 0x2797) or 0x27B0 or 0x27BF or 0x2B1B or 0x2B1C or 0x2B50 or 0x2B55 => 2,
            >= 0x2E80 and <= 0x303E => 2,
            >= 0x3041 and <= 0x33FF => 2,
            >= 0x3400 and <= 0x4DBF => 2,
            >= 0x4E00 and <= 0x9FFF => 2,
            >= 0xA000 and <= 0xA4CF => 2,
            >= 0xAC00 and <= 0xD7A3 => 2,
            >= 0xF900 and <= 0xFAFF => 2,
            >= 0xFE30 and <= 0xFE4F => 2,
            >= 0xFF00 and <= 0xFF60 => 2,
            >= 0xFFE0 and <= 0xFFE6 => 2,
            >= 0x1F300 and <= 0x1F64F => 2,
            >= 0x1F680 and <= 0x1F6FF => 2,
            >= 0x1F900 and <= 0x1F9FF => 2,
            >= 0x1FA70 and <= 0x1FAFF => 2,
            >= 0x20000 and <= 0x3FFFD => 2,
            _ => 1,
        };
    }

    /// <summary>The text of lines <paramref name="from"/>..<paramref name="to"/> (absolute, columns inclusive), trailing blanks dropped per line.</summary>
    public string Text(int fromLine, int fromCol, int toLine, int toCol)
    {
        var sb = new StringBuilder();
        for (int l = fromLine; l <= toLine; l++)
        {
            if (l < 0 || l >= TotalLines) continue;
            var line = Line(l);
            int a = l == fromLine ? fromCol : 0, b = l == toLine ? Math.Min(toCol, line.Length - 1) : line.Length - 1;
            var row = new StringBuilder();
            for (int c = a; c <= b; c++)
            {
                if (line[c].Style.HasFlag(CellStyle.WideTail)) continue;
                row.Append(line[c].Ch == 0 ? " " : char.ConvertFromUtf32(line[c].Ch));
            }
            sb.Append(row.ToString().TrimEnd());
            if (l < toLine) sb.Append(Environment.NewLine);
        }
        return sb.ToString();
    }
}
