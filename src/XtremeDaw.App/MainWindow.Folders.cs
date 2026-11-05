using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Model;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// Track folders and track presets (native only, asked by the user). Folders group tracks in the timeline and the
/// mixer and are a channel of their own (the user's call, #134): their tracks' sum goes through the folder's slots, pan,
/// fader and mute to its output and sends (<see cref="ApplyRouting"/>), and its solo solos its tracks. Moving tracks in
/// and out and removing a folder are timeline edits (<see cref="EditTimeline"/>); renaming, color, fader, pan, M, S,
/// slots, output and sends are folder edits with their own undo step (<see cref="FolderEdit"/>); folding is only a view,
/// saved without undo.
/// Presets keep a track's whole strip to add it again from the "nueva pista" menu (<see cref="TrackPresets"/>).
/// </summary>
public partial class MainWindow
{
    void InitFolders()
    {
        Timeline.FolderMoveRequested += async (id, at) => await EditTimeline(p =>
        {
            TrackFolders.MoveFolder(p, id, at);
            return Timeline.SelectedFolderId ?? sel?.Track.Id;
        }, "tracks-reordered");
        Timeline.FolderFoldRequested += f => FolderEdit(f, x => x.Collapsed = !x.Collapsed, "fold", undoable: false);
        Timeline.FolderRemoveRequested += (f, at) => _ = RemoveFolder(f, at);
        Timeline.FolderToggled += (f, solo) => FolderChannelEdit(f.Id, x => { if (solo) x.Solo = !x.Solo; else x.Muted = !x.Muted; }, solo ? "solo" : "muted");
        Timeline.FolderContextRequested += ShowFolderMenu;
        Timeline.FolderLookRequested += (f, at) => ShowFolderLook(f, Timeline, at);
        InitFolderInspector();
        InitBusInspector();
        InitNameEditor();
        Timeline.RowHeightRequested += SetRowHeight;
        ResetHeightsButton.Click += (_, _) => ResetRowHeights();
        SplitAllButton.Click += (_, _) => SplitAllWaves();
    }

    /// <summary>Every stereo or mixed track's left and right waves (asked by the user): on for all if any is off, else
    /// off for all. A view setting like <see cref="SetSplitWave"/>: saved, without undo.</summary>
    void SplitAllWaves()
    {
        if (store == null || store.ReadOnly || restoring) return;
        var stereo = tracks.Where(SplitsWave).ToList();
        if (stereo.Count == 0) return;
        bool on = stereo.Any(t => !t.Track.SplitWave);
        foreach (var t in stereo) t.Track.SplitWave = on;
        MarkDirty();
        Timeline.InvalidateVisual();
        RefreshInspectorText();
    }

    static bool SplitsWave(LiveTrack t) => !t.Track.IsMidi && t.Layout is ChannelLayout.Stereo or ChannelLayout.Mixed;

    /// <summary>Every tick: <see cref="SplitAllButton"/> on while there's a stereo track, in accent while all of them
    /// show both sides. No LINQ; touches the button only when its state changed.</summary>
    void RefreshSplitAllButton()
    {
        int stereo = 0, split = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            if (!SplitsWave(tracks[i])) continue;
            stereo++;
            if (tracks[i].Track.SplitWave) split++;
        }
        bool enabled = stereo > 0 && store is { ReadOnly: false } && !restoring, lit = stereo > 0 && split == stereo;
        if (SplitAllButton.IsEnabled == enabled && SplitAllButton.Classes.Contains("lit") == lit) return;
        SplitAllButton.IsEnabled = enabled;
        SplitAllButton.Classes.Set("lit", lit);
        ((TextBlock)SplitAllButton.Content!).Foreground = lit ? Palette.Accent : Palette.Dim;
        ToolTip.SetTip(SplitAllButton, Loc.T(lit ? "timeline.splitAll.off" : "timeline.splitAll.on"));
    }

    /// <summary>A track's or a folder's own height in px (null = its usual one). A track and a folded folder keep it as
    /// a multiple of the shared height and an open folder as px at the default one, so the slider goes on scaling it.
    /// A view setting like folding: saved, without undo.</summary>
    void SetRowHeight(string id, double? h)
    {
        if (store == null || store.ReadOnly || restoring) return;
        // a folded folder's usual height is its own, not the shared one: any height dragged to stays
        if (FolderById(id) is { Collapsed: true } folded) folded.HeightScale = h / Timeline.TrackHeight;
        else if (FolderById(id) is { } f) f.Height = h is { } px ? Timeline.FolderUnzoomed(px) : null;
        else if (tracks.Find(t => t.Track.Id == id) is { } t) t.Track.HeightScale = Scale(h);
        else if (tracks.Select(x => Automations.ById(x.Track, id)).Concat(store.Project.Folders.Select(x => Automations.ById(x, id))).FirstOrDefault(l => l != null) is { } lane)
            lane.HeightScale = h / Timeline.AutoBaseH;
        else return;
        RowHeightsChanged();
    }

    /// <summary>A track's stereo clips as two waves, left over right, or as one (asked by the user). A view setting like
    /// its height: saved, without undo.</summary>
    void SetSplitWave(LiveTrack t, bool on)
    {
        if (store == null || store.ReadOnly || restoring || t.Track.SplitWave == on) return;
        t.Track.SplitWave = on;
        MarkDirty();
        Timeline.InvalidateVisual();
        RefreshInspectorText();
    }

    double? Scale(double? h) => h is { } px && Math.Abs(px - Timeline.TrackHeight) >= 1 ? px / Timeline.TrackHeight : null;

    /// <summary>Every track and folder back to its usual height.</summary>
    void ResetRowHeights()
    {
        if (store == null || store.ReadOnly || restoring) return;
        foreach (var t in store.Project.Tracks)
        {
            t.HeightScale = null;
            foreach (var l in t.Automation ?? []) l.HeightScale = null;
        }
        foreach (var f in store.Project.Folders)
        {
            f.Height = null;
            f.HeightScale = null;
            foreach (var l in f.Automation ?? []) l.HeightScale = null;
        }
        RowHeightsChanged();
    }

    void RowHeightsChanged()
    {
        MarkDirty();
        Timeline.UpdateTracks(tracks, store!.Project.Folders);
        UpdateResetHeights();
    }

    /// <summary>The reset button is on only while some row has a height of its own.</summary>
    void UpdateResetHeights() => ResetHeightsButton.IsEnabled = store is { ReadOnly: false } st
        && (st.Project.Tracks.Any(t => t.HeightScale != null || t.Automation?.Exists(l => l.HeightScale != null) == true) || st.Project.Folders.Any(f => f.Height != null || f.HeightScale != null || f.Automation?.Exists(l => l.HeightScale != null) == true));

    /// <summary>The folder selected in the timeline (or the mixer), or null; then no track is selected.</summary>
    Folder? SelectedFolder => Timeline.SelectedFolderId is { } id ? FolderById(id) : null;

    /// <summary>
    /// A folder edit: one undo step per control (folding is not one), heard at once, saved, on screen. A channel control
    /// (fader, pan, M, S, a slot: <paramref name="relayout"/> false) only redraws the timeline: a fader drag doesn't lay
    /// its rows out again on every move.
    /// </summary>
    void FolderEdit(Folder f, Action<Folder> change, string key, bool undoable = true, bool relayout = true)
    {
        if (syncing || restoring || store == null || store.ReadOnly) return;
        if (undoable) undo.Checkpoint(store.Project, $"folder:{f.Id}:{key}");
        change(f);
        // a folder is a channel (#134): its fader, M, S and slots reach the engine with the routing, its VST3 slots start or stop
        ApplyRouting(store.Project, tracks);
        SyncEffects();
        MarkDirty();
        UpdateUndoButtons();
        if (relayout) Timeline.UpdateTracks(tracks, store.Project.Folders);
        else Timeline.InvalidateVisual();
        RefreshInspectorText();
        RefreshMixer();
    }

    /// <summary>A folder's slots, for the panels (<see cref="SlotPanel"/>): like a bus's, its compressor measured against its first track with audio.</summary>
    SlotOwner SlotsOf(Folder folder)
    {
        string id = folder.Id;
        return new()
        {
            OwnerId = id,
            Slots = () => (FolderById(id) ?? folder).Inserts,
            Edit = (change, key) => FolderChannelEdit(id, x => change(x.Inserts), key),
            Recalculate = ChannelDsp.RecalculateComp,
            Take = () => (tracks.FirstOrDefault(t => t.Track.FolderId == id && t.FirstAudio != null) ?? tracks.FirstOrDefault(t => t.FirstAudio != null))?.FirstAudio,
            Insert = slot => engine.Node(id)?.Find(slot),
            AddTip = Loc.T("mix.slot.add.folder.tip"),
        };
    }

    /// <summary>A folder's channel control (fader, pan, M, S, its slots): a <see cref="FolderEdit"/> on the folder as the project has it now.</summary>
    void FolderChannelEdit(string id, Action<Folder> change, string key)
    {
        if (FolderById(id) is { Separator: false } f) FolderEdit(f, change, key, relayout: false);
    }

    /// <summary>
    /// Removes a folder. With tracks inside it asks first (asked by the user): just the folder, its tracks staying at
    /// the root, or the folder and its tracks. The question opens at <paramref name="at"/> on the timeline, or under
    /// the folder's name; with an <paramref name="anchor"/> other than the timeline (its mixer strip), under it.
    /// </summary>
    Task RemoveFolder(Folder f, Point? at = null, Control? anchor = null)
    {
        int n = store?.Project.Tracks.Count(t => t.FolderId == f.Id) ?? 0;
        if (n == 0) return RemoveFolderNow(f, false);
        if (store == null || restoring) return Task.CompletedTask;
        var list = new List<object>
        {
            new MenuItem { Header = Loc.T("folder.remove.ask", f.Name), IsEnabled = false, FontSize = 11 },
            MenuAction(Loc.T("folder.remove.only"), () => RemoveFolderNow(f, false), tip: Loc.T("folder.remove.only.tip")),
            MenuAction(n == 1 ? Loc.T("folder.remove.all.one") : Loc.T("folder.remove.all", n), () => RemoveFolderNow(f, true), danger: true, tip: Loc.T("folder.remove.all.tip")),
        };
        if (anchor != null && anchor != Timeline)
        {
            OpenAnchoredMenu(Popup(list, 240), anchor);
            return Task.CompletedTask;
        }
        var name = Timeline.FolderNameRect(f);
        OpenTimelineMenu(Popup(list, 240), at ?? (name.Y >= 0 && name.Y < Timeline.Bounds.Height ? name.BottomLeft + new Point(0, 4) : new Point(20, 40)), 110);
        return Task.CompletedTask;
    }

    Task RemoveFolderNow(Folder f, bool withTracks) =>
        EditTimeline(p => { TrackFolders.Remove(p, f.Id, withTracks); return withTracks && sel?.Track.FolderId == f.Id ? null : sel?.Track.Id; }, "folder-removed");

    Task NewFolder(IReadOnlyCollection<string>? trackIds = null) => EditTimeline(p => TrackFolders.Add(p, null, trackIds), "folder-added");

    void RenameFolder(Folder f, Control anchor, Point? at) =>
        AskName(anchor, at, Loc.T("folder.rename"), f.Name, Loc.T("common.ok"), name => FolderEdit(f, x => x.Name = name, "name"));

    /// <summary>
    /// A small card with a name box: Enter or the button accepts, Esc or a click outside cancels. With
    /// <paramref name="exists"/>, a name already taken is not replaced at once (asked by the user, #141): the card says so
    /// and the button turns into Replace, which a second Enter or click confirms; editing the name asks again.
    /// </summary>
    void AskName(Control anchor, Point? at, string title, string initial, string action, Action<string> done, string? note = null,
        Func<string, bool>? exists = null)
    {
        var box = new TextBox { Classes = { "field" }, Text = initial, Width = 240 };
        var ok = new Button { Classes = { "btn", "sm", "primary" }, Content = action, HorizontalAlignment = HorizontalAlignment.Right };
        var taken = new TextBlock { Classes = { "notice" }, Text = Loc.T("name.exists"), TextWrapping = TextWrapping.Wrap, MaxWidth = 240, IsVisible = false };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Classes = { "label" }, Text = title.ToUpperInvariant() });
        body.Children.Add(box);
        if (note != null) body.Children.Add(new TextBlock { Text = note, FontSize = 11, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap, MaxWidth = 240 });
        body.Children.Add(taken);
        body.Children.Add(ok);
        string? confirmed = null;
        box.TextChanged += (_, _) =>
        {
            if (confirmed == null) return;
            confirmed = null;
            taken.IsVisible = false;
            ok.Content = action;
        };
        var card = new Border
        {
            Padding = new Thickness(12, 10), Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = body,
        };
        var flyout = new Flyout { Content = card, FlyoutPresenterClasses = { "bare" } };
        if (at is { } p)
        {
            flyout.Placement = PlacementMode.AnchorAndGravity;
            flyout.PlacementAnchor = PopupAnchor.TopLeft;
            flyout.PlacementGravity = PopupGravity.BottomRight;
            flyout.HorizontalOffset = p.X;
            flyout.VerticalOffset = p.Y;
        }
        else flyout.Placement = PlacementMode.BottomEdgeAlignedLeft;
        void Commit()
        {
            string name = box.Text?.Trim() ?? "";
            if (name.Length == 0) return;
            if (exists != null && confirmed != name && exists(name))
            {
                confirmed = name;
                taken.IsVisible = true;
                ok.Content = Loc.T("name.replace");
                return;
            }
            flyout.Hide();
            done(name);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { flyout.Hide(); e.Handled = true; }
        };
        ok.Click += (_, _) => Commit();
        flyout.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        flyout.ShowAt(anchor);
    }

    /// <summary>A color square for menu headers (folder colors).</summary>
    static Control Swatch(string? hex) => new Border
    {
        Width = 12, Height = 12, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center,
        Background = hex == null ? Palette.Dim : Palette.Brush(hex),
    };

    void ShowFolderMenu(Folder f, Point point)
    {
        if (store == null || restoring) return;
        OpenTimelineMenu(Popup(f.Separator ? SeparatorMenu(f, Timeline, point) : FolderMenu(f, Timeline, point), 240), point, 200);
    }

    /// <summary>A folder's menu, from the timeline (at <paramref name="point"/>) or its mixer strip (<paramref name="anchor"/>, no point).</summary>
    List<object> FolderMenu(Folder f, Control anchor, Point? point)
    {
        bool enabled = store is { ReadOnly: false } && !restoring;
        string id = f.Id;
        var refresh = new List<Action>();
        var list = new List<object> { new MenuItem { Header = f.Name, IsEnabled = false, FontSize = 11 } };
        list.Add(MenuAction(Loc.T("folder.rename.more"), () =>
        {
            if (point is { } at) RenameInPlace(f.Id, () => RenameFolder(f, anchor, at));
            else RenameFolder(f, anchor, null);
            return Task.CompletedTask;
        }, enabled: enabled, icon: ""));
        var color = new MenuItem { Header = MenuHeader(Loc.T("look.color.more"), Swatch(f.Color)), IsEnabled = enabled };
        color.Click += (_, _) => ShowFolderLook(f, anchor, point);
        list.Add(color);
        // looked up by id at each click: a right click keeps the menu open while the folder changes under it
        list.Add(MenuChoice(() => FolderById(id)?.Collapsed == true ? (Loc.T("folder.unfold"), "") : (Loc.T("folder.fold"), ""),
            () => { if (FolderById(id) is { } x) FolderEdit(x, y => y.Collapsed = !y.Collapsed, "fold", undoable: false); }, refresh, enabled: enabled));
        list.Add(new Separator());
        var sendsIcon = FxIcon.Create(FxIcon.Bus, 12);
        sendsIcon.VerticalAlignment = VerticalAlignment.Center;
        list.Add(new MenuItem { Header = MenuHeader(Loc.T("send.menu"), sendsIcon), ItemsSource = SendChoices(RouterOf(f)), IsEnabled = enabled });
        // a folder is a channel: its lanes (#136), like a track's
        list.Add(new MenuItem { Header = MenuHeader(Loc.T("auto.menu"), new TextBlock { Classes = { "icon" }, Text = "\uE9D2", FontSize = 12 }), ItemsSource = AutomationItems(f), IsEnabled = enabled });
        if (f.Automation is { Count: > 0 })
            list.Add(MenuCheck(Loc.T("folder.lanes"), () => FolderById(id) is { AutomationFolded: false }, () => { if (FolderById(id) is { } x) FoldLanes(x); }, refresh,
                tip: Loc.T("folder.lanes.tip"), enabled: enabled));
        list.Add(new Separator());
        list.Add(MenuAction(Loc.T("folder.newTrack"), () => EditTimeline(p =>
        {
            string id = ClipEditing.AddEmpty(p, !AudioOnly);
            TrackFolders.MoveInto(p, id, f.Id);
            return id;
        }, "track-added"), enabled: enabled, icon: ""));
        list.Add(new Separator());
        list.Add(MenuAction(Loc.T("folder.remove"), () => RemoveFolder(f, point, anchor), enabled: enabled, danger: true, tip: Loc.T("folder.remove.tip")));
        return list;
    }

    /// <summary>
    /// A separator's menu, from the timeline or its mixer strip: rename, color, show or hide it in the timeline (it
    /// always shows in the mixer) and remove it.
    /// </summary>
    List<object> SeparatorMenu(Folder f, Control anchor, Point? point)
    {
        bool enabled = store is { ReadOnly: false } && !restoring;
        string id = f.Id;
        var refresh = new List<Action>();
        var list = new List<object> { new MenuItem { Header = f.Name, IsEnabled = false, FontSize = 11 } };
        list.Add(MenuAction(Loc.T("separator.rename.more"), () =>
        {
            if (FolderById(id) is { } x)
            {
                if (point is { } at) RenameInPlace(id, () => RenameFolder(x, anchor, at));
                else AskName(anchor, null, Loc.T("separator.rename"), x.Name, Loc.T("common.ok"), n => FolderEdit(x, y => y.Name = n.Trim(), "name"));
            }
            return Task.CompletedTask;
        }, enabled: enabled, icon: ""));
        var color = new MenuItem { Header = MenuHeader(Loc.T("look.color.more"), Swatch(f.Color)), IsEnabled = enabled };
        color.Click += (_, _) => { if (FolderById(id) is { } x) ShowFolderLook(x, anchor, point); };
        list.Add(color);
        list.Add(MenuChoice(() => FolderById(id)?.HiddenInTimeline == true ? (Loc.T("separator.show"), "") : (Loc.T("separator.hide"), ""),
            () => { if (FolderById(id) is { } x) FolderEdit(x, y => y.HiddenInTimeline = !y.HiddenInTimeline, "hidden"); }, refresh,
            tip: Loc.T("separator.hide.tip"), enabled: enabled));
        list.Add(new Separator());
        list.Add(MenuAction(Loc.T("separator.remove"), () => FolderById(id) is { } x ? RemoveFolderNow(x, false) : Task.CompletedTask, enabled: enabled, danger: true));
        return list;
    }

    /// <summary>A new separator right above <paramref name="track"/> (above its folder, if it is in one): asked for from the timeline, it shows there.</summary>
    Task NewSeparatorAbove(LiveTrack track) => EditTimeline(p =>
    {
        object item = TrackFolders.Of(p, track.Track) is { } folder ? folder : track.Track;
        TrackFolders.AddSeparator(p, null, TrackFolders.Sequence(p).IndexOf(item), hidden: false);
        return track.Track.Id;
    }, "separator-added");

    /// <summary>A menu header with a leading control (a swatch) and its text.</summary>
    static Control MenuHeader(string text, Control lead)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(lead);
        header.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        return header;
    }

    /// <summary>The track menu's folder entries: a new folder with it, right in the menu (one click to group), and a
    /// "Carpeta" submenu only when there is somewhere else to go (out of its folder, into another one). On a track
    /// picked with others they act on all the picked ones.</summary>
    IEnumerable<object> FolderItems(LiveTrack track, bool enabled)
    {
        var items = new List<object>();
        var p = store!.Project;
        var group = Timeline.PickedTrackIds.Contains(track.Track.Id) ? Timeline.PickedTrackIds : [track.Track.Id];
        var current = TrackFolders.Of(p, track.Track);
        async Task MoveGroup(Action<Project> move)
        {
            await EditTimeline(x => { move(x); return track.Track.Id; }, "tracks-reordered");
            Timeline.PickTracks(group);
        }
        if (current != null)
            // each one goes right after the folder's end, so the last one first keeps their order
            items.Add(MenuAction(Loc.T("folder.takeOut", current.Name), () => MoveGroup(x => { foreach (var id in group.Reverse()) TrackFolders.TakeOut(x, id); }),
                enabled: enabled, icon: ""));
        foreach (var f in p.Folders.Where(f => f != current && !f.Separator))
        {
            string id = f.Id;
            var item = new MenuItem { Header = MenuHeader(Loc.T("folder.moveTo", f.Name), Swatch(f.Color)), IsEnabled = enabled };
            item.Click += async (_, _) => await MoveGroup(x => { foreach (var t in group) TrackFolders.MoveInto(x, t, id); });
            items.Add(item);
        }
        yield return MenuAction(group.Count > 1 ? Loc.T("folder.group") : Loc.T("folder.newWith"), () => NewFolder(group), enabled: enabled, icon: "");
        if (items.Count > 0)
            yield return new MenuItem { Header = MenuHeader(Loc.T("folder.menu"), new TextBlock { Text = "", Classes = { "icon" }, FontSize = 12 }), ItemsSource = items, IsEnabled = enabled };
    }

    // --- folders in the mixer ----------------------------------------------------------------------

    IReadOnlyList<Folder> Folders => store?.Project.Folders ?? [];
    Folder? FolderById(string id) => Folders.FirstOrDefault(f => f.Id == id);
    Folder? FolderOfTrack(LiveTrack t) => t.Track.FolderId is { } id ? FolderById(id) : null;
    /// <summary>The band above the strips where folder names sit (every column gets it while the project has folders).</summary>
    const double FolderBandH = 26;
    /// <summary>A mixer column with the folder band on top (empty for strips at the root and the returns).</summary>
    static Grid Banded(Control? band, Control body)
    {
        var g = new Grid { RowDefinitions = new RowDefinitions("26,*") };
        if (band != null) g.Children.Add(band);
        Grid.SetRow(body, 1);
        g.Children.Add(body);
        return g;
    }

    /// <summary>A folder in the mixer: a tinted group with its name, fold arrow and track count above its strips.</summary>
    Control FolderGroup(Folder folder, double stripW)
    {
        string id = folder.Id;
        Folder F() => FolderById(id) ?? folder;
        var strips = new StackPanel { Orientation = Orientation.Horizontal };
        // the folder's strip drags the whole folder; its open tracks drag on their own (MainWindow.MixerOrder.cs)
        var own = FolderStrip(folder, stripW);
        MixerDraggable(own, folder);
        strips.Children.Add(own);
        if (!folder.MixerCollapsed)
        {
            mx.Order.FolderStrips[id] = own;
            foreach (var t in tracks.Where(t => t.Track.FolderId == id).ToList())
            {
                var card = Strip(t, stripW);
                MixerDraggable(card, t.Track);
                mx.Order.Inner.Add((card, t.Track, id));
                strips.Children.Add(card);
            }
        }

        var fold = new Button { Classes = { "ghost" }, Width = 20, Height = 20, MinHeight = 20, Padding = new Thickness(0), CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Classes = { "icon" }, Text = folder.MixerCollapsed ? "" : "", FontSize = 9, Foreground = Palette.Dim } };
        ToolTip.SetTip(fold, Loc.T(folder.MixerCollapsed ? "mixer.folder.unfold" : "mixer.folder.fold"));
        fold.Click += (_, _) => FolderEdit(F(), x => x.MixerCollapsed = !x.MixerCollapsed, "mixerFold", undoable: false);
        // name and count are one text so they trim together when the band is as narrow as a folded compact strip
        var nameRun = new Run { FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg };
        var count = new Run { FontSize = 10.5, Foreground = Palette.Faint };
        // left-aligned so the band past the text stays free for the fold double click (#170)
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260, Margin = new Thickness(6, 0, 0, 0) };
        name.Inlines!.AddRange([nameRun, new Run("  "), count]);
        RenameOnDoubleClick(name, () => RenameFolder(F(), name, null));
        // the band never widens the group: the strips set the width and the name trims to it
        // the folder strip already shows the folder glyph: the band leaves that room to the name
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(fold);
        Grid.SetColumn(name, 1); row.Children.Add(name);
        // a double click on the band where there is no text folds or unfolds it (#170); on the name it renames
        // (handled on the tunnel) and the arrow handles its own press
        var hit = new Border { Background = Brushes.Transparent, Padding = new Thickness(8, 4, 8, 0), Child = row };
        hit.PointerPressed += (_, e) =>
        {
            if (e.Handled || e.ClickCount != 2 || !e.GetCurrentPoint(hit).Properties.IsLeftButtonPressed || store == null || restoring) return;
            e.Handled = true;
            FolderEdit(F(), x => x.MixerCollapsed = !x.MixerCollapsed, "mixerFold", undoable: false);
        };
        var band = new NoWidth { Child = hit };
        var decor = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = new Thickness(1, 2, 1, StripGap / 2), IsHitTestVisible = false };
        mx.Refresh.Add(() =>
        {
            var f = F();
            var c = TimelineView.FolderColor(f);
            int n = tracks.Count(t => t.Track.FolderId == id);
            nameRun.Text = f.Name;
            count.Text = n == 0 ? Loc.T("folder.empty") : n == 1 ? Loc.T("folder.count.one") : Loc.T("folder.count", n);
            decor.Background = Palette.Alpha(c, 0.06);
            decor.BorderBrush = Palette.Alpha(c, 0.35);
        });
        return new Grid { Children = { decor, Banded(band, strips) } };
    }

    /// <summary>
    /// The folder's own strip, a channel like a bus's (#134): its effects as a list, its sends as bars, the pan bar and
    /// M S over its fader, its meter after them (what goes to its output) and its dB at the foot. Its head has the folder
    /// glyph, "CARPETA" and its output selector (a track's strip has no output there: a folder's goes to the master or a
    /// bus). It takes the mixer's zoom width and gives way as it narrows, like the other strips.
    /// </summary>
    Control FolderStrip(Folder folder, double width)
    {
        string id = folder.Id;
        Folder F() => FolderById(id) ?? folder;
        // as wide as the other strips (asked by the user: it follows the mixer's zoom)
        var box = StripCard(width);
        mx.Boxes.Add(box);
        // a click on the folder's strip selects the folder, like a track's strip selects its track
        var top = StripCardLook(box, () => Timeline.SelectedFolderId == id, () => false, () => TimelineView.FolderColor(F()), () => Timeline.SelectFolder(id));
        // its own menu, the timeline's (without it, the right click reached the mixer's background menu)
        box.PointerReleased += (_, e) =>
        {
            if (e.Handled || e.InitialPressMouseButton != MouseButton.Right || store == null || restoring) return;
            e.Handled = true;
            Timeline.SelectFolder(id);
            OpenAnchoredMenu(Popup(FolderMenu(F(), box, null), 240), box);
        };
        var fader = new Fader();
        fader.Changed += v => FolderChannelEdit(id, x => x.Volume = v, "volume");
        var db = new TextBlock { Classes = { "mono" }, FontSize = 10, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center };
        mx.Refresh.Add(() => { fader.Value = F().Volume; db.Text = ValueFormat.Db(F().Volume); });
        var meter = new MixerMeter();
        var faders = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { fader, meter } };
        var tag = new TextBlock { Text = Loc.T("mixer.folderTag"), FontSize = 9, LetterSpacing = 0.8, Foreground = Palette.Faint };
        ToolTip.SetTip(tag, Loc.T("mixer.folderTag.tip"));
        var glyph = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var router = RouterOf(folder);
        var output = OutputSelect(router, mx.Refresh);
        output.Height = output.MinHeight = 22;
        mx.Refresh.Add(() => glyph.Foreground = Palette.Alpha(TimelineView.FolderColor(F()), F().Muted ? 0.5 : 1));
        // like a channel's head (StripHead): the glyph beside "CARPETA" and its output, or over the output when narrow
        var text = new StackPanel { Spacing = 2, Children = { tag, output } };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Height = StripHeadH };
        head.Children.Add(glyph);
        head.Children.Add(text);
        mx.Resize.Add(w =>
        {
            // its icon grows with the strip, like a channel's; "CARPETA" goes when it doesn't fit
            glyph.FontSize = Math.Clamp(Math.Round(10 + w * 0.045), 12, 17);
            bool alone = w < HeadNameW;
            tag.IsVisible = !alone;
            Grid.SetColumn(text, alone ? 0 : 1);
            Grid.SetColumnSpan(text, alone ? 2 : 1);
            Grid.SetColumnSpan(glyph, alone ? 2 : 1);
            glyph.HorizontalAlignment = alone ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            glyph.VerticalAlignment = alone ? VerticalAlignment.Top : VerticalAlignment.Center;
            text.VerticalAlignment = alone ? VerticalAlignment.Bottom : VerticalAlignment.Center;
            text.Margin = new Thickness(alone ? 0 : 7, 0, 0, 0);
        });
        var (controls, gr) = CompactChain(SlotsOf(folder), router, () => F().Pan, v => FolderChannelEdit(id, x => x.Pan = v, "pan"),
            StripToggle("M", Loc.T("folder.mute"), 26, () => F().Muted, true, () => FolderChannelEdit(id, x => x.Muted = !x.Muted, "muted")),
            StripToggle("S", Loc.T("folder.solo"), 26, () => F().Solo, false, () => FolderChannelEdit(id, x => x.Solo = !x.Solo, "solo")));
        mx.NodeMeters.Add((id, meter, gr));
        var content = new Grid();
        var layout = StripLayout(head, controls, faders, db);
        NarrowParts(layout, faders, fader, meter, db);
        content.Children.Add(layout);
        content.Children.Add(top);
        box.Child = content;
        return box;
    }

    // --- track presets ------------------------------------------------------------------------------

    void SavePreset(LiveTrack track, Control anchor, Point? at)
    {
        string name = track.Track.Name;
        AskName(anchor, at, Loc.T("preset.save"), name, Loc.T("preset.save.ok"), chosen =>
        {
            try
            {
                bool replaced = TrackPresets.All().Any(x => string.Equals(x.Name, chosen.Trim(), StringComparison.OrdinalIgnoreCase));
                TrackPresets.Save(TrackPresets.From(track.Track, chosen));
                Toast(Loc.T(replaced ? "preset.replaced" : "preset.saved", chosen));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Toast(Loc.T("preset.saveFail", e.Message));
            }
        }, exists: n => TrackPresets.All().Any(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The "Plantilla" submenu of the new-track menu: one entry per saved preset, each with its trash can to delete it.</summary>
    MenuItem PresetSubmenu(bool enabled)
    {
        var items = new List<object>();
        var presets = TrackPresets.All();
        foreach (var preset in presets)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), MinWidth = 200 };
            var badge = new TrackIconView { Icon = preset.Icon, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
                Foreground = preset.Color != null ? Palette.Brush(preset.Color) : Palette.Dim, IsVisible = preset.Icon != null };
            row.Children.Add(badge);
            var name = new TextBlock { Text = preset.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1); row.Children.Add(name);
            var delete = new Button { Classes = { "ghost" }, Content = new TextBlock { Text = "", Classes = { "icon" }, FontSize = 11 }, Width = 22, Height = 22, MinHeight = 22,
                Padding = new Thickness(0), Margin = new Thickness(12, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(delete, Loc.T("preset.delete.tip"));
            Grid.SetColumn(delete, 2); row.Children.Add(delete);
            var item = new MenuItem { Header = row, IsEnabled = enabled };
            ToolTip.SetTip(item, preset.Summary);
            ToolTip.SetShowDelay(item, (int)MenuTipDelay.TotalMilliseconds);
            item.Click += async (_, _) => await EditTimeline(p => TrackPresets.AddTo(p, preset, AudioOnly), "track-added");
            delete.Click += (_, e) =>
            {
                e.Handled = true;
                timelineMenu?.Close();
                try
                {
                    TrackPresets.Delete(preset.Id);
                    Status(Loc.T("preset.deleted", preset.Name));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Toast(Loc.T("preset.saveFail", ex.Message)); }
            };
            items.Add(item);
        }
        if (presets.Count == 0)
            items.Add(new MenuItem { IsEnabled = false, Header = new TextBlock { Text = Loc.T("preset.none"), FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 220 } });
        return new MenuItem { Header = MenuHeader(Loc.T("preset.menu"), new TextBlock { Text = "", Classes = { "icon" }, FontSize = 12 }), ItemsSource = items };
    }

    /// <summary>
    /// The track menu's "Usar plantilla": puts a saved preset's sound on this track, keeping its takes, name, look and
    /// routing (<see cref="TrackPresets.ApplyTo"/>). One undo step; the compressors are measured again on its takes.
    /// </summary>
    MenuItem ApplyPresetSubmenu(LiveTrack track, bool enabled)
    {
        var items = new List<object>();
        var presets = TrackPresets.All();
        foreach (var preset in presets)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, MinWidth = 180 };
            row.Children.Add(new TrackIconView { Icon = preset.Icon, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
                Foreground = preset.Color != null ? Palette.Brush(preset.Color) : Palette.Dim, IsVisible = preset.Icon != null });
            row.Children.Add(new TextBlock { Text = preset.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var item = new MenuItem { Header = row, IsEnabled = enabled };
            ToolTip.SetTip(item, preset.Summary);
            ToolTip.SetShowDelay(item, (int)MenuTipDelay.TotalMilliseconds);
            item.Click += (_, _) => ApplyPreset(track, preset);
            items.Add(item);
        }
        if (presets.Count == 0)
            items.Add(new MenuItem { IsEnabled = false, Header = new TextBlock { Text = Loc.T("preset.none"), FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 220 } });
        var menu = new MenuItem { Header = MenuHeader(Loc.T("preset.apply"), new TextBlock { Text = "", Classes = { "icon" }, FontSize = 12 }), ItemsSource = items };
        ToolTip.SetTip(menu, Loc.T("preset.apply.tip"));
        ToolTip.SetShowDelay(menu, (int)MenuTipDelay.TotalMilliseconds);
        return menu;
    }

    void ApplyPreset(LiveTrack track, TrackPreset preset)
    {
        if (store == null) return;
        int skipped = 0;
        EditTrack(track, x =>
        {
            skipped = TrackPresets.ApplyTo(store.Project, x.Track, preset);
            foreach (var s in x.Channel.Slots)
                if (s.Comp is { } c && c.Preset != "custom") x.RecalculateCompPreset(c);
        }, $"preset.{preset.Id}");
        RefreshMixer();
        RebuildInspectorSends(track);
        Toast(Loc.T("preset.applied", preset.Name, track.Track.Name) + (skipped > 0 ? " " + Loc.T("preset.applied.skipped", skipped) : ""));
    }

    // --- the folder in the inspector ----------------------------------------------------------------

    readonly StackPanel folderPanel = new() { Spacing = 12, IsVisible = false };
    readonly List<Action> folderRefresh = [];
    readonly TextBox folderName = new() { Classes = { "title" } };
    /// <summary>The folder's name row: where the arrow from its header points (#180, MainWindow.InspectorPointer.cs).</summary>
    readonly Grid folderHead = new() { ColumnDefinitions = new ColumnDefinitions("28,*,Auto,Auto") };

    /// <summary>What the inspector shows while a folder is selected: its name, color, output, sends and its actions.</summary>
    void InitFolderInspector()
    {
        var glyph = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 18 };
        var badge = new Button { Classes = { "ghost" }, Content = glyph, Width = 28, Height = 28, MinHeight = 28, Padding = new Thickness(0), CornerRadius = new CornerRadius(5),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(badge, Loc.T("folder.color.tip"));
        badge.Click += (_, _) => { if (SelectedFolder is { } f) ShowFolderLook(f, badge); };
        ToolTip.SetTip(folderName, Loc.T("folder.name.tip"));
        folderName.Margin = new Thickness(4, 0, 0, 0);
        folderName.TextChanged += (_, _) =>
        {
            if (syncing || SelectedFolder is not { } f) return;
            string name = folderName.Text?.Trim() ?? "";
            if (name.Length > 0 && name != f.Name) FolderEdit(f, x => x.Name = name, "name");
        };
        // the color square and, next to it, "give the tracks this color" (#145)
        var pickColor = ColorPickButton(() => SelectedFolder?.Color, hex => { if (SelectedFolder is { } f) FolderEdit(f, x => x.Color = hex, "color"); }, folderRefresh);
        var paint = FolderPaintButton(() => SelectedFolder, folderRefresh);
        var head = folderHead;
        head.Children.Add(badge);
        Grid.SetColumn(folderName, 1); head.Children.Add(folderName);
        Grid.SetColumn(pickColor, 2); head.Children.Add(pickColor);
        Grid.SetColumn(paint, 3); head.Children.Add(paint);
        folderRefresh.Add(() => paint.IsVisible = SelectedFolder is { Separator: false });
        var count = new TextBlock { FontSize = 11, Foreground = Palette.Faint, Margin = new Thickness(36, -6, 0, 0) };
        folderPanel.Children.Add(head);
        folderPanel.Children.Add(count);

        var card = new StackPanel { Spacing = 14 };
        // its output and sends go in the strip, as a track's (#149)
        card.Children.Add(Help(Loc.T("folder.route.hint")));
        folderPanel.Children.Add(new Border { Classes = { "section" }, Background = Palette.Panel, BorderThickness = new Thickness(0, 1), CornerRadius = new CornerRadius(0),
            Margin = new Thickness(-14, 4, -14, 0), Padding = new Thickness(16, 14, 16, 16), Child = card });

        var add = new Button { Classes = { "chip" }, Content = Loc.T("folder.newTrack"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, MinHeight = 32 };
        add.Click += async (_, _) =>
        {
            if (SelectedFolder is not { } f) return;
            string id = f.Id;
            await EditTimeline(p =>
            {
                string track = ClipEditing.AddEmpty(p, !AudioOnly);
                TrackFolders.MoveInto(p, track, id);
                return track;
            }, "track-added");
        };
        var remove = RemoveButton(Loc.T("folder.remove"));
        ToolTip.SetTip(remove, Loc.T("folder.remove.tip"));
        remove.Click += async (_, _) => { if (SelectedFolder is { } f) await RemoveFolder(f); };
        folderPanel.Children.Add(add);
        folderPanel.Children.Add(remove);

        folderRefresh.Add(() =>
        {
            if (SelectedFolder is not { } f) return;
            glyph.Foreground = Palette.Alpha(TimelineView.FolderColor(f), 1);
            if (!folderName.IsFocused || folderName.Text?.Trim() != f.Name) folderName.Text = f.Name;
            int n = tracks.Count(t => t.Track.FolderId == f.Id);
            count.Text = n == 0 ? Loc.T("folder.empty") : n == 1 ? Loc.T("folder.count.one") : Loc.T("folder.count", n);
        });
        Inspector.Children.Add(folderPanel);
    }
}
