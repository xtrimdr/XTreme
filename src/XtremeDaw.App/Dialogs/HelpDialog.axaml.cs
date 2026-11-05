using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using XtremeDaw.App.Controls;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// "Ayuda" (F1, or the ? in the title bar and in the project manager): what people come looking for, by category,
/// each topic named after the task and told in numbered steps (<see cref="HelpGuide"/> has the contents). Left, the
/// search and the categories, one open at a time; right, the page. Everything is rebuilt on each change of page: it
/// is a few controls, and it never runs while something else needs the UI thread to be quiet. Every topic shows its
/// icon, so pages and lists can be told apart at a glance.
/// </summary>
public sealed partial class HelpDialog : UserControl
{
    const double BodySize = 13, BodyLine = 20;
    const double ThreeCardsWidth = 1100;
    const double HeadBadge = 44, RowBadge = 32, CardBadge = 36;
    /// <summary>The topics people come for most; the home page keeps them at hand.</summary>
    static readonly string[] Popular = ["first-take", "no-sound", "export-video", "add-fx", "latency", "shortcuts"];
    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    string? openCategory;
    HelpGuide.Topic? shownTopic;
    List<HelpGuide.Topic> results = [];
    Action showPage;
    bool settingsAllowed = true;

    public event Action? Dismissed;

    /// <summary>A topic's button to a tab of Configuración; the help has closed already.</summary>
    public event Action<string>? SettingsRequested;

    /// <summary>The "Novedades" card of the home page; the window shows them over the help.</summary>
    public event Action? NewsRequested;

    public HelpDialog()
    {
        InitializeComponent();
        showPage = ShowHome;
        CloseButton.Click += (_, _) => HideDialog();
        PointerPressed += (_, e) => { if (e.Source == this) HideDialog(); };
        SearchBox.TextChanged += (_, _) => Search();
    }

    /// <summary>Reopens where it was. Configuración can't open over the project manager, so there its buttons go.</summary>
    public void Show(bool canOpenSettings)
    {
        settingsAllowed = canOpenSettings;
        HelpGuide.Index();
        IsVisible = true;
        Refresh();
        SearchBox.Focus();
    }

    /// <summary>Opens straight on a topic.</summary>
    public void Show(string topic, bool canOpenSettings)
    {
        Show(canOpenSettings);
        if (HelpGuide.Find(topic) is { } t) OpenTopic(t);
    }

    public void HideDialog()
    {
        IsVisible = false;
        Dismissed?.Invoke();
    }

    /// <summary>Esc clears the search, then closes; Enter opens the first result. No shortcut reaches the window.</summary>
    public void HandleKey(KeyEventArgs e)
    {
        bool typing = e.Source is Control c && c.FindAncestorOfType<TextBox>(true) != null;
        if (e.Key == Key.Escape)
        {
            if (string.IsNullOrEmpty(SearchBox.Text)) HideDialog();
            else SearchBox.Text = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && typing && results.Count > 0 && !string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            OpenTopic(results[0]);
            e.Handled = true;
        }
        else if ((!typing || e.Key is >= Key.F1 and <= Key.F24) && Shortcuts.Find(e) == "help")
        {
            HideDialog();
            e.Handled = true;
        }
    }

    void Refresh()
    {
        RebuildNav();
        showPage();
    }

    void Go(Action page)
    {
        showPage = page;
        Refresh();
        Scroller.Offset = default;
    }

    void Search()
    {
        string q = SearchBox.Text ?? "";
        if (string.IsNullOrWhiteSpace(q))
        {
            results = [];
            if (showPage == ShowResults) Go(ShowHome);
            return;
        }
        results = HelpGuide.Search(q);
        shownTopic = null;
        Go(ShowResults);
    }

    void OpenCategory(HelpGuide.Category c)
    {
        bool closing = openCategory == c.Id && shownTopic == null && Page.Tag == c;
        openCategory = closing ? null : c.Id;
        shownTopic = null;
        ClearSearch();
        Go(closing ? ShowHome : () => ShowCategory(c));
    }

    void OpenTopic(HelpGuide.Topic t)
    {
        openCategory = t.Category.Id;
        shownTopic = t;
        ClearSearch();
        Go(() => ShowTopic(t));
    }

    void GoHome()
    {
        openCategory = null;
        shownTopic = null;
        ClearSearch();
        Go(ShowHome);
    }

    void ClearSearch()
    {
        if (string.IsNullOrEmpty(SearchBox.Text)) return;
        results = [];
        SearchBox.Text = "";
    }

    // --- left column --------------------------------------------------------------------------------

    void RebuildNav()
    {
        Nav.Children.Clear();
        foreach (var c in HelpGuide.Categories)
        {
            bool open = c.Id == openCategory;
            var row = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 10 };
            row.Children.Add(new TextBlock { Classes = { "icon" }, Text = HelpGuide.Glyph(c), FontSize = 15 });
            row.Children.Add(Column(new TextBlock { Text = c.Title, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }, 1));
            row.Children.Add(Column(new TextBlock { Classes = { "icon" }, Text = open ? "" : "", FontSize = 9 }, 2));
            var button = new Button { Classes = { "nav", "rowmain" }, Content = row };
            button.Classes.Set("active", open && shownTopic == null);
            button.Click += (_, _) => OpenCategory(c);
            Nav.Children.Add(button);
            if (!open) continue;
            foreach (var t in c.Topics)
            {
                var line = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8 };
                line.Children.Add(new TextBlock { Classes = { "icon" }, Text = HelpGuide.Glyph(t), FontSize = 12, Width = 15, TextAlignment = TextAlignment.Center });
                line.Children.Add(Column(new TextBlock { Text = t.Title, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }, 1));
                var item = new Button { Classes = { "nav", "topic", "rowmain" }, Content = line };
                item.Classes.Set("active", t == shownTopic);
                item.Click += (_, _) => OpenTopic(t);
                Nav.Children.Add(item);
            }
        }
    }

    static T Column<T>(T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }

    // --- pages --------------------------------------------------------------------------------------

    void ShowHome()
    {
        Page.Tag = null;
        Page.Children.Clear();
        Page.Children.Add(Title(Loc.T("help.home.title")));
        Page.Children.Add(Rich(Loc.T("help.home.body"), Palette.Dim));
        var first = new UniformGrid { Columns = 2, Margin = new Thickness(-4, 0) };
        if (HelpGuide.Find("tour") is { } tour)
            first.Children.Add(BigCard("\uE768", Loc.T("help.startHere"), tour.Title, () => OpenTopic(tour)));
        if (NewsGuide.Latest is { } latest)
            first.Children.Add(BigCard("\uE789", Loc.T("news.title"), Loc.T("news.summary", latest.Version, latest.Items.Length), () => NewsRequested?.Invoke()));
        Page.Children.Add(first);
        Page.Children.Add(Label(Loc.T("help.popular")));
        var popular = new WrapPanel();
        foreach (var t in Popular.Select(HelpGuide.Find).OfType<HelpGuide.Topic>()) popular.Children.Add(TopicChip(t));
        Page.Children.Add(popular);
        Page.Children.Add(Label(Loc.T("help.allTopics")));
        // from the window: on the first opening this page is built before the dialog has been measured
        bool wide = TopLevel.GetTopLevel(this)?.ClientSize.Width >= ThreeCardsWidth;
        var cards = new UniformGrid { Columns = wide ? 3 : 2, Margin = new Thickness(-4, -6, -4, 0) };
        foreach (var c in HelpGuide.Categories)
        {
            var body = new StackPanel { Spacing = 6 };
            body.Children.Add(Badge(HelpGuide.Glyph(c), CardBadge));
            body.Children.Add(new TextBlock { Text = c.Title, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = c.Hint, FontSize = 11, LineHeight = 15, Foreground = Palette.Dim, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = Loc.T("help.topics", c.Topics.Length), FontSize = 11, Foreground = Palette.Faint });
            var card = new Button { Classes = { "card", "rowmain" }, Margin = new Thickness(4), Content = body };
            card.Click += (_, _) => OpenCategory(c);
            cards.Children.Add(card);
        }
        Page.Children.Add(cards);
    }

    void ShowCategory(HelpGuide.Category c)
    {
        Page.Tag = c;
        Page.Children.Clear();
        Page.Children.Add(Crumbs(null));
        Page.Children.Add(Head(HelpGuide.Glyph(c), c.Title, c.Hint));
        Page.Children.Add(TopicList(c.Topics, showCategory: false));
    }

    void ShowResults()
    {
        Page.Tag = null;
        Page.Children.Clear();
        Page.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("help.results", results.Count) });
        if (results.Count == 0) Page.Children.Add(Rich(Loc.T("help.noResults", (SearchBox.Text ?? "").Trim()), Palette.Dim));
        else Page.Children.Add(TopicList(results, showCategory: true));
    }

    void ShowTopic(HelpGuide.Topic t)
    {
        Page.Tag = t;
        Page.Children.Clear();
        Page.Children.Add(Crumbs(t.Category));
        Page.Children.Add(Head(HelpGuide.Glyph(t), t.Title, t.Intro));
        for (int i = 0; i < t.Steps.Length; i++) Page.Children.Add(Step(i + 1, t.Steps[i], i == t.Steps.Length - 1));
        foreach (var note in t.Notes) Page.Children.Add(Note(note));
        if (t.Settings != null && settingsAllowed)
        {
            string tab = t.Settings;
            var open = new Button
            {
                Classes = { "chip" },
                HorizontalAlignment = HorizontalAlignment.Left,
                Content = IconText("", Loc.T("help.openSettings", Loc.T($"settings.tab.{tab}"))),
            };
            open.Click += (_, _) => { HideDialog(); SettingsRequested?.Invoke(tab); };
            Page.Children.Add(open);
        }
        var related = t.See.Select(HelpGuide.Find).OfType<HelpGuide.Topic>().ToList();
        if (related.Count > 0)
        {
            Page.Children.Add(Label(Loc.T("help.see")));
            var chips = new WrapPanel();
            foreach (var r in related) chips.Children.Add(TopicChip(r));
            Page.Children.Add(chips);
        }
        Page.Children.Add(Neighbors(t));
    }

    // --- pieces -------------------------------------------------------------------------------------

    static TextBlock Title(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg, TextWrapping = TextWrapping.Wrap };

    static TextBlock Label(string text) => new() { Classes = { "label" }, Text = text, Margin = new Thickness(0, 8, 0, -6) };

    static StackPanel IconText(string glyph, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 13 });
        row.Children.Add(new TextBlock { Text = text });
        return row;
    }

    /// <summary>A glyph on a round tint of the accent: what identifies a topic, a category or a piece of news.</summary>
    internal static Border Badge(string glyph, double size) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Background = Palette.AccentSoft,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Child = new TextBlock
        {
            Classes = { "icon" },
            Text = glyph,
            FontSize = Math.Round(size * 0.46),
            Foreground = Palette.Accent,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    static Grid Head(string glyph, string title, string intro)
    {
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Title(title));
        text.Children.Add(Rich(intro, Palette.Dim));
        var grid = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 14 };
        grid.Children.Add(Badge(glyph, HeadBadge));
        grid.Children.Add(Column(text, 1));
        return grid;
    }

    Button TopicChip(HelpGuide.Topic t)
    {
        var chip = new Button { Classes = { "chip" }, Content = IconText(HelpGuide.Glyph(t), t.Title), Margin = new Thickness(0, 0, 8, 8) };
        chip.Click += (_, _) => OpenTopic(t);
        return chip;
    }

    static Button BigCard(string glyph, string title, string text, Action go)
    {
        var body = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = text, FontSize = 11, LineHeight = 15, Foreground = Palette.Dim, TextWrapping = TextWrapping.Wrap });
        var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
        row.Children.Add(Badge(glyph, CardBadge));
        row.Children.Add(Column(body, 1));
        var card = new Button { Classes = { "card", "rowmain" }, Margin = new Thickness(4), Content = row };
        card.Click += (_, _) => go();
        return card;
    }

    /// <summary>The topic before and after this one in its category, to keep reading without going back.</summary>
    Grid Neighbors(HelpGuide.Topic t)
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        int at = Array.IndexOf(t.Category.Topics, t);
        if (at > 0) grid.Children.Add(Neighbor(t.Category.Topics[at - 1], "\uE72B", 0));
        if (at < t.Category.Topics.Length - 1) grid.Children.Add(Neighbor(t.Category.Topics[at + 1], "\uE72A", 2));
        return grid;
    }

    Button Neighbor(HelpGuide.Topic t, string arrow, int column)
    {
        var chip = Column(new Button { Classes = { "chip" }, Content = IconText(arrow, t.Title) }, column);
        chip.Click += (_, _) => OpenTopic(t);
        return chip;
    }

    /// <summary>"Inicio / Grabar": where this page is, and the way back.</summary>
    Control Crumbs(HelpGuide.Category? category)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(Crumb(Loc.T("help.home"), GoHome));
        if (category != null)
        {
            row.Children.Add(new TextBlock { Text = "/", FontSize = 11, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(Crumb(category.Title, () => { openCategory = category.Id; shownTopic = null; Go(() => ShowCategory(category)); }));
        }
        return row;
    }

    static Button Crumb(string text, Action go)
    {
        var b = new Button { Classes = { "link" }, Content = text };
        b.Click += (_, _) => go();
        return b;
    }

    StackPanel TopicList(IEnumerable<HelpGuide.Topic> topics, bool showCategory)
    {
        var list = new StackPanel { Spacing = 8 };
        foreach (var t in topics)
        {
            var body = new StackPanel { Spacing = 3 };
            body.Children.Add(new TextBlock { Text = t.Title, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(Rich(showCategory ? $"{t.Category.Title} · {t.Intro}" : t.Intro, Palette.Dim, 12, 17));
            var line = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
            line.Children.Add(Badge(HelpGuide.Glyph(t), RowBadge));
            line.Children.Add(Column(body, 1));
            var row = new Button { Classes = { "card", "rowmain" }, Content = line };
            row.Click += (_, _) => OpenTopic(t);
            list.Children.Add(row);
        }
        return list;
    }

    /// <summary>A numbered step; a line joins it to the next, so the page reads as one path.</summary>
    static Grid Step(int number, string text, bool last)
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
        if (!last)
            grid.Children.Add(new Border { Width = 2, Background = Palette.AccentSoft, Margin = new Thickness(0, 24, 0, -14), HorizontalAlignment = HorizontalAlignment.Center });
        grid.Children.Add(new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = Palette.AccentSoft,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Classes = { "mono" },
                Text = number.ToString(Loc.Culture),
                FontSize = 12,
                Foreground = Palette.Accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        grid.Children.Add(Column(Rich(text, Palette.Fg), 1));
        return grid;
    }

    static Border Note(string text)
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10 };
        grid.Children.Add(new TextBlock { Classes = { "icon" }, Text = "", FontSize = 14, Foreground = Palette.Accent, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) });
        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(new TextBlock { Text = Loc.T("help.tip"), FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Palette.Accent });
        body.Children.Add(Rich(text, Palette.Dim, 12, 18));
        grid.Children.Add(Column(body, 1));
        return new Border { Classes = { "section" }, Padding = new Thickness(12, 10), Child = grid };
    }

    /// <summary>A paragraph with its marks: what's on screen stands out, and shortcuts read like keys.</summary>
    internal static TextBlock Rich(string text, IBrush color, double size = BodySize, double line = BodyLine)
    {
        var block = new TextBlock { FontSize = size, LineHeight = line, Foreground = color, TextWrapping = TextWrapping.Wrap };
        var inlines = block.Inlines ??= new InlineCollection();
        foreach (var (piece, mark) in HelpGuide.Pieces(text))
        {
            inlines.Add(mark switch
            {
                HelpGuide.Mark.Key => new Run($" {piece} ") { FontFamily = Palette.Code, FontSize = size - 1, Foreground = Palette.Fg, Background = Palette.Hover },
                HelpGuide.Mark.Bold => new Run(piece) { FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg },
                HelpGuide.Mark.Icon => new Run(piece) { FontFamily = IconFont, Foreground = Palette.Accent },
                _ => new Run(piece),
            });
        }
        return block;
    }
}
