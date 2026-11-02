using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using XtremeDaw.App.Controls;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// "Para probar" (asked by the user): the changes waiting to be tried, each marked Anda or No anda with a note. A
/// window of its own and not a dialog, so it stays next to the app while trying things. It opens without taking the
/// focus, and marking gives the focus back to the main window, so Space keeps meaning play instead of pressing the
/// same button again. <see cref="MainWindow"/> reads the file; the list here only shows it.
/// </summary>
public sealed partial class TestListWindow : Window
{
    static readonly IBrush Amber = Palette.Brush("#f59e0b");
    /// <summary>A list that changes under the pointer would swallow the click that took the focus off a note.</summary>
    const int NoteSaveDelayMs = 300;

    readonly string path;
    readonly Dictionary<int, (TextBox Box, TestItem Item)> notes = [];
    TestListData? waiting;
    int focusNote;

    /// <summary>The file changed from here.</summary>
    public event Action? Changed;

    public TestListWindow() : this("") { }

    public TestListWindow(string path)
    {
        this.path = path;
        InitializeComponent();
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
                    string? text = n.Box.Text;
                    Save(() => TestList.Mark(path, id, n.Item.State, text), NoteSaveDelayMs);
                }
        };
        Closing += (_, _) =>
        {
            // the app may be closing too: what is on the screen goes to disk now
            foreach (var (id, n) in notes)
                if (NoteChanged(n.Box, n.Item)) Try(() => TestList.Mark(path, id, n.Item.State, n.Box.Text));
        };
    }

    /// <summary>Shows the list, unless a note is being written: rebuilding would take it away from under the keyboard.</summary>
    public void Fill(TestListData data)
    {
        if (IsActive && notes.Values.Any(n => n.Box.IsFocused))
        {
            waiting = data;
            return;
        }
        waiting = null;
        notes.Clear();
        Rows.Children.Clear();
        foreach (var (state, key) in new[]
                 {
                     (TestItem.Pending, "test.list.pending"), (TestItem.Fails, "test.list.fails"), (TestItem.Works, "test.list.works"),
                 })
        {
            var items = data.Items.Where(i => i.State == state).ToList();
            if (items.Count == 0) continue;
            Rows.Children.Add(new TextBlock { Classes = { "label" }, Text = $"{Loc.T(key)} · {items.Count}", Margin = new(2, 4, 0, 0) });
            foreach (var i in items) Rows.Children.Add(Row(i));
        }
        if (data.Items.Count == 0) Rows.Children.Add(new TextBlock { Classes = { "dim" }, Text = Loc.T("test.list.empty"), Margin = new(2, 4, 0, 0) });
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

        var info = new TextBlock { Classes = { "dim", "mono" }, FontSize = 11, Text = Info(item) };
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
            body.Children.Add(new TextBlock { Text = Loc.T("test.list.missing"), Foreground = Amber, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            return Card(body);
        }

        var note = new TextBox
        {
            Classes = { "field" }, Text = item.Note, Watermark = Loc.T("test.list.note"), TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            IsVisible = item.State == TestItem.Fails || item.Note != null,
        };
        note.LostFocus += (_, _) =>
        {
            if (NoteChanged(note, item)) Save(() => TestList.Mark(path, item.Id, item.State, note.Text), NoteSaveDelayMs);
            else if (waiting is { } d) Dispatcher.UIThread.Post(() => Fill(d));
        };
        notes[item.Id] = (note, item);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(Verdict(item, note, TestItem.Works, "", "test.list.ok", Palette.InTune));
        buttons.Children.Add(Verdict(item, note, TestItem.Fails, "", "test.list.fail", Amber));
        body.Children.Add(buttons);
        body.Children.Add(note);
        return Card(body);
    }

    static Border Card(Control body) => new() { Classes = { "section" }, Padding = new(12, 10), Child = body };

    static string Info(TestItem item)
    {
        var at = item.At.Date == DateTime.Today ? item.At.ToString("HH:mm", CultureInfo.CurrentCulture) : item.At.ToString("dd/MM HH:mm", CultureInfo.CurrentCulture);
        string t = $"#{item.Id} · {at}";
        return item.Tries > 1 ? $"{t} · {Loc.T("test.list.try", item.Tries)}" : t;
    }

    static bool NoteChanged(TextBox box, TestItem item) => (box.Text ?? "").Trim() != (item.Note ?? "");

    /// <summary>Anda or No anda; pressing the one already lit takes the item back to pending.</summary>
    Button Verdict(TestItem item, TextBox note, string state, string glyph, string key, IBrush color)
    {
        bool lit = item.State == state;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Classes = { "icon" }, Text = glyph, FontSize = 12, Foreground = lit ? color : Palette.Dim, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = Loc.T(key), Foreground = lit ? color : Palette.Dim, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Classes = { "chip" }, MinHeight = 30, Content = row };
        if (lit) b.BorderBrush = color;
        b.Click += (_, _) =>
        {
            string next = lit ? TestItem.Pending : state;
            string? text = note.Text;
            if (next == TestItem.Fails) focusNote = item.Id;
            else (Owner as Window)?.Activate();
            Save(() => TestList.Mark(path, item.Id, next, text));
        };
        return b;
    }

    /// <summary>Writes off the UI thread (other sessions may hold the list a moment), then asks to read it again.</summary>
    async void Save(Action write, int delayMs = 0)
    {
        ErrorText.Text = null;
        bool ok = await Task.Run(() => Try(write));
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
