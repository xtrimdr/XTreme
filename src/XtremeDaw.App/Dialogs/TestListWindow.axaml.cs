using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using XtremeDaw.App.Controls;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// The "Banco de pruebas" as tickets (asked by the user, 2026-11-04): a compact list, one line per ticket, in one vertical
/// list by topic (asked by the user, #125: no tabs), and inside each topic its sections (Para probar, A arreglar, Pedidos,
/// Resuelto); a click opens the ticket with its whole conversation — the request or
/// what to try, each fix an agent sent, each verdict with its note, and comments from either side — and under it the
/// verdicts and a box to answer. Nothing said is ever overwritten: a fix that comes back starts an empty reply.
/// <para>
/// Anda takes effect at once; the other verdicts wait for the reply and Guardar, which reaches the disk as a draft before
/// that, because the copy being tried can die. A window of its own and not a dialog, so it stays next to the app while
/// trying things; it opens without taking the focus, and marking gives the focus back to the main window, so Space keeps
/// meaning play. Claude's skills are one menu in the title bar, and whether they open in the app or a console is picked
/// there once. What an agent adds, sends back or says is "Nuevo" until the ticket is opened, and arriving while the bench
/// is open it is announced on top. Every click carries the ticket's <see cref="TestItem.Rev"/>: a ticket that went
/// stale on the screen cannot undo what an agent did meanwhile.
/// </para>
/// <see cref="Bench"/> reads the file; the window only shows it.
/// </summary>
public sealed partial class TestListWindow : Window
{
    /// <summary>A list that changes under the pointer would swallow the click that took the focus off the reply.</summary>
    const int ReplySaveDelayMs = 300;
    /// <summary>The sections inside each topic, in this order; each one with its groups.</summary>
    static readonly (string Key, (Func<TestItem, bool> Match, string Key)[] Groups)[] TabList =
    [
        ("test.list.totest", [(TestList.Back, "test.list.back"), (i => i.State == TestItem.Pending && !TestList.Back(i), "test.list.first")]),
        ("test.list.fix", [(Is(TestItem.Fails), "test.list.fail"), (Is(TestItem.WorksBadly), "test.list.bad"), (Is(TestItem.Improve), "test.list.improve")]),
        ("test.list.requests", [(Is(TestItem.Request), "")]),
        ("test.list.done", [(Is(TestItem.Works), "")]),
    ];
    const int TestTabIndex = 0, FixTabIndex = 1, RequestTabIndex = 2, DoneTabIndex = 3;
    /// <summary>Segoe Fluent Icons "Robot": the agents' button.</summary>
    const string ReviewGlyph = "";
    /// <summary>Segoe Fluent Icons "CheckMark", "More", "History", "Back".</summary>
    const string CheckGlyph = "", MoreGlyph = "", HistoryGlyph = "", BackGlyph = "";
    /// <summary>Segoe Fluent Icons "ChevronRightMed" and "ChevronDownMed": a topic folded or open in the tree.</summary>
    const string FoldedGlyph = "\uE974", UnfoldedGlyph = "\uE972";
    /// <summary>Segoe Fluent Icons "Add", "Edit", "Delete", "Send".</summary>
    const string AddGlyph = "", EditGlyph = "", DeleteGlyph = "", SendGlyph = "";
    /// <summary>Where the skills open, remembered in <c>config.json</c>: "app" (the default) or "console".</summary>
    const string OpenInKey = "benchClaude";
    /// <summary>The topics folded in the tree, remembered in <c>config.json</c> (asked by the user, #124): ids joined by commas.</summary>
    const string FoldedKey = "benchFolded";
    /// <summary>The tree's node for the tickets nobody gave a topic yet; never a stored value.</summary>
    const string NoTopic = "-";
    /// <summary>How many changes the whole history shows: the latest.</summary>
    const int HistoryAll = 80;

    /// <summary>Room for about three lines of a reply before typing; it grows up to <see cref="ReplyMaxHeight"/>, then scrolls.</summary>
    const double ReplyMinHeight = 64;
    const double ReplyMaxHeight = 220;
    static readonly Color GreenState = Color.Parse("#22c55e"), BlueState = Color.Parse("#60a5fa"), YellowState = Color.Parse("#f59e0b");
    /// <summary>Only for the requests' "Función nueva": the one kind no verdict color stands for.</summary>
    static readonly Color VioletKind = Color.Parse("#a78bfa");
    /// <summary>How strong a card's edge is in its verdict's color: visible, without shouting over the text.</summary>
    const double StateEdge = 0.6;
    /// <summary>
    /// The color a card carries (asked by the user: the window had no character): a stripe on its left edge and a wash
    /// this faint over its background, so the color says what it is before the text is read.
    /// </summary>
    const double CardWash = 0.05;
    const double CardStripe = 3;
    /// <summary>How far the bar stays from the card's edge, top and bottom: past the curve of its corners.</summary>
    const double CardStripeInset = 6;
    /// <summary>Background of a colored badge, under its text in the full color.</summary>
    const double BadgeTint = 0.16;

    readonly string path;
    TestListData? current;
    TestListData? waiting;
    /// <summary>The section of the open ticket: its "back" says it and its badge takes its color.</summary>
    int tab;
    /// <summary>The ticket open; 0 = the list.</summary>
    int open;
    /// <summary>The open ticket's reply box, and the ticket as it was drawn.</summary>
    TextBox? replyBox;
    TestItem? replyItem;
    bool focusReply;
    /// <summary>
    /// A reply was just sent: the next list is drawn even with the box focused. Ctrl+Enter keeps the focus there, and
    /// waiting for it to leave showed the ticket as before, the text still in the box (#127).
    /// </summary>
    bool sent;
    /// <summary>The request being edited in the composer, and the version it had; 0 = writing a new one.</summary>
    int editing, editingRev;
    /// <summary>What the composer writes: a request, or a new skill for Claude.</summary>
    bool skillMode;
    /// <summary>The composer was opened from elsewhere (+ or Ctrl+N): it closes once the request is added.</summary>
    bool composerAsked;
    /// <summary>The open ticket is asking whether to take it off the list.</summary>
    bool confirmRemove;
    /// <summary>The open ticket shows its history of changes (who took it, moved it…), under the conversation.</summary>
    bool auditOpen;
    /// <summary>The topics folded in the tree (<see cref="NoTopic"/> = the ones without) and the sections folded inside each ("topic:section"), kept across runs.</summary>
    readonly HashSet<string> folded = [.. (AppConfig.Get(FoldedKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)];
    /// <summary>The words in the search box, each one has to be in the ticket (empty = no search).</summary>
    string[] search = [];
    /// <summary>The end of the writes in order: a reply saved by losing the focus cannot land after the click that took it.</summary>
    Task<bool> writes = Task.FromResult(true);

    /// <summary>The "Nuevo" tickets already announced, so the bar tells only of what just arrived. Null before the first list.</summary>
    HashSet<int>? announced;
    /// <summary>The ticket "Ver" opens.</summary>
    int newsTarget;

    /// <summary>The file changed from here.</summary>
    public event Action? Changed;

    /// <summary>The repository the list belongs to, for "Revisar" and the skills (<see cref="AgentLauncher"/>); null hides them.</summary>
    readonly string? root;
    string build = TestBuild.CopyName ?? "";

    /// <summary>
    /// The copy being tried: requests are made in it and newer tickets say they are missing. This copy's own, unless
    /// the bench, which outlives copies, says the newest (<see cref="Bench"/>); then the badge says that one's commit.
    /// </summary>
    public string Build
    {
        get => build;
        set
        {
            build = value;
            // "20261104-144921-d7cedd3": the commit alone is the same for every copy of a day's work, so the badge
            // also says when the copy was made (asked by the user: it was not clear which one was open)
            var parts = value.Split('-');
            if (parts.Length < 3 || parts[1].Length < 4) return;
            string label = value == TestBuild.CopyName && TestBuild.Label is { } own ? own : parts[^1];
            BuildText.Text = $"{label} · {parts[1][..2]}:{parts[1][2..4]}";
            BuildBadge.IsVisible = true;
        }
    }

    public TestListWindow() : this("") { }

    public TestListWindow(string path)
    {
        this.path = path;
        root = path == "" ? null : AgentLauncher.Root(path);
        InitializeComponent();
        if (TestBuild.Label is { } label)
        {
            BuildText.Text = label;
            BuildBadge.IsVisible = true;
        }

        ToolTip.SetTip(NewRequestButton, $"{Loc.T("test.list.request.new")} (Ctrl+N)");
        NewRequestButton.Click += (_, _) => OpenComposer(skill: false);
        ToolTip.SetTip(OpenAppButton, Loc.T("test.list.openapp.tip"));
        OpenAppButton.IsVisible = root != null;
        OpenAppButton.Click += (_, _) =>
        {
            if (root != null) ErrorText.Text = AgentLauncher.OpenApp(root) is { } why ? Loc.T("test.list.openapp.error", why) : null;
        };
        ToolTip.SetTip(ClaudeButton, Loc.T("test.list.claude.tip"));
        ClaudeButton.IsVisible = root != null;
        ClaudeButton.Click += (_, _) => ShowClaudeMenu();

        AddButton.Click += (_, _) => SubmitComposer();
        CancelEditButton.Click += (_, _) => CloseComposer();
        // Tunnel: with AcceptsReturn the TextBox takes Enter (Ctrl included) as a new line before a bubbling handler sees it.
        RequestBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                SubmitComposer();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && (editing != 0 || skillMode || composerAsked))
            {
                CloseComposer();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        // Ctrl+Enter confirms from anywhere in the bench, not only from inside a box: the open ticket's reply, else the composer.
        // Tunnel, so a focused button or chip does not take it as its own Enter; the boxes keep their own handlers.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter || !e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Source is TextBox) return;
            if (replyBox is { IsEffectivelyVisible: true } box && replyItem != null)
            {
                Send(replyItem, box);
                e.Handled = true;
            }
            else if (Composer.IsEffectivelyVisible)
            {
                SubmitComposer();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.N && e.KeyModifiers == KeyModifiers.Control)
            {
                OpenComposer(skill: false);
                e.Handled = true;
            }
            else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && open != 0 && !e.Handled)
            {
                CloseTicket();
                e.Handled = true;
            }
        };
        ToolTip.SetTip(SearchBox, $"{Loc.T("test.list.search.tip")} (Ctrl+F)");
        SearchBox.TextChanged += (_, _) =>
        {
            search = (SearchBox.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // a search shows the list, not the ticket that was open
            if (open != 0)
            {
                HoldReply(0);
                open = 0;
                UpdateComposer();
            }
            HistoryButton.IsChecked = false;
            Refill();
        };
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(SearchBox.Text))
            {
                SearchBox.Text = "";
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && search.Length > 0)
            {
                // Enter opens the first match, as when the search was for its number
                var first = current == null ? null : Tree(current).SelectMany(t => t.Sections).SelectMany(x => x.Groups).SelectMany(g => g.Items).FirstOrDefault();
                if (first != null) OpenTicket(first.Id);
                e.Handled = true;
            }
        };
        NewsShow.Click += (_, _) =>
        {
            NewsBar.IsVisible = false;
            OpenTicket(newsTarget);
        };
        NewsClose.Click += (_, _) => NewsBar.IsVisible = false;

        Header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        ClearButton.Click += (_, _) => Save(() => TestList.ClearWorking(path));
        FoldAllButton.Click += (_, _) => FoldAll();
        HistoryButton.IsCheckedChanged += (_, _) => Refill();
        Activated += (_, _) => StopFlash();
        // going back to the app keeps the box focused, so LostFocus does not come
        Deactivated += (_, _) => HoldReply(ReplySaveDelayMs);
        PositionChanged += (_, _) => NotePlaceSoon();
        Resized += (_, _) => NotePlaceSoon();
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            if (WindowState == WindowState.Maximized) wasMaximized = true;
            else if (WindowState == WindowState.Normal) wasMaximized = false;
            NotePlaceSoon();
        };
        Closing += (_, _) =>
        {
            SavePlace();
            // the app may be closing too: what is on the screen goes to disk now
            if (replyBox != null && replyItem != null && ReplyChanged(replyBox, replyItem))
            {
                int id = replyItem.Id;
                string? draft = replyItem.Draft, text = replyBox.Text;
                Try(() => TestList.Hold(path, id, draft, text));
            }
        };
        UpdateComposer();
    }

    /// <summary>
    /// Where the bench was and its size, remembered in <c>config.json</c> (asked by the user, #128): "x,y,width,height,maximized".
    /// As in the main window, the normal bounds are read once a move or resize settles, so maximizing keeps the size before.
    /// </summary>
    const string PlaceKey = "benchWindow";
    /// <summary>A point this far into the title bar has to be on a monitor's working area to reuse the saved place.</summary>
    static readonly PixelPoint GrabPoint = new(80, 16);
    PixelPoint normalPos;
    Size normalSize;
    bool placeKnown, wasMaximized;

    void NotePlaceSoon() => Dispatcher.UIThread.Post(() =>
    {
        if (WindowState != WindowState.Normal || !IsVisible) return;
        normalPos = Position;
        normalSize = ClientSize;
        placeKnown = true;
    }, DispatcherPriority.Background);

    /// <summary>
    /// Opens where it was left, before showing; false (nothing saved, or its monitor is gone) leaves the caller's
    /// startup place.
    /// </summary>
    public bool RestorePlace()
    {
        string? saved;
        try { saved = AppConfig.Get(PlaceKey); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        var p = saved?.Split(',');
        if (p is not { Length: 5 }
            || !int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
            || !double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
            || !double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double h)) return false;
        var pos = new PixelPoint(x, y);
        var screen = Screens.All.FirstOrDefault(s => s.WorkingArea.Contains(pos + GrabPoint));
        if (screen == null || !double.IsFinite(w) || !double.IsFinite(h)) return false;
        var area = screen.WorkingArea.Size.ToSize(screen.Scaling);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = pos;
        Width = Math.Clamp(w, MinWidth, Math.Max(MinWidth, area.Width));
        Height = Math.Clamp(h, MinHeight, Math.Max(MinHeight, area.Height));
        normalPos = pos;
        normalSize = new Size(Width, Height);
        placeKnown = true;
        if (p[4] == "1") WindowState = WindowState.Maximized;
        return true;
    }

    void SavePlace()
    {
        if (!placeKnown) return;
        string value = string.Join(',',
            normalPos.X.ToString(CultureInfo.InvariantCulture), normalPos.Y.ToString(CultureInfo.InvariantCulture),
            Math.Round(normalSize.Width).ToString(CultureInfo.InvariantCulture), Math.Round(normalSize.Height).ToString(CultureInfo.InvariantCulture),
            wasMaximized ? "1" : "0");
        try { AppConfig.Set(PlaceKey, value); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* View preference only. */ }
    }

    /// <summary>Opens a ticket: from its line, or from "Ver". Opening it is seeing it: it stops being "Nuevo".</summary>
    void OpenTicket(int id)
    {
        if (current?.Items.Find(i => i.Id == id) is not { } item) return;
        HoldReply(0);
        tab = Math.Max(0, SectionOf(item));
        open = id;
        confirmRemove = false;
        auditOpen = false;
        HistoryButton.IsChecked = false;
        UpdateComposer();
        if (item.Unseen) Save(() => TestList.See(path, [id]));
        Refill();
        Scroller.Offset = default;
    }

    void CloseTicket()
    {
        HoldReply(0);
        open = 0;
        UpdateComposer();
        Refill();
    }

    /// <summary>What is in the reply box reaches the disk as a draft (the copy being tried can die).</summary>
    void HoldReply(int delayMs)
    {
        if (replyBox == null || replyItem == null || !ReplyChanged(replyBox, replyItem)) return;
        int id = replyItem.Id;
        string? draft = replyItem.Draft, text = replyBox.Text;
        replyItem.Reply = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        Save(() => TestList.Hold(path, id, draft, text), delayMs);
    }

    void Refill()
    {
        if (current != null) Fill(current);
    }

    /// <summary>Shows the list or the open ticket, unless a reply is being written: rebuilding would take it away from under the keyboard.</summary>
    public void Fill(TestListData data)
    {
        current = data;
        Announce(data);
        if (!sent && IsActive && replyBox?.IsFocused == true)
        {
            waiting = data;
            return;
        }
        sent = false;
        waiting = null;
        replyBox = null;
        replyItem = null;
        Rows.Children.Clear();
        if (HistoryButton.IsChecked == true) FillHistory();
        else if (open != 0 && data.Items.Find(i => i.Id == open) is { } ticket) FillTicket(ticket);
        else
        {
            // a ticket that left the list (removed, or cleared) takes the person back to it
            open = 0;
            FillTab(data);
        }
        FillTotals(data);
        ClearButton.IsVisible = data.Count(TestItem.Works) > 0 && open == 0;
        UpdateFoldAll(data);
        if (focusReply && replyBox != null)
        {
            Activate();
            replyBox.Focus();
            replyBox.CaretIndex = replyBox.Text?.Length ?? 0;
        }
        focusReply = false;
    }

    void FillTab(TestListData data)
    {
        // everything with work reviews itself whole: the requests and everything to fix (the skill with no arguments)
        int count = data.Items.Count(i => TabReview(SectionOf(i)) != null);
        if (count > 0 && root != null) Rows.Children.Add(ReviewAllRow("", count, Palette.AccentColor));
        foreach (var (topic, sections) in Tree(data))
        {
            string node = topic ?? NoTopic;
            // a search opens every topic and section, so what it found is in sight
            bool shut = search.Length == 0 && folded.Contains(node);
            Rows.Children.Add(TopicHead(topic, sections.SelectMany(x => x.Groups).SelectMany(g => g.Items).ToList(), shut));
            if (shut) continue;
            var inner = new StackPanel { Spacing = 8, Margin = new(14, 0, 0, 4) };
            foreach (var (section, groups) in sections)
            {
                var items = groups.SelectMany(g => g.Items).ToList();
                bool closed = search.Length == 0 && SectionShut(node, section);
                inner.Children.Add(SectionHead(node, section, items, closed));
                if (closed) continue;
                foreach (var (key, list) in groups)
                {
                    if (key != "")
                    {
                        var c = StateColor(list[0].State) ?? TabColor(section);
                        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new(2, 2, 0, 0) };
                        head.Children.Add(Dot(c));
                        head.Children.Add(new TextBlock { Classes = { "label" }, Text = $"{Loc.T(key)} · {list.Count}", Foreground = Palette.Alpha(c, 1), VerticalAlignment = VerticalAlignment.Center });
                        inner.Children.Add(head);
                    }
                    foreach (var i in list) inner.Children.Add(Line(i));
                }
            }
            Rows.Children.Add(inner);
        }
        if (Rows.Children.Count == 0) Rows.Children.Add(Empty());
    }

    /// <summary>The section a ticket is in (an index of <see cref="TabList"/>), or -1.</summary>
    static int SectionOf(TestItem item) => Array.FindIndex(TabList, t => t.Groups.Any(g => g.Match(item)));

    /// <summary>
    /// The list as a tree (asked by the user, 2026-11-04, #125: all vertical, no tabs): each topic with tickets, in the
    /// order of <see cref="TestItem.Topics"/> and "A categorizar" last; inside it each section with tickets, and in it its
    /// groups, each one <see cref="Ordered"/>.
    /// </summary>
    List<(string? Topic, List<(int Section, List<(string Key, List<TestItem> Items)> Groups)> Sections)> Tree(TestListData data)
    {
        var tree = new List<(string?, List<(int, List<(string, List<TestItem>)>)>)>();
        foreach (var topic in TestItem.Topics.Append(null))
        {
            var sections = new List<(int, List<(string, List<TestItem>)>)>();
            for (int s = 0; s < TabList.Length; s++)
            {
                var groups = new List<(string, List<TestItem>)>();
                foreach (var (match, key) in TabList[s].Groups)
                {
                    var items = Ordered(data.Items.Where(match).Where(i => i.Topic == topic).Where(Found)).ToList();
                    if (items.Count > 0) groups.Add((key, items));
                }
                if (groups.Count > 0) sections.Add((s, groups));
            }
            if (sections.Count > 0) tree.Add((topic, sections));
        }
        return tree;
    }

    /// <summary>
    /// Whether a section of a topic is folded, kept with the topics' folds. Resuelto starts folded, the rest open: what
    /// works is the least looked at, so its fold is kept the other way round ("topic:3+" = open).
    /// </summary>
    bool SectionShut(string node, int section) => section == DoneTabIndex
        ? !folded.Contains(SectionNode(node, section))
        : folded.Contains(SectionNode(node, section));

    static string SectionNode(string node, int section) => section == DoneTabIndex
        ? $"{node}:{section}+"
        : $"{node}:{section}";

    void ToggleFold(string key)
    {
        if (!folded.Remove(key)) folded.Add(key);
        SaveFolds();
    }

    void SaveFolds()
    {
        try { AppConfig.Set(FoldedKey, string.Join(",", folded)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Refill();
    }

    /// <summary>The topics the list shows now, as tree nodes.</summary>
    List<string> ShownTopics(TestListData data) => [.. Tree(data).Select(t => t.Topic ?? NoTopic)];

    /// <summary>
    /// Folds every topic in the list (asked by the user, #126), or opens them all when they are all folded already. The
    /// sections inside keep their own folds.
    /// </summary>
    void FoldAll()
    {
        if (current == null) return;
        var topics = ShownTopics(current);
        if (topics.All(folded.Contains)) folded.ExceptWith(topics);
        else folded.UnionWith(topics);
        SaveFolds();
    }

    /// <summary>The fold-all button: only on the list without a search (a search opens everything), and saying what a click does.</summary>
    void UpdateFoldAll(TestListData data)
    {
        var topics = open == 0 && search.Length == 0 && HistoryButton.IsChecked != true ? ShownTopics(data) : [];
        FoldAllButton.IsVisible = topics.Count > 0;
        if (topics.Count == 0) return;
        bool allShut = topics.All(folded.Contains);
        // ChevronDownMed opens them, ChevronUpMed folds them
        FoldAllIcon.Text = allShut ? UnfoldedGlyph : "";
        string tip = Loc.T(allShut ? "test.list.unfoldall" : "test.list.foldall");
        ToolTip.SetTip(FoldAllButton, tip);
        AutomationProperties.SetName(FoldAllButton, tip);
    }

    /// <summary>
    /// A section inside a topic: its arrow, dot and name in its color, how many and whether something in it is new; a
    /// click folds it. In A arreglar and Pedidos, "Revisar" on the right sends its tickets to the agents by number.
    /// </summary>
    Control SectionHead(string node, int section, List<TestItem> items, bool shut)
    {
        var c = TabColor(section);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock
        {
            Classes = { "icon" }, Text = shut ? FoldedGlyph : UnfoldedGlyph, FontSize = 9, Width = 12,
            Foreground = Palette.Alpha(Palette.DimColor, 1), VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(Dot(c));
        row.Children.Add(new TextBlock { Text = Loc.T(TabList[section].Key), Foreground = Palette.Alpha(c, 1), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Classes = { "dim", "mono" }, FontSize = 11, Text = items.Count.ToString(CultureInfo.CurrentCulture), VerticalAlignment = VerticalAlignment.Center });
        if (items.Any(i => i.Unseen)) row.Children.Add(Badge(Loc.T("test.list.new"), false, Palette.AccentColor, strong: true));
        var b = new Button
        {
            Classes = { "rowmain" }, Content = row, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(2, 2), Background = Brushes.Transparent,
        };
        AutomationProperties.SetName(b, Loc.T(TabList[section].Key));
        b.Click += (_, _) => ToggleFold(SectionNode(node, section));
        if (TabReview(section) == null || root == null) return b;
        var grid = new Grid { ColumnDefinitions = new("*,Auto") };
        grid.Children.Add(b);
        var review = ReviewLink(string.Join(" ", items.Select(i => i.Id.ToString(CultureInfo.InvariantCulture))), c);
        Grid.SetColumn(review, 1);
        grid.Children.Add(review);
        return grid;
    }

    /// <summary>
    /// A topic's node in the tree: its arrow, name, how many and whether something in it is new; a click folds it.
    /// </summary>
    Control TopicHead(string? topic, List<TestItem> items, bool shut)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        row.Children.Add(new TextBlock
        {
            Classes = { "icon" }, Text = shut ? FoldedGlyph : UnfoldedGlyph, FontSize = 9, Width = 12,
            Foreground = Palette.Alpha(Palette.DimColor, 1), VerticalAlignment = VerticalAlignment.Center,
        });
        var name = new TextBlock { Text = TopicName(topic), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        if (topic == null) name.Foreground = Palette.Alpha(Palette.FaintColor, 1);
        row.Children.Add(name);
        row.Children.Add(new TextBlock { Classes = { "dim", "mono" }, FontSize = 11, Text = items.Count.ToString(CultureInfo.CurrentCulture), VerticalAlignment = VerticalAlignment.Center });
        if (items.Any(i => i.Unseen)) row.Children.Add(Badge(Loc.T("test.list.new"), false, Palette.AccentColor, strong: true));
        // how many an agent is working on, with the same spinner as each ticket (asked by the user, #123)
        int busy = items.Count(i => i.Taken != null && TestList.Open(i));
        if (busy > 0) row.Children.Add(TakenBadge(busy));
        var b = new Button
        {
            Classes = { "rowmain" }, Content = row, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(2, 4), Background = Brushes.Transparent,
        };
        if (topic != null) ToolTip.SetTip(b, Loc.T("test.list.topic." + topic + ".tip"));
        AutomationProperties.SetName(b, TopicName(topic));
        string node = topic ?? NoTopic;
        b.Click += (_, _) => ToggleFold(node);
        return b;
    }

    /// <summary>
    /// A ticket in the list: one line with its number, "Nuevo", kind, subject and the last thing said, and on the right
    /// its attempt. The whole line opens it.
    /// </summary>
    Control Line(TestItem item)
    {
        var grid = new Grid { ColumnDefinitions = new("*,Auto") };
        var left = new StackPanel { Spacing = 3 };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        top.Children.Add(Badge($"#{item.Id}", true, Palette.AccentColor));
        if (item.Unseen) top.Children.Add(Badge(Loc.T("test.list.new"), false, Palette.AccentColor, strong: true));
        if (item.Kind is { } k) top.Children.Add(Badge(Loc.T("test.list.kind." + k), false, KindColor(k)));
        if (item.Taken != null && TestList.Open(item)) top.Children.Add(TakenBadge());
        left.Children.Add(top);
        left.Children.Add(new TextBlock { Text = item.Title ?? item.Text, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, TextWrapping = TextWrapping.Wrap, LineHeight = 18 });
        if (LastSaid(item) is { } m)
            left.Children.Add(new TextBlock
            {
                Classes = { "dim" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
                Text = $"{RoleName(m.Who)} · {MessageLabel(m)}{(m.Text is { } t ? ": " + t : "")}",
            });
        grid.Children.Add(left);
        var right = new TextBlock
        {
            Classes = { "dim", "mono" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Top, Margin = new(10, 2, 0, 0),
            Text = item.Tries > 1 ? Loc.T("test.list.try", item.Tries) : When(item.At),
        };
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        var b = new Button
        {
            Classes = { "rowmain" }, Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(0), Background = Brushes.Transparent,
        };
        AutomationProperties.SetName(b, $"#{item.Id} {item.Title ?? item.Text}");
        b.Click += (_, _) => OpenTicket(item.Id);
        return Card(b, item.State == TestItem.Request ? item.Kind is { } kc ? KindColor(kc) : null : StateColor(item.State),
            item.Unseen || (item.Taken != null && TestList.Open(item)));
    }

    /// <summary>
    /// The order of a group: what asks for the person's attention first ("Nuevo": an agent answered, asked or sent it
    /// back; asked by the user), then oldest first by number, so a fix keeps its place instead of jumping to the end.
    /// </summary>
    static IEnumerable<TestItem> Ordered(IEnumerable<TestItem> items) => items.OrderByDescending(i => i.Unseen).ThenBy(i => i.Id);

    /// <summary>What was said after the ticket opened, the latest first: what the person wants to know before opening it.</summary>
    static TestMessage? LastSaid(TestItem i) => i.Thread.Count > 1 ? i.Thread[^1] : null;

    /// <summary>
    /// The open ticket: its subject and state, the conversation in order, and under it the verdicts and the reply. Revisar
    /// and "⋯" (edit a request, its history, take it off) on top.
    /// </summary>
    void FillTicket(TestItem item)
    {
        var body = new StackPanel { Spacing = 10 };

        var bar = new Grid { ColumnDefinitions = new("Auto,*,Auto") };
        var back = new Button { Classes = { "link" }, Padding = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        back.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children =
            {
                new TextBlock { Classes = { "icon" }, Text = BackGlyph, FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = Loc.T(TabList[tab].Key), VerticalAlignment = VerticalAlignment.Center },
            },
        };
        ToolTip.SetTip(back, Loc.T("test.list.ticket.back") + " (Esc)");
        back.Click += (_, _) => CloseTicket();
        bar.Children.Add(back);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (ReviewButton(item) is { } review) actions.Children.Add(review);
        actions.Children.Add(MoreButton(item));
        Grid.SetColumn(actions, 2);
        bar.Children.Add(actions);
        body.Children.Add(bar);

        var info = new WrapPanel { ItemSpacing = 8, LineSpacing = 4 };
        info.Children.Add(Badge($"#{item.Id}", true, Palette.AccentColor));
        info.Children.Add(Badge(StateName(item.State), false, StateColor(item.State) ?? TabColor(tab)));
        if (item.Kind is { } k) info.Children.Add(Badge(Loc.T("test.list.kind." + k), false, KindColor(k)));
        if (item.Taken != null && TestList.Open(item)) info.Children.Add(TakenBadge());
        info.Children.Add(TopicBadge(item.Topic));
        if (item.Tries > 0) info.Children.Add(new TextBlock { Classes = { "dim", "mono" }, FontSize = 11, Text = Loc.T("test.list.try", item.Tries), VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(info);
        body.Children.Add(new TextBlock { Text = item.Title ?? item.Text, TextWrapping = TextWrapping.Wrap, LineHeight = 19, FontWeight = FontWeight.SemiBold });

        // the timeline; without a title the opening message is the subject, already shown
        var said = item.Thread.Skip(item.Title == null && item.Thread.Count > 0 && item.Thread[0].Kind == TestMessage.Open ? 1 : 0).ToList();
        var thread = new StackPanel();
        for (int n = 0; n < said.Count; n++) thread.Children.Add(Message(said[n], n == said.Count - 1));
        if (thread.Children.Count > 0) body.Children.Add(thread);

        if (confirmRemove) body.Children.Add(RemoveConfirm(item));
        if (auditOpen) body.Children.Add(Audit(item.Id));

        if (TestList.Missing(item, build))
            body.Children.Add(new TextBlock { Text = Loc.T("test.list.missing"), Foreground = Palette.Mute, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        else body.Children.Add(Answer(item));

        Rows.Children.Add(Card(body, StateColor(item.Draft ?? item.State), item.Unseen));
    }

    /// <summary>
    /// One entry of the ticket's timeline (asked by the user, 2026-11-04): a dot on a line that joins the entries, then
    /// who said it (Usuario or Agente, the session in the tooltip), what it is (a fix, a verdict in its color, a comment),
    /// when, and its text. The person's dot in the text color, an agent's in the accent, so the conversation reads at a glance.
    /// </summary>
    static Control Message(TestMessage m, bool last)
    {
        bool mine = m.Who == TestList.User;
        var c = m.Kind == TestMessage.Verdict && m.State is { } s ? StateColor(s) ?? Palette.FgColor : mine ? Palette.FgColor : Palette.AccentColor;
        var grid = new Grid { ColumnDefinitions = new("Auto,*") };
        var rail = new Grid { Width = TimelineRail, RowDefinitions = new("Auto,*") };
        rail.Children.Add(new Border
        {
            Width = TimelineDot, Height = TimelineDot, CornerRadius = new(TimelineDot / 2), Margin = new(0, 4, 0, 0),
            Background = new SolidColorBrush(c), HorizontalAlignment = HorizontalAlignment.Center,
        });
        if (!last)
        {
            var line = new Border { Width = 1, Background = Palette.Line, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 3, 0, 0) };
            Grid.SetRow(line, 1);
            rail.Children.Add(line);
        }
        grid.Children.Add(rail);
        var box = new StackPanel { Spacing = 3, Margin = new(6, 0, 0, last ? 0 : 12) };
        var head = new WrapPanel { ItemSpacing = 8, LineSpacing = 2 };
        var who = new TextBlock { FontSize = 11, FontWeight = FontWeight.SemiBold, Text = RoleName(m.Who), Foreground = mine ? Palette.Fg : Palette.Accent, VerticalAlignment = VerticalAlignment.Center };
        if (!mine) ToolTip.SetTip(who, WhoName(m.Who));
        head.Children.Add(who);
        head.Children.Add(Badge(MessageLabel(m), false, c));
        head.Children.Add(new TextBlock { Classes = { "dim", "mono" }, FontSize = 11, Text = $"{When(m.At)} · {Loc.T("test.list.try", Math.Max(1, m.Try))}", VerticalAlignment = VerticalAlignment.Center });
        box.Children.Add(head);
        if (m.Text is { } t) box.Children.Add(new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, LineHeight = 18 });
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        return grid;
    }

    /// <summary>Width of the timeline's column and the size of its dots.</summary>
    const double TimelineRail = 12, TimelineDot = 8;

    /// <summary>Who said an entry, as the timeline names it: Usuario or Agente.</summary>
    static string RoleName(string who) => who == TestList.User ? Loc.T("test.list.who.user") : Loc.T("test.list.who.someagent");

    /// <summary>What a message is, in a word or two.</summary>
    static string MessageLabel(TestMessage m) => m.Kind switch
    {
        TestMessage.Fix => Loc.T("test.list.msg.fix"),
        TestMessage.Verdict => m.State is { } s ? StateName(s) : Loc.T("test.list.msg.note"),
        TestMessage.Open => Loc.T("test.list.msg.open"),
        _ => Loc.T("test.list.msg.comment"),
    };

    /// <summary>
    /// Under the conversation: the verdicts (not on a request) and the reply box. Guardar keeps the verdict picked with the
    /// reply as its note; with no verdict picked it says the reply as a comment, without changing the state.
    /// </summary>
    Control Answer(TestItem item)
    {
        var box = new StackPanel { Spacing = 8 };
        var reply = new TextBox
        {
            Classes = { "field", "note" }, Text = item.Reply, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            MinHeight = ReplyMinHeight, MaxHeight = ReplyMaxHeight,
            Watermark = Loc.T(item.Draft != null ? "test.list.note" : "test.list.reply"),
        };
        reply.LostFocus += (_, _) =>
        {
            if (ReplyChanged(reply, item)) HoldReply(ReplySaveDelayMs);
            else if (waiting is { } d) Dispatcher.UIThread.Post(() => Fill(d));
        };
        reply.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                Send(item, reply);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        replyBox = reply;
        replyItem = item;

        if (item.State != TestItem.Request)
        {
            var verdicts = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
            verdicts.Children.Add(Verdict(item, reply, TestItem.Works, CheckGlyph, "test.list.ok"));
            verdicts.Children.Add(Verdict(item, reply, TestItem.Improve, "", "test.list.improve"));
            verdicts.Children.Add(Verdict(item, reply, TestItem.WorksBadly, "", "test.list.bad"));
            verdicts.Children.Add(Verdict(item, reply, TestItem.Fails, "", "test.list.fail"));
            box.Children.Add(verdicts);
        }
        box.Children.Add(reply);

        var send = new Button { Classes = { "btn", "sm", "primary" }, HorizontalAlignment = HorizontalAlignment.Right };
        send.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children =
            {
                new TextBlock { Classes = { "icon" }, Text = item.Draft != null ? "" : SendGlyph, FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = item.Draft is { } d ? Loc.T("test.list.save.as", StateName(d)) : Loc.T("test.list.comment"), VerticalAlignment = VerticalAlignment.Center },
            },
        };
        ToolTip.SetTip(send, "Ctrl+Enter");
        send.Click += (_, _) => Send(item, reply);
        box.Children.Add(send);
        return new Border { BorderBrush = Palette.Line, BorderThickness = new(0, 1, 0, 0), Padding = new(0, 10, 0, 0), Child = box };
    }

    /// <summary>Guardar: the verdict picked with the reply as its note (and back to the list), or the reply as a comment.</summary>
    void Send(TestItem item, TextBox reply)
    {
        string? text = reply.Text;
        int id = item.Id, rev = item.Rev;
        if (item.Draft is { } state)
        {
            (Owner as Window)?.Activate();
            open = 0;
            sent = true;
            Save(() => TestList.Mark(path, id, state, text, rev));
            return;
        }
        if (string.IsNullOrWhiteSpace(text)) return;
        item.Reply = null;
        reply.Text = "";
        // the ticket is drawn again with the comment in it, and the keyboard stays in the (empty) box
        sent = true;
        focusReply = true;
        Save(() => TestList.Comment(path, id, text, rev));
    }

    /// <summary>The latest changes of the whole list, newest first: also what left it, which has no ticket any more.</summary>
    void FillHistory()
    {
        var events = TestList.History(path);
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new(2, 0, 0, 2) };
        head.Children.Add(new TextBlock { Classes = { "icon" }, Text = HistoryGlyph, FontSize = 11, Foreground = Palette.Accent, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("test.list.history.title"), Foreground = Palette.Accent, VerticalAlignment = VerticalAlignment.Center });
        Rows.Children.Add(head);
        if (events.Count == 0)
        {
            Rows.Children.Add(Empty());
            return;
        }
        var list = new StackPanel { Spacing = 2 };
        foreach (var e in Enumerable.Reverse(events).Take(HistoryAll)) list.Children.Add(EventLine(e, withId: true));
        Rows.Children.Add(Card(list));
    }

    /// <summary>A ticket's history of changes, under its conversation: who took it, moved it, cleared it.</summary>
    Control Audit(int id)
    {
        var events = TestList.History(path, id);
        var list = new StackPanel { Spacing = 0 };
        list.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("test.list.history"), Margin = new(0, 0, 0, 4) });
        if (events.Count == 0) list.Children.Add(new TextBlock { Classes = { "dim" }, FontSize = 11, Text = Loc.T("test.list.history.empty") });
        foreach (var e in events) list.Children.Add(EventLine(e, withId: false));
        return new Border { BorderBrush = Palette.Line, BorderThickness = new(0, 1, 0, 0), Padding = new(0, 8, 0, 0), Child = list };
    }

    /// <summary>One change: when, who, what (and the note of a verdict).</summary>
    static Control EventLine(TestEvent e, bool withId)
    {
        var grid = new Grid { ColumnDefinitions = new(withId ? "Auto,Auto,Auto,*" : "Auto,Auto,*"), Margin = new(0, 2) };
        int col = 0;
        void Add(Control c)
        {
            Grid.SetColumn(c, col++);
            grid.Children.Add(c);
        }
        Add(new TextBlock { Classes = { "dim", "mono" }, FontSize = 11, Text = When(e.At), Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top });
        if (withId) Add(new TextBlock { Classes = { "mono" }, FontSize = 11, Text = $"#{e.Id}", Foreground = Palette.Accent, Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top });
        Add(new TextBlock { FontSize = 11, Text = WhoName(e.Who), Foreground = e.Who == TestList.User ? Palette.Fg : Palette.Accent, Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top });
        string what = WhatName(e);
        if (e.Note != null && e.What != TestEvent.Topic) what += $" · «{e.Note}»";
        Add(new TextBlock { Classes = { "dim" }, FontSize = 11, Text = what, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top });
        return grid;
    }

    static string When(DateTime at) => at.Date == DateTime.Today
        ? at.ToString("HH:mm", CultureInfo.CurrentCulture)
        : at.ToString("dd/MM HH:mm", CultureInfo.CurrentCulture);

    static string WhoName(string who) => who switch
    {
        TestList.User => Loc.T("test.list.who.user"),
        TestList.SomeAgent => Loc.T("test.list.who.someagent"),
        _ when who.StartsWith("agent:", StringComparison.Ordinal) => Loc.T("test.list.who.agent", who[6..]),
        _ => who,
    };

    static string WhatName(TestEvent e)
    {
        string text = e.Text is { Length: > 0 } t ? $": {t}" : "";
        return e.What switch
        {
            TestEvent.Added => Loc.T("test.list.what.add") + text,
            TestEvent.Asked => Loc.T("test.list.what.request") + text,
            TestEvent.Edited => Loc.T("test.list.what.edit") + text,
            TestEvent.Moved => Loc.T("test.list.what.resend"),
            TestEvent.Took => Loc.T("test.list.what.take"),
            TestEvent.Released => Loc.T("test.list.what.release"),
            TestEvent.Fixed => Loc.T("test.list.what.fix"),
            TestEvent.FixRefused => Loc.T("test.list.what.fixclosed"),
            TestEvent.Marked => Loc.T("test.list.what.mark", StateName(e.State)),
            TestEvent.Commented => Loc.T("test.list.what.comment"),
            TestEvent.Removed => Loc.T("test.list.what.remove"),
            TestEvent.Cleared => Loc.T("test.list.what.clear"),
            TestEvent.Topic => Loc.T("test.list.what.topic", TopicName(e.Note)),
            TestEvent.Titled => Loc.T("test.list.what.title", e.Note ?? "—"),
            _ => e.What,
        };
    }

    static string StateName(string? s) => s switch
    {
        TestItem.Works => Loc.T("test.list.ok"),
        TestItem.Fails => Loc.T("test.list.fail"),
        TestItem.WorksBadly => Loc.T("test.list.bad"),
        TestItem.Improve => Loc.T("test.list.improve"),
        TestItem.Request => Loc.T("test.list.request"),
        _ => Loc.T("test.list.totest"),
    };

    /// <summary>
    /// Tells of the "Nuevo" tickets not told yet: on the first list, what came while the bench was closed; later, what
    /// an agent just added, sent back or said. Outside the window's focus the taskbar button flashes too.
    /// </summary>
    void Announce(TestListData data)
    {
        var fresh = data.Items.Where(i => i.Unseen).OrderBy(i => i.Id).ToList();
        bool first = announced == null;
        var arrived = first ? fresh : fresh.Where(i => !announced!.Contains(i.Id)).ToList();
        announced = [.. fresh.Select(i => i.Id)];
        // the one being read is no news
        arrived.RemoveAll(i => i.Id == open && IsActive);
        if (arrived.Count == 0)
        {
            // what was seen meanwhile is no news any more
            if (NewsBar.IsVisible && !announced.Contains(newsTarget)) NewsBar.IsVisible = false;
            return;
        }
        var top = arrived[0];
        newsTarget = top.Id;
        string said = LastSaid(top) is { Kind: TestMessage.Comment, Text: { } t } ? t : top.Text;
        NewsText.Text = arrived.Count == 1
            ? Loc.T("test.list.news.one", top.Id, said)
            : Loc.T("test.list.news.many", arrived.Count, top.Id, said);
        ToolTip.SetTip(NewsText, string.Join("\n", arrived.Select(i => $"#{i.Id} · {i.Text}")));
        NewsBar.IsVisible = true;
        // only what arrives while open: a bench that moves to a newer copy starts over and would blink at every build
        if (!IsActive && !first) Flash();
    }

    /// <summary>The verdicts so far, each in its color, so the state of the whole bank reads at a glance.</summary>
    void FillTotals(TestListData data)
    {
        Totals.Children.Clear();
        foreach (var (state, key) in (ReadOnlySpan<(string, string)>)[(TestItem.Fails, "test.list.fail"), (TestItem.WorksBadly, "test.list.bad"),
                     (TestItem.Improve, "test.list.improve"), (TestItem.Works, "test.list.ok")])
        {
            int n = data.Count(state);
            if (n == 0) continue;
            var c = StateColor(state)!.Value;
            var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            cell.Children.Add(Dot(c));
            cell.Children.Add(new TextBlock { Classes = { "mono" }, FontSize = 11, Text = n.ToString(CultureInfo.CurrentCulture), Foreground = Palette.Alpha(c, 1), VerticalAlignment = VerticalAlignment.Center });
            ToolTip.SetTip(cell, Loc.T(key));
            Totals.Children.Add(cell);
        }
    }

    Control Empty()
    {
        var box = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 40, 0, 0) };
        var c = Palette.AccentColor;
        box.Children.Add(new Border
        {
            Width = 40, Height = 40, CornerRadius = new(20), Background = Palette.Alpha(c, BadgeTint), HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock { Classes = { "icon" }, Text = CheckGlyph, FontSize = 18, Foreground = Palette.Alpha(c, 1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        box.Children.Add(new TextBlock { Classes = { "dim" }, Text = Loc.T(HistoryButton.IsChecked == true ? "test.list.history.empty" : search.Length > 0 ? "test.list.search.none" : "test.list.empty"), HorizontalAlignment = HorizontalAlignment.Center });
        return box;
    }

    static Border Dot(Color c) => new() { Width = 7, Height = 7, CornerRadius = new(3.5), Background = Palette.Alpha(c, 1), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Each section with its color: to try in the accent, the fixes in red, the requests in violet, what works in green.</summary>
    static Color TabColor(int i) => i switch
    {
        FixTabIndex => Palette.RecColor,
        RequestTabIndex => VioletKind,
        DoneTabIndex => GreenState,
        _ => Palette.AccentColor,
    };

    /// <summary>Each kind of request in its color: a bug red, an improvement blue, a new function violet, an idea amber.</summary>
    static Color KindColor(string k) => k switch
    {
        TestItem.Bug => Palette.RecColor,
        TestItem.Better => BlueState,
        TestItem.Feature => VioletKind,
        _ => YellowState,
    };

    /// <summary>
    /// The composer: always there in the list of Pedidos; from anywhere else, + or Ctrl+N opens it for one request and it
    /// closes once added. Claude's "Nueva skill…" uses it too. No kind (asked by the user, #129): the agent that takes a request gives it one.
    /// </summary>
    void OpenComposer(bool skill)
    {
        if (editing != 0) EndEdit();
        skillMode = skill;
        composerAsked = true;
        RequestBox.Text = "";
        UpdateComposer();
        Activate();
        RequestBox.Focus();
    }

    void CloseComposer()
    {
        EndEdit();
        skillMode = false;
        composerAsked = false;
        UpdateComposer();
    }

    void UpdateComposer()
    {
        Composer.IsVisible = composerAsked || editing != 0;
        CancelEditButton.IsVisible = editing != 0 || composerAsked;
        if (editing != 0) return;
        ComposerTitle.Text = Loc.T(skillMode ? "test.list.claude.newskill.title" : "test.list.request.new");
        ComposerIcon.Text = skillMode ? ReviewGlyph : AddGlyph;
        RequestBox.Watermark = Loc.T(skillMode ? "test.list.claude.newskill" : "test.list.request.hint");
        AddButton.Content = Loc.T(skillMode ? "test.list.claude.newskill.create" : "test.list.request.add");
    }

    /// <summary>Adds the request in the box, saves the one being edited, or asks Claude for the skill. The box empties at once: the list catches up.</summary>
    void SubmitComposer()
    {
        string text = (RequestBox.Text ?? "").Trim();
        if (text == "") return;
        if (skillMode)
        {
            Run(NewSkillPrompt(text));
            CloseComposer();
            return;
        }
        // no kind nor topic from here (asked by the user): the agent that takes the request gives it both
        string copy = build;
        int id = editing, rev = editingRev;
        if (id != 0) Save(() => TestList.EditRequest(path, id, text, null, rev, null));
        else Save(() => TestList.AddRequest(path, text, null, copy, null));
        if (composerAsked || id != 0) CloseComposer();
        else
        {
            EndEdit();
            UpdateComposer();
            RequestBox.Focus();
        }
    }

    void BeginEdit(TestItem item)
    {
        skillMode = false;
        editing = item.Id;
        editingRev = item.Rev;
        RequestBox.Text = item.Text;
        UpdateComposer();
        AddButton.Content = Loc.T("test.list.save");
        ComposerTitle.Text = Loc.T("test.list.request.editing", item.Id);
        ComposerIcon.Text = EditGlyph;
        RequestBox.Watermark = Loc.T("test.list.request.hint");
        Activate();
        RequestBox.Focus();
        RequestBox.CaretIndex = item.Text.Length;
    }

    void EndEdit()
    {
        editing = 0;
        RequestBox.Text = "";
    }

    /// <summary>The second click before a ticket leaves the list (asked by the user: a stray click lost items).</summary>
    Control RemoveConfirm(TestItem item)
    {
        var grid = new Grid { ColumnDefinitions = new("*,Auto,Auto") };
        grid.Children.Add(new TextBlock { Classes = { "notice" }, Text = Loc.T("test.list.remove.ask", item.Id), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var cancel = new Button { Classes = { "btn", "sm" }, Content = Loc.T("common.cancel"), Margin = new(8, 0, 0, 0) };
        cancel.Click += (_, _) =>
        {
            confirmRemove = false;
            Refill();
        };
        Grid.SetColumn(cancel, 1);
        grid.Children.Add(cancel);
        var yes = new Button { Classes = { "btn", "sm", "danger" }, Content = Loc.T("test.list.remove.yes"), Margin = new(8, 0, 0, 0) };
        int rev = item.Rev, id = item.Id;
        yes.Click += (_, _) =>
        {
            confirmRemove = false;
            open = 0;
            if (editing == id) CloseComposer();
            Save(() => TestList.Remove(path, id, rev));
        };
        Grid.SetColumn(yes, 2);
        grid.Children.Add(yes);
        return grid;
    }

    /// <summary>"⋯": what is not done every time (edit a request, the history of changes, take it off the list).</summary>
    Button MoreButton(TestItem item)
    {
        var more = new Button { Classes = { "act" }, Content = new TextBlock { Classes = { "icon" }, Text = MoreGlyph, FontSize = 12 } };
        ToolTip.SetTip(more, Loc.T("test.list.more"));
        AutomationProperties.SetName(more, Loc.T("test.list.more"));
        more.Click += (_, _) =>
        {
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
            if (item.State == TestItem.Request) menu.Items.Add(Item(Loc.T("test.list.request.edit"), EditGlyph, () => BeginEdit(item)));
            menu.Items.Add(Item(Loc.T("test.list.history"), HistoryGlyph, () =>
            {
                auditOpen = !auditOpen;
                Refill();
            }, check: auditOpen));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(Loc.T("test.list.remove"), DeleteGlyph, () =>
            {
                confirmRemove = true;
                Refill();
            }));
            menu.ShowAt(more);
        };
        return more;
    }

    static MenuItem Item(string text, string glyph, Action act, bool check = false)
    {
        var item = new MenuItem
        {
            Header = text,
            Icon = new TextBlock { Classes = { "icon" }, Text = check ? CheckGlyph : glyph, FontSize = 12, Foreground = check ? Palette.Accent : Palette.Dim },
        };
        item.Click += (_, _) => act();
        return item;
    }

    /// <summary>A badge tinted in its color, with the text in the full color; strong is filled, for "Nuevo".</summary>
    static Border Badge(string text, bool mono, Color c, bool strong = false)
    {
        var t = new TextBlock { FontSize = 11, Text = text, Foreground = strong ? Palette.App : Palette.Alpha(c, 1) };
        if (mono) t.Classes.Add("mono");
        if (strong) t.FontWeight = FontWeight.SemiBold;
        return new() { Background = Palette.Alpha(c, strong ? 1 : BadgeTint), CornerRadius = new(5), Padding = new(6, 1), VerticalAlignment = VerticalAlignment.Center, Child = t };
    }

    /// <summary>
    /// "Tomado", with a turning ring (asked by the user): a session is working on it right now. The ring is the app's
    /// loader (<c>Arc.spin</c> in <c>App.axaml</c>), so it turns from the style and costs no rebuild.
    /// </summary>
    static Border TakenBadge(int count = 0)
    {
        var c = Palette.AccentColor;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        row.Children.Add(new Avalonia.Controls.Shapes.Arc
        {
            Classes = { "spin" }, Width = 10, Height = 10, StrokeThickness = 1.5, Stroke = Palette.Alpha(c, 1),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock { FontSize = 11, Text = count > 0 ? Loc.T("test.list.topic.taken", count) : Loc.T("test.list.request.taken"), Foreground = Palette.Alpha(c, 1), VerticalAlignment = VerticalAlignment.Center });
        return new() { Background = Palette.Alpha(c, BadgeTint), CornerRadius = new(5), Padding = new(6, 1), VerticalAlignment = VerticalAlignment.Center, Child = row };
    }

    /// <summary>
    /// A ticket's topic: plain, so the kind and the state keep the color. Only to read: the agent that takes a ticket
    /// gives it its topic (asked by the user, too much to fill from here); until then it says "A categorizar".
    /// </summary>
    static Border TopicBadge(string? topic) => Badge(TopicName(topic), false, topic == null ? Palette.FaintColor : Palette.DimColor);

    static string TopicName(string? topic) => topic == null ? Loc.T("test.list.topic.none") : Loc.T("test.list.topic." + topic);

    /// <summary>
    /// Whether a ticket passes the search: every word is in its number, its subject, something said in it, its topic or
    /// its kind, ignoring case and accents ("cancion" finds "canción"). "#114" or "114" finds the ticket by its number.
    /// </summary>
    bool Found(TestItem i)
    {
        foreach (var word in search)
        {
            string w = word.TrimStart('#');
            if (w == "") continue;
            if (w.All(char.IsAsciiDigit) && i.Id.ToString(CultureInfo.InvariantCulture) == w) continue;
            if (!Has(i.Text, w) && !Has(i.Title, w) &&!i.Thread.Any(m => Has(m.Text, w)) && !Has(i.Topic == null ? null : TopicName(i.Topic), w)
                && !Has(i.Kind == null ? null : Loc.T("test.list.kind." + i.Kind), w)) return false;
        }
        return true;
    }

    static bool Has(string? text, string word) => text != null
        && CultureInfo.CurrentCulture.CompareInfo.IndexOf(text, word, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    static Func<TestItem, bool> Is(string state) => i => i.State == state;

    /// <summary>What the review skill is handed for a whole section: only the ones with work (Para probar waits for the person).</summary>
    static string? TabReview(int i) => i switch
    {
        FixTabIndex => "a arreglar",
        RequestTabIndex => "pedidos",
        _ => null,
    };

    /// <summary>Where Claude opens: the desktop app (by default) or a console, picked once in the Claude menu.</summary>
    static bool InApp
    {
        get => AppConfig.Get(OpenInKey) != "console";
        set
        {
            try { AppConfig.Set(OpenInKey, value ? "app" : "console"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    static string ReviewPrompt(string args) => $"/{AgentLauncher.ReviewSkill} {args}";

    /// <summary>
    /// "Revisar" on every ticket with something left (asked by the user: all but Resuelto), as two buttons (pedido del
    /// usuario, #121): Desktop prefills a new session of the Claude app, Consola starts Claude Code at once with the same
    /// command. Naming the number is enough for the skill to go ahead without a proposal first; it still asks a design decision.
    /// </summary>
    Control? ReviewButton(TestItem item)
    {
        if (root == null || item.State == TestItem.Works) return null;
        string prompt = ReviewPrompt(item.Id.ToString(CultureInfo.InvariantCulture));
        var pair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        pair.Children.Add(ReviewChip(ReviewGlyph, Loc.T("test.list.review.desktop"), Loc.T("test.list.review.desktop.tip", item.Id), () => ReviewAndClose(prompt, desktop: true)));
        pair.Children.Add(ReviewChip(ConsoleGlyph, Loc.T("test.list.review.console"), Loc.T("test.list.review.console.tip", item.Id), () => ReviewAndClose(prompt, desktop: false)));
        return pair;
    }

    /// <summary>Sends a ticket to review and goes back to the list (pedido del usuario, #119); on a failure it stays, with the error.</summary>
    void ReviewAndClose(string prompt, bool desktop)
    {
        if (RunIn(prompt, desktop)) CloseTicket();
    }

    /// <summary>The console's glyph (Fluent "CommandPrompt").</summary>
    const string ConsoleGlyph = "";

    static Button ReviewChip(string glyph, string text, string tip, Action click)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        row.Children.Add(new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 11, Foreground = Palette.Accent, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Classes = { "chip" }, Content = row, MinHeight = 26, Padding = new(8, 0), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(b, tip);
        AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>The prompt of "Nueva skill": the project's rules for skills, so it needs no follow-up. One line (cmd).</summary>
    static string NewSkillPrompt(string what) =>
        $"Creá una skill nueva para este proyecto: «{what}». Va en .claude/skills/xt_<nombre>/SKILL.md, con el prefijo xt_ en la carpeta y en su name (decisión del usuario). " +
        "Frontmatter: name, description (en español, qué hace y cuándo usarla) y argument-hint si recibe argumentos. " +
        "Seguí AGENTS.md y el estilo de las skills que ya están en .claude/skills. Si algo del diseño no queda claro, preguntame antes de escribirla.";

    /// <summary>
    /// The skills with work of their own, in this order and with these names; any other xt_ skill gets an entry too, by
    /// its name, with its description as tooltip, so a new skill shows here without touching this.
    /// </summary>
    static readonly (string Key, string Prompt)[] SkillButtons =
    [
        ("test.list.claude.launch", "/xt_launch"),
        ("test.list.claude.last", "/xt_launch sin compilar: abrí la última copia (-Abrir)"),
        ("test.list.claude.list", "/xt_lista-workbench"),
        ("test.list.claude.review", "/xt_revisar-workbench"),
        ("test.list.claude.publish", "/xt_publicar-version"),
    ];

    /// <summary>
    /// Claude's menu (asked by the user: a row of buttons, each with its console twin, took the top of the window): the
    /// project's skills, "Nueva skill…", and where they open, remembered.
    /// </summary>
    void ShowClaudeMenu()
    {
        if (root == null) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var skills = AgentLauncher.Skills(root);
        foreach (var (key, prompt) in SkillButtons)
        {
            string skill = prompt.Split(' ')[0][1..];
            if (skills.All(s => s.Name != skill)) continue;
            var item = Item(Loc.T(key), key == "test.list.claude.launch" ? "" : "", () => Run(prompt));
            ToolTip.SetTip(item, Loc.T(key + ".tip"));
            menu.Items.Add(item);
        }
        foreach (var (name, description) in skills)
        {
            if (SkillButtons.Any(b => b.Prompt.Split(' ')[0] == "/" + name)) continue;
            string title = name.StartsWith("xt_", StringComparison.Ordinal) ? name[3..] : name;
            title = title.Replace('-', ' ').Replace('_', ' ');
            title = title.Length > 0 ? char.ToUpperInvariant(title[0]) + title[1..] : name;
            var item = Item(title, "", () => Run("/" + name));
            if (description != "") ToolTip.SetTip(item, description);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Loc.T("test.list.claude.newskill.menu"), AddGlyph, () => OpenComposer(skill: true)));
        menu.Items.Add(new Separator());
        bool app = InApp;
        menu.Items.Add(Item(Loc.T("test.list.claude.in.app"), "", () => InApp = true, check: app));
        menu.Items.Add(Item(Loc.T("test.list.claude.in.console"), "", () => InApp = false, check: !app));
        menu.ShowAt(ClaudeButton);
    }

    void Run(string prompt) => RunIn(prompt, InApp);

    /// <summary>Starts the agent; false (and the reason shown) if it could not.</summary>
    bool RunIn(string prompt, bool desktop)
    {
        if (root == null) return false;
        string? why = AgentLauncher.Run(root, prompt, desktop);
        ErrorText.Text = why != null ? Loc.T("test.list.review.error", why) : null;
        return why == null;
    }

    /// <summary>A section's link in the tree, in its color: reviews that topic's tickets of the section together.</summary>
    Control ReviewLink(string args, Color c)
    {
        var b = new Button
        {
            Classes = { "link" }, Content = Loc.T("test.list.review"), Foreground = Palette.Alpha(c, 1), Padding = new(6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => Run(ReviewPrompt(args));
        return b;
    }

    /// <summary>The row on top of the list: reviews all the work (requests and fixes) in one session.</summary>
    Control ReviewAllRow(string args, int count, Color c)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Classes = { "icon" }, Text = ReviewGlyph, FontSize = 12, Foreground = Palette.Alpha(c, 1), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = Loc.T("test.list.review.all", count), VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Classes = { "btn", "sm" }, Content = row, HorizontalAlignment = HorizontalAlignment.Left };
        ToolTip.SetTip(b, Loc.T("test.list.review.all.tip"));
        AutomationProperties.SetName(b, Loc.T("test.list.review.all", count));
        b.Click += (_, _) => Run(ReviewPrompt(args));
        return b;
    }

    /// <summary>
    /// Each ticket in its own box, so it is clear where one ends (asked by the user). Drawn here: <c>Border.section</c>
    /// lives in the main window's styles and does not reach this window. A strong one (taken, or new) has the accent edge.
    /// </summary>
    static Border Card(Control body, Color? state = null, bool strong = false)
    {
        var c = state ?? Palette.AccentColor;
        var grid = new Grid { ColumnDefinitions = new("Auto,*") };
        // the wash under the whole card, inside its 1 px edge
        var wash = new Border { Background = Palette.Alpha(c, CardWash), CornerRadius = new(7) };
        Grid.SetColumnSpan(wash, 2);
        grid.Children.Add(wash);
        // a bar of its own, clear of the rounded corners: a 3 px column with the card's radius drew as a crescent (asked by the user)
        grid.Children.Add(new Border
        {
            Width = CardStripe, Margin = new(CardStripeInset, CardStripeInset, 0, CardStripeInset), CornerRadius = new(CardStripe / 2),
            Background = Palette.Alpha(c, state == null ? 0.35 : 1),
        });
        var inner = new Border { Padding = new(9, 10, 12, 10), Child = body };
        Grid.SetColumn(inner, 1);
        grid.Children.Add(inner);
        return new()
        {
            Background = Palette.Raised, BorderBrush = strong ? Palette.Accent : state is { } s ? Palette.Alpha(s, StateEdge * 0.5) : Palette.Hover,
            BorderThickness = new(1), CornerRadius = new(8), Child = grid,
        };
    }

    /// <summary>
    /// Each verdict in its own color (asked by the user): green works, blue to improve, yellow works badly, red fails.
    /// Fixed, not the theme's state colors, so a verdict reads the same with any accent. Pending has none.
    /// </summary>
    static Color? StateColor(string state) => state switch
    {
        TestItem.Works => GreenState,
        TestItem.Improve => BlueState,
        TestItem.WorksBadly => YellowState,
        TestItem.Fails => Palette.RecColor,
        _ => null,
    };

    static bool ReplyChanged(TextBox box, TestItem item) => (box.Text ?? "").Trim() != (item.Reply ?? "");

    /// <summary>
    /// Anda takes effect at once (and goes back to the list); the others are picked and wait for Guardar, with the reply
    /// as their note. Pressing the one lit undoes it: a verdict still picked is dropped, a saved one takes the ticket back
    /// to Para probar.
    /// </summary>
    Button Verdict(TestItem item, TextBox reply, string state, string glyph, string key)
    {
        bool lit = (item.Draft ?? item.State) == state;
        var color = Palette.Alpha(StateColor(state) ?? Palette.FgColor, 1);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 12, Foreground = lit ? color : Palette.Dim, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = Loc.T(key), Foreground = lit ? color : Palette.Dim, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Classes = { "chip" }, MinHeight = 30, Content = row };
        if (lit)
        {
            b.BorderBrush = color;
            b.Background = Palette.Alpha(StateColor(state) ?? Palette.FgColor, BadgeTint);
        }
        int rev = item.Rev, id = item.Id;
        b.Click += (_, _) =>
        {
            string? text = reply.Text;
            if (!lit && state != TestItem.Works)
            {
                focusReply = true;
                Save(() => TestList.Hold(path, id, state, text));
                return;
            }
            if (lit && item.Draft != null)
            {
                Save(() => TestList.Hold(path, id, null, text));
                return;
            }
            (Owner as Window)?.Activate();
            open = 0;
            Save(() => TestList.Mark(path, id, lit ? TestItem.Pending : state, text, rev));
        };
        return b;
    }

    /// <summary>
    /// Writes off the UI thread (other sessions may hold the list a moment), then asks to read it again. <paramref name="write"/>
    /// must not touch a control: it runs on that other thread.
    /// </summary>
    async void Save(Action write, int delayMs = 0)
    {
        ErrorText.Text = null;
        var mine = writes.ContinueWith(_ => Try(write), TaskScheduler.Default);
        writes = mine;
        bool ok = await mine;
        if (delayMs > 0) await Task.Delay(delayMs);
        if (ok) Changed?.Invoke();
    }

    /// <summary>Runs a write; a stale ticket counts as done, so the list is read again and shows what changed.</summary>
    bool Try(Action write)
    {
        try
        {
            write();
            return true;
        }
        catch (TestListStaleException e)
        {
            Dispatcher.UIThread.Post(() => ErrorText.Text = Loc.T("test.list.stale", e.Id));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Dispatcher.UIThread.Post(() => ErrorText.Text = Loc.T("test.list.error", e.Message));
            return false;
        }
    }

    /// <summary>The taskbar button blinks until the window is in front: something new came while the person was elsewhere.</summary>
    void Flash() => FlashTimes(FlashAll | FlashTimerNoForeground);

    void StopFlash() => FlashTimes(FlashStop);

    void FlashTimes(uint flags)
    {
        if (TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;
        var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Hwnd = hwnd, Flags = flags };
        FlashWindowEx(ref info);
    }

    const uint FlashStop = 0, FlashAll = 3, FlashTimerNoForeground = 12;

    [StructLayout(LayoutKind.Sequential)]
    struct FlashInfo
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    static extern bool FlashWindowEx(ref FlashInfo info);
}
