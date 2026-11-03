using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using XtremeDaw.App.Controls;
using XtremeDaw.Audio.Vst;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Dialogs;

/// <summary>
/// "Plugins" (native only): the VST3 plugins of this computer, through <see cref="PluginScan"/> (the same list the
/// MIDI track's menu uses). The folders to look in (Windows' two, plus the ones the user adds), looking for new or
/// changed plugins or opening them all again (cancellable, never while recording), the list of what was found with a
/// filter and a search, where a plugin can be hidden from the menus or given a category of the user's, how the menus
/// group them (by maker, by category or one list, and the list here groups the same way), and the files that didn't
/// open, with why and a retry. Everything but the search box is rebuilt on <see cref="PluginScan.Changed"/>, only while the page is shown.
/// </summary>
public sealed partial class SettingsDialog
{
    readonly StackPanel pluginsPage = new();
    readonly StackPanel pluginFolders = new() { Spacing = 6 };
    readonly StackPanel pluginScanBox = new() { Spacing = 10 };
    readonly StackPanel pluginRows = new() { Spacing = 2 };
    readonly StackPanel pluginFailed = new() { Spacing = 14 };
    Border? pluginFailedCard;
    readonly TextBlock pluginCount = new() { FontSize = 11, Foreground = Palette.Faint, Classes = { "mono" }, VerticalAlignment = VerticalAlignment.Center };
    readonly List<ToggleButton> pluginFilterButtons = [];
    readonly TextBox pluginSearch = new() { Classes = { "field" }, Height = 32, FontSize = 12, Watermark = Loc.T("set.vst.search"), VerticalContentAlignment = VerticalAlignment.Center };
    /// <summary>"all", "instruments" or "effects".</summary>
    string pluginFilter = "all";
    readonly List<ToggleButton> pluginGroupingButtons = [];

    void BuildPlugins()
    {
        var add = Act("", Loc.T("set.vst.folders.add"), () => _ = AddPluginFolder());
        var folders = Section(Loc.T("set.vst.folders"), add);
        folders.Children.Add(pluginFolders);
        folders.Children.Add(Help(Loc.T("set.vst.folders.help")));
        pluginsPage.Children.Add(Card(folders));

        var scan = Section(Loc.T("set.vst.scan"));
        scan.Children.Add(pluginScanBox);
        pluginsPage.Children.Add(Card(scan));

        var list = Section(Loc.T("set.vst.list"), pluginCount);
        var groupings = new UniformGrid { Columns = 3 };
        foreach (var (id, text, tip) in new[]
        {
            (PluginCatalog.ByVendor, Loc.T("set.vst.group.vendor"), Loc.T("set.vst.group.vendor.tip")),
            (PluginCatalog.ByCategory, Loc.T("set.vst.group.category"), Loc.T("set.vst.group.category.tip")),
            (PluginCatalog.AsList, Loc.T("set.vst.group.list"), Loc.T("set.vst.group.list.tip")),
        })
        {
            var b = new ToggleButton { Content = text, Tag = id, Classes = { "seg" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => PluginScan.SetGrouping(id);
            pluginGroupingButtons.Add(b);
            groupings.Children.Add(b);
        }
        list.Children.Add(Field(Loc.T("set.vst.group"), new Border { Classes = { "seg" }, Child = groupings }));
        var filters = new UniformGrid { Columns = 3 };
        foreach (var (id, text) in new[] { ("all", Loc.T("set.vst.filter.all")), ("instruments", Loc.T("set.vst.filter.instruments")), ("effects", Loc.T("set.vst.filter.effects")) })
        {
            var b = new ToggleButton { Content = text, Tag = id, Classes = { "seg" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) => { pluginFilter = id; FillPluginRows(); };
            pluginFilterButtons.Add(b);
            filters.Children.Add(b);
        }
        var tools = new Grid { ColumnDefinitions = new ColumnDefinitions("*,200"), ColumnSpacing = 8 };
        tools.Children.Add(new Border { Classes = { "seg" }, Child = filters });
        Grid.SetColumn(pluginSearch, 1);
        tools.Children.Add(pluginSearch);
        pluginSearch.TextChanged += (_, _) => FillPluginRows();
        list.Children.Add(tools);
        list.Children.Add(Help(Loc.T("set.vst.list.help")));
        list.Children.Add(pluginRows);
        pluginsPage.Children.Add(Card(list));

        pluginFailedCard = Card(pluginFailed);
        pluginsPage.Children.Add(pluginFailedCard);

        PluginScan.Changed += () => { if (IsVisible && Body.Content == pluginsPage) RefreshPlugins(); };
    }

    void RefreshPlugins()
    {
        FillPluginFolders();
        FillPluginScan();
        // the list only changes when a scan ends: not rebuilt on every file a scan opens
        if (!PluginScan.Running) FillPluginRows();
        FillPluginFailed();
    }

    void FillPluginFolders()
    {
        pluginFolders.Children.Clear();
        var extra = PluginCatalog.ExtraFolders();
        foreach (var dir in PluginCatalog.Folders)
        {
            bool mine = extra.Contains(dir), exists = Directory.Exists(dir);
            var path = new TextBlock { Text = dir, FontSize = 12, Foreground = exists ? Palette.Dim : Palette.Faint, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(path, dir);
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            right.Children.Add(new TextBlock
            {
                Text = !exists ? Loc.T("set.vst.folder.missing") : mine ? Loc.T("set.vst.folder.mine") : Loc.T("set.vst.folder.windows"),
                FontSize = 11, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
            });
            var open = Act("", Loc.T("set.vst.folder.open"), () => Explorer.Open(dir));
            open.IsEnabled = exists;
            right.Children.Add(open);
            if (mine)
                right.Children.Add(Act("", Loc.T("set.vst.folder.remove"), () =>
                {
                    try { PluginCatalog.RemoveFolder(dir); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    PluginScan.FoldersChanged();
                }));
            pluginFolders.Children.Add(Row(path, right));
        }
    }

    async Task AddPluginFolder()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } sp) return;
        try
        {
            var picked = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("set.vst.folders.pick") });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is not { } dir) return;
            PluginCatalog.AddFolder(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        PluginScan.FoldersChanged();
        Focus();
    }

    void FillPluginScan()
    {
        pluginScanBox.Children.Clear();
        var plugins = PluginCatalog.Plugins(PluginScan.Entries);
        int instruments = plugins.Count(p => p.IsInstrument), failed = PluginScan.Entries.Count(e => e.Error != null);
        string status = PluginScan.Progress is { } p ? Loc.T("set.vst.opening", p.Name, p.N, p.Total)
            : PluginScan.Running ? Loc.T("set.vst.looking")
            : PluginScan.Entries.Count == 0 ? Loc.T("set.vst.never")
            : failed > 0 ? Loc.T("set.vst.summary.failed", instruments, plugins.Count - instruments, failed)
            : Loc.T("set.vst.summary", instruments, plugins.Count - instruments);
        pluginScanBox.Children.Add(new Border
        {
            Background = Palette.Raised, CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8),
            Child = new TextBlock { Text = status, FontSize = 12, Foreground = PluginScan.Running ? Palette.Accent : Palette.Fg, TextTrimming = TextTrimming.CharacterEllipsis },
        });

        bool host = VstHost.Find() != null, recording = busy();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (PluginScan.Running)
        {
            var cancel = new Button { Classes = { "btn", "sm", "outline" }, Content = Loc.T("common.cancel") };
            ToolTip.SetTip(cancel, Loc.T("set.vst.cancel.tip"));
            cancel.Click += (_, _) => PluginScan.Cancel();
            buttons.Children.Add(cancel);
        }
        else
        {
            var news = new Button { Classes = { "btn", "sm", "primary" }, Content = Loc.T("set.vst.scanNew"), IsEnabled = host && !recording };
            ToolTip.SetTip(news, Loc.T("set.vst.scanNew.tip"));
            news.Click += (_, _) => _ = PluginScan.Run();
            var all = new Button { Classes = { "btn", "sm", "outline" }, Content = Loc.T("set.vst.scanAll"), IsEnabled = host && !recording };
            ToolTip.SetTip(all, Loc.T("set.vst.scanAll.tip"));
            all.Click += (_, _) => _ = PluginScan.Run(all: true);
            buttons.Children.Add(news);
            buttons.Children.Add(all);
        }
        pluginScanBox.Children.Add(buttons);
        if (!host) pluginScanBox.Children.Add(Warning(Loc.T("vst.noHost")));
        else if (recording && !PluginScan.Running) pluginScanBox.Children.Add(Warning(Loc.T("set.vst.recording")));
        pluginScanBox.Children.Add(Help(Loc.T("set.vst.scan.help")));
    }

    void FillPluginRows()
    {
        foreach (var b in pluginFilterButtons) b.IsChecked = (string)b.Tag! == pluginFilter;
        string grouping = PluginCatalog.Grouping;
        foreach (var b in pluginGroupingButtons) b.IsChecked = (string)b.Tag! == grouping;
        pluginRows.Children.Clear();
        var all = PluginCatalog.Plugins(PluginScan.Entries);
        var hidden = PluginCatalog.Hidden();
        var custom = PluginCatalog.CustomCategories();
        string q = pluginSearch.Text?.Trim() ?? "";
        var shown = all.Where(p => pluginFilter switch { "instruments" => p.IsInstrument, "effects" => !p.IsInstrument, _ => true })
            .Where(p => q.Length == 0 || p.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || p.Vendor.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || PluginScan.CategoryName(PluginCatalog.Category(p, custom, all)).Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        pluginCount.Text = shown.Count == all.Count ? $"{all.Count}" : $"{shown.Count} / {all.Count}";
        if (shown.Count == 0)
        {
            pluginRows.Children.Add(Help(Loc.T(all.Count == 0 ? "set.vst.empty" : "set.vst.noMatch")));
            return;
        }
        // the list as the menus show it: under each maker or category, or one list
        var choices = CategoryChoices(all, custom);
        if (grouping == PluginCatalog.AsList)
        {
            foreach (var p in shown) pluginRows.Children.Add(PluginRow(p, !hidden.Contains(PluginCatalog.Key(p)), custom, choices, all));
            return;
        }
        foreach (var group in shown.GroupBy(p => PluginCatalog.GroupOf(p, grouping, custom, all) ?? "")
            .OrderBy(g => g.Key.Length == 0).ThenBy(g => PluginScan.GroupName(g.Key, grouping), StringComparer.CurrentCultureIgnoreCase))
        {
            pluginRows.Children.Add(new TextBlock
            {
                Text = $"{PluginScan.GroupName(group.Key, grouping).ToUpperInvariant()} ({group.Count()})", Classes = { "label" },
                Margin = new Thickness(0, pluginRows.Children.Count == 0 ? 4 : 14, 0, 4),
            });
            foreach (var p in group) pluginRows.Children.Add(PluginRow(p, !hidden.Contains(PluginCatalog.Key(p)), custom, choices, all));
        }
    }

    /// <summary>The categories a plugin can go in: the automatic ones and the ones the user made, by name as shown.</summary>
    static List<string> CategoryChoices(List<VstPluginInfo> all, Dictionary<string, string> custom) =>
    [
        .. PluginCatalog.AutoCategories.Concat(custom.Values).Concat(all.Select(p => PluginCatalog.AutoCategory(p, all)))
            .Where(c => c.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(PluginScan.CategoryName, StringComparer.CurrentCultureIgnoreCase),
    ];

    /// <summary>
    /// A plugin's category chip: the one it has (the user's, or the automatic one in faint), and on click every category
    /// to move it to, back to the automatic one, or a new one written right there.
    /// </summary>
    Button CategoryChip(VstPluginInfo p, Dictionary<string, string> custom, List<string> choices, List<VstPluginInfo> all)
    {
        bool mine = custom.TryGetValue(PluginCatalog.Key(p), out var own);
        string auto = PluginCatalog.AutoCategory(p, all), now = mine ? own! : auto;
        var chip = new Button
        {
            Classes = { "chip" }, Height = 26, MinHeight = 26, Width = 132, Padding = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        var label = new TextBlock
        {
            Text = now.Length > 0 ? PluginScan.CategoryName(now) : Loc.T("vst.cat.none"), FontSize = 11,
            Foreground = mine ? Palette.Fg : Palette.Faint, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        var chevron = new TextBlock { Classes = { "icon" }, Text = "\uE70D", FontSize = 8, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        Grid.SetColumn(chevron, 1);
        chip.Content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { label, chevron } };
        ToolTip.SetTip(chip, Loc.T(mine ? "set.vst.cat.mine.tip" : "set.vst.cat.auto.tip"));
        chip.Click += (_, _) =>
        {
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
            MenuItem Item(string text, bool on, Action pick)
            {
                var item = new MenuItem { Header = text, FontSize = 12, FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal };
                item.Click += (_, _) => pick();
                return item;
            }
            menu.Items.Add(Item(Loc.T("set.vst.cat.auto", auto.Length > 0 ? PluginScan.CategoryName(auto) : Loc.T("vst.cat.none")), !mine,
                () => PluginScan.SetCategory(p, null)));
            menu.Items.Add(new Separator());
            foreach (var c in choices)
                menu.Items.Add(Item(PluginScan.CategoryName(c), mine && c.Equals(own, StringComparison.CurrentCultureIgnoreCase), () => PluginScan.SetCategory(p, c)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(Loc.T("set.vst.cat.new"), false, () => AskCategory(chip, p)));
            menu.ShowAt(chip);
        };
        return chip;
    }

    /// <summary>A new category, written in a box under the chip: Enter gives it to the plugin, Esc leaves it as it was.</summary>
    void AskCategory(Control under, VstPluginInfo p)
    {
        var box = new TextBox { Classes = { "field" }, Width = 200, Height = 32, FontSize = 12, Watermark = Loc.T("set.vst.cat.name"), VerticalContentAlignment = VerticalAlignment.Center };
        var ok = new Button { Classes = { "btn", "sm", "primary" }, Content = Loc.T("common.ok") };
        var fly = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight, Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { box, ok } } };
        void Apply()
        {
            string name = box.Text?.Trim() ?? "";
            fly.Hide();
            if (name.Length > 0) PluginScan.SetCategory(p, name);
        }
        ok.Click += (_, _) => Apply();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; Apply(); }
            else if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; fly.Hide(); }
        };
        fly.Opened += (_, _) => box.Focus();
        fly.ShowAt(under);
    }

    Control PluginRow(VstPluginInfo p, bool visible, Dictionary<string, string> custom, List<string> choices, List<VstPluginInfo> all)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), ColumnSpacing = 10, MinHeight = 40, Opacity = visible ? 1 : 0.5 };
        var icon = new TextBlock { Classes = { "icon" }, Text = p.IsInstrument ? "" : "", FontSize = 14, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(icon, Loc.T(p.IsInstrument ? "set.vst.kind.instrument" : "set.vst.kind.effect"));
        g.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { icon, PluginThumbView.Row(p) } });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(new TextBlock { Text = p.Name, FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        string details = string.Join(" · ", new[] { p.FormatText, p.Vendor, p.Version, p.Categories.Replace("|", ", ") }.Where(s => s.Length > 0));
        text.Children.Add(new TextBlock { Text = details, FontSize = 11, Foreground = Palette.Faint, TextTrimming = TextTrimming.CharacterEllipsis });
        ToolTip.SetTip(text, p.Path);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        var chip = CategoryChip(p, custom, choices, all);
        Grid.SetColumn(chip, 2);
        g.Children.Add(chip);
        var where = Act("", Loc.T("set.vst.showFile"), () => Explorer.Select(p.Path));
        Grid.SetColumn(where, 3);
        g.Children.Add(where);
        var box = new CheckBox { IsChecked = visible, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
        ToolTip.SetTip(box, Loc.T("set.vst.visible.tip"));
        box.IsCheckedChanged += (_, _) =>
        {
            g.Opacity = box.IsChecked == true ? 1 : 0.5;
            PluginScan.SetHidden(p, box.IsChecked != true);
        };
        Grid.SetColumn(box, 4);
        g.Children.Add(box);
        return g;
    }

    void FillPluginFailed()
    {
        pluginFailed.Children.Clear();
        var failed = PluginScan.Entries.Where(e => e.Error != null).OrderBy(e => Path.GetFileName(e.Path), StringComparer.CurrentCultureIgnoreCase).ToList();
        if (pluginFailedCard != null) pluginFailedCard.IsVisible = failed.Count > 0;
        if (failed.Count == 0) return;
        var s = Section(Loc.T("set.vst.failed", failed.Count));
        s.Spacing = 6;
        s.Children.Add(Help(Loc.T("set.vst.failed.help")));
        foreach (var e in failed)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            text.Children.Add(new TextBlock { Text = Path.GetFileNameWithoutExtension(e.Path), FontSize = 12, Foreground = Palette.Dim, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = e.Error, FontSize = 11, Foreground = Palette.Faint, TextTrimming = TextTrimming.CharacterEllipsis });
            ToolTip.SetTip(text, $"{e.Path}\n{e.Error}");
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            right.Children.Add(Act("", Loc.T("set.vst.showFile"), () => Explorer.Select(e.Path)));
            var retry = Act("", Loc.T("set.vst.retry"), () => _ = PluginScan.Run(file: e.Path));
            retry.IsEnabled = !PluginScan.Running && !busy() && VstHost.Find() != null;
            right.Children.Add(retry);
            s.Children.Add(Row(text, right));
        }
        pluginFailed.Children.Add(s);
    }
}
