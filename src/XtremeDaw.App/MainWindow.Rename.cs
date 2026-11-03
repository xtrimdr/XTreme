using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// Renaming a track, a folder, a marker or a chord right where it is drawn (native only, asked by the user), like a DAW: a
/// double click on the name (or "Renombrar…" in its menu) lays a text box over it. Enter or a click elsewhere keeps the name, Esc
/// drops it; a scroll keeps it too, since the box would no longer sit on its row. Each rename is one undo step. A chord
/// written by hand uses the same box: it is added only when its name is kept, so Esc leaves nothing behind.
/// </summary>
public partial class MainWindow
{
    readonly TextBox nameEditor = new()
    {
        Classes = { "field" }, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        FontSize = 12.5, FontWeight = FontWeight.SemiBold, Padding = new Thickness(4, 0), MinHeight = 0, VerticalContentAlignment = VerticalAlignment.Center,
    };
    string? nameEditId;
    /// <summary>Narrowest box for a marker's name.</summary>
    const double MarkerEditorW = 140;
    /// <summary>Narrowest box for a chord's name.</summary>
    const double ChordEditorW = 70;
    /// <summary>The id the box edits while it writes a new chord, and where that chord goes.</summary>
    const string NewChordId = "new-chord";
    (double Sec, double End) newChord;

    void InitNameEditor()
    {
        TimelineHost.Children.Add(nameEditor);
        Timeline.NameEditRequested += BeginNameEdit;
        nameEditor.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter) { EndNameEdit(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndNameEdit(false); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        nameEditor.LostFocus += (_, _) => EndNameEdit(true);
        Timeline.AddHandler(PointerWheelChangedEvent, (_, _) => EndNameEdit(true), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Renames in place when the row is on screen, otherwise falls back to <paramref name="elsewhere"/>.</summary>
    void RenameInPlace(string id, Action elsewhere)
    {
        // the menu closes first: the box would lose the focus to it
        Dispatcher.UIThread.Post(() =>
        {
            if (Timeline.IsEffectivelyVisible && Timeline.NameRectOf(id) is { } r) BeginNameEdit(id, r);
            else elsewhere();
        }, DispatcherPriority.Background);
    }

    /// <summary>A double click on the chords' empty strip: the box opens where the new chord goes.</summary>
    void BeginChordEntry(double sec, double end, Rect r)
    {
        if (!CanEditMarkers) return;
        EndNameEdit(true);
        newChord = (sec, end);
        OpenNameEditor(NewChordId, r, Math.Max(r.Width, ChordEditorW), "");
    }

    void BeginNameEdit(string id, Rect r)
    {
        if (store == null || store.ReadOnly || restoring) return;
        EndNameEdit(true);
        var marker = store.Project.Markers.Find(m => m.Id == id);
        var chord = store.Project.ChordMarkers.Find(c => c.Id == id);
        string? name = FolderById(id)?.Name ?? tracks.Find(t => t.Track.Id == id)?.Track.Name ?? marker?.Name ?? chord?.Name;
        if (name == null) return;
        // a marker's flag is as wide as its name: the box leaves room to write a longer one
        OpenNameEditor(id, r, marker != null ? Math.Max(r.Width, MarkerEditorW) : chord != null ? Math.Max(r.Width, ChordEditorW) : r.Width, name);
    }

    void OpenNameEditor(string id, Rect r, double width, string name)
    {
        nameEditId = id;
        Timeline.EditingNameId = id;
        var origin = Timeline.TranslatePoint(default, TimelineHost) ?? default;
        nameEditor.Margin = new Thickness(origin.X + r.X, origin.Y + r.Y, 0, 0);
        nameEditor.Width = width;
        nameEditor.Height = r.Height;
        nameEditor.Text = name;
        nameEditor.IsVisible = true;
        Dispatcher.UIThread.Post(() => { nameEditor.Focus(); nameEditor.SelectAll(); });
    }

    void EndNameEdit(bool keep)
    {
        if (nameEditId is not { } id) return;
        nameEditId = null;
        string text = nameEditor.Text?.Trim() ?? "";
        bool focused = nameEditor.IsFocused;
        nameEditor.IsVisible = false;
        Timeline.EditingNameId = null;
        if (focused) Timeline.Focus();
        if (!keep || text.Length == 0) return;
        if (id == NewChordId)
        {
            var (sec, end) = newChord;
            MarkerEdit(p => ProjectMarkers.AddChord(p, sec, end, text));
        }
        else if (FolderById(id) is { } f)
        {
            if (f.Name != text) FolderEdit(f, x => x.Name = text, "name");
        }
        else if (store?.Project.Markers.Find(m => m.Id == id) is { } m)
        {
            if (m.Name != text) MarkerEdit(p => ProjectMarkers.Rename(p, id, text), $"marker:{id}:name");
        }
        else if (store?.Project.ChordMarkers.Find(c => c.Id == id) is { } c)
        {
            if (c.Name != text) MarkerEdit(p => ProjectMarkers.RenameChord(p, id, text), $"chord:{id}:name");
        }
        else if (tracks.Find(t => t.Track.Id == id) is { } t && t.Track.Name != text) EditTrack(t, x => x.Track.Name = text, "name");
    }
}
