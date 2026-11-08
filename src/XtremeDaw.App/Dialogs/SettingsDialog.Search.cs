using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XtremeDaw.App.Controls;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// The search of Configuración (asked by the user, #302): every word typed in the box above the tabs is looked for, without
/// accents or case, in what each page shows (titles, labels, options, helps and tooltips) and in the names of every
/// shortcut, also those of folded groups. The pages are read as they are built, not from a list kept by hand, so a new
/// setting is found by itself. The results replace the page, grouped by tab, and the tabs with nothing are dimmed; a
/// result opens its page (unfolding its group) and lights the card for a moment. Enter opens the first one, Esc clears.
/// </summary>
public sealed partial class SettingsDialog
{
    /// <summary>Class of the cards of a page (<see cref="Card"/>): what a result scrolls to and lights.</summary>
    const string SearchCard = "setcard";
    /// <summary>How many results the list shows at most.</summary>
    const int MaxResults = 80;
    /// <summary>How much of a long help is shown around the first word found.</summary>
    const int SnippetChars = 140;
    /// <summary>How long a card stays lit after a result opened it.</summary>
    static readonly TimeSpan FlashTime = TimeSpan.FromMilliseconds(1400);

    /// <summary>One thing found: the tab, the group of Apariencia or of shortcuts to unfold, the card's title and the text.</summary>
    sealed record Hit(string Page, string? Fold, string? Shortcuts, string Section, string Text);

    /// <summary>The page shown (the tab chosen), apart from the results that may cover it.</summary>
    string tab = "audio";
    /// <summary>The foldable groups of Apariencia: their title and how to open them.</summary>
    readonly Dictionary<Control, (string Title, Action Open)> foldBodies = [];
    readonly List<Hit> hits = [];

    (string Id, Button Tab, Control Page)[] Pages() =>
    [
        ("audio", AudioTab, audioPage), ("midi", MidiTab, midiPage), ("camera", CameraTab, cameraPage), ("sync", SyncTab, syncPage),
        ("save", SaveTab, savePage), ("files", FilesTab, filesPage), ("view", ViewTab, viewPage), ("plugins", PluginsTab, pluginsPage),
        ("components", ComponentsTab, componentsPage), ("shortcuts", ShortcutsTab, shortcutsPage), ("language", LanguageTab, languagePage),
    ];

    void InitSearch()
    {
        SearchBox.TextChanged += (_, _) => Search();
        SearchClear.Click += (_, _) => { SearchBox.Text = ""; SearchBox.Focus(); };
        // a tab chosen by hand ends the search
        foreach (var (_, b, _) in Pages()) b.Click += (_, _) => SearchBox.Text = "";
    }

    /// <summary>Ctrl+F goes to the box; in it, Enter opens the first result and Esc clears it (then closes, as always).</summary>
    bool HandleSearchKey(KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return true;
        }
        if (string.IsNullOrWhiteSpace(SearchBox.Text)) return false;
        bool inBox = e.Source is Control c && c.FindAncestorOfType<TextBox>(true) == SearchBox;
        if (e.Key == Key.Escape)
        {
            SearchBox.Text = "";
            SearchBox.Focus();
            e.Handled = true;
            return true;
        }
        if (e.Key == Key.Enter && inBox && hits.Count > 0)
        {
            Open(hits[0]);
            e.Handled = true;
            return true;
        }
        return false;
    }

    void Search()
    {
        string q = SearchBox.Text ?? "";
        SearchClear.IsVisible = q.Length > 0;
        var words = HelpGuide.Fold(q).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        hits.Clear();
        if (words.Length == 0)
        {
            foreach (var (_, b, _) in Pages()) b.Opacity = 1;
            ShowTab(tab);
            return;
        }
        var all = Index();
        var seen = new HashSet<(string, string, string)>();
        var found = all
            .Select(h => (Hit: h, Text: HelpGuide.Fold(h.Text), Where: HelpGuide.Fold(PageName(h.Page) + " " + (h.Fold ?? "") + " " + h.Section)))
            .Where(x => words.All(w => x.Text.Contains(w) || x.Where.Contains(w)))
            .Where(x => seen.Add((x.Hit.Page, x.Hit.Section, x.Text)))
            // the ones with every word in their own text first; inside that, in the order of the tabs and the page
            .OrderByDescending(x => words.Count(x.Text.Contains))
            .ThenBy(x => Array.FindIndex(Pages(), p => p.Id == x.Hit.Page))
            .Select(x => x.Hit)
            .Take(MaxResults);
        hits.AddRange(found);
        foreach (var (id, b, _) in Pages()) b.Opacity = hits.Any(h => h.Page == id) ? 1 : 0.4;
        ShowResults(q.Trim(), words);
    }

    string PageName(string id) => Loc.T("settings.tab." + id);

    /// <summary>Everything a page shows, read from its controls, plus the shortcuts by name (a folded group has no rows).</summary>
    List<Hit> Index()
    {
        var list = new List<Hit>();
        foreach (var (id, b, page) in Pages())
        {
            list.Add(new Hit(id, null, null, "", PageName(id)));
            string? section = null, fold = null;
            Walk(page, id, ref section, ref fold, list);
        }
        foreach (var g in Shortcuts.All.GroupBy(a => a.Group))
            foreach (var a in g)
                list.Add(new Hit("shortcuts", null, g.Key, g.Key, a.Name));
        string wheel = Loc.T("sc.group.wheel");
        list.Add(new Hit("shortcuts", null, WheelGroup, wheel, Loc.T("sc.wheelScroll")));
        foreach (var a in Shortcuts.Wheel)
            list.Add(new Hit("shortcuts", null, WheelGroup, wheel, a.Name));
        return list;
    }

    /// <summary>Goes through a page in the order it reads: a card starts a section, its title names it, a folded group of
    /// Apariencia is read too (hidden on purpose), anything else hidden is not.</summary>
    void Walk(ILogical node, string page, ref string? section, ref string? fold, List<Hit> list)
    {
        if (node is ComboBox or TextBox) return;
        bool foldBody = node is Control fc && foldBodies.ContainsKey(fc);
        if (node is Visual { IsVisible: false } && !foldBody) return;
        string? keepFold = fold;
        if (foldBody) fold = foldBodies[(Control)node].Title;
        if (node is Border b && b.Classes.Contains(SearchCard)) section = "";
        if (node is TextBlock t && !t.Classes.Contains("icon") && t.Text is { Length: > 1 } text)
        {
            if (t.Classes.Contains("label") && t.Tag is string title) section = title;
            else list.Add(new Hit(page, fold, null, section ?? "", text));
        }
        if (node is ContentControl { Content: string s } && s.Length > 1) list.Add(new Hit(page, fold, null, section ?? "", s));
        if (node is Control c && ToolTip.GetTip(c) is string tip && tip.Length > 1) list.Add(new Hit(page, fold, null, section ?? "", tip));
        foreach (var child in node.LogicalChildren)
            Walk(child, page, ref section, ref fold, list);
        if (foldBody) { fold = keepFold; section = null; }
    }

    void ShowResults(string query, string[] words)
    {
        var list = new StackPanel { Spacing = 2, Margin = new Thickness(12, 14, 12, 16) };
        if (hits.Count == 0)
        {
            list.Children.Add(Help(Loc.T("settings.search.none", query)));
            Body.Content = list;
            return;
        }
        list.Children.Add(new TextBlock { Text = Loc.T("settings.search.count", hits.Count), Classes = { "dim" }, Margin = new Thickness(4, 0, 0, 8) });
        foreach (var g in hits.GroupBy(h => h.Page))
        {
            list.Children.Add(new TextBlock { Text = PageName(g.Key).ToUpperInvariant(), Classes = { "label" }, Margin = new Thickness(4, 10, 0, 4) });
            foreach (var h in g) list.Children.Add(ResultRow(h, words));
        }
        Body.Content = list;
        Body.Offset = default;
    }

    Button ResultRow(Hit h, string[] words)
    {
        var lines = new StackPanel { Spacing = 2 };
        string path = string.Join(" › ", new[] { h.Fold, h.Section }.Where(s => !string.IsNullOrEmpty(s) && s != h.Text));
        if (path.Length > 0) lines.Children.Add(new TextBlock { Text = path, FontSize = 11, Foreground = Palette.Faint, TextTrimming = TextTrimming.CharacterEllipsis });
        lines.Children.Add(Marked(Snippet(h.Text, words), words));
        var b = new Button
        {
            Classes = { "rowmain" }, Content = lines, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 7), CornerRadius = new CornerRadius(6),
        };
        b.Click += (_, _) => Open(h);
        return b;
    }

    /// <summary>A long text cut around the first word found, so the word is in sight.</summary>
    static string Snippet(string text, string[] words)
    {
        text = text.ReplaceLineEndings(" ");
        if (text.Length <= SnippetChars) return text;
        var folded = HelpGuide.Fold(text);
        int at = words.Select(w => folded.IndexOf(w, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        int from = Math.Clamp(at - SnippetChars / 3, 0, text.Length - SnippetChars);
        return (from > 0 ? "…" : "") + text.Substring(from, SnippetChars).Trim() + (from + SnippetChars < text.Length ? "…" : "");
    }

    /// <summary>The text with the words found in the accent. <see cref="HelpGuide.Fold"/> keeps the length of plain letters, so
    /// the places found in the folded text are the same in the shown one; a text where they differ is shown plain.</summary>
    static TextBlock Marked(string text, string[] words)
    {
        var t = new TextBlock { FontSize = 12.5, Foreground = Palette.Fg, TextWrapping = TextWrapping.Wrap };
        var folded = HelpGuide.Fold(text);
        if (folded.Length != text.Length) { t.Text = text; return t; }
        var on = new bool[text.Length];
        foreach (var w in words)
            for (int i = folded.IndexOf(w, StringComparison.Ordinal); i >= 0; i = folded.IndexOf(w, i + w.Length, StringComparison.Ordinal))
                Array.Fill(on, true, i, w.Length);
        t.Inlines = [];
        for (int i = 0; i < text.Length;)
        {
            int j = i;
            while (j < text.Length && on[j] == on[i]) j++;
            var run = new Run(text[i..j]);
            if (on[i]) { run.Foreground = Palette.Accent; run.FontWeight = FontWeight.SemiBold; }
            t.Inlines.Add(run);
            i = j;
        }
        return t;
    }

    /// <summary>Opens the result's page, unfolding its group, and once laid out scrolls to its card and lights it. The box
    /// keeps the words: typing again brings the results back.</summary>
    void Open(Hit h)
    {
        if (h.Shortcuts != null) openGroups.Add(h.Shortcuts);
        if (h.Fold != null && foldBodies.FirstOrDefault(f => f.Value.Title == h.Fold) is { Key: not null } fb) fb.Value.Open();
        ShowTab(h.Page);
        Dispatcher.UIThread.Post(() =>
        {
            if (Body.Content is not Control page) return;
            var found = new List<(Hit Hit, Control At)>();
            string? section = null;
            Locate(page, h.Page, ref section, found);
            var at = found.FirstOrDefault(f => f.Hit.Text == h.Text && f.Hit.Section == h.Section).At
                     ?? found.FirstOrDefault(f => f.Hit.Text == h.Text).At;
            if (at == null) return;
            var target = CardOf(at);
            target.BringIntoView();
            if (target is Border border) Flash(border);
        }, DispatcherPriority.Background);
    }

    static Control CardOf(Control c)
    {
        for (ILogical? p = c; p != null; p = p.LogicalParent)
            if (p is Border b && b.Classes.Contains(SearchCard)) return b;
        return c;
    }

    /// <summary>Same reading as <see cref="Walk"/>, keeping which control showed each text.</summary>
    static void Locate(ILogical node, string page, ref string? section, List<(Hit, Control)> found)
    {
        if (node is ComboBox or TextBox || node is Visual { IsVisible: false }) return;
        if (node is Border b && b.Classes.Contains(SearchCard)) section = "";
        if (node is TextBlock t && !t.Classes.Contains("icon") && t.Text is { Length: > 1 } text)
        {
            if (t.Classes.Contains("label") && t.Tag is string title) section = title;
            else found.Add((new Hit(page, null, null, section ?? "", text), t));
        }
        if (node is Control c)
        {
            if (c is ContentControl { Content: string s }) found.Add((new Hit(page, null, null, section ?? "", s), c));
            if (ToolTip.GetTip(c) is string tip) found.Add((new Hit(page, null, null, section ?? "", tip), c));
        }
        foreach (var child in node.LogicalChildren)
            Locate(child, page, ref section, found);
    }

    /// <summary>Lights a card in the accent and lets it fade, so the eye finds what the search opened.</summary>
    static void Flash(Border card)
    {
        card.Transitions ??= [new BrushTransition { Property = Border.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(600) }];
        card.Background = Palette.AccentSoft;
        DispatcherTimer.RunOnce(() => card.Background = null, FlashTime);
    }
}
