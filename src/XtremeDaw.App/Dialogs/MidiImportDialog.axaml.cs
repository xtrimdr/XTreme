using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// "Importar MIDI", asked when a file with more than one track comes in (asked by the user, #200: often only one part
/// is wanted): each track with its name, the GM instrument it asks for and its notes, all ticked to start. Same frame as
/// <see cref="TempoDialog"/>. Enter imports what is ticked, Escape or a click outside cancels.
/// </summary>
public sealed partial class MidiImportDialog : UserControl
{
    TaskCompletionSource<List<int>?>? pending;
    readonly List<CheckBox> boxes = [];

    public MidiImportDialog()
    {
        InitializeComponent();
        ImportButton.Click += (_, _) => Answer(true);
        CancelButton.Click += (_, _) => Answer(false);
        AllButton.Click += (_, _) => TickAll(true);
        NoneButton.Click += (_, _) => TickAll(false);
        PointerPressed += (_, e) => { if (e.Source == this) Answer(false); };
    }

    /// <summary>Shows the tracks of <paramref name="data"/> and waits: the indices ticked, or null when it was cancelled.</summary>
    public Task<List<int>?> Ask(string fileName, MidiFileData data)
    {
        pending?.TrySetResult(null);
        pending = new TaskCompletionSource<List<int>?>();
        Body.Text = Loc.T("midi.pick.body", fileName, data.Tracks.Count);
        Rows.Children.Clear();
        boxes.Clear();
        foreach (var t in data.Tracks)
        {
            string kind = t.Drums ? GmNames.Kit(t.Program ?? 0) : GmNames.Program(t.Program ?? 0);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock { Text = t.Name, FontSize = 12.5, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var info = new TextBlock { Text = $"{kind} · {Loc.T("midi.pick.notes", t.Notes.Count)}", Classes = { "dim" }, FontSize = 11, Margin = new Avalonia.Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(info, 1);
            row.Children.Add(info);
            var box = new CheckBox { IsChecked = true, Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            box.IsCheckedChanged += (_, _) => Refresh();
            boxes.Add(box);
            Rows.Children.Add(box);
        }
        Refresh();
        IsVisible = true;
        ImportButton.Focus();
        return pending.Task;
    }

    void TickAll(bool on)
    {
        foreach (var b in boxes) b.IsChecked = on;
    }

    void Refresh()
    {
        int n = boxes.Count(b => b.IsChecked == true);
        ImportButton.IsEnabled = n > 0;
        ImportButton.Content = n == boxes.Count ? Loc.T("midi.pick.import") : Loc.T("midi.pick.importSome", n);
    }

    void Answer(bool import)
    {
        if (import && !ImportButton.IsEnabled) return;
        IsVisible = false;
        var p = pending;
        pending = null;
        p?.TrySetResult(import ? [.. boxes.Select((b, i) => (b, i)).Where(x => x.b.IsChecked == true).Select(x => x.i)] : null);
    }

    /// <summary>Enter imports, Escape cancels; every other key stays here while it is open.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsVisible) return false;
        if (e.Key == Key.Tab) return true;
        if (e.Key == Key.Escape) Answer(false);
        else if (e.Key == Key.Enter) Answer(e.Source != CancelButton);
        else if (e.Key == Key.Space && e.Source is CheckBox) return true;
        else if (e.Key == Key.Space) Answer(e.Source != CancelButton);
        e.Handled = true;
        return true;
    }
}
