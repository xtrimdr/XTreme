using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Model;

namespace XtremeDaw.App;

/// <summary>
/// A track's or a bus's color and icon (native only, asked by the user): one picker, opened from the badge in the
/// timeline, the inspector or the mixer. A color applies at once and leaves the picker open so an icon can follow;
/// an icon closes it. Each pick is an edit like any other (<see cref="EditTrack"/>: undo, autosave).
/// </summary>
public partial class MainWindow
{
    Flyout? lookFlyout;

    void InitTrackLook() => Timeline.LookRequested += (t, at) => ShowTrackLook(t, Timeline, at);

    void ShowTrackLook(LiveTrack t, Control anchor, Point? at = null) =>
        ShowLook(() => t.Track.Color, () => t.Track.Icon, hex => SetTrackLook(t, x => x.Color = hex, "color"), id => SetTrackLook(t, x => x.Icon = id, "icon"),
            Loc.T("look.icon.none.tip"), anchor, at);

    /// <summary>A bus's color and icon: the same picker as a track's; without an icon it shows the one every bus shares.</summary>
    void ShowBusLook(string id, Control anchor, Point? at = null) =>
        ShowLook(() => BusById(id)?.Color, () => BusById(id)?.Icon, hex => BusEdit(id, b => b.Color = hex, "color"), icon => BusEdit(id, b => b.Icon = icon, "icon"),
            Loc.T("look.icon.none.bus.tip"), anchor, at);

    /// <summary>The color and icon picker of whatever has them (a track or a bus): <paramref name="color"/> and
    /// <paramref name="icon"/> read what it has now, the setters edit it (one undo step each).</summary>
    void ShowLook(Func<string?> color, Func<string?> icon, Action<string?> setColor, Action<string?> setIcon, string noneTip, Control anchor, Point? at = null)
    {
        if (store == null || store.ReadOnly || restoring) return;
        var refresh = new List<Action>();
        var body = new StackPanel { Spacing = 8 };

        body.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("look.color").ToUpperInvariant() });
        body.Children.Add(ColorChips(color, setColor, refresh));

        var none = new Button { Classes = { "chip" }, Content = Loc.T("look.icon.none"), FontSize = 11, MinHeight = 26, Padding = new Thickness(8, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(none, noneTip);
        none.Click += (_, _) => { setIcon(null); lookFlyout?.Hide(); };
        refresh.Add(() => none.IsEnabled = icon() != null);
        var iconHead = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        iconHead.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("look.icon").ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(none, 1); iconHead.Children.Add(none);
        body.Children.Add(iconHead);
        foreach (string category in TrackIcons.Categories)
        {
            body.Children.Add(new TextBlock { Text = Loc.T("icon.cat." + category), FontSize = 11, Foreground = Palette.Faint, Margin = new Thickness(0, 4, 0, 0) });
            var grid = new WrapPanel { Margin = new Thickness(-2, 0) };
            foreach (string id in TrackIcons.InCategory(category))
            {
                var view = new TrackIconView { Icon = id, Width = 20, Height = 20 };
                var b = new Button { Classes = { "ghost" }, Content = view, Width = 34, Height = 34, MinHeight = 34, Padding = new Thickness(0), Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(6), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(b, TrackIcons.Name(id));
                Avalonia.Automation.AutomationProperties.SetName(b, TrackIcons.Name(id));
                b.Click += (_, _) => { setIcon(id); lookFlyout?.Hide(); };
                refresh.Add(() =>
                {
                    bool on = TrackIcons.Resolve(icon()) == id;
                    var tint = TrackIcons.ColorOf(color());
                    b.Classes.Set("lit", on);
                    b.Background = on ? tint is { } c ? Palette.Alpha(c, 0.22) : Palette.AccentSoft : Brushes.Transparent;
                    view.Foreground = on ? tint is { } k ? Palette.Alpha(k, 1) : Palette.Accent : Palette.Dim;
                });
                grid.Children.Add(b);
            }
            body.Children.Add(grid);
        }
        foreach (var r in refresh) r();

        var card = new Border
        {
            Width = 344, Padding = new Thickness(12, 10), Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = new ScrollViewer { Content = body, MaxHeight = Math.Max(240, Bounds.Height - 120), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
        lookFlyout?.Hide();
        var flyout = lookFlyout = new Flyout { Content = card, FlyoutPresenterClasses = { "bare" } };
        if (at is { } p)
        {
            flyout.Placement = PlacementMode.AnchorAndGravity;
            flyout.PlacementAnchor = PopupAnchor.TopLeft;
            flyout.PlacementGravity = PopupGravity.BottomRight;
            flyout.HorizontalOffset = p.X;
            flyout.VerticalOffset = p.Y;
        }
        else flyout.Placement = PlacementMode.BottomEdgeAlignedLeft;
        flyout.ShowAt(anchor);
    }

    void SetTrackLook(LiveTrack t, Action<XtremeDaw.Projects.Track> change, string key) => EditTrack(t, x => change(x.Track), key);

    /// <summary>The color grid of the pickers (#145): "no color" and the custom color on top, then
    /// <see cref="TrackIcons.Colors"/> in rows of <see cref="TrackIcons.ColorsPerRow"/>, in hue order. A pick applies at
    /// once and repaints every entry of <paramref name="refresh"/>, so the picker stays open. The custom color opens a
    /// <see cref="ColorField"/> and a hex field under the grid; what they pick goes through <paramref name="live"/>
    /// (<paramref name="pick"/> when null), which must not close the picker. <paramref name="size"/> shrinks the
    /// swatches where the grid has less room (the inspector).</summary>
    static StackPanel ColorChips(Func<string?> current, Action<string?> pick, List<Action> refresh, double size = 20, Action<string?>? live = null)
    {
        var apply = live ?? pick;
        var colors = new StackPanel { Margin = new Thickness(-2, 0), Width = TrackIcons.ColorsPerRow * (size + 4), HorizontalAlignment = HorizontalAlignment.Left };

        Button Chip(string? hex)
        {
            var chip = new Button { Width = size, Height = size, MinHeight = size, Padding = new Thickness(0), Margin = new Thickness(2), CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(2), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            chip.Classes.Set("lit", true);
            if (hex == null) chip.Content = new TextBlock { Classes = { "icon" }, Text = "\uE711", FontSize = 10, Foreground = Palette.Faint };
            ToolTip.SetTip(chip, hex == null ? Loc.T("look.color.none") : hex.ToUpperInvariant());
            chip.Click += (_, _) => { pick(hex); foreach (var r in refresh) r(); };
            refresh.Add(() =>
            {
                chip.Background = hex == null ? Palette.Hover : Palette.Brush(hex);
                chip.BorderBrush = current() == hex ? Palette.Fg : hex == null ? Palette.Line : Brushes.Transparent;
            });
            return chip;
        }

        // top row: no color, and the custom color (its face shows the color when it is not one of the palette)
        var top = new WrapPanel();
        top.Children.Add(Chip(null));
        var customIcon = new TextBlock { Classes = { "icon" }, Text = "\uE790", FontSize = 10, Foreground = Palette.Dim };
        var custom = new ToggleButton { Width = size, Height = size, MinHeight = size, Padding = new Thickness(0), Margin = new Thickness(2), CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(2), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Content = customIcon };
        custom.Classes.Set("lit", true);
        ToolTip.SetTip(custom, Loc.T("look.color.custom"));
        Avalonia.Automation.AutomationProperties.SetName(custom, Loc.T("look.color.custom"));
        top.Children.Add(custom);
        colors.Children.Add(top);

        for (int n = 0; n < TrackIcons.Colors.Length; n += TrackIcons.ColorsPerRow)
        {
            var row = new WrapPanel();
            for (int i = n; i < Math.Min(n + TrackIcons.ColorsPerRow, TrackIcons.Colors.Length); i++) row.Children.Add(Chip(TrackIcons.Colors[i]));
            colors.Children.Add(row);
        }

        // the custom color: a square and hue strip, and its hex
        var field = new ColorField { Height = 132, Margin = new Thickness(2, 6, 2, 0) };
        var hexBox = new TextBox { Classes = { "mono" }, Watermark = "#rrggbb", MaxLength = 7, FontSize = 12, MinHeight = 28, Height = 28, Width = 96,
            Margin = new Thickness(2, 8, 2, 2), HorizontalAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center };
        var panel = new StackPanel { IsVisible = false };
        panel.Children.Add(field); panel.Children.Add(hexBox);
        colors.Children.Add(panel);
        static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
        void Live(string hex) { apply(hex); foreach (var r in refresh) r(); }
        field.Changed += c => { hexBox.Text = Hex(c); Live(Hex(c)); };
        void TypeHex()
        {
            string t = (hexBox.Text ?? "").Trim();
            if (!t.StartsWith('#')) t = "#" + t;
            if (t.Length == 7 && Color.TryParse(t, out var c))
            {
                field.Set(c);
                if (Hex(c) != current()) Live(Hex(c));
            }
            else hexBox.Text = current() ?? "";
        }
        hexBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { TypeHex(); e.Handled = true; } };
        hexBox.LostFocus += (_, _) => { if (panel.IsVisible) TypeHex(); };
        custom.Click += (_, _) =>
        {
            panel.IsVisible = custom.IsChecked == true;
            if (!panel.IsVisible) return;
            field.Set(TrackIcons.ColorOf(current()) ?? Color.Parse(TrackIcons.Colors[0]));
            hexBox.Text = current() ?? "";
        };
        refresh.Add(() =>
        {
            string? hex = current();
            // "" = several colors (tracks picked together): no chip is lit
            bool mine = !string.IsNullOrEmpty(hex) && Array.IndexOf(TrackIcons.Colors, hex) < 0;
            custom.Background = mine ? Palette.Brush(hex!) : Palette.Hover;
            custom.BorderBrush = mine ? Palette.Fg : Palette.Line;
            customIcon.IsVisible = !mine;
            if (panel.IsVisible && !hexBox.IsFocused) hexBox.Text = hex ?? "";
        });
        return colors;
    }

    /// <summary>The inspector's compact color picker (asked by the user, #145): a square of the current color that
    /// opens the color grid under it; a pick applies and closes it. <paramref name="refresh"/> is the list the inspector
    /// runs after an edit, so the square follows the color.</summary>
    Button ColorPickButton(Func<string?> current, Action<string?> pick, List<Action> refresh)
    {
        var none = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 9, Foreground = Palette.Faint,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var face = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Child = none };
        var b = new Button { Classes = { "ghost" }, Content = face, Width = 28, Height = 28, MinHeight = 28, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(5), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(b, Loc.T("look.color.pick.tip"));
        Avalonia.Automation.AutomationProperties.SetName(b, Loc.T("look.color"));
        b.Click += (_, _) =>
        {
            if (store == null || store.ReadOnly || restoring) return;
            var chips = new List<Action>();
            var grid = ColorChips(current, hex => { pick(hex); lookFlyout?.Hide(); }, chips, 18, pick);
            foreach (var r in chips) r();
            var card = new Border { Padding = new Thickness(10, 8), Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Child = grid };
            lookFlyout?.Hide();
            var flyout = lookFlyout = new Flyout { Content = card, FlyoutPresenterClasses = { "bare" }, Placement = PlacementMode.BottomEdgeAlignedRight };
            flyout.ShowAt(b);
        };
        refresh.Add(() =>
        {
            string? hex = current();
            face.Background = hex == null ? Palette.Hover : Palette.Brush(hex);
            face.BorderBrush = hex == null ? Palette.Line : Brushes.Transparent;
            none.IsVisible = hex == null;
        });
        return b;
    }

    /// <summary>The button next to a folder's color square (#145): gives its tracks the folder's color, enabled while
    /// a track inside has another one.</summary>
    Button FolderPaintButton(Func<XtremeDaw.Projects.Folder?> folder, List<Action> refresh)
    {
        var b = new Button { Classes = { "ghost" }, Width = 28, Height = 28, MinHeight = 28, Padding = new Thickness(0), CornerRadius = new CornerRadius(5),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 14 } };
        ToolTip.SetTip(b, Loc.T("folder.color.tracks.tip"));
        Avalonia.Automation.AutomationProperties.SetName(b, Loc.T("folder.color.tracks.tip"));
        b.Click += (_, _) => { if (folder() is { } f) PaintFolderTracks(f); };
        refresh.Add(() =>
        {
            var f = folder();
            b.IsEnabled = f != null && store != null && store.Project.Tracks.Any(t => t.FolderId == f.Id && t.Color != f.Color);
        });
        return b;
    }

    /// <summary>The question under a folder's colors (asked by the user): right after a pick it offers to give the
    /// folder's tracks that same color, in one undo step. It shows only after a pick on that folder, and only while a
    /// track inside has another color; <c>Ask</c> is what the color pick calls.</summary>
    (Control View, Action<XtremeDaw.Projects.Folder> Ask) FolderTracksAsk(Func<XtremeDaw.Projects.Folder?> folder, List<Action> refresh)
    {
        string? askedId = null;
        var text = new TextBlock { FontSize = 11, Foreground = Palette.Dim, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var yes = new Button { Classes = { "chip" }, Content = Loc.T("folder.color.tracks.yes"), FontSize = 11, MinHeight = 26, Padding = new Thickness(8, 0),
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(yes, Loc.T("folder.color.tracks.tip"));
        var view = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), IsVisible = false };
        view.Children.Add(text);
        Grid.SetColumn(yes, 1); view.Children.Add(yes);
        yes.Click += (_, _) =>
        {
            if (folder() is { } f) PaintFolderTracks(f);
            askedId = null;
            foreach (var r in refresh) r();
        };
        refresh.Add(() =>
        {
            var f = folder();
            var inside = f == null || store == null ? [] : store.Project.Tracks.Where(t => t.FolderId == f.Id).ToList();
            view.IsVisible = f != null && f.Id == askedId && inside.Any(t => t.Color != f.Color);
            if (view.IsVisible) text.Text = Loc.T(inside.Count == 1 ? "folder.color.tracks.ask1" : "folder.color.tracks.ask", inside.Count);
        });
        return (view, f => askedId = f.Id);
    }

    /// <summary>Gives every track of <paramref name="f"/> the folder's color (none included): one undo step.</summary>
    void PaintFolderTracks(XtremeDaw.Projects.Folder f)
    {
        if (restoring || store == null || store.ReadOnly) return;
        undo.Checkpoint(store.Project);
        foreach (var t in store.Project.Tracks)
            if (t.FolderId == f.Id) t.Color = f.Color;
        MarkDirty();
        UpdateUndoButtons();
        Timeline.UpdateTracks(tracks, store.Project.Folders);
        RefreshInspectorText();
        RefreshMixer();
    }

    /// <summary>How far apart in <see cref="TrackIcons.Colors"/> the gradient's neighbors go, at most: close enough to
    /// read as one family, far enough to tell them apart.</summary>
    const int GradientMaxStep = 3;
    /// <summary>How much of the palette a gradient covers before its step shrinks to one: half the hue circle.</summary>
    const int GradientSpan = 24;

    /// <summary>"Colorear en degradé" of the picked tracks (asked by the user, #181): neighboring hues of the palette in
    /// timeline order, starting from the first track's color (the palette entry nearest to it; the accent when it has
    /// none). One undo step.</summary>
    void PaintTracksGradient(IReadOnlyList<string> ids)
    {
        if (store == null || ids.Count == 0) return;
        var order = Timeline.PickedTrackIds.Count == ids.Count ? Timeline.PickedTrackIds : ids;
        var first = store.Project.Tracks.Find(t => t.Id == order[0]);
        int start = NearestPaletteColor(TrackIcons.ColorOf(first?.Color) ?? Palette.AccentColor);
        int step = ids.Count < 2 ? 1 : Math.Clamp(GradientSpan / (ids.Count - 1), 1, GradientMaxStep);
        var colors = new Dictionary<string, string>();
        for (int i = 0; i < order.Count; i++) colors[order[i]] = TrackIcons.Colors[(start + i * step) % TrackIcons.Colors.Length];
        PaintTracks(ids, id => colors[id], "color-gradient");
    }

    /// <summary>The entry of <see cref="TrackIcons.Colors"/> closest to <paramref name="c"/>.</summary>
    static int NearestPaletteColor(Color c)
    {
        int best = 0;
        double bestD = double.MaxValue;
        for (int i = 0; i < TrackIcons.Colors.Length; i++)
        {
            var p = Color.Parse(TrackIcons.Colors[i]);
            double dr = p.R - c.R, dg = p.G - c.G, db = p.B - c.B, d = dr * dr + dg * dg + db * db;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>Gives each of these tracks a color (null = none): one undo step per gesture (<paramref name="key"/>
    /// merges a custom color's drag), the picked tracks stay picked.</summary>
    void PaintTracks(IReadOnlyList<string> ids, Func<string, string?> color, string key)
    {
        if (restoring || store == null || store.ReadOnly) return;
        undo.Checkpoint(store.Project, $"tracks:{string.Join(",", ids)}:{key}");
        foreach (var t in store.Project.Tracks)
            if (ids.Contains(t.Id)) t.Color = color(t.Id);
        MarkDirty();
        UpdateUndoButtons();
        Timeline.UpdateTracks(tracks, store.Project.Folders);
        Timeline.PickTracks(ids.ToList());
        RefreshInspectorText();
        RefreshMixer();
    }

    /// <summary>"Un color para todos" of the picked tracks (#181): the color grid, and what it picks goes to all of them.
    /// It shows their color while they share one.</summary>
    void ShowTracksColor(IReadOnlyList<string> ids, Control anchor, Point? at = null)
    {
        if (store == null || store.ReadOnly || restoring) return;
        string? Shared()
        {
            var list = store.Project.Tracks.Where(t => ids.Contains(t.Id)).Select(t => t.Color).Distinct().ToList();
            return list.Count == 1 ? list[0] : "";
        }
        var refresh = new List<Action>();
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("look.tracks.one.title", ids.Count).ToUpperInvariant() });
        body.Children.Add(ColorChips(Shared, hex => { PaintTracks(ids, _ => hex, "color"); lookFlyout?.Hide(); }, refresh,
            live: hex => PaintTracks(ids, _ => hex, "color")));
        foreach (var r in refresh) r();
        var card = new Border
        {
            Width = 344, Padding = new Thickness(12, 10), Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = body,
        };
        lookFlyout?.Hide();
        var flyout = lookFlyout = new Flyout { Content = card, FlyoutPresenterClasses = { "bare" } };
        if (at is { } p)
        {
            flyout.Placement = PlacementMode.AnchorAndGravity;
            flyout.PlacementAnchor = PopupAnchor.TopLeft;
            flyout.PlacementGravity = PopupGravity.BottomRight;
            flyout.HorizontalOffset = p.X;
            flyout.VerticalOffset = p.Y;
        }
        else flyout.Placement = PlacementMode.BottomEdgeAlignedLeft;
        flyout.ShowAt(anchor);
    }

    /// <summary>
    /// A folder's color picker (folders have no icon): opened from its icon in the timeline, its menu or the inspector.
    /// A separator's closes as soon as a color is picked (asked by the user): it has no tracks to offer the color to.
    /// </summary>
    void ShowFolderLook(XtremeDaw.Projects.Folder f, Control anchor, Point? at = null)
    {
        if (store == null || store.ReadOnly || restoring) return;
        string id = f.Id;
        XtremeDaw.Projects.Folder F() => FolderById(id) ?? f;
        var refresh = new List<Action>();
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T(f.Separator ? "separator.color" : "folder.color").ToUpperInvariant() });
        var ask = FolderTracksAsk(F, refresh);
        body.Children.Add(ColorChips(() => F().Color, hex =>
        {
            FolderEdit(F(), x => x.Color = hex, "color");
            if (F().Separator) lookFlyout?.Hide();
            else ask.Ask(F());
        }, refresh));
        body.Children.Add(ask.View);
        foreach (var r in refresh) r();
        var card = new Border
        {
            Width = 344, Padding = new Thickness(12, 10), Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = body,
        };
        lookFlyout?.Hide();
        var flyout = lookFlyout = new Flyout { Content = card, FlyoutPresenterClasses = { "bare" } };
        if (at is { } p)
        {
            flyout.Placement = PlacementMode.AnchorAndGravity;
            flyout.PlacementAnchor = PopupAnchor.TopLeft;
            flyout.PlacementGravity = PopupGravity.BottomRight;
            flyout.HorizontalOffset = p.X;
            flyout.VerticalOffset = p.Y;
        }
        else flyout.Placement = PlacementMode.BottomEdgeAlignedLeft;
        flyout.ShowAt(anchor);
    }
}
