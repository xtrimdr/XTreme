using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using XtremeDaw.App.Controls;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// "Para probar" (asked by the user): the changes waiting to be tried, in tabs, each marked Anda, A mejorar, Anda mal or
/// No anda. All but Anda open a note and the item stays in place until Guardar, so it does not jump away while being explained;
/// the note reaches the disk as a draft before that, because the copy being tried can die. A window of its own and not
/// a dialog, so it stays next to the app while trying things. It opens without taking the focus, and marking gives the
/// focus back to the main window, so Space keeps meaning play instead of pressing the same button again.
/// <see cref="MainWindow"/> reads the file; the list here only shows it.
/// </summary>
public sealed partial class TestListWindow : Window
{
    /// <summary>A list that changes under the pointer would swallow the click that took the focus off a note.</summary>
    const int NoteSaveDelayMs = 300;
    /// <summary>What each tab shows, in the order of the buttons.</summary>
    static readonly (string Key, (Func<TestItem, bool> Match, string Key)[] Groups)[] TabList =
    [
        ("test.list.pending", [(i => i.State == TestItem.Pending && !Noted(i), "")]),
        ("test.list.noted", [(i => i.State == TestItem.Pending && Noted(i), "")]),
        ("test.list.fix", [(Is(TestItem.Fails), "test.list.fail"), (Is(TestItem.WorksBadly), "test.list.bad"), (Is(TestItem.Improve), "test.list.improve")]),
        ("test.list.done", [(Is(TestItem.Works), "")]),
    ];

    /// <summary>Room for about four lines of a note before typing; it grows up to <see cref="NoteMaxHeight"/>, then scrolls.</summary>
    const double NoteMinHeight = 84;
    const double NoteMaxHeight = 220;
    static readonly Color GreenState = Color.Parse("#22c55e"), BlueState = Color.Parse("#60a5fa"), YellowState = Color.Parse("#f59e0b");
    /// <summary>How strong a card's edge is in its verdict's color: visible, without shouting over the text.</summary>
    const double StateEdge = 0.6;

    readonly string path;
    readonly ToggleButton[] tabs;
    readonly Dictionary<int, (TextBox Box, TestItem Item)> notes = [];
    TestListData? current;
    TestListData? waiting;
    int focusNote;
    int tab;
    /// <summary>The end of the writes in order: a note saved by losing the focus cannot land after the click that took it.</summary>
    Task<bool> writes = Task.FromResult(true);

    /// <summary>The file changed from here.</summary>
    public event Action? Changed;

    public TestListWindow() : this("") { }

    public TestListWindow(string path)
    {
        this.path = path;
        InitializeComponent();
        tabs = [PendingTab, NotedTab, FixTab, DoneTab];
        for (int i = 0; i < tabs.Length; i++)
        {
            int index = i;
            tabs[i].Click += (_, _) =>
            {
                tab = index;
                if (current != null) Fill(current);
            };
        }
        Header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        ClearButton.Click += (_, _) => Save(() => TestList.ClearWorking(path));
        // going back to the app keeps the box focused, so LostFocus does not come
        Deactivated += (_, _) =>
        {
            foreach (var (id, n) in notes)
                if (NoteChanged(n.Box, n.Item))
                {
                    string? text = n.Box.Text, draft = n.Item.Draft;
                    Save(() => TestList.Hold(path, id, draft, text), NoteSaveDelayMs);
                }
        };
        Closing += (_, _) =>
        {
            // the app may be closing too: what is on the screen goes to disk now
            foreach (var (id, n) in notes)
                if (NoteChanged(n.Box, n.Item)) Try(() => TestList.Hold(path, id, n.Item.Draft, n.Box.Text));
        };
    }

    /// <summary>Shows the list, unless a note is being written: rebuilding would take it away from under the keyboard.</summary>
    public void Fill(TestListData data)
    {
        current = data;
        if (IsActive && notes.Values.Any(n => n.Box.IsFocused))
        {
            waiting = data;
            return;
        }
        waiting = null;
        notes.Clear();
        Rows.Children.Clear();
        for (int i = 0; i < tabs.Length; i++)
        {
            tabs[i].IsChecked = i == tab;
            tabs[i].Content = $"{Loc.T(TabList[i].Key)} · {TabList[i].Groups.Sum(g => data.Items.Count(g.Match))}";
        }
        foreach (var (match, key) in TabList[tab].Groups)
        {
            // oldest first (asked by the user), by number: a fix keeps its place instead of jumping to the end
            var items = data.Items.Where(match).OrderBy(i => i.Id).ToList();
            if (items.Count == 0) continue;
            if (key != "")
            {
                var label = new TextBlock { Classes = { "label" }, Text = $"{Loc.T(key)} · {items.Count}", Margin = new(2, 4, 0, 0) };
                if (StateColor(items[0].State) is { } c) label.Foreground = Palette.Alpha(c, 1);
                Rows.Children.Add(label);
            }
            foreach (var i in items) Rows.Children.Add(Row(i));
        }
        if (Rows.Children.Count == 0) Rows.Children.Add(new TextBlock { Classes = { "dim" }, Text = Loc.T("test.list.empty"), Margin = new(2, 4, 0, 0) });
        ClearButton.IsEnabled = data.Count(TestItem.Works) > 0;
        if (notes.TryGetValue(focusNote, out var note))
        {
            Activate();
            note.Box.Focus();
        }
        focusNote = 0;
    }

    Control Row(TestItem item)
    {
        string build = TestBuild.CopyName ?? "";
        bool missing = TestList.Missing(item, build);
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap, LineHeight = 18 });

        var badge = new Border
        {
            Background = Palette.Hover, CornerRadius = new(5), Padding = new(6, 1), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Classes = { "mono" }, FontSize = 11, Text = $"#{item.Id}" },
        };
        var info = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        info.Children.Add(badge);
        info.Children.Add(new TextBlock { Classes = { "dim", "mono" }, FontSize = 11, Text = Info(item), VerticalAlignment = VerticalAlignment.Center });
        var top = new Grid { ColumnDefinitions = new("*,Auto") };
        top.Children.Add(info);
        var remove = new Button { Classes = { "act" }, Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 11 } };
        ToolTip.SetTip(remove, Loc.T("test.list.remove"));
        remove.Click += (_, _) => Save(() => TestList.Remove(path, item.Id));
        Grid.SetColumn(remove, 1);
        top.Children.Add(remove);
        body.Children.Add(top);

        if (missing)
        {
            body.Children.Add(new TextBlock { Text = Loc.T("test.list.missing"), Foreground = Palette.Mute, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            return Card(body);
        }

        string shown = item.Draft ?? item.State;
        var note = new TextBox
        {
            Classes = { "field", "note" }, Text = item.Note, Watermark = Loc.T("test.list.note"), TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            MinHeight = NoteMinHeight, MaxHeight = NoteMaxHeight,
            IsVisible = shown is TestItem.Fails or TestItem.WorksBadly or TestItem.Improve || item.Note != null,
        };
        note.LostFocus += (_, _) =>
        {
            if (NoteChanged(note, item))
            {
                string? text = note.Text;
                Save(() => TestList.Hold(path, item.Id, item.Draft, text), NoteSaveDelayMs);
            }
            else if (waiting is { } d) Dispatcher.UIThread.Post(() => Fill(d));
        };
        notes[item.Id] = (note, item);

        var buttons = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        buttons.Children.Add(Verdict(item, note, TestItem.Works, "", "test.list.ok"));
        buttons.Children.Add(Verdict(item, note, TestItem.Improve, "", "test.list.improve"));
        buttons.Children.Add(Verdict(item, note, TestItem.WorksBadly, "", "test.list.bad"));
        buttons.Children.Add(Verdict(item, note, TestItem.Fails, "", "test.list.fail"));
        if (note.IsVisible) buttons.Children.Add(SaveButton(item, note));
        body.Children.Add(buttons);
        body.Children.Add(note);
        return Card(body, StateColor(shown));
    }

    static Func<TestItem, bool> Is(string state) => i => i.State == state;

    /// <summary>
    /// A pending item that already has a note (asked by the user: a fix back with its note got mixed with the new ones).
    /// A new item being explained has a draft and stays in Pendiente until it is saved; a fix stays here while it is.
    /// </summary>
    static bool Noted(TestItem i) => i.Note != null && (i.Tries > 1 || i.Draft == null);

    /// <summary>
    /// Each item in its own box, so it is clear where one ends (asked by the user). Drawn here: <c>Border.section</c>
    /// lives in the main window's styles and does not reach this window.
    /// </summary>
    static Border Card(Control body, Color? state = null) => new()
    {
        Background = Palette.Raised, BorderBrush = state is { } c ? Palette.Alpha(c, StateEdge) : Palette.Hover,
        BorderThickness = new(1), CornerRadius = new(8), Padding = new(12, 10), Child = body,
    };

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

    static string Info(TestItem item)
    {
        var at = item.At.Date == DateTime.Today ? item.At.ToString("HH:mm", CultureInfo.CurrentCulture) : item.At.ToString("dd/MM HH:mm", CultureInfo.CurrentCulture);
        return item.Tries > 1 ? $"{at} · {Loc.T("test.list.try", item.Tries)}" : at;
    }

    static bool NoteChanged(TextBox box, TestItem item) => (box.Text ?? "").Trim() != (item.Note ?? "");

    /// <summary>
    /// Anda takes effect at once; the others wait for the note and <see cref="SaveButton"/>. Pressing the one
    /// lit undoes it: a verdict still in draft is dropped, a saved one takes the item back to pending.
    /// </summary>
    Button Verdict(TestItem item, TextBox note, string state, string glyph, string key)
    {
        bool lit = (item.Draft ?? item.State) == state;
        var color = Palette.Alpha(StateColor(state) ?? Palette.FgColor, 1);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 12, Foreground = lit ? color : Palette.Dim, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = Loc.T(key), Foreground = lit ? color : Palette.Dim, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Classes = { "chip" }, MinHeight = 30, Content = row };
        if (lit) b.BorderBrush = color;
        b.Click += (_, _) =>
        {
            string? text = note.Text;
            if (!lit && state != TestItem.Works)
            {
                focusNote = item.Id;
                Save(() => TestList.Hold(path, item.Id, state, text));
                return;
            }
            (Owner as Window)?.Activate();
            if (lit && item.Draft != null) Save(() => TestList.Hold(path, item.Id, null, text));
            else Save(() => TestList.Mark(path, item.Id, lit ? TestItem.Pending : state, text));
        };
        return b;
    }

    /// <summary>Keeps the verdict and the note: only here does the item leave its tab.</summary>
    Button SaveButton(TestItem item, TextBox note)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = Loc.T("test.list.save"), VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Classes = { "chip" }, MinHeight = 30, Content = row };
        b.Click += (_, _) =>
        {
            string? text = note.Text;
            string state = item.Draft ?? item.State;
            (Owner as Window)?.Activate();
            Save(() => TestList.Mark(path, item.Id, state, text));
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

    bool Try(Action write)
    {
        try
        {
            write();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Dispatcher.UIThread.Post(() => ErrorText.Text = Loc.T("test.list.error", e.Message));
            return false;
        }
    }
}
