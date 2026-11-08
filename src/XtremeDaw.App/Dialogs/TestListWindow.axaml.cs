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
/// is open it comes as a toast, like XTreme's (asked by the user, #260), and the bell in the title bar counts them. Every click carries the ticket's <see cref="TestItem.Rev"/>: a ticket that went
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
    /// <summary>
    /// A mouse button is down. The press that takes the focus out of the reply box must not redraw the ticket before
    /// it is released: the button under it would be replaced and the click lost (#147).
    /// </summary>
    bool pressing;
    /// <summary>
    /// The request whose text is edited in place, in its open ticket (asked by the user, #177: the pencil beside the
    /// text, not the composer), and the version it had; 0 = none. The text is kept here, so a list that arrives
    /// meanwhile draws the box again with what was written.
    /// </summary>
    int inline, inlineRev;
    string inlineText = "";
    TextBox? inlineBox;
    bool focusInline;
    /// <summary>What the composer writes: a request, or a new skill for Claude.</summary>
    bool skillMode;
    /// <summary>The composer was opened with Ctrl+Shift+N (asked by the user, #178): once added, the request goes to an agent in a console.</summary>
    bool agentMode;
    /// <summary>The composer was opened from elsewhere (+ or Ctrl+N): it closes once the request is added.</summary>
    bool composerAsked;
    /// <summary>The open ticket is asking whether to take it off the list.</summary>
    bool confirmRemove;
    /// <summary>The open ticket shows its history of changes (who took it, moved it…), under the conversation.</summary>
    bool auditOpen;
    /// <summary>The topics folded in the tree (<see cref="NoTopic"/> = the ones without) and the sections folded inside each ("topic:section"), kept across runs.</summary>
    readonly HashSet<string> folded = [.. (AppConfig.Get(FoldedKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)];
    /// <summary>
    /// What the list shows (asked by the user, #249: with many tickets at once the states got mixed up), remembered in
    /// <c>config.json</c>: "pending" (requests and fixes nobody is on), "busy" (an agent is starting or working on it),
    /// "totest" (waits for the person to try it), "done" (works) or "all".
    /// </summary>
    const string ViewKey = "benchView";
    static readonly string[] Views = ["pending", "busy", "totest", "done", "all"];
    string view = AppConfig.Get(ViewKey) is { } savedView && Views.Contains(savedView) ? savedView : "all";
    /// <summary>The words in the search box, each one has to be in the ticket (empty = no search).</summary>
    string[] search = [];
    /// <summary>The tickets unfolded in the list to read their conversation in place (#197), for this run only.</summary>
    readonly HashSet<int> expanded = [];
    /// <summary>The end of the writes in order: a reply saved by losing the focus cannot land after the click that took it.</summary>
    Task<bool> writes = Task.FromResult(true);

    /// <summary>The "Nuevo" tickets already announced, so the toasts tell only of what just arrived. Null before the first list.</summary>
    HashSet<int>? announced;
    /// <summary>XTreme's toasts, bottom right over the list: one per ticket that arrives (#260).</summary>
    readonly ToastHost toasts = new() { Margin = new(12) };
    /// <summary>What closes each ticket's toast, so a ticket seen meanwhile takes its toast away.</summary>
    readonly Dictionary<int, Action> newsToasts = [];
    /// <summary>More arriving at once than this go in one toast, not a pile of them.</summary>
    const int MaxNewsToasts = 3;
    /// <summary>The widest a toast gets: XTreme's, less in a narrow window.</summary>
    const double ToastWidth = 420;

    /// <summary>The file changed from here.</summary>
    public event Action? Changed;

    /// <summary>The repository the list belongs to, for "Revisar" and the skills (<see cref="AgentLauncher"/>); null hides them.</summary>
    readonly string? root;

    /// <summary>Each session's lock traffic light (#167), read at every fill: what a ticket's "en curso" shows.</summary>
    Dictionary<string, LockLight> lights = new();
    /// <summary>The alerts between the agents' sessions (#263), read at every fill: toasts and each "en curso" tooltip.</summary>
    List<AgentAlert> alerts = [];
    /// <summary>The alerts already told in a toast. Null before the first list: what was there then is not news.</summary>
    HashSet<string>? alertsTold;
    /// <summary>Alerts in the tooltip of a ticket's "en curso": the newest few.</summary>
    const int AlertTipMax = 3;
    /// <summary>Which agents' sessions are running (#173): a ticket whose session ended is not "Tomado". Read once per fill.</summary>
    AgentSessions sessions = AgentSessions.None;
    /// <summary>Each session's model and effort (#315), beside its "en curso" and on the Agentes map. Read once per fill.</summary>
    Dictionary<string, string> models = [];
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
        InitImages();
        InitAgents();
        InitConsoles();
        if (TestBuild.Label is { } label)
        {
            BuildText.Text = label;
            BuildBadge.IsVisible = true;
        }

        ToolTip.SetTip(NewRequestButton, root == null
            ? $"{Loc.T("test.list.request.new")} (Ctrl+N)"
            : $"{Loc.T("test.list.request.new")} (Ctrl+N)\n{Loc.T("test.list.request.agent")} (Ctrl+Shift+N)");
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
        ToolTip.SetTip(AddButton, root == null ? "Ctrl+Enter" : $"Ctrl+Enter\n{Loc.T("test.list.request.agent")} (Ctrl+Shift+Enter)");
        CancelEditButton.Click += (_, _) => CloseComposer();
        // Tunnel: with AcceptsReturn the TextBox takes Enter (Ctrl included) as a new line before a bubbling handler sees it.
        RequestBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                SubmitComposer(agent: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && (skillMode || composerAsked))
            {
                CloseComposer();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, _) => pressing = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) =>
        {
            pressing = false;
            // posted: the click runs first (it may draw on its own, which leaves nothing waiting)
            Dispatcher.UIThread.Post(FillWaiting);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        PointerCaptureLost += (_, _) => pressing = false;
        // Ctrl+Enter confirms from anywhere in the bench, not only from inside a box: the open ticket's reply, else the composer.
        // Tunnel, so a focused button or chip does not take it as its own Enter; the boxes keep their own handlers.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter || !e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Source is TextBox) return;
            if (replyBox is { IsEffectivelyVisible: true } box && replyItem != null)
            {
                Send(replyItem, box, agent: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
            }
            else if (Composer.IsEffectivelyVisible)
            {
                SubmitComposer(agent: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
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
            else if (e.Key == Key.N && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && root != null)
            {
                OpenComposer(skill: false, agent: true);
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
                var first = current == null ? null : Shown(current).FirstOrDefault();
                if (first != null) OpenTicket(first.Id);
                e.Handled = true;
            }
        };
        Grid.SetRow(toasts, 1);
        toasts.ZIndex = 10;
        Shell.Children.Add(toasts);
        Shell.SizeChanged += (_, e) => toasts.Width = Math.Max(0, Math.Min(ToastWidth, e.NewSize.Width - 24));
        ToolTip.SetTip(BellButton, Loc.T("test.list.bell"));
        BellButton.Click += (_, _) => ShowBellMenu();

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
        InitZoom();
    }

    /// <summary>
    /// The bench's own text size (asked by the user, #143), remembered in <c>config.json</c> apart from XTreme's
    /// <c>uiScale</c>: a scale over the list, the search and the composer, on top of the app's.
    /// </summary>
    const string ZoomKey = "benchZoom";
    const double ZoomMin = 0.8, ZoomMax = 1.6;
    /// <summary>The slider writes the setting once it rests, not at every step of a drag.</summary>
    static readonly TimeSpan ZoomSaveDelay = TimeSpan.FromMilliseconds(400);

    void InitZoom()
    {
        double zoom = 1;
        try
        {
            if (double.TryParse(AppConfig.Get(ZoomKey), NumberStyles.Float, CultureInfo.InvariantCulture, out double z) && double.IsFinite(z))
                zoom = Math.Clamp(z, ZoomMin, ZoomMax);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var save = new DispatcherTimer { Interval = ZoomSaveDelay };
        save.Tick += (_, _) =>
        {
            save.Stop();
            try { AppConfig.Set(ZoomKey, ZoomSlider.Value.ToString(CultureInfo.InvariantCulture)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* View preference only. */ }
        };
        void Apply(double z)
        {
            Zoomed.LayoutTransform = Math.Abs(z - 1) < 0.001 ? null : new ScaleTransform(z, z);
            ZoomText.Text = $"{Math.Round(z * 100).ToString(CultureInfo.CurrentCulture)}%";
        }
        ZoomSlider.Value = zoom;
        Apply(zoom);
        ZoomSlider.ValueChanged += (_, e) =>
        {
            Apply(e.NewValue);
            save.Stop();
            save.Start();
        };
        // a double click goes back to 100 %
        ZoomSlider.DoubleTapped += (_, _) => ZoomSlider.Value = 1;
        ToolTip.SetTip(ZoomText, Loc.T("test.list.zoom.tip"));
        ToolTip.SetTip(ZoomPanel.Children[0], Loc.T("test.list.zoom.tip"));
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

    /// <summary>
    /// The bench's language and theme button (asked by the user, #151), first in the header. <paramref name="language"/>
    /// gets the code picked: the bench builds the window again in it (<see cref="TakeOver"/>).
    /// </summary>
    public void AddLookPicker(Action<string> language) => HeaderButtons.Children.Insert(0, new Controls.ThemePicker(language));

    /// <summary>
    /// What <paramref name="old"/> had on the screen that is not on disk yet: the search and an unsent request. Its
    /// place is saved here so <see cref="RestorePlace"/> reads it; a reply being written is held by its Closing.
    /// </summary>
    public void TakeOver(TestListWindow old)
    {
        old.SavePlace();
        if (!RestorePlace())
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = old.Position;
            Width = old.Bounds.Width;
            Height = old.Bounds.Height;
        }
        SearchBox.Text = old.SearchBox.Text;
        if (old.composerAsked)
        {
            // not OpenComposer: this window is not shown yet, nothing to activate
            skillMode = old.skillMode;
            composerAsked = true;
            UpdateComposer();
            RequestBox.Text = old.RequestBox.Text;
            RequestBox.CaretIndex = RequestBox.Text?.Length ?? 0;
        }
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
        inline = 0;
        confirmRemove = false;
        auditOpen = false;
        HistoryButton.IsChecked = false;
        UpdateComposer();
        if (item.Unseen) Save(() => TestList.See(path, [id]));
        Refill();
        Scroller.Offset = default;
    }

    /// <summary>Opens a ticket asked from outside: XTreme's "give the verdict" on the copy being tried (#198).</summary>
    public void ShowTicket(int id) => OpenTicket(id);

    void CloseTicket()
    {
        HoldReply(0);
        open = 0;
        inline = 0;
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

    /// <summary>Draws the list that waited for the reply box to lose the focus, once the click that took it is over.</summary>
    void FillWaiting()
    {
        if (waiting is { } d && !pressing && replyBox?.IsFocused != true && inlineBox?.IsFocused != true) Fill(d);
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
        AnnounceAlerts(data);
        // its own window, with a box that is never redrawn: it does not wait for a reply being written here
        lights = LockLight.Read(root, data.Items);
        FillAgents(data);
        if (!sent && IsActive && (replyBox?.IsFocused == true || inlineBox?.IsFocused == true))
        {
            waiting = data;
            return;
        }
        // the box keeps its focus across the redraw (a list that came while writing)
        focusInline |= inlineBox?.IsFocused == true;
        sent = false;
        waiting = null;
        replyBox = null;
        replyItem = null;
        inlineBox = null;
        sessions = AgentSessions.Read(root);
        models = AgentLauncher.Models(root);
        Rows.Children.Clear();
        TicketBar.Child = null;
        TicketBar.IsVisible = false;
        if (HistoryButton.IsChecked == true) FillHistory();
        else if (open != 0 && data.Items.Find(i => i.Id == open) is { } ticket) FillTicket(ticket);
        else
        {
            // a ticket that left the list (removed, or cleared) takes the person back to it
            open = 0;
            FillTab(data);
        }
        FillTotals(data);
        FillViews(data);
        ClearButton.IsVisible = data.Count(TestItem.Works) > 0 && open == 0;
        UpdateFoldAll(data);
        if (focusReply && replyBox != null)
        {
            Activate();
            replyBox.Focus();
            replyBox.CaretIndex = replyBox.Text?.Length ?? 0;
        }
        else if (focusInline && inlineBox != null)
        {
            Activate();
            inlineBox.Focus();
            inlineBox.CaretIndex = inlineBox.Text?.Length ?? 0;
        }
        focusReply = false;
        focusInline = false;
    }

    void FillTab(TestListData data)
    {
        // everything with work reviews itself whole: the requests and everything to fix (the skill with no arguments)
        var work = view is "all" or "pending" ? data.Items.Where(i => TabReview(SectionOf(i)) != null).ToList() : [];
        if (work.Count > 0 && root != null && ReviewAllRow(work, Palette.AccentColor) is { } all) Rows.Children.Add(all);
        foreach (var (topic, sections) in Tree(data))
        {
            string node = Node(topic);
            // a search opens every topic, so what it found is in sight
            bool shut = search.Length == 0 && folded.Contains(node);
            var items = sections.SelectMany(x => x.Groups).SelectMany(g => g.Items).ToList();
            Rows.Children.Add(TopicHead(topic, items, shut));
            if (shut) continue;
            var inner = new StackPanel { Spacing = 4, Margin = new(14, 0, 0, 6) };
            // no section or group heads (asked by the user, #197: too much clutter): each line says its state in its chip
            foreach (var i in Ranked(items.Where(i => SectionOf(i) != DoneTabIndex))) inner.Children.Add(Line(i));
            var done = items.Where(i => SectionOf(i) == DoneTabIndex).ToList();
            // Terminado alone shows them without the fold: they are all there is
            if (view == "done") foreach (var i in Ordered(done)) inner.Children.Add(Line(i));
            else if (done.Count > 0)
            {
                bool closed = search.Length == 0 && SectionShut(node, DoneTabIndex);
                inner.Children.Add(DoneHead(node, done.Count, closed));
                if (!closed) foreach (var i in Ordered(done)) inner.Children.Add(Line(i));
            }
            Rows.Children.Add(inner);
        }
        if (Rows.Children.Count == 0) Rows.Children.Add(Empty());
    }

    /// <summary>
    /// The open tickets of a topic in one list (#197): what an agent just said first, then what needs work (No anda,
    /// Anda mal, A mejorar, the requests), then what waits to be tried, the ones back from a fix before the new ones.
    /// </summary>
    static IEnumerable<TestItem> Ranked(IEnumerable<TestItem> items) =>
        items.OrderByDescending(i => i.Unseen).ThenBy(Rank).ThenBy(i => i.Id);

    static int Rank(TestItem i) => i.State switch
    {
        TestItem.Fails => 0,
        TestItem.WorksBadly => 1,
        TestItem.Improve => 2,
        TestItem.Request => 3,
        _ when TestList.Back(i) => 4,
        TestItem.Pending => 5,
        _ => 6,
    };

    /// <summary>The tickets as the list shows them, top to bottom (Enter in the search opens the first).</summary>
    IEnumerable<TestItem> Shown(TestListData data) => Tree(data).SelectMany(t =>
    {
        var items = t.Sections.SelectMany(x => x.Groups).SelectMany(g => g.Items).ToList();
        return Ranked(items.Where(i => SectionOf(i) != DoneTabIndex)).Concat(Ordered(items.Where(i => SectionOf(i) == DoneTabIndex)));
    });

    /// <summary>What a ticket's chip says and its color: its verdict, "Para probar", or a request's kind.</summary>
    static (string Text, Color Color) StateChip(TestItem i) => i.State switch
    {
        TestItem.Request => i.Kind is { } k ? (Loc.T("test.list.kind." + k), KindColor(k)) : (Loc.T("test.list.request.chip"), VioletKind),
        TestItem.Pending => (Loc.T("test.list.totest"), Palette.AccentColor),
        _ => (StateName(i.State), StateColor(i.State) ?? Palette.AccentColor),
    };

    /// <summary>The fold of a topic's resolved tickets, under the rest: a green check, "Resuelto" and how many. Starts folded.</summary>
    Control DoneHead(string node, int count, bool shut)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock
        {
            Classes = { "icon" }, Text = shut ? FoldedGlyph : UnfoldedGlyph, FontSize = 9, Width = 12,
            Foreground = Palette.Alpha(Palette.DimColor, 1), VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock { Classes = { "icon" }, Text = CheckGlyph, FontSize = 11, Foreground = Palette.Alpha(GreenState, 1), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Classes = { "dim" }, Text = Loc.T("test.list.done"), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Classes = { "dim", "mono" }, FontSize = 11, Text = count.ToString(CultureInfo.CurrentCulture), VerticalAlignment = VerticalAlignment.Center });
        var b = new Button
        {
            Classes = { "rowmain" }, Content = row, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(2, 2), Background = Brushes.Transparent,
        };
        AutomationProperties.SetName(b, Loc.T("test.list.done"));
        b.Click += (_, _) => ToggleFold(SectionNode(node, DoneTabIndex));
        return b;
    }

    /// <summary>Whether a ticket is in one of the <see cref="Views"/>. What an agent is on is only "En curso", whatever its state.</summary>
    bool InView(TestItem i, string v) => v switch
    {
        "busy" => Busy(i),
        "pending" => !Busy(i) && SectionOf(i) is FixTabIndex or RequestTabIndex,
        "totest" => !Busy(i) && i.State == TestItem.Pending,
        "done" => i.State == TestItem.Works,
        _ => true,
    };

    /// <summary>
    /// The view picker over the list (#249): one segment per view with how many tickets it has (counting the search),
    /// only on the list, not in a ticket or the history.
    /// </summary>
    void FillViews(TestListData data)
    {
        ViewBar.IsVisible = open == 0 && HistoryButton.IsChecked != true;
        ViewRow.Children.Clear();
        if (!ViewBar.IsVisible) return;
        var found = data.Items.Where(Found).ToList();
        foreach (var v in Views)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center };
            row.Children.Add(new TextBlock { Text = Loc.T("test.list.view." + v), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            if (v != "all")
                row.Children.Add(new TextBlock
                {
                    Classes = { "mono" }, FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center,
                    Text = found.Count(i => InView(i, v)).ToString(CultureInfo.CurrentCulture),
                });
            var b = new ToggleButton { Classes = { "seg" }, Content = row, Padding = new(4, 0), IsChecked = v == view, Focusable = false };
            ToolTip.SetTip(b, Loc.T("test.list.view." + v + ".tip"));
            AutomationProperties.SetName(b, Loc.T("test.list.view." + v));
            b.Click += (_, _) =>
            {
                view = v;
                try { AppConfig.Set(ViewKey, v); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                Refill();
            };
            ViewRow.Children.Add(b);
        }
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
                    var items = Ordered(data.Items.Where(match).Where(i => i.Topic == topic).Where(Found).Where(i => InView(i, view))).ToList();
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
    List<string> ShownTopics(TestListData data) => [.. Tree(data).Select(t => Node(t.Topic))];

    /// <summary>
    /// A topic's fold key in the current view: each view keeps its own folds (asked by the user, #258), "Todo" with the
    /// bare topic as before ("pending/mixer", "mixer").
    /// </summary>
    string Node(string? topic) => view == "all" ? topic ?? NoTopic : $"{view}/{topic ?? NoTopic}";

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
    /// A topic's node in the list: its arrow and name, then how its tickets stand, a colored dot and a count per state
    /// (asked by the user, #197: the state at a glance, folded or not), "Nuevo", how many are in progress, and on the
    /// right Revisar for its requests and fixes. A click folds it.
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
        var counts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, Margin = new(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var (state, c, key) in (ReadOnlySpan<(string, Color, string)>)[
                     (TestItem.Fails, Palette.RecColor, "test.list.fail"), (TestItem.WorksBadly, YellowState, "test.list.bad"),
                     (TestItem.Improve, BlueState, "test.list.improve"), (TestItem.Request, VioletKind, "test.list.requests"),
                     (TestItem.Pending, Palette.AccentColor, "test.list.totest"), (TestItem.Works, GreenState, "test.list.done")])
        {
            int n = items.Count(i => i.State == state);
            if (n == 0) continue;
            var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            cell.Children.Add(Dot(c));
            cell.Children.Add(new TextBlock { Classes = { "mono" }, FontSize = 11, Text = n.ToString(CultureInfo.CurrentCulture), Foreground = Palette.Alpha(c, 1), VerticalAlignment = VerticalAlignment.Center });
            ToolTip.SetTip(cell, Loc.T(key));
            counts.Children.Add(cell);
        }
        row.Children.Add(counts);
        if (items.Any(i => i.Unseen)) row.Children.Add(Badge(Loc.T("test.list.new"), false, Palette.AccentColor, strong: true));
        // how many an agent is working on, with the same spinner as each ticket (asked by the user, #123)
        int busy = items.Count(i => TestList.Held(i, sessions));
        if (busy > 0)
        {
            var worst = LockLight.Free;
            foreach (var i in items)
                if (TestList.Held(i, sessions) && LockLight.For(lights, i.TakenBy) is { } l && l.State > worst.State) worst = l;
            row.Children.Add(TakenBadge(worst, busy));
        }
        // an agent starting on some of them, as each ticket says it (#172)
        else if (items.Any(Starting)) row.Children.Add(StartingBadge(items.Where(Starting).All(Queued)));
        var b = new Button
        {
            Classes = { "rowmain" }, Content = row, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(2, 4), Background = Brushes.Transparent,
        };
        if (topic != null) ToolTip.SetTip(b, Loc.T("test.list.topic." + topic + ".tip"));
        AutomationProperties.SetName(b, TopicName(topic));
        string node = Node(topic);
        b.Click += (_, _) => ToggleFold(node);
        var work = items.Where(i => TabReview(SectionOf(i)) != null).ToList();
        if (work.Count == 0 || root == null) return b;
        var grid = new Grid { ColumnDefinitions = new("*,Auto") };
        grid.Children.Add(b);
        var review = ReviewLink(work, Palette.AccentColor);
        Grid.SetColumn(review, 1);
        grid.Children.Add(review);
        return grid;
    }

    /// <summary>
    /// A ticket in the list, in two lines (asked by the user, #204): on top its number, its kind and its title, which get
    /// the line to themselves; under them a chip with its state in its color (the verdict, "Para probar" or "Pedido"),
    /// "Nuevo" or "Tomado", and on the right its attempt or when, beside Probar or the console. Only a "Nuevo" one adds a
    /// third line with what was just said; the rest is in the tooltip. The whole card opens it.
    /// </summary>
    Control Line(TestItem item)
    {
        var top = new Grid { ColumnDefinitions = new("Auto,Auto,*") };
        top.Children.Add(new TextBlock { Classes = { "dim", "mono" }, FontSize = 11, MinWidth = LineIdWidth, Text = $"#{item.Id}", VerticalAlignment = VerticalAlignment.Center });
        if (item.Kind is { } k)
        {
            var kind = Badge(Loc.T("test.list.kind." + k), false, KindColor(k));
            kind.Margin = new(4, 0, 8, 0);
            Grid.SetColumn(kind, 1);
            top.Children.Add(kind);
        }
        var subject = new TextBlock
        {
            Text = Subject(item), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.SemiBold, Margin = new(item.Kind == null ? 4 : 0, 0, 0, 0),
        };
        Grid.SetColumn(subject, 2);
        top.Children.Add(subject);

        var under = new Grid { ColumnDefinitions = new("Auto,*,Auto"), Margin = new(LineIdWidth + 4, LineGap, 0, 0) };
        var (chip, chipColor) = item.State == TestItem.Request ? (Loc.T("test.list.request.chip"), VioletKind) : StateChip(item);
        var stateChip = Badge(chip, false, chipColor);
        // a little taller than the other badges (asked by the user, #197: too small to read at a glance)
        stateChip.Padding = new(8, 3);
        under.Children.Add(stateChip);
        var marks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        if (item.Unseen) marks.Children.Add(Badge(Loc.T("test.list.new"), false, Palette.AccentColor, strong: true));
        if (Starting(item)) marks.Children.Add(StartingBadge(Queued(item)));
        else if (TestList.Held(item, sessions))
        {
            marks.Children.Add(TakenBadge(LockLight.For(lights, item.TakenBy), tip: TakenTip(LockLight.For(lights, item.TakenBy), item.TakenBy)));
            if (ModelLabel(item.TakenBy) is { } model) marks.Children.Add(model);
        }
        Grid.SetColumn(marks, 1);
        under.Children.Add(marks);
        var when = new TextBlock
        {
            Classes = { "dim", "mono" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new(10, 0, 0, 0),
            Text = item.Tries > 1 ? Loc.T("test.list.try", item.Tries) : When(item.At),
        };
        Grid.SetColumn(when, 2);
        under.Children.Add(when);

        bool open = expanded.Contains(item.Id);
        // what is new is worth reading before opening it; the rest stays in two lines
        TextBlock? said = null;
        if (!open && item.Unseen && LastSaid(item) is { } m)
            said = new TextBlock
            {
                Classes = { "dim" }, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(LineIdWidth + 4, LineGap, 0, 0),
                Text = Said(m),
            };
        // the whole card opens the ticket (asked by the user, #259): the button lies under the lines, over the card's
        // padding too, and the texts let the click through to it; the arrow and Probar / Ver consola sit on top of it
        var b = new Button
        {
            Classes = { "rowmain" }, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new(0), Background = Brushes.Transparent, Margin = new(0, -CardPadY, -CardPadRight, open ? -LineGap : -CardPadY),
        };
        top.IsHitTestVisible = false;
        stateChip.IsHitTestVisible = false;
        when.IsHitTestVisible = false;
        if (said != null) said.IsHitTestVisible = false;
        // "en curso" keeps its tooltip (which file it waits for), and a click on it still opens the ticket
        marks.Tapped += (_, _) => OpenTicket(item.Id);
        AutomationProperties.SetName(b, $"#{item.Id} {Subject(item)} · {chip}");
        // the last thing said, or who opened it (#157), without opening it; unfolded, it is all in sight
        if (!open && LastSaid(item) is { } last) ToolTip.SetTip(b, Said(last));
        else if (!open && Creator(item) is { } c) ToolTip.SetTip(b, Loc.T("test.list.creator", WhoName(c.Who)));
        b.Click += (_, _) => OpenTicket(item.Id);
        // the arrow unfolds the conversation under the line, to read it without opening the ticket (asked by the user, #197)
        var fold = new Button
        {
            Classes = { "ghost" }, Width = 24, Height = 24, Padding = new(0), Margin = new(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock
            {
                Classes = { "icon" }, Text = open ? UnfoldedGlyph : FoldedGlyph, FontSize = 9,
                Foreground = Palette.Alpha(Palette.DimColor, 1), HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
        string foldTip = Loc.T(open ? "test.list.line.collapse" : "test.list.line.expand");
        ToolTip.SetTip(fold, foldTip);
        AutomationProperties.SetName(fold, foldTip);
        fold.Click += (_, _) =>
        {
            if (!expanded.Remove(item.Id)) expanded.Add(item.Id);
            Refill();
        };
        // the title runs the whole width, over the column of Probar / Ver consola (asked by the user, #259)
        var row = new Grid { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("Auto,Auto,Auto") };
        Grid.SetColumnSpan(b, 3);
        Grid.SetRowSpan(b, 3);
        row.Children.Add(b);
        Grid.SetColumn(top, 1);
        Grid.SetColumnSpan(top, 2);
        row.Children.Add(top);
        Grid.SetColumn(under, 1);
        Grid.SetRow(under, 1);
        row.Children.Add(under);
        if (said != null)
        {
            Grid.SetColumn(said, 1);
            Grid.SetColumnSpan(said, 2);
            Grid.SetRow(said, 2);
            row.Children.Add(said);
        }
        Grid.SetRowSpan(fold, 3);
        row.Children.Add(fold);
        // Probar, or Ver consola while an agent is on it (#172), at the end of the second line, with the state and time
        // (#204); a button of its own over the card's
        if ((SessionButton(item) ?? TryButton(item)) is { } side)
        {
            side.Margin = new(8, LineGap, 0, 0);
            side.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(side, 2);
            Grid.SetRow(side, 1);
            row.Children.Add(side);
        }
        Control body = row;
        if (open)
        {
            var whole = new StackPanel { Spacing = 8 };
            whole.Children.Add(row);
            whole.Children.Add(Unfolded(item));
            body = whole;
        }
        return Card(body, StateChip(item).Color, item.Unseen || Busy(item), compact: true);
    }

    /// <summary>
    /// A ticket summed up under its line (#197: a summary to read in place, not the whole conversation): what was asked,
    /// the latest agent's message and the latest word of the person after it, a couple of lines each. The rest of the
    /// conversation, writing and the verdicts stay in the open ticket.
    /// </summary>
    static Control Unfolded(TestItem item)
    {
        var list = new StackPanel { Spacing = 5, Margin = new(28 + LineIdWidth, 0, 4, 2) };
        void Row(string label, string? text, IBrush tint)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = SummaryLines };
            t.Inlines!.Add(new Avalonia.Controls.Documents.Run(label + "  ") { Foreground = tint, FontSize = 11, FontWeight = FontWeight.SemiBold });
            // one paragraph: the line breaks of a long note would eat the few lines there are
            t.Inlines.Add(new Avalonia.Controls.Documents.Run(string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))));
            list.Children.Add(t);
        }
        var shown = new List<TestMessage>();
        if (Creator(item) is { } open) shown.Add(open);
        int agent = item.Thread.FindLastIndex(m => m.Who != TestList.User && m.Kind != TestMessage.Open);
        if (agent >= 0) shown.Add(item.Thread[agent]);
        int user = item.Thread.FindLastIndex(m => m.Who == TestList.User && m.Kind != TestMessage.Open);
        if (user > agent) shown.Add(item.Thread[user]);
        if (shown.Count == 0) Row(Loc.T("test.list.msg.open"), item.Text, Palette.Dim);
        foreach (var m in shown)
            Row(m.Who == TestList.User ? MessageLabel(m) : $"{RoleName(m.Who)} · {MessageLabel(m)}", m.Text, m.Who == TestList.User ? Palette.Dim : Palette.Accent);
        int rest = item.Thread.Count - shown.Count;
        if (rest > 0)
            list.Children.Add(new TextBlock { Classes = { "dim" }, FontSize = 11, Foreground = Palette.Faint, Text = Loc.T("test.list.line.more", rest) });
        return list;
    }

    /// <summary>Lines each message gets in a ticket's summary: enough to know where it stands, not to read it all.</summary>
    const int SummaryLines = 2;

    /// <summary>Room for the number at the start of a line, so the chips line up.</summary>
    const double LineIdWidth = 34;

    /// <summary>Space between the lines of a ticket in the list.</summary>
    const double LineGap = 5;

    /// <summary>
    /// The compact card's padding above, below and on the right: the line's button reaches over it, so the whole card
    /// opens the ticket (#259).
    /// </summary>
    const double CardPadY = 9, CardPadRight = 10;

    /// <summary>A message in one line: who, what, and its text.</summary>
    static string Said(TestMessage m) => $"{RoleName(m.Who)} · {MessageLabel(m)}{(m.Text is { } t ? ": " + t : "")}";

    /// <summary>What a ticket is about in a line: its title, or the first line of its text while it has none.</summary>
    static string Subject(TestItem i)
    {
        if (!string.IsNullOrWhiteSpace(i.Title)) return i.Title;
        string t = i.Text.Trim();
        int nl = t.IndexOfAny(['\r', '\n']);
        return nl < 0 ? t : t[..nl];
    }

    /// <summary>
    /// The order of a group: what asks for the person's attention first ("Nuevo": an agent answered, asked or sent it
    /// back; asked by the user), then oldest first by number, so a fix keeps its place instead of jumping to the end.
    /// </summary>
    static IEnumerable<TestItem> Ordered(IEnumerable<TestItem> items) => items.OrderByDescending(i => i.Unseen).ThenBy(i => i.Id);

    /// <summary>What was said after the ticket opened, the latest first: what the person wants to know before opening it.</summary>
    static TestMessage? LastSaid(TestItem i) => i.Thread.Count > 1 ? i.Thread[^1] : null;

    /// <summary>
    /// The entry that opened the ticket, which says who created it (asked by the user, #157): the person or an agent's
    /// session. Null on tickets from before the thread kept it.
    /// </summary>
    static TestMessage? Creator(TestItem i) => i.Thread.Count > 0 && i.Thread[0].Kind == TestMessage.Open ? i.Thread[0] : null;

    /// <summary>
    /// The open ticket: its subject and state, the conversation in order, and under it the verdicts and the reply, with
    /// Probar, the agent's console and Revisar beside Comentar (#166). "⋯" (its history, take it off) on top; a request's pencil beside its text (#177).
    /// </summary>
    void FillTicket(TestItem item)
    {
        var body = new StackPanel { Spacing = 10 };

        var bar = new Grid { ColumnDefinitions = new("Auto,*,Auto") };
        // a lit chip, larger than the rest of the bar (asked by the user, #182): the way out of a ticket is easy to hit
        var back = new Button
        {
            Classes = { "chip", "on" }, Padding = new(14, 7, 16, 7), MinHeight = 34,
            VerticalAlignment = VerticalAlignment.Center,
        };
        back.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                new TextBlock { Classes = { "icon" }, Text = BackGlyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock
                {
                    Text = Loc.T(TabList[tab].Key), FontSize = 12.5, FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        ToolTip.SetTip(back, Loc.T("test.list.ticket.back") + " (Esc)");
        back.Click += (_, _) => CloseTicket();
        bar.Children.Add(back);
        var more = MoreButton(item);
        Grid.SetColumn(more, 2);
        bar.Children.Add(more);
        // fixed over the scroll (asked by the user, #175): back to the list without scrolling up a long ticket
        TicketBar.Child = bar;
        TicketBar.IsVisible = true;

        var info = new WrapPanel { ItemSpacing = 8, LineSpacing = 4 };
        info.Children.Add(Badge($"#{item.Id}", true, Palette.AccentColor));
        info.Children.Add(Badge(StateName(item.State), false, StateColor(item.State) ?? TabColor(tab)));
        if (item.Kind is { } k) info.Children.Add(Badge(Loc.T("test.list.kind." + k), false, KindColor(k)));
        if (Starting(item)) info.Children.Add(StartingBadge(Queued(item)));
        else if (TestList.Held(item, sessions))
        {
            info.Children.Add(TakenBadge(LockLight.For(lights, item.TakenBy), tip: TakenTip(LockLight.For(lights, item.TakenBy), item.TakenBy)));
            if (ModelLabel(item.TakenBy) is { } model) info.Children.Add(model);
        }
        info.Children.Add(TopicBadge(item.Topic));
        if (item.Tries > 0) info.Children.Add(new TextBlock { Classes = { "dim", "mono" }, FontSize = 11, Text = Loc.T("test.list.try", item.Tries), VerticalAlignment = VerticalAlignment.Center });
        if (Creator(item) is { } creator)
            info.Children.Add(new TextBlock
            {
                Classes = { "dim" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                Text = $"{Loc.T("test.list.creator", WhoName(creator.Who))} · {When(creator.At)}",
            });
        body.Children.Add(info);
        // a request's text is edited where it is read (#177): the subject when it has no title, else its opening message
        bool editable = item.State == TestItem.Request;
        if (inline != 0 && (inline != item.Id || !editable)) inline = 0;
        var subject = new TextBlock { Text = item.Title ?? item.Text, TextWrapping = TextWrapping.Wrap, LineHeight = 19, FontWeight = FontWeight.SemiBold };
        body.Children.Add(editable && item.Title == null ? EditableText(item, subject) : subject);

        // the timeline; without a title the opening message is the subject, already shown
        bool skipOpen = item.Title == null && item.Thread.Count > 0 && item.Thread[0].Kind == TestMessage.Open;
        var said = item.Thread.Skip(skipOpen ? 1 : 0).ToList();
        var thread = new StackPanel();
        for (int n = 0; n < said.Count; n++)
            thread.Children.Add(Message(said[n], n == said.Count - 1, editable && !skipOpen && n == 0 && said[n].Kind == TestMessage.Open ? item : null));
        if (thread.Children.Count > 0) body.Children.Add(thread);

        if (confirmRemove) body.Children.Add(RemoveConfirm(item));
        if (auditOpen) body.Children.Add(Audit(item.Id));

        if (NotHere(item))
        {
            string key = item.Topic == TestItem.TopicBench ? "test.list.missing.bench" : "test.list.missing";
            body.Children.Add(new TextBlock { Text = Loc.T(key), Foreground = Palette.Mute, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            if (AgentButtons(item) is { } agent) body.Children.Add(agent);
        }
        else body.Children.Add(Answer(item));

        Rows.Children.Add(Card(body, StateColor(item.Draft ?? item.State), item.Unseen));
    }

    /// <summary>
    /// One entry of the ticket's timeline (asked by the user, 2026-11-04): a dot on a line that joins the entries, then
    /// who said it (Usuario or Agente, the session in the tooltip), what it is (a fix, a verdict in its color, a comment),
    /// when, and its text. The person's dot in the text color, an agent's in the accent, so the conversation reads at a glance.
    /// </summary>
    Control Message(TestMessage m, bool last, TestItem? editable = null)
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
        if (m.Text is { } t)
        {
            var text = new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
            box.Children.Add(editable != null ? EditableText(editable, text) : text);
        }
        if (m.Images is { Count: > 0 } images) box.Children.Add(MessageImages(images));
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        return grid;
    }

    /// <summary>
    /// A request's text with its pencil beside it, or, while it is edited, the box in its place with Cancelar and
    /// Guardar (asked by the user, #177: in the ticket itself, not in the composer). Ctrl+Enter saves, Esc cancels.
    /// </summary>
    Control EditableText(TestItem item, TextBlock shown)
    {
        if (inline != item.Id)
        {
            var row = new Grid { ColumnDefinitions = new("*,Auto") };
            row.Children.Add(shown);
            var pencil = new Button
            {
                Classes = { "act" }, VerticalAlignment = VerticalAlignment.Top, Margin = new(6, -4, 0, 0),
                Content = new TextBlock { Classes = { "icon" }, Text = EditGlyph, FontSize = 12 },
            };
            ToolTip.SetTip(pencil, Loc.T("test.list.request.edit"));
            AutomationProperties.SetName(pencil, Loc.T("test.list.request.edit"));
            pencil.Click += (_, _) => BeginInline(item);
            Grid.SetColumn(pencil, 1);
            row.Children.Add(pencil);
            return row;
        }

        var box = new TextBox
        {
            Classes = { "field", "note" }, Text = inlineText, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            MinHeight = ReplyMinHeight, Watermark = Loc.T("test.list.request.hint"),
        };
        box.TextChanged += (_, _) => inlineText = box.Text ?? "";
        box.LostFocus += (_, _) =>
        {
            if (waiting is { } d && !pressing) Dispatcher.UIThread.Post(() => Fill(d));
        };
        box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                SaveInline();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                EndInline();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        inlineBox = box;

        var cancel = new Button { Classes = { "btn", "sm" }, Content = Loc.T("common.cancel") };
        cancel.Click += (_, _) => EndInline();
        var save = new Button { Classes = { "btn", "sm", "primary" }, Content = Loc.T("test.list.save") };
        ToolTip.SetTip(save, "Ctrl+Enter");
        save.Click += (_, _) => SaveInline();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } };
        return new StackPanel { Spacing = 8, Children = { box, buttons } };
    }

    void BeginInline(TestItem item)
    {
        inline = item.Id;
        inlineRev = item.Rev;
        inlineText = item.Text;
        focusInline = true;
        sent = true;
        Refill();
    }

    void EndInline()
    {
        inline = 0;
        sent = true;
        Refill();
    }

    /// <summary>Saves the text written in place; the ticket shows it as text again at once, and the list catches up.</summary>
    void SaveInline()
    {
        string text = inlineText.Trim();
        int id = inline, rev = inlineRev;
        if (id == 0) return;
        if (text != "" && current?.Items.Find(i => i.Id == id) is { } item && text != item.Text)
        {
            item.Text = text;
            if (item.Thread.Count > 0 && item.Thread[0].Kind == TestMessage.Open) item.Thread[0].Text = text;
            Save(() => TestList.EditRequest(path, id, text, null, rev, null));
        }
        EndInline();
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
            // a click took the focus: the waiting list is drawn once it is released (FillWaiting)
            else if (waiting is { } d && !pressing) Dispatcher.UIThread.Post(() => Fill(d));
        };
        reply.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                Send(item, reply, agent: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
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
        box.Children.Add(ReplyStrip(item));

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
        ToolTip.SetTip(send, root == null ? "Ctrl+Enter" : $"Ctrl+Enter\n{Loc.T("test.list.reply.agent")} (Ctrl+Shift+Enter)");
        send.Click += (_, _) => Send(item, reply);
        // Probar, the agent's console and Revisar beside Comentar (asked by the user, #166), not above the ticket
        var foot = new Grid { ColumnDefinitions = new("*,Auto") };
        if (AgentButtons(item) is { } agent) foot.Children.Add(agent);
        Grid.SetColumn(send, 1);
        send.VerticalAlignment = VerticalAlignment.Top;
        foot.Children.Add(send);
        box.Children.Add(foot);
        return new Border { BorderBrush = Palette.Line, BorderThickness = new(0, 1, 0, 0), Padding = new(0, 10, 0, 0), Child = box };
    }

    /// <summary>A ticket's Probar, its agent's console and Revisar, in a row that wraps; null when it has none.</summary>
    Control? AgentButtons(TestItem item)
    {
        var row = new WrapPanel { ItemSpacing = 4, LineSpacing = 4, Margin = new(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        if (TryButton(item) is { } tryIt) row.Children.Add(tryIt);
        // with Ver consola there, "En curso" would say it twice (#172)
        if (SessionButton(item) is { } session) row.Children.Add(session);
        else if (ReviewButton(item) is { } review) row.Children.Add(review);
        return row.Children.Count > 0 ? row : null;
    }

    /// <summary>
    /// Guardar: the verdict picked with the reply as its note (and back to the list), or the reply as a comment. With
    /// <paramref name="agent"/> (Ctrl+Shift+Enter, asked by the user, #178) an agent starts on the ticket in a console once
    /// it is written, the same as its Consola; not after Anda, which leaves nothing to do.
    /// </summary>
    void Send(TestItem item, TextBox reply, bool agent = false)
    {
        string? text = reply.Text;
        int id = item.Id, rev = item.Rev;
        agent &= root != null;
        if (item.Draft is { } state)
        {
            var marked = TakeReplyImages(id);
            (Owner as Window)?.Activate();
            open = 0;
            sent = true;
            Save(() => TestList.Mark(path, id, state, text, rev, marked),
                after: agent && state != TestItem.Works ? () => RunIn(TicketPrompt(item), desktop: false, [item]) : null);
            return;
        }
        bool empty = string.IsNullOrWhiteSpace(text) && (replyImagesFor != id || replyImages.Count == 0);
        if (empty)
        {
            // nothing to say: Ctrl+Shift+Enter still starts the agent on the ticket as it is
            if (agent) ReviewAndClose(TicketPrompt(item), desktop: false, item);
            return;
        }
        var images = TakeReplyImages(id);
        item.Reply = null;
        reply.Text = "";
        // the ticket is drawn again with the comment in it, and the keyboard stays in the (empty) box
        sent = true;
        focusReply = true;
        Save(() => TestList.Comment(path, id, text, rev, images),
            after: agent ? () => ReviewAndClose(TicketPrompt(item), desktop: false, item) : null);
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
    /// Tells of the "Nuevo" tickets not told yet, in toasts (#260): on the first list, what came while the bench was
    /// closed; later, what an agent just added, sent back or said. One toast per ticket, and more than
    /// <see cref="MaxNewsToasts"/> at once in one. Outside the window's focus the taskbar button flashes too.
    /// </summary>
    void Announce(TestListData data)
    {
        var fresh = data.Items.Where(i => i.Unseen).OrderBy(i => i.Id).ToList();
        bool first = announced == null;
        var arrived = first ? fresh : fresh.Where(i => !announced!.Contains(i.Id)).ToList();
        announced = [.. fresh.Select(i => i.Id)];
        UpdateBell(fresh);
        // what was seen meanwhile is no news any more
        foreach (int id in newsToasts.Keys.Where(id => !announced.Contains(id)).ToList())
        {
            newsToasts[id]();
            newsToasts.Remove(id);
        }
        // the ticket on the screen is no news (asked by the user, #143): it shows there, focused or not
        arrived.RemoveAll(i => i.Id == open && HistoryButton.IsChecked != true && IsVisible && WindowState != WindowState.Minimized);
        if (arrived.Count == 0) return;
        string show = Loc.T("test.list.news.show");
        if (arrived.Count > MaxNewsToasts)
        {
            int top = arrived[0].Id;
            var lines = arrived.Take(MaxNewsToasts).Select(i => $"#{i.Id} · {Subject(i)}").ToList();
            lines.Add(Loc.T("test.list.news.more", arrived.Count - MaxNewsToasts));
            toasts.Show(string.Join('\n', lines), Loc.T("test.list.news.many", arrived.Count), action: (show, () => OpenTicket(top)));
        }
        else
            // the ticket's title, not what was said (asked by the user, #197: short; the ticket has the rest)
            foreach (var i in arrived)
            {
                int id = i.Id;
                if (newsToasts.Remove(id, out var old)) old();
                newsToasts[id] = toasts.Show(Subject(i), Loc.T("test.list.news.one", id), action: (show, () => OpenTicket(id)));
            }
        // only what arrives while open: a bench that moves to a newer copy starts over and would blink at every build
        if (!IsActive && !first) Flash();
    }

    /// <summary>
    /// Tells in a toast of each alert between agents that just arrived (#263): to whom and from whom, by their tickets,
    /// and what it says; "Ver" opens the recipient's ticket. An ATENCIÓN stays until it is closed.
    /// </summary>
    void AnnounceAlerts(TestListData data)
    {
        alerts = AgentAlert.Load(root);
        bool first = alertsTold == null;
        List<AgentAlert> arrived = first ? [] : alerts.Where(a => !alertsTold!.Contains(a.Id)).ToList();
        alertsTold = [.. alerts.Select(a => a.Id)];
        foreach (var a in arrived)
        {
            int to = LockLight.TicketOf(data.Items, a.ToSession);
            string title = to > 0 ? Loc.T("test.list.alert.to", to) : Loc.T("test.list.alert.title");
            if (a.Urgent) title += " · " + Loc.T("test.list.lock.clash");
            toasts.Show(AlertLine(data.Items, a), title, sticky: a.Urgent,
                action: to > 0 ? (Loc.T("test.list.news.show"), () => OpenTicket(to)) : null);
        }
        if (arrived.Count > 0 && !IsActive) Flash();
    }

    /// <summary>An alert in one line: who sent it ("#60", "otra sesión", "vos") and what it says.</summary>
    static string AlertLine(IReadOnlyList<TestItem> items, AgentAlert a)
    {
        int from = a.From == "" ? 0 : LockLight.TicketOf(items, a.From);
        string who = a.From == "" ? Loc.T("test.list.alert.user") : from > 0 ? "#" + from : Loc.T("test.list.lock.other");
        return Loc.T("test.list.alert.from", who, a.Text);
    }

    /// <summary>The badge's tooltip: the light, and below the newest alerts for that ticket's session (#263).</summary>
    string TakenTip(LockLight light, string? takenBy)
    {
        var mine = AgentAlert.For(alerts, takenBy).Take(AlertTipMax).ToList();
        if (mine.Count == 0) return LightTip(light);
        var items = current?.Items ?? [];
        return LightTip(light) + "\n\n" + Loc.T("test.list.alert.recent") + "\n" + string.Join('\n', mine.Select(a => "· " + AlertLine(items, a)));
    }

    /// <summary>The bell's count: every ticket still "Nuevo", told or not.</summary>
    void UpdateBell(List<TestItem> fresh)
    {
        int n = fresh.Count;
        BellBadge.IsVisible = n > 0;
        BellCount.Text = n > 99 ? "99+" : n.ToString(CultureInfo.CurrentCulture);
        ToolTip.SetTip(BellButton, n == 0 ? Loc.T("test.list.bell") : $"{Loc.T("test.list.bell")} · {Loc.T("test.list.news.many", n)}");
    }

    /// <summary>The bell's menu: the "Nuevo" tickets, newest first, each opens its ticket; and one click to see them all.</summary>
    void ShowBellMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var fresh = current?.Items.Where(i => i.Unseen).OrderByDescending(i => i.Id).ToList() ?? [];
        if (fresh.Count == 0)
            menu.Items.Add(new MenuItem { Header = Loc.T("test.list.bell.none"), IsEnabled = false });
        foreach (var i in fresh)
        {
            int id = i.Id;
            menu.Items.Add(Item($"#{id} · {Subject(i)}", "", () => OpenTicket(id)));
        }
        if (fresh.Count > 1)
        {
            int[] ids = [.. fresh.Select(i => i.Id)];
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(Loc.T("test.list.bell.seen"), CheckGlyph, () => Save(() => TestList.See(path, ids))));
        }
        menu.ShowAt(BellButton);
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
        box.Children.Add(new TextBlock { Classes = { "dim" }, Text = Loc.T(HistoryButton.IsChecked == true ? "test.list.history.empty" : search.Length > 0 ? "test.list.search.none" : view != "all" ? "test.list.view.empty" : "test.list.empty"), HorizontalAlignment = HorizontalAlignment.Center });
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
    /// Ctrl+Shift+N opens it for a request that an agent starts in a console as soon as it is added (#178).
    /// </summary>
    void OpenComposer(bool skill, bool agent = false)
    {
        skillMode = skill;
        agentMode = agent && !skill;
        composerAsked = true;
        RequestBox.Text = "";
        UpdateComposer();
        Activate();
        RequestBox.Focus();
    }

    void CloseComposer()
    {
        DropRequestImages();
        RequestBox.Text = "";
        skillMode = false;
        agentMode = false;
        composerAsked = false;
        UpdateComposer();
    }

    void UpdateComposer()
    {
        Composer.IsVisible = composerAsked;
        CancelEditButton.IsVisible = composerAsked;
        ComposerTitle.Text = Loc.T(skillMode ? "test.list.claude.newskill.title" : agentMode ? "test.list.request.agent" : "test.list.request.new");
        ComposerIcon.Text = skillMode ? ReviewGlyph : agentMode ? ConsoleGlyph : AddGlyph;
        RequestBox.Watermark = Loc.T(skillMode ? "test.list.claude.newskill" : agentMode ? "test.list.request.agent.hint" : "test.list.request.hint");
        AddButton.Content = Loc.T(skillMode ? "test.list.claude.newskill.create" : agentMode ? "test.list.request.agent.add" : "test.list.request.add");
    }

    /// <summary>
    /// Adds the request in the box, or asks Claude for the skill. The box empties at once: the list catches up.
    /// <paramref name="agent"/> (Ctrl+Shift+Enter, asked by the user, #178) starts an agent on it, as Ctrl+Shift+N does.
    /// </summary>
    void SubmitComposer(bool agent = false)
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
        var images = TakeRequestImages();
        if ((agentMode || agent) && root != null)
        {
            // the agent gets the new number once it is written (#178): the same command as the ticket's Consola
            int added = 0;
            Save(() => added = TestList.AddRequest(path, text, null, copy, null, images),
                after: () =>
                {
                    if (added <= 0) return;
                    string prompt = ReviewPrompt(added.ToString(CultureInfo.InvariantCulture));
                    if (!RunIn(prompt, desktop: false)) return;
                    // the new ticket shows "Iniciando…" like one launched from its own Consola (#188); the list may not
                    // have read it back yet, so it goes in by number, with no one holding it
                    starting[prompt].Ids[added] = null;
                    Refill();
                });
        }
        else Save(() => TestList.AddRequest(path, text, null, copy, null, images));
        if (composerAsked) CloseComposer();
        else
        {
            RequestBox.Text = "";
            UpdateComposer();
            RequestBox.Focus();
        }
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
            inline = 0;
            Save(() => TestList.Remove(path, id, rev));
        };
        Grid.SetColumn(yes, 2);
        grid.Children.Add(yes);
        return grid;
    }

    /// <summary>"⋯": what is not done every time (the history of changes, take it off the list). A request is edited with its pencil (#177).</summary>
    Button MoreButton(TestItem item)
    {
        var more = new Button { Classes = { "act" }, Content = new TextBlock { Classes = { "icon" }, Text = MoreGlyph, FontSize = 12 } };
        ToolTip.SetTip(more, Loc.T("test.list.more"));
        AutomationProperties.SetName(more, Loc.T("test.list.more"));
        more.Click += (_, _) =>
        {
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
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
    /// loader (<c>Arc.spin</c> in <c>App.axaml</c>), so it turns from the style and costs no rebuild. Beside it, the
    /// session's lock traffic light (asked by the user, #167): green while nothing stops it, amber while it waits in line
    /// for a file, red when it had to stop (a line that loops back, a command that changed another session's files);
    /// the tooltip says which file and who has it.
    /// </summary>
    static Border TakenBadge(LockLight light, int count = 0, string? tip = null)
    {
        var c = Palette.AccentColor;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        row.Children.Add(new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 7, Height = 7, Fill = Palette.Alpha(LightColor(light.State), 1), VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new Avalonia.Controls.Shapes.Arc
        {
            Classes = { "spin" }, Width = 10, Height = 10, StrokeThickness = 1.5, Stroke = Palette.Alpha(c, 1),
            VerticalAlignment = VerticalAlignment.Center,
        });
        string text = count > 0 ? Loc.T("test.list.topic.taken", count) : Loc.T("test.list.request.taken");
        if (light.State != LockLight.Level.Free) text += " · " + LightName(light);
        row.Children.Add(new TextBlock { FontSize = 11, Text = text, Foreground = Palette.Alpha(c, 1), VerticalAlignment = VerticalAlignment.Center });
        var badge = new Border { Background = Palette.Alpha(c, BadgeTint), CornerRadius = new(5), Padding = new(6, 1), VerticalAlignment = VerticalAlignment.Center, Child = row };
        ToolTip.SetTip(badge, tip ?? LightTip(light));
        return badge;
    }

    /// <summary>The model and effort of the session working on a ticket (#315), in dim mono beside its "en curso"; null when unknown.</summary>
    TextBlock? ModelLabel(string? takenBy)
    {
        string label = AgentLauncher.ModelOf(models, takenBy);
        if (label == "") return null;
        var t = new TextBlock { Classes = { "dim", "mono" }, FontSize = 11, Text = label, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(t, Loc.T("test.list.model.tip"));
        return t;
    }

    static Color LightColor(LockLight.Level level) => level switch
    {
        LockLight.Level.Free => GreenState,
        LockLight.Level.Waiting => YellowState,
        _ => Palette.RecColor,
    };

    /// <summary>
    /// What the badge says beside "en curso" when the light is not green: "espera al #177", "espera al #177 · 2° de 3",
    /// "bloqueado"… The place only when someone else is in the same line.
    /// </summary>
    static string LightName(LockLight l) => l.State switch
    {
        LockLight.Level.Waiting when l.HolderTicket > 0 && l.Line > 1 => Loc.T("test.list.lock.waiting.for.place", l.HolderTicket, l.Place, l.Line),
        LockLight.Level.Waiting when l.HolderTicket > 0 => Loc.T("test.list.lock.waiting.for", l.HolderTicket),
        LockLight.Level.Waiting when l.Line > 1 => Loc.T("test.list.lock.waiting.place", l.Place, l.Line),
        LockLight.Level.Waiting when l.Holder != "" => Loc.T("test.list.lock.waiting.session", l.Holder),
        LockLight.Level.Waiting => Loc.T("test.list.lock.waiting"),
        // who it waits for in the loop (#183): the holder's ticket, or its session's title when it has none; never an id
        LockLight.Level.Blocked when l.HolderTicket > 0 => Loc.T("test.list.lock.blocked.by", l.HolderTicket),
        LockLight.Level.Blocked when l.Holder != "" => Loc.T("test.list.lock.blocked.by.session", l.Holder),
        LockLight.Level.Blocked => Loc.T("test.list.lock.blocked"),
        _ => Loc.T("test.list.lock.clash"),
    };

    /// <summary>Whom a wait is for in a tooltip: its ticket, the session's title, or "otra sesión".</summary>
    static string HolderName(LockLight l) =>
        l.HolderTicket > 0 ? "#" + l.HolderTicket : l.Holder != "" ? Loc.T("test.list.lock.session", l.Holder) : Loc.T("test.list.lock.other");

    static string LightTip(LockLight l) => l.State switch
    {
        LockLight.Level.Free => Loc.T("test.list.lock.free.tip"),
        LockLight.Level.Waiting when l.HolderTicket > 0 => Loc.T("test.list.lock.waiting.for.tip", l.Path, l.HolderTicket),
        LockLight.Level.Waiting => Loc.T("test.list.lock.waiting.tip", l.Path, HolderName(l)),
        LockLight.Level.Blocked when l.HolderTicket > 0 || l.Holder != "" => Loc.T("test.list.lock.blocked.by.tip", l.Path, HolderName(l)),
        LockLight.Level.Blocked => Loc.T("test.list.lock.blocked.tip", l.Path),
        _ => Loc.T("test.list.lock.clash.tip", l.Path),
    };

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
        string prompt = TicketPrompt(item);
        if (Starting(item)) return StartingChip(Queued(item));
        // an agent already on it: no second one (asked by the user, #172); Ver consola brings that one forward
        if (TestList.Held(item, sessions)) return BusyChip();
        var pair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        pair.Children.Add(ReviewChip(ReviewGlyph, Loc.T("test.list.review.desktop"), Loc.T("test.list.review.desktop.tip", item.Id), () => ReviewAndClose(prompt, desktop: true, item)));
        pair.Children.Add(ReviewChip(ConsoleGlyph, Loc.T("test.list.review.console"), Loc.T("test.list.review.console.tip", item.Id), () => ReviewAndClose(prompt, desktop: false, item)));
        return pair;
    }

    static string TicketPrompt(TestItem item) => ReviewPrompt(item.Id.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// How long a launched agent counts as starting (asked by the user, #172): the console's Claude takes its ticket in
    /// under a minute, and in the desktop app the person still has to send the message.
    /// </summary>
    static readonly TimeSpan StartingFor = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The agents launched from here that have not started yet (#172), by prompt: when, and each ticket it covers with
    /// who had it then (a ticket's own Revisar, a section's, or Revisar todo: every ticket it covers shows "Iniciando…").
    /// Shared by every list window of the process, so reopening the window does not let a second one through.
    /// </summary>
    static readonly Dictionary<string, (DateTime At, Dictionary<int, string?> Ids)> starting = [];

    /// <summary>Drops the launches whose wait is over.</summary>
    static void ExpireStarting()
    {
        foreach (var key in starting.Where(s => DateTime.UtcNow - s.Value.At >= StartingFor).Select(s => s.Key).ToList())
            starting.Remove(key);
    }

    /// <summary>
    /// A ticket's agent is starting: launched a moment ago, and no session took the ticket since (that ends it: the
    /// hook takes the numbers named as the message is sent, so from there it shows "en curso").
    /// </summary>
    bool Starting(TestItem item)
    {
        if (Queued(item)) return true;
        ExpireStarting();
        foreach (var (_, s) in starting)
            if (s.Ids.TryGetValue(item.Id, out var by) && by == item.TakenBy && TestList.Takeable(item)) return true;
        return false;
    }

    /// <summary>Whether <paramref name="prompt"/> was launched a moment ago and some ticket it covers is still waiting for it.</summary>
    bool Starting(string prompt)
    {
        if (Queued(prompt)) return true;
        ExpireStarting();
        if (!starting.TryGetValue(prompt, out var s)) return false;
        if (s.Ids.Count == 0) return true;
        foreach (var (id, by) in s.Ids)
            if (current?.Items.Find(i => i.Id == id) is { } i && i.TakenBy == by && TestList.Takeable(i)) return true;
        return false;
    }

    /// <summary>An agent is starting on the ticket or working on it (#172): no Probar, no second Revisar.</summary>
    bool Busy(TestItem item) => Starting(item) || TestList.Held(item, sessions);

    /// <summary>"Iniciando…" in place of Revisar while its agent starts (#172): disabled, so a second one can't be opened.</summary>
    static Control StartingChip(bool queued = false) => Inert(StartingRow(queued), Loc.T(queued ? "test.list.queued.tip" : "test.list.starting.tip"));

    /// <summary>"En curso" in place of Revisar while an agent works on the ticket (asked by the user, #172).</summary>
    static Control BusyChip()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        row.Children.Add(new TextBlock { Classes = { "icon" }, Text = ReviewGlyph, FontSize = 11, Foreground = Palette.Accent, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = Loc.T("test.list.review.busy"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        return Inert(row, Loc.T("test.list.review.busy.tip"));
    }

    /// <summary>A chip that only says something: it takes no clicks.</summary>
    static Button Inert(Control content, string tip)
    {
        var b = ReviewChip(ReviewGlyph, "", tip, () => { });
        b.Content = content;
        // not IsEnabled = false: at the disabled opacity it could hardly be read; it just takes no clicks
        b.IsHitTestVisible = false;
        b.Focusable = false;
        return b;
    }

    /// <summary>The loader ring and "Iniciando…": the same ring as "Tomado", in the faint text.</summary>
    static StackPanel StartingRow(bool queued = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        // #318: one waiting for a place says "En cola", still, with a clock in place of the ring
        if (queued)
        {
            row.Children.Add(new TextBlock { Classes = { "icon" }, Text = "", FontSize = 10, Foreground = Palette.Accent, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = Loc.T("test.list.queued"), FontSize = 11, Foreground = Palette.Accent, VerticalAlignment = VerticalAlignment.Center });
            return row;
        }
        row.Children.Add(new Avalonia.Controls.Shapes.Arc
        {
            Classes = { "spin" }, Width = 10, Height = 10, StrokeThickness = 1.5, Stroke = Palette.Accent,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock { Text = Loc.T("test.list.starting"), FontSize = 11, Foreground = Palette.Accent, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    /// <summary>The "Iniciando…" badge of a ticket whose agent is starting (#172), where "Tomado" goes once it takes it.</summary>
    static Border StartingBadge(bool queued = false)
    {
        var c = Palette.AccentColor;
        var b = new Border { Background = Palette.Alpha(c, BadgeTint), CornerRadius = new(5), Padding = new(6, 1), VerticalAlignment = VerticalAlignment.Center, Child = StartingRow(queued) };
        ToolTip.SetTip(b, Loc.T(queued ? "test.list.queued.tip" : "test.list.starting.tip"));
        return b;
    }

    /// <summary>
    /// Whether what is open here can't have the ticket's change yet. A Banco ticket is built into the Banco, not into an
    /// XTreme copy (asked by the user, 2026-11-05): it's measured against this bench's build, and inside XTreme it's
    /// never missing (XTreme doesn't carry it).
    /// </summary>
    bool NotHere(TestItem item) => item.Topic == TestItem.TopicBench
        ? Bench.RunningBuild is { } own && string.CompareOrdinal(item.Build, own) > 0
        : TestList.Missing(item, build);

    /// <summary>Segoe Fluent Icons "Play": Probar.</summary>
    const string TryGlyph = "";

    /// <summary>
    /// Probar (asked by the user, #143): on a ticket to try that the newest copy has, opens that copy with the ticket's
    /// toast (what to try) through <c>tools\probar.ps1 -Abrir -Probar</c>, as the header's button does with none.
    /// </summary>
    Control? TryButton(TestItem item)
    {
        if (root == null || item.State != TestItem.Pending) return null;
        // an agent starting or working on it is changing what there is to try (asked by the user, #172): its row shows
        // Ver consola instead, and the copy that has the change comes with the agent's fix
        if (Busy(item)) return null;
        // a change to the Banco is tried in the Banco (asked by the user, 2026-11-05): its newest build, unless this
        // bench already is one that has it
        if (item.Topic == TestItem.TopicBench)
        {
            if (Bench.NewestName(path) is not { } newest || string.CompareOrdinal(item.Build, newest) > 0) return null;
            if (Bench.RunningBuild is { } own && string.CompareOrdinal(own, item.Build) >= 0) return null;
            return ReviewChip(TryGlyph, Loc.T("test.list.try.button"), Loc.T("test.list.try.bench.tip", item.Id), () =>
                ErrorText.Text = AgentLauncher.OpenBench(root) is { } why ? Loc.T("test.list.openapp.error", why) : null);
        }
        if (TestList.Missing(item, build)) return null;
        return ReviewChip(TryGlyph, Loc.T("test.list.try.button"), Loc.T("test.list.try.button.tip", item.Id), () =>
            ErrorText.Text = AgentLauncher.OpenApp(root, item.Id) is { } why ? Loc.T("test.list.openapp.error", why) : null);
    }

    /// <summary>
    /// Ver consola (asked by the user, #164): on a ticket an agent is working on, brings that session's window forward
    /// (its console, or the app it runs in). Only while its Claude is running.
    /// </summary>
    Control? SessionButton(TestItem item)
    {
        if (root == null || !TestList.Held(item, sessions)) return null;
        if (AgentLauncher.SessionProcess(root, item.TakenBy) is not { } pid) return null;
        return ReviewChip(ConsoleGlyph, Loc.T("test.list.session.button"), Loc.T("test.list.session.button.tip", item.Id), () =>
            ErrorText.Text = ShowInBench(pid) || AgentLauncher.ShowSession(pid) ? null : Loc.T("test.list.session.error"));
    }

    /// <summary>Sends a ticket to review and goes back to the list (pedido del usuario, #119); on a failure it stays, with the error.</summary>
    void ReviewAndClose(string prompt, bool desktop, TestItem item)
    {
        if (RunIn(prompt, desktop, [item])) CloseTicket();
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

    /// <summary>
    /// Starts the agent; false (and the reason shown) if it could not. Not on a ticket an agent is starting on or
    /// working on (asked by the user, #172): every ticket in <paramref name="items"/> shows "Iniciando…" until a
    /// session takes it, and "en curso" from there.
    /// </summary>
    bool RunIn(string prompt, bool desktop, IReadOnlyList<TestItem>? items = null)
    {
        if (root == null) return false;
        items ??= [];
        if (Starting(prompt) || items.Any(Starting))
        {
            ErrorText.Text = Loc.T("test.list.starting.already");
            return false;
        }
        if (items.Count > 0 && items.All(i => TestList.Held(i, sessions)))
        {
            ErrorText.Text = Loc.T("test.list.review.busy.tip");
            return false;
        }
        // #318: past the most agents at once it waits its turn, and starts when one ends
        if (ActiveAgents() >= MaxAgents)
        {
            Enqueue(prompt, desktop, items);
            return true;
        }
        return Launch(prompt, desktop, items);
    }

    /// <summary>Starts the agent now, with no turn to wait; false (and the reason shown) if it could not.</summary>
    bool Launch(string prompt, bool desktop, IReadOnlyList<TestItem> items)
    {
        if (root == null) return false;
        // #278: the console opens inside the bench; Windows Terminal only when that cannot start
        string? why = desktop && AgentLauncher.DesktopInstalled ? AgentLauncher.Run(root, prompt, true)
            : RunInBench(prompt) == null ? null : AgentLauncher.Run(root, prompt, false);
        ErrorText.Text = why != null ? Loc.T("test.list.review.error", why) : null;
        if (why != null) return false;
        starting[prompt] = (DateTime.UtcNow, items.ToDictionary(i => i.Id, i => i.TakenBy));
        // the list shows it starting now, and drops it once the wait is over if no session took it
        Refill();
        DispatcherTimer.RunOnce(Refill, StartingFor + TimeSpan.FromSeconds(1));
        return true;
    }

    /// <summary>
    /// What a section's or the whole list's Revisar can still send (#172): its tickets with no agent on them or
    /// starting; and whether some of the rest is starting.
    /// </summary>
    (List<TestItem> Free, bool AnyStarting) FreeOf(IEnumerable<TestItem> items)
    {
        var free = new List<TestItem>();
        bool anyStarting = false;
        foreach (var i in items)
        {
            if (Starting(i)) anyStarting = true;
            else if (!TestList.Held(i, sessions)) free.Add(i);
        }
        return (free, anyStarting);
    }

    static string IdList(IEnumerable<TestItem> items) => string.Join(" ", items.Select(i => i.Id.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// A section's link in the tree, in its color: reviews that topic's tickets of the section together, but only the
    /// ones no agent is on yet; with none left, it says "Iniciando…" or "En curso" (#172).
    /// </summary>
    Control ReviewLink(List<TestItem> items, Color c)
    {
        var b = new Button
        {
            Classes = { "link" }, Content = Loc.T("test.list.review"), Foreground = Palette.Alpha(c, 1), Padding = new(6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var (free, anyStarting) = FreeOf(items);
        if (free.Count == 0)
        {
            b.Content = anyStarting ? StartingRow() : new TextBlock { Text = Loc.T("test.list.review.busy"), FontSize = 11 };
            ToolTip.SetTip(b, Loc.T(anyStarting ? "test.list.starting.tip" : "test.list.review.busy.tip"));
            b.IsHitTestVisible = false;
            return b;
        }
        string prompt = ReviewPrompt(IdList(free));
        b.Click += (_, _) => RunIn(prompt, InApp, free);
        return b;
    }

    /// <summary>
    /// The row on top of the list: reviews all the work (requests and fixes) in one session; the tickets an agent is
    /// already on are not counted, and with none left it says so (#172). Null when there's nothing to say.
    /// </summary>
    Control? ReviewAllRow(List<TestItem> items, Color c)
    {
        var (free, anyStarting) = FreeOf(items);
        if (free.Count == 0 && !anyStarting) return null;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var b = new Button { Classes = { "btn", "sm" }, Content = row, HorizontalAlignment = HorizontalAlignment.Left };
        if (free.Count == 0)
        {
            row.Children.Add(StartingRow());
            ToolTip.SetTip(b, Loc.T("test.list.starting.tip"));
            b.IsHitTestVisible = false;
            return b;
        }
        row.Children.Add(new TextBlock { Classes = { "icon" }, Text = ReviewGlyph, FontSize = 12, Foreground = Palette.Alpha(c, 1), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = Loc.T("test.list.review.all", free.Count), VerticalAlignment = VerticalAlignment.Center });
        ToolTip.SetTip(b, Loc.T("test.list.review.all.tip"));
        AutomationProperties.SetName(b, Loc.T("test.list.review.all", free.Count));
        // the skill reads the list itself (and proposes first); the tickets another session has, it can't take
        string prompt = ReviewPrompt("");
        b.Click += (_, _) => RunIn(prompt, InApp, free);
        return b;
    }

    /// <summary>
    /// Each ticket in its own box, so it is clear where one ends (asked by the user). Drawn here: <c>Border.section</c>
    /// lives in the main window's styles and does not reach this window. A strong one (taken, or new) has the accent edge.
    /// </summary>
    static Border Card(Control body, Color? state = null, bool strong = false, bool compact = false)
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
        // a line of the list is lower than the open ticket (#197)
        var inner = new Border { Padding = compact ? new(6, CardPadY, CardPadRight, CardPadY) : new(9, 10, 12, 10), Child = body };
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
            var images = TakeReplyImages(id);
            (Owner as Window)?.Activate();
            open = 0;
            Save(() => TestList.Mark(path, id, lit ? TestItem.Pending : state, text, rev, images));
        };
        return b;
    }

    /// <summary>
    /// Writes off the UI thread (other sessions may hold the list a moment), then asks to read it again. <paramref name="write"/>
    /// must not touch a control: it runs on that other thread.
    /// </summary>
    async void Save(Action write, int delayMs = 0, Action? after = null)
    {
        ErrorText.Text = null;
        var mine = writes.ContinueWith(_ => Try(write), TaskScheduler.Default);
        writes = mine;
        bool ok = await mine;
        if (delayMs > 0) await Task.Delay(delayMs);
        if (!ok) return;
        Changed?.Invoke();
        after?.Invoke();
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
