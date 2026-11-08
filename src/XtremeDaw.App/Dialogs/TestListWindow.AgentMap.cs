using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using XtremeDaw.App.Controls;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// The bench's Agentes window (asked by the user, #273: the view in the list did not show where agents got stuck). On
/// top a map (<see cref="AgentMap"/>): each open session on the left, the files on the right, a line for each file a
/// session holds and one for each it waits for or that stopped it. Below, the picked session's console and the box to
/// write to them, which <see cref="TestListWindow"/> fills: the window only lays them out. The conversation between the
/// sessions is not shown (#300: the user did not use it). A window of its own,
/// so it can stay open beside the list or on another screen.
/// </summary>
public sealed class AgentsWindow : Window
{
    /// <summary>"4 min" on a wait moves on its own: the map is drawn again this often.</summary>
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);
    /// <summary>The console and the waits are read from the transcripts this often (#278): only the new lines.</summary>
    static readonly TimeSpan Follow = TimeSpan.FromSeconds(2);

    public AgentMap Map { get; } = new();
    /// <summary>What the lower half says while no session is picked (#300: never the chat between the sessions).</summary>
    readonly TextBlock idle = new() { Classes = { "dim" }, Margin = new(12, 8, 12, 8), TextWrapping = TextWrapping.Wrap };
    /// <summary>The console of the session picked on the map (#278), which <see cref="TestListWindow"/> fills.</summary>
    public StackPanel Console { get; } = new() { Spacing = 4, Margin = new(12, 8, 12, 8) };
    public ScrollViewer ConsoleScroller { get; }
    /// <summary>Where a bench console's real terminal goes (#278: level Todo, or a permission or question to answer).</summary>
    public Decorator Terminal { get; } = new() { IsVisible = false, Margin = new(12, 6, 12, 8) };
    /// <summary>How much of a session the console shows (#278): 0 Resumen, 1 Normal, 2 Todo.</summary>
    public int Level { get; private set; }
    public event Action? LevelChanged;
    /// <summary>Ends the picked session (#294: a bench console or any session whose Claude runs): it asks for a second click.</summary>
    public Button End { get; }
    public event Action? Ended;
    readonly ToggleButton[] levels = new ToggleButton[3];
    readonly Border levelBox;
    /// <summary>A tab per open session (#278: always at hand); a click picks it like a click on its node.</summary>
    readonly StackPanel sessionTabs = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    readonly List<(string Id, ToggleButton Tab)> tabsOf = [];
    /// <summary>What the tabs were drawn from: the same list does not build them again.</summary>
    string tabsKey = "";
    /// <summary>The session whose console is shown, "" = none.</summary>
    string selected = "";
    /// <summary>A session's tab was clicked.</summary>
    public event Action<string>? SessionPicked;
    /// <summary>Every <see cref="Follow"/> while the window is open.</summary>
    public event Action? Following;
    public Decorator Composer { get; } = new() { Margin = new(12, 0, 12, 12) };
    public TextBlock Count { get; } = new() { Classes = { "dim", "mono" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };

    public AgentsWindow()
    {
        Title = Loc.T("test.list.agents.title");
        Width = 900;
        Height = 820;
        MinWidth = 560;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.App;

        var head = new Grid { ColumnDefinitions = new("Auto,Auto,*"), ColumnSpacing = 10, Margin = new(12, 10) };
        head.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("test.list.agents.sessions"), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(Count, 1);
        head.Children.Add(Count);
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Right };
        legend.Children.Add(Key(Palette.AccentColor, false, "test.list.agents.map.holds"));
        legend.Children.Add(Key(AgentMap.WaitColor, true, "test.list.agents.map.waits"));
        legend.Children.Add(Key(Palette.RecColor, true, "test.list.agents.map.stuck"));
        Grid.SetColumn(legend, 2);
        head.Children.Add(legend);

        var mapScroll = new ScrollViewer { Content = Map, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        // #286: the map shrinks to the room it has; it scrolls only past its smallest size
        mapScroll.SizeChanged += (_, e) => Map.FitHeight = e.NewSize.Height;
        idle.Text = Loc.T("test.list.agents.none");
        ConsoleScroller = new ScrollViewer { Content = Console, IsVisible = false };

        // #278: a tab per open session, always there; #294, #300: no chat between the sessions
        var tabsBox = new Border { Classes = { "seg" }, Child = sessionTabs, HorizontalAlignment = HorizontalAlignment.Left };
        var chatHead = new ScrollViewer
        {
            Content = tabsBox, Margin = new(12, 8, 12, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        // #278: how much to show of the picked session, and the end of a bench console
        var levelRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        string[] names = ["summary", "normal", "all"];
        for (int i = 0; i < 3; i++)
        {
            int lv = i;
            var b = new ToggleButton { Classes = { "seg" }, Padding = new(10, 0), Focusable = false, IsChecked = i == Level, Content = Loc.T("test.list.agents.level." + names[i]) };
            ToolTip.SetTip(b, Loc.T($"test.list.agents.level.{names[i]}.tip"));
            b.Click += (_, _) => SetLevel(lv);
            levels[i] = b;
            levelRow.Children.Add(b);
        }
        levelBox = new Border { Classes = { "seg" }, Child = levelRow, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        End = new Button
        {
            Classes = { "act" }, Width = 28, Height = 28, Padding = new(0), Focusable = false, IsVisible = false, VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12 },
        };
        ToolTip.SetTip(End, Loc.T("test.consoles.close.tip"));
        bool armed = false;
        End.Click += (_, _) =>
        {
            if (!armed)
            {
                armed = true;
                ((TextBlock)End.Content!).Foreground = Palette.Rec;
                ToolTip.SetTip(End, Loc.T("test.consoles.close.confirm"));
                ToolTip.SetIsOpen(End, true);
                DispatcherTimer.RunOnce(() =>
                {
                    armed = false;
                    ((TextBlock)End.Content!).ClearValue(TextBlock.ForegroundProperty);
                    ToolTip.SetIsOpen(End, false);
                    ToolTip.SetTip(End, Loc.T("test.consoles.close.tip"));
                }, TimeSpan.FromSeconds(3));
                return;
            }
            armed = false;
            ToolTip.SetIsOpen(End, false);
            Ended?.Invoke();
        };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new(0, 8, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        tools.Children.Add(levelBox);
        tools.Children.Add(End);
        var headRow = new Grid { ColumnDefinitions = new("*,Auto") };
        headRow.Children.Add(chatHead);
        Grid.SetColumn(tools, 1);
        headRow.Children.Add(tools);

        var lower = new Grid { RowDefinitions = new("Auto,*,Auto") };
        lower.Children.Add(headRow);
        Grid.SetRow(Terminal, 1);
        lower.Children.Add(Terminal);
        Grid.SetRow(idle, 1);
        lower.Children.Add(idle);
        Grid.SetRow(ConsoleScroller, 1);
        lower.Children.Add(ConsoleScroller);
        Grid.SetRow(Composer, 2);
        lower.Children.Add(Composer);
        var lowerCard = new Border { Child = lower, Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new(0, 1, 0, 0) };

        var grid = new Grid { RowDefinitions = new("Auto,*,4,2*") };
        grid.Children.Add(new Border { Child = head, Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new(0, 0, 0, 1) });
        Grid.SetRow(mapScroll, 1);
        grid.Children.Add(mapScroll);
        var split = new GridSplitter { Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(split, 2);
        grid.Children.Add(split);
        Grid.SetRow(lowerCard, 3);
        grid.Children.Add(lowerCard);
        grid.RowDefinitions[3].MinHeight = 200;
        grid.RowDefinitions[1].MinHeight = 120;
        Content = grid;

        var timer = new DispatcherTimer { Interval = Tick };
        timer.Tick += (_, _) => Map.InvalidateVisual();
        var follow = new DispatcherTimer { Interval = Follow };
        follow.Tick += (_, _) => Following?.Invoke();
        Opened += (_, _) => { timer.Start(); follow.Start(); };
        Closed += (_, _) => { timer.Stop(); follow.Stop(); };
    }

    /// <summary>The console of a session, or a quiet line while none is picked.</summary>
    public void ShowConsole(bool on)
    {
        if (on && selected == "") on = false;

        foreach (var (id, tab) in tabsOf) tab.IsChecked = on && id.Equals(selected, StringComparison.OrdinalIgnoreCase);
        idle.IsVisible = !on;
        ConsoleScroller.IsVisible = on && !TerminalOn;
        Terminal.IsVisible = on && TerminalOn;
        levelBox.IsVisible = on;
        End.IsVisible = on && Endable;
        // in the real terminal the person types in it
        Composer.IsVisible = !(on && TerminalOn);
        if (on && !TerminalOn) Dispatcher.UIThread.Post(ConsoleScroller.ScrollToEnd, DispatcherPriority.Background);
    }

    /// <summary>The picked session can be ended from here: a running bench console, or a session whose Claude process is known.</summary>
    public bool Endable
    {
        get => endable;
        set
        {
            endable = value;
            End.IsVisible = value && ConsoleOn;
        }
    }
    bool endable;

    /// <summary>The real terminal is shown instead of the conversation read from the transcript.</summary>
    public bool TerminalOn { get; private set; }

    /// <summary>Shows <paramref name="view"/> (a bench console) in place of the transcript, or the transcript back (null).</summary>
    public void ShowTerminal(Control? view)
    {
        if (Terminal.Child != view)
        {
            // a console's view lives in one place at a time
            if (view?.Parent is Decorator old) old.Child = null;
            Terminal.Child = view;
        }
        bool was = TerminalOn;
        TerminalOn = view != null;
        ShowConsole(ConsoleOn || Terminal.IsVisible);
        if (TerminalOn && !was && view != null) Dispatcher.UIThread.Post(() => view.Focus(), DispatcherPriority.Background);
    }

    void SetLevel(int level)
    {
        Level = level;
        for (int i = 0; i < 3; i++) levels[i].IsChecked = i == level;
        LevelChanged?.Invoke();
    }

    public bool ConsoleOn => ConsoleScroller.IsVisible || Terminal.IsVisible;

    /// <summary>Points the tabs (and the map) at the session whose console is shown, "" = none.</summary>
    public void Select(string session)
    {
        selected = session;
        Map.Selected = session;
    }

    /// <summary>
    /// The sessions' tabs (#278): one per open session, in the map's order, its name trimmed and a dot in the accent
    /// when it waits for the person; one that closes leaves at once (#287). Built again only when the list changes, so a fill does not take the tab from under the mouse.
    /// </summary>
    public void SetSessions(IReadOnlyList<(string Id, string Name, bool Waiting, bool Live)> list)
    {
        string key = string.Join("\n", list.Select(s => $"{s.Id}|{s.Name}|{s.Waiting}|{s.Live}"));
        if (key == tabsKey) return;
        tabsKey = key;
        sessionTabs.Children.Clear();
        tabsOf.Clear();
        foreach (var s in list)
        {
            string id = s.Id;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (s.Waiting)
                row.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Width = 7, Height = 7, Fill = Palette.Accent, VerticalAlignment = VerticalAlignment.Center });
            var name = new TextBlock { Text = s.Name, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            if (!s.Live) name.Foreground = Palette.Faint;
            row.Children.Add(name);
            var tab = new ToggleButton { Classes = { "seg" }, Padding = new(10, 0), Focusable = false, Content = row };
            ToolTip.SetTip(tab, s.Waiting ? $"{s.Name} · {Loc.T("test.list.agents.tab.waiting")}" : s.Name);
            tab.Click += (_, _) => SessionPicked?.Invoke(id);
            tabsOf.Add((id, tab));
            sessionTabs.Children.Add(tab);
        }
        ShowConsole(ConsoleOn);
    }

    /// <summary>One entry of the legend: a short line in its color (dashed for a wait) and what it means.</summary>
    static Control Key(Color c, bool dashed, string key)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var line = new Avalonia.Controls.Shapes.Line
        {
            StartPoint = new(0, 1), EndPoint = new(18, 1), StrokeThickness = 2, VerticalAlignment = VerticalAlignment.Center,
            Stroke = Palette.Alpha(c, 1), StrokeDashArray = dashed ? [2, 1.5] : null,
        };
        row.Children.Add(line);
        row.Children.Add(new TextBlock { Classes = { "dim" }, FontSize = 11, Text = Loc.T(key), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }
}

/// <summary>
/// The map of the agents (#273): sessions on the left, files on the right, each session's files beside it, so a line
/// that crosses to another session's files is a wait. Lines: solid in the accent for a file held, dashed yellow for a
/// place in its line (with the place and how long), dashed red for a stuck one (a line that loops back, a command that
/// changed another session's files). Hovering a box lights only its lines; a click on a session points the box at it,
/// on a held file asks its holder to let it go. Drawn by hand: a node graph in controls would be rebuilt at every fill.
/// It shrinks to fit its room (#286, down to <see cref="MinScale"/>, then it scrolls) and always fills the width.
/// A session that closes leaves the map at the next fill, with its files.
/// </summary>
public sealed class AgentMap : Control
{
    /// <summary>A session on the map: its id, how the bench names it, its worst light, a short status and whether it waits for the person (#278).</summary>
    public sealed record Session(string Id, string Name, LockLight.Level State, string Status, bool Awaiting = false);

    /// <summary>A file on the map; <c>Holder</c> is a session id, "" for one only waited for.</summary>
    public sealed record File(string Path, string Holder, string Owner);

    /// <summary>What the map shows: the bench reads it at each fill.</summary>
    public sealed record Snapshot(List<Session> Sessions, List<File> Files, List<LockLight.Snag> Snags);

    public static readonly Color WaitColor = Color.Parse("#f59e0b"), FreeColor = Color.Parse("#22c55e");

    const double Pad = 16, SessionH = 54, SessionGap = 14, FileH = 40, FileGap = 6, GroupGap = 18, MaxNodeW = 300, MinColumnGap = 120;
    /// <summary>A line not touching the hovered box fades to this.</summary>
    const double Faded = 0.15;

    /// <summary>
    /// The map shrinks to fit the room it has (#286), down to this much: smaller, the names stop being readable and it
    /// scrolls instead.
    /// </summary>
    const double MinScale = 0.5;

    Snapshot data = new([], [], []);
    readonly Dictionary<string, Rect> sessionAt = new(StringComparer.OrdinalIgnoreCase), fileAt = new(StringComparer.OrdinalIgnoreCase);
    double laidWidth = -1, laidFit = -1;
    /// <summary>The map's height and its scale once fitted (the boxes are laid out unscaled).</summary>
    double height = 200, scale = 1;

    /// <summary>The height the map has to fit in (#286): its scroller's viewport, which the window sets; 0 = no limit.</summary>
    public double FitHeight
    {
        get;
        set
        {
            if (Math.Abs(field - value) < 0.5) return;
            field = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }
    /// <summary>The box under the mouse: a session id or a file path.</summary>
    string? hover;

    /// <summary>A click on a session (its id).</summary>
    public event Action<string>? SessionClicked;
    /// <summary>A click on a held file: its path and its owner (session/agent).</summary>
    public event Action<string, string>? FileClicked;

    public string Empty { get; set; } = "";

    /// <summary>The session whose console is open (#278): its box stays outlined.</summary>
    public string Selected
    {
        get;
        set { field = value; InvalidateVisual(); }
    } = "";

    public void Show(Snapshot snapshot)
    {
        data = snapshot;
        laidWidth = -1;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 800 : available.Width;
        Fit(w);
        return new Size(w, height * scale);
    }

    double NodeW(double w) => Math.Min(MaxNodeW, Math.Max(160, (w - 2 * Pad - MinColumnGap) / 2));

    /// <summary>
    /// Lays the map out for <paramref name="w"/> and shrinks it to fit <see cref="FitHeight"/> (#286). The boxes' heights
    /// do not depend on the width, so the scale comes from one layout; the boxes are then laid out again in w / scale,
    /// so the scaled map still fills the width.
    /// </summary>
    void Fit(double w)
    {
        double fit = FitHeight;
        if (Math.Abs(w - laidWidth) < 0.5 && Math.Abs(fit - laidFit) < 0.5) return;
        laidWidth = w;
        laidFit = fit;
        Lay(w);
        scale = fit > 0 && height > fit ? Math.Max(MinScale, fit / height) : 1;
        if (scale < 1) Lay(w / scale);
    }

    /// <summary>
    /// Places the boxes: each session at the height of its files, its files under one another; the files nobody holds
    /// (waited for, being let go) at the end.
    /// </summary>
    void Lay(double w)
    {
        sessionAt.Clear();
        fileAt.Clear();
        double nw = NodeW(w), fx = w - Pad - nw;
        double sy = Pad, fy = Pad;
        foreach (var s in data.Sessions)
        {
            double top = Math.Max(sy, fy);
            sessionAt[s.Id] = new Rect(Pad, top, nw, SessionH);
            double y = top;
            bool any = false;
            foreach (var f in data.Files.Where(f => f.Holder.Equals(s.Id, StringComparison.OrdinalIgnoreCase)))
            {
                fileAt[f.Path] = new Rect(fx, y, nw, FileH);
                y += FileH + FileGap;
                any = true;
            }
            sy = top + SessionH + SessionGap;
            fy = any ? y - FileGap + GroupGap : fy;
        }
        foreach (var f in data.Files.Where(f => !fileAt.ContainsKey(f.Path)))
        {
            fileAt[f.Path] = new Rect(fx, fy, nw, FileH);
            fy += FileH + FileGap;
        }
        height = Math.Max(Math.Max(sy, fy) + Pad, 160);
    }

    public override void Render(DrawingContext ctx)
    {
        Fit(Bounds.Width);
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (data.Sessions.Count == 0 && data.Files.Count == 0)
        {
            var t = Palette.Text(Empty, 12, Palette.Faint);
            ctx.DrawText(t, new Point((Bounds.Width - t.Width) / 2, Pad + 20));
            return;
        }
        using var _ = ctx.PushTransform(Matrix.CreateScale(scale, scale));

        // the lines go under the boxes
        foreach (var f in data.Files)
            if (sessionAt.TryGetValue(f.Holder, out var s) && fileAt.TryGetValue(f.Path, out var r))
                Link(ctx, s, r, Palette.AccentColor, false, Lit(f.Holder, f.Path), null);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var n in data.Snags)
        {
            if (!sessionAt.TryGetValue(n.Session, out var s) || !fileAt.TryGetValue(n.Path, out var r)) continue;
            bool waiting = n.State == LockLight.Level.Waiting;
            string label = waiting
                ? Loc.T("test.list.agents.map.place", n.Place, n.Line, Ago(now - n.Since))
                : Loc.T(n.State == LockLight.Level.Blocked ? "test.list.lock.blocked" : "test.list.lock.clash");
            Link(ctx, s, r, waiting ? WaitColor : Palette.RecColor, true, Lit(n.Session, n.Path), label);
        }

        foreach (var s in data.Sessions)
            if (sessionAt.TryGetValue(s.Id, out var r)) SessionBox(ctx, s, r);
        foreach (var f in data.Files)
            if (fileAt.TryGetValue(f.Path, out var r)) FileBox(ctx, f, r);
    }

    bool Lit(string session, string path) =>
        hover == null || hover.Equals(session, StringComparison.OrdinalIgnoreCase) || hover.Equals(path, StringComparison.OrdinalIgnoreCase);

    static string Ago(long sec) => sec < 60 ? $"{Math.Max(sec, 0)} s" : sec < 3600 ? $"{sec / 60} min" : $"{sec / 3600} h {sec % 3600 / 60} min";

    /// <summary>A curve from a session's right side to a file's left side, its arrow at the file and, for a snag, its label in the middle.</summary>
    static void Link(DrawingContext ctx, Rect from, Rect to, Color c, bool dashed, bool lit, string? label)
    {
        double a = lit ? 1 : Faded;
        var p0 = new Point(from.Right, from.Center.Y);
        var p3 = new Point(to.Left - 4, to.Center.Y);
        double dx = Math.Max(40, (p3.X - p0.X) * 0.5);
        var p1 = new Point(p0.X + dx, p0.Y);
        var p2 = new Point(p3.X - dx, p3.Y);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(p0, false);
            g.CubicBezierTo(p1, p2, p3);
            g.EndFigure(false);
        }
        var brush = Palette.Alpha(c, (dashed ? 0.95 : 0.6) * a);
        ctx.DrawGeometry(null, dashed ? Palette.Dashed(brush, 2) : Palette.Pen(brush, 1.5), geo);

        var arrow = new StreamGeometry();
        using (var g = arrow.Open())
        {
            g.BeginFigure(new Point(to.Left, to.Center.Y), true);
            g.LineTo(new Point(to.Left - 7, to.Center.Y - 4));
            g.LineTo(new Point(to.Left - 7, to.Center.Y + 4));
            g.EndFigure(true);
        }
        ctx.DrawGeometry(brush, null, arrow);

        if (label == null) return;
        // the curve's middle (t = 0.5 of a cubic Bézier)
        var mid = new Point((p0.X + 3 * p1.X + 3 * p2.X + p3.X) / 8, (p0.Y + 3 * p1.Y + 3 * p2.Y + p3.Y) / 8);
        var t = Palette.Text(label, 11, Palette.Alpha(c, a), Palette.Mono);
        var box = new Rect(mid.X - t.Width / 2 - 6, mid.Y - t.Height / 2 - 2, t.Width + 12, t.Height + 4);
        ctx.DrawRectangle(Palette.Alpha(Palette.RaisedColor, a < 1 ? 0.5 : 1), Palette.Pen(Palette.Alpha(c, 0.6 * a), 1), box, 5, 5);
        ctx.DrawText(t, new Point(box.X + 6, box.Y + 2));
    }

    static Color StateColor(LockLight.Level l) => l switch
    {
        LockLight.Level.Free => FreeColor,
        LockLight.Level.Waiting => WaitColor,
        _ => Palette.RecColor,
    };

    void SessionBox(DrawingContext ctx, Session s, Rect r)
    {
        // waiting for the person (#278) shows in the accent, unless something of the locks stops it
        bool awaiting = s.Awaiting && s.State == LockLight.Level.Free;
        var c = awaiting ? Palette.AccentColor : StateColor(s.State);
        bool on = (hover != null && hover.Equals(s.Id, StringComparison.OrdinalIgnoreCase)) || s.Id.Equals(Selected, StringComparison.OrdinalIgnoreCase);
        ctx.DrawRectangle(Palette.Raised, Palette.Pen(Palette.Alpha(c, on ? 1 : 0.6), on ? 2 : 1), r, 8, 8);
        ctx.DrawEllipse(Palette.Alpha(c, 1), null, new Point(r.X + 16, r.Y + 18), 4, 4);
        ctx.DrawText(s.Name, 12.5, Palette.Fg, new Point(r.X + 28, r.Y + 9), Palette.UiBold, r.Width - 40);
        ctx.DrawText(s.Status, 11, s.State == LockLight.Level.Free && !awaiting ? Palette.Dim : Palette.Alpha(c, 1), new Point(r.X + 28, r.Y + 30), Palette.Ui, r.Width - 40);
    }

    void FileBox(DrawingContext ctx, File f, Rect r)
    {
        bool snag = data.Snags.Exists(n => n.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase));
        bool red = data.Snags.Exists(n => n.State != LockLight.Level.Waiting && n.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase));
        bool on = hover != null && hover.Equals(f.Path, StringComparison.OrdinalIgnoreCase);
        IBrush edge = on ? Palette.Accent : red ? Palette.Alpha(Palette.RecColor, 0.7) : snag ? Palette.Alpha(WaitColor, 0.7) : Palette.Line;
        ctx.DrawRectangle(Palette.Panel, f.Holder == "" ? Palette.Dashed(edge, 1) : Palette.Pen(edge, on ? 2 : 1), r, 6, 6);
        int cut = Math.Max(f.Path.LastIndexOf('\\'), f.Path.LastIndexOf('/'));
        string name = cut >= 0 ? f.Path[(cut + 1)..] : f.Path, dir = cut >= 0 ? f.Path[..cut] : "";
        ctx.DrawText(name, 12, Palette.Fg, new Point(r.X + 10, r.Y + 4), Palette.UiBold, r.Width - 20);
        if (dir != "") ctx.DrawText(dir, 10.5, Palette.Faint, new Point(r.X + 10, r.Y + 21), Palette.Ui, r.Width - 20);
    }

    string? Hit(Point p)
    {
        p /= scale;
        foreach (var (id, r) in sessionAt) if (r.Contains(p)) return id;
        foreach (var (path, r) in fileAt) if (r.Contains(p)) return path;
        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var h = Hit(e.GetPosition(this));
        if (h == hover) return;
        hover = h;
        bool clickable = h != null && (sessionAt.ContainsKey(h) || data.Files.Exists(f => f.Path.Equals(h, StringComparison.OrdinalIgnoreCase) && f.Holder != ""));
        Cursor = clickable ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (hover == null) return;
        hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Hit(e.GetPosition(this)) is not { } h) return;
        if (sessionAt.ContainsKey(h)) SessionClicked?.Invoke(h);
        else if (data.Files.Find(f => f.Path.Equals(h, StringComparison.OrdinalIgnoreCase)) is { Holder.Length: > 0 } f) FileClicked?.Invoke(f.Path, f.Owner);
        e.Handled = true;
    }

    /// <summary>"{n} archivos" for a session's status.</summary>
    public static string Files(int n) => n == 1 ? Loc.T("test.list.agents.map.file") : Loc.T("test.list.agents.map.files", n.ToString(CultureInfo.CurrentCulture));
}
