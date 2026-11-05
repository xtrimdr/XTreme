using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.App.Controls;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// One effect's controls, the same wherever it shows (asked by the user): its section in the inspector and its window
/// over the mixer, the timeline or the Mastering view all call <see cref="FxBody"/>. The knobs come from the effect's list
/// (<see cref="FxDefs"/>) and each kind adds its choices (presets, modes); a new native effect only needs its list.
/// The knobs share the width they get (<see cref="FxCellW"/>) and grow with it. The Mastering view's knobs are the same
/// cell (<see cref="FxCell"/>).
/// </summary>
public partial class MainWindow
{
    /// <summary>The widest a knob's cell wants to be and the narrowest it can be: how many fit in a row comes from these.</summary>
    const double FxCellW = 92, FxCellMinW = 66;
    /// <summary>A knob's size in a panel, from its smallest to what a cell from <see cref="FxBigCellW"/> gets.</summary>
    const double FxKnobW = 36, FxKnobBigW = 44, FxBigCellW = 76;

    /// <summary>A knob with its label on top and its value under it, in the effect panels and the Mastering view.
    /// What it does (<paramref name="tip"/>) shows over the label, never over the knob: there it would cover the drag.</summary>
    static StackPanel FxCell(string label, Control knob, TextBlock value, string? tip = null)
    {
        var name = new TextBlock { Text = label.ToUpperInvariant(), FontSize = 10, LetterSpacing = 0.8, Foreground = Palette.Faint, HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Background = Brushes.Transparent };
        ToolTip.SetTip(name, tip);
        return new() { Spacing = 5, Children = { name, knob, value } };
    }

    static TextBlock FxValue() => new() { Classes = { "mono" }, FontSize = 11, Foreground = Palette.Dim, HorizontalAlignment = HorizontalAlignment.Center };

    /// <summary>A value as its unit reads, by the same rules as the rest of the window.</summary>
    static string FxText(FxParam p, double v)
    {
        if (v <= p.OffBelow) return Loc.T(p.OffText ?? "master.off");
        if (p.StepName(v) is { } name) return Loc.T(name);
        return p.Unit switch
        {
            FxUnit.Percent => $"{v * 100:0}%",
            FxUnit.Db => FmtDb(v),
            FxUnit.Hz => $"{FmtHz(v)} Hz",
            FxUnit.Ms => v < 0.1 ? $"{v * 1000:0} µs" : v < 10 ? $"{v:0.0} ms" : $"{v:0} ms",
            FxUnit.Sec => $"{v:0.0} s",
            FxUnit.Ratio => p.Steps != null ? $"{v:0.#}:1" : $"{v:0.0}:1",
            FxUnit.Ceiling => $"{v:0.0} dBTP",
            FxUnit.Recorte => v <= 0 ? Loc.T("master.clip.auto") : $"{v:0.0} dB",
            FxUnit.Rate => $"{v:0.00} Hz",
            FxUnit.Count => $"{v:0}",
            FxUnit.Division => PumpParams.Names[Math.Clamp((int)Math.Round(v), 0, PumpParams.Names.Length - 1)],
            FxUnit.Signed => $"{v * 100:+0;-0;0}%",
            _ => $"{v:0.#} dB",
        };
    }

    /// <summary>An edit of one parameter: a compressor follows its preset or leaves it, as its knobs always did.</summary>
    void FxEdit(SlotOwner o, string id, FxParam p, double v) => o.Edit(slots =>
    {
        if (slots.Find(s => s.Id == id) is not { } s) return;
        if (s.Comp is { } before) o.CompEdited?.Invoke(before);
        p.Set(s, v);
        if (p.TurnsOn) s.On = true;
        if (s.Comp is { } c)
        {
            if (p.Manual) c.Preset = "custom";
            else if (p.Id == "amount" && c.Preset != "custom") o.Recalculate(c);
        }
    }, $"{id}.{p.Id}");

    /// <summary>One knob of the list; hidden and disabled as <paramref name="shown"/> and <paramref name="enabled"/> say.</summary>
    (Control Cell, Knob Knob) FxKnobCell(SlotOwner o, string id, FxParam p, List<Action> refresh, Func<bool>? enabled = null, Func<bool>? shown = null)
    {
        string label = Loc.T(p.Label);
        var knob = new Knob(FxKnobBigW, label) { Min = 0, Max = 1, ResetTo = p.ToKnob(p.Default), HorizontalAlignment = HorizontalAlignment.Center };
        knob.Changed += k => { if (!syncing) FxEdit(o, id, p, p.FromKnob(k)); };
        var value = FxValue();
        var cell = FxCell(label, knob, value, p.Tip != null ? Loc.T(p.Tip) : null);
        refresh.Add(() =>
        {
            if (o.Slot(id) is not { } s) return;
            double v = p.Get(s);
            knob.Value = p.ToKnob(v);
            value.Text = FxText(p, v);
            if (enabled != null) { bool e = enabled(); knob.IsEnabled = e; cell.Opacity = e ? 1 : 0.4; }
            if (shown != null) cell.IsVisible = shown();
        });
        AutomateOn(cell, () => TrackOf(o), AutoTarget.Fx(id, p.Id), k =>
        {
            knob.Value = k;
            value.Text = FxText(p, p.FromKnob(k));
        });
        return (cell, knob);
    }

    /// <summary>A grid of knobs that deals out its width (<see cref="KnobGrid"/>).</summary>
    static KnobGrid FxGrid(List<Knob> knobs, double top = 2) =>
        new(knobs, FxCellW, FxCellMinW, FxBigCellW, FxKnobW, FxKnobBigW) { Margin = new Thickness(0, top, 0, 0) };

    /// <summary>A segmented choice (the modes of a saturation, a gate or a delay) that fills the width it has.</summary>
    Control FxSegments((string Id, string Label, string? Tip)[] items, Func<string> get, Action<string> pick, List<Action> refresh)
    {
        var row = new UniformGrid { Rows = 1 };
        foreach (var (id, label, tip) in items)
        {
            var b = new ToggleButton { Content = label, Classes = { "seg" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Focusable = false };
            if (tip != null) ToolTip.SetTip(b, tip);
            b.Click += (_, _) => pick(id);
            refresh.Add(() => b.IsChecked = get() == id);
            row.Children.Add(b);
        }
        return new Border { Classes = { "seg" }, Child = row };
    }

    /// <summary>
    /// The panel of a native effect, into <paramref name="body"/>: its choices, then its knobs and the ones every effect
    /// has. <paramref name="onEq"/> gets an EQ's editor (the inspector feeds the first one its live spectrum).
    /// <paramref name="full"/>: for an effect with two sizes (<see cref="FxDefs.Layout"/>), its compact (false) or full
    /// (true) view; null = the single view every effect had, with its fine controls behind a button.
    /// </summary>
    void FxBody(StackPanel body, SlotOwner o, InsertSlot slot, List<Action> refresh, Action<EqEditor>? onEq = null, string[]? hide = null, bool? full = null)
    {
        string id = slot.Id, kind = slot.Type;
        if (kind == TrackInserts.Pump)
        {
            body.Children.Add(PresetBar(o, id, kind, refresh, []));
            PumpBody(body, o, id, refresh);
            return;
        }
        var knobs = new List<Knob>();
        var grid = FxGrid(knobs);
        void Add(FxParam p, Panel to, List<Knob> list, Func<bool>? enabled = null, Func<bool>? shown = null)
        {
            var (cell, knob) = FxKnobCell(o, id, p, refresh, enabled, shown);
            to.Children.Add(cell);
            list.Add(knob);
        }
        switch (kind)
        {
            case InsertSlot.SatKind:
                body.Children.Add(PresetBar(o, id, kind, refresh, []));
                SatSettings S() => o.Slot(id)?.Sat ?? GoneSat;
                body.Children.Add(FxSegments([("tube", Loc.T("insp.sat.tube"), Loc.T("insp.sat.tube.hint")), ("tape", Loc.T("insp.sat.tape"), Loc.T("insp.sat.tape.hint"))],
                    () => S().Mode, m => SlotEdit(o, id, s => { s.Sat!.Mode = m; s.Sat.On = true; }, "sat.mode"), refresh));
                break;
            case InsertSlot.GateKind:
                body.Children.Add(PresetBar(o, id, kind, refresh, []));
                GateSettings G() => o.Slot(id)?.Gate ?? GoneGate;
                body.Children.Add(FxSegments(GateModes.Select(m => (m, Loc.T("gate.mode." + m), (string?)Loc.T($"gate.mode.{m}.hint"))).ToArray(),
                    () => G().Mode, m => SlotEdit(o, id, s => { s.Gate!.Mode = m; s.Gate.On = true; }, "gate.mode"), refresh));
                break;
            case InsertSlot.CompKind:
                CompChoices(body, o, id, refresh);
                break;
            case InsertSlot.ReverbKind:
                body.Children.Add(PresetBar(o, id, kind, refresh, ReverbPresetItems(o, id)));
                break;
            case InsertSlot.DelayKind:
                DelayChoices(body, o, id, refresh);
                break;
            case InsertSlot.EqKind:
                var bigEq = InspectorIcon("", Loc.T("eq.big.tip"), () => ShowEqBig(o, id, o.OwnerId == sel?.Track.Id));
                body.Children.Add(PresetBar(o, id, kind, refresh, EqPresetItems(o, id), [bigEq]));
                var eq = new EqEditor { Live = () => LiveEq(o, id), ShowsPresets = false };
                onEq?.Invoke(eq);
                eq.Changed += value => SlotEdit(o, id, s => s.Eq = value, "eq");
                eq.BigRequested += () => ShowEqBig(o, id, o.OwnerId == sel?.Track.Id);
                refresh.Add(() => eq.Set(o.Slot(id)?.Eq ?? GoneEq));
                body.Children.Add(eq);
                break;
            default:
                body.Children.Add(PresetBar(o, id, kind, refresh, []));
                break;
        }

        if (full is { } big && FxDefs.Layout(kind) is { } layout)
        {
            FxSized(body, o, id, kind, refresh, layout, big);
            return;
        }

        if (TrackInserts.HasCompGraph(kind)) body.Children.Add(CompGraphOf(o, id, refresh));
        if (TrackInserts.TakesKey(kind)) body.Children.Add(KeyChoice(o, id, refresh));

        bool Manual() => o.Slot(id)?.Comp?.Preset == "custom";
        bool Free() => o.Slot(id)?.Delay?.Sync != true;
        var all = FxDefs.Params(kind);
        Func<bool>? Shown(FxParam p) => kind == InsertSlot.DelayKind && p.Id == "time" ? Free : null;
        foreach (var p in all.Where(p => !p.Fine && hide?.Contains(p.Id) != true))
            Add(p, grid, knobs, kind == InsertSlot.CompKind && p.Id == "amount" ? () => !Manual() : null, Shown(p));
        foreach (var p in FxDefs.Common(kind)) Add(p, grid, knobs);
        body.Children.Add(grid);

        // the fine controls wait behind a button (asked by the user: the simple way stays in front), open when set by hand
        var fine = all.Where(p => p.Fine).ToArray();
        if (fine.Length == 0) return;
        var fineKnobs = new List<Knob>();
        var fineGrid = FxGrid(fineKnobs, 6);
        foreach (var p in fine) Add(p, fineGrid, fineKnobs, shown: Shown(p));
        fineGrid.IsVisible = Manual();
        var button = new Button { Classes = { "ghost" }, FontSize = 11, Padding = new Thickness(8, 6), Focusable = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        void Label() => button.Content = (fineGrid.IsVisible ? "▾ " : "▸ ") + Loc.T("insp.fine");
        Label();
        button.Click += (_, _) => { fineGrid.IsVisible = !fineGrid.IsVisible; Label(); };
        body.Children.Add(button);
        body.Children.Add(fineGrid);
    }

    /// <summary>
    /// The compact or full view of an effect that has both (asked by the user, #140): compact, its main knobs (and a
    /// compressor's graph); full, every control in inner panels, row by row, each panel as wide as its share. Nothing
    /// waits behind a "fine controls" button in either: what isn't in the compact view is one click away, in the full one.
    /// </summary>
    void FxSized(StackPanel body, SlotOwner o, string id, string kind, List<Action> refresh, FxLayout layout, bool full)
    {
        FxParam[] all = [.. FxDefs.Params(kind), .. FxDefs.Common(kind)];
        bool Manual() => o.Slot(id)?.Comp?.Preset == "custom";
        KnobGrid Knobs(IEnumerable<string> ids, double top)
        {
            var knobs = new List<Knob>();
            var grid = FxGrid(knobs, top);
            foreach (string pid in ids)
            {
                if (Array.Find(all, p => p.Id == pid) is not { } p) continue;
                var (cell, knob) = FxKnobCell(o, id, p, refresh, kind == InsertSlot.CompKind && p.Id == "amount" ? () => !Manual() : null);
                grid.Children.Add(cell);
                knobs.Add(knob);
            }
            return grid;
        }
        Control? Part(string part) => part switch
        {
            FxDefs.GraphPart when TrackInserts.HasCompGraph(kind) => CompGraphOf(o, id, refresh),
            FxDefs.KeyPart when TrackInserts.TakesKey(kind) => KeyChoice(o, id, refresh, labeled: false),
            _ => null,
        };

        if (!full)
        {
            if (Part(FxDefs.GraphPart) is { } graph) body.Children.Add(graph);
            body.Children.Add(Knobs(layout.Compact, 2));
            return;
        }
        foreach (var row in layout.Rows)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", row.Select(g => g.Weight.ToString(CultureInfo.InvariantCulture) + "*"))) };
            for (int i = 0; i < row.Length; i++)
            {
                var g = row[i];
                var stack = new StackPanel { Spacing = 6, Children = { new TextBlock { Classes = { "label" }, Text = Loc.T(g.Label) } } };
                foreach (string part in g.Ids.Where(x => x.StartsWith('@')))
                    if (Part(part) is { } c) stack.Children.Add(c);
                string[] knobIds = [.. g.Ids.Where(x => !x.StartsWith('@'))];
                if (knobIds.Length > 0) stack.Children.Add(Knobs(knobIds, 0));
                var panel = new Border { Child = stack, Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                    BorderBrush = Palette.Line, Background = Palette.Alpha(Palette.AppColor, FxInnerAlpha), Margin = new Thickness(i == 0 ? 0 : FxInnerGap, 0, 0, 0) };
                Grid.SetColumn(panel, i);
                grid.Children.Add(panel);
            }
            body.Children.Add(grid);
        }
    }

    /// <summary>An inner panel of a full view: a step down from the effect's box, and the room between two of them.</summary>
    const double FxInnerAlpha = 0.35, FxInnerGap = 8;

    /// <summary>
    /// What a compressor, a gate or the pump listens to (its sidechain): its own input or another track, by name. A track that
    /// was removed shows as such and it listens to its own input meanwhile (an undo brings it back).
    /// </summary>
    Control KeyChoice(SlotOwner o, string id, List<Action> refresh, bool labeled = true)
    {
        var pick = StripSelect([]);
        ToolTip.SetTip(pick, Loc.T("fx.key.tip"));
        var sources = new List<string>();
        pick.Selected += i => SlotEdit(o, id, s => s.Sidechain = i <= 0 || i > sources.Count ? null : sources[i - 1], "sidechain");
        refresh.Add(() =>
        {
            sources.Clear();
            var names = new List<string> { Loc.T("fx.key.own") };
            foreach (var t in store?.Project.Tracks ?? [])
            {
                if (t.Id == o.OwnerId) continue;
                sources.Add(t.Id);
                names.Add(t.Name);
            }
            pick.SetItems([.. names]);
            string? key = o.Slot(id)?.Sidechain;
            int at = key == null ? 0 : sources.IndexOf(key) + 1;
            if (key != null && at == 0) pick.ShowNone(Loc.T("fx.key.gone"));
            else pick.SelectedIndex = at;
        });
        // in a full view's Sidechain panel the panel's title says it already
        var label = new TextBlock { Text = Loc.T("fx.key"), Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), IsVisible = labeled };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(label);
        Grid.SetColumn(pick, 1);
        row.Children.Add(pick);
        return row;
    }

    /// <summary>Whether a slot listens to a track that is in the project (one that was removed leaves it on its own input).</summary>
    bool Keyed(InsertSlot s) => s.Sidechain is { } k && store?.Project.Tracks.Exists(t => t.Id == k) == true;

    /// <summary>
    /// A compressor's graph (asked by the user, for every compressor): the running effect when it is in the engine, and the
    /// curve from the slot's settings when it isn't, rebuilt on every edit.
    /// </summary>
    CompGraph CompGraphOf(SlotOwner o, string id, List<Action> refresh)
    {
        var graph = new CompGraph { Live = () => (o.Insert(id) is FxShell s ? s.Inner : o.Insert(id)) as ICompGraph, Margin = new Thickness(0, 2, 0, 2) };
        refresh.Add(() =>
        {
            if (o.Slot(id) is { } slot && ChannelDsp.Specs([slot], 0) is [var spec])
                graph.Offline = TrackInserts.CurveOf(spec, graph.Offline, Audio.PlaybackEngine.SampleRate);
            graph.InvalidateVisual();
        });
        return graph;
    }

    static EqInsert? InsertEq(ITrackInsert? insert) => insert is FxShell shell ? shell.Inner as EqInsert : null;

    EqInsert? LiveEq(SlotOwner o, string id) => InsertEq(o.Insert(id));

    /// <summary>The EQ open in its big window, when it is the one the live spectrum feeds.</summary>
    EqEditor? eqBig;

    /// <summary>What the big window takes around its graph (the band's controls), for the graph to get the rest of its height.</summary>
    const double EqBigChrome = 330;

    /// <summary>
    /// An EQ's big window (asked by the user, for finer control): the same editor with a graph that follows the window. It
    /// edits the same slot, follows it (an undo, the small editor) and closes if the slot goes.
    /// <paramref name="spectrum"/>: it is the selected track's first EQ, the one with the live spectrum.
    /// </summary>
    void ShowEqBig(SlotOwner o, string id, bool spectrum)
    {
        if (o.Slot(id)?.Eq is not { } start) return;
        var editor = new EqEditor(big: true) { Live = () => LiveEq(o, id), Follow = () => o.Slot(id)?.Eq, Margin = new Thickness(16) };
        editor.Set(start);
        editor.Changed += value =>
        {
            SlotEdit(o, id, s => s.Eq = value, "eq");
            if (o.Slot(id)?.Eq is { } now) editor.Set(now);
        };
        var window = new Window
        {
            Title = Loc.T("eq.big.title"), Width = 980, Height = 680, MinWidth = 640, MinHeight = 520, Background = Palette.App,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = editor, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        window.SizeChanged += (_, e) => editor.GraphHeight = Math.Max(EqEditor.BigGraphMinH, e.NewSize.Height - EqBigChrome);
        editor.Gone += window.Close;
        if (spectrum) eqBig = editor;
        window.Closed += (_, _) => { if (eqBig == editor) eqBig = null; };
        window.Show(this);
    }

    /// <summary>
    /// One entry of an effect's preset menu (#141): its name, hint and category, what choosing it does to the slot and
    /// whether the slot is on it now. <see cref="Mine"/>: a preset the user saved.
    /// </summary>
    sealed record FxPresetItem(string Name, string? Hint, string? Group, Action Pick, Func<InsertSlot, bool> Is, FxPreset? Mine = null);

    /// <summary>The user's saved presets, read once and again after each save or delete (the bar refreshes on every edit).</summary>
    List<FxPreset>? userFxPresets;

    List<FxPreset> UserFxPresets() => userFxPresets ??= FxPresets.All();

    /// <summary>An effect's Default here: a new slot of its kind, started as one added to this owner (<see cref="StartSlot"/>).</summary>
    InsertSlot DefaultSlot(SlotOwner o, string kind)
    {
        var fresh = InsertSlot.New(kind);
        StartSlot(o, fresh);
        return fresh;
    }

    /// <summary>
    /// The preset bar every native effect has (asked by the user, #141): Default first, then the effect's own presets by
    /// category and the user's ("Mis presets"), and "Personalizado" (Manual on a compressor) while it is on none. Next
    /// to it, the effect's own buttons (<paramref name="extra"/>), then save the settings as a preset and delete the
    /// saved one it is on. The user's presets live in the machine (<see cref="FxPresets"/>), for every project.
    /// </summary>
    Control PresetBar(SlotOwner o, string id, string kind, List<Action> refresh, FxPresetItem[] factory, Control[]? extra = null)
    {
        bool comp = kind == InsertSlot.CompKind;
        var select = StripSelect([]);
        var items = new List<FxPresetItem>();
        var defaultItem = new FxPresetItem(Loc.T("fx.preset.default"), Loc.T("fx.preset.default.hint"), null,
            () => SlotEdit(o, id, s => FxPresets.Apply(s, DefaultSlot(o, kind)), "preset"),
            s => comp ? s.Comp?.Preset == CompPresets.DefaultId : FxPresets.Same(s, DefaultSlot(o, kind)));
        select.Selected += i =>
        {
            if (i >= 0 && i < items.Count) items[i].Pick();
        };
        var save = InspectorIcon("", Loc.T("fx.preset.save.tip"), () => { });
        save.Click += (_, _) =>
        {
            if (o.Slot(id) is null) return;
            string initial = items.ElementAtOrDefault(select.SelectedIndex)?.Mine?.Name ?? "";
            AskName(save, null, Loc.T("fx.preset.save"), initial, Loc.T("preset.save.ok"), name =>
            {
                if (o.Slot(id) is not { } now) return;
                try
                {
                    bool replaced = FxPresets.Exists(kind, name);
                    var saved = FxPresets.Save(now, name);
                    userFxPresets = null;
                    Toast(Loc.T(replaced ? "fx.preset.replaced" : "fx.preset.saved", saved.Name));
                    RefreshPresetBars();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    Toast(Loc.T("fx.preset.saveFail", e.Message));
                }
            }, exists: n => FxPresets.Exists(kind, n));
        };
        var delete = InspectorIcon("", Loc.T("fx.preset.delete.tip"), () => { });
        delete.Click += (_, _) =>
        {
            if (items.ElementAtOrDefault(select.SelectedIndex)?.Mine is not { } mine) return;
            try
            {
                FxPresets.Delete(mine.Id);
                userFxPresets = null;
                Toast(Loc.T("fx.preset.deleted", mine.Name));
                RefreshPresetBars();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Toast(Loc.T("fx.preset.saveFail", e.Message));
            }
        };
        refresh.Add(() =>
        {
            if (o.Slot(id) is not { } s) return;
            string mineGroup = Loc.T("fx.preset.mine");
            var mine = UserFxPresets().Where(p => p.Kind == kind).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => new FxPresetItem(p.Name, null, mineGroup, () => SlotEdit(o, id, x => FxPresets.Apply(x, p.Slot), "preset"), x => FxPresets.Same(x, p.Slot), p));
            items.Clear();
            items.Add(defaultItem);
            items.AddRange(factory);
            items.AddRange(mine);
            // a saved preset wins over the factory one it was made from; on none, the bar says so
            int at = items.FindIndex(x => x.Mine != null && x.Is(s));
            if (at < 0) at = items.FindIndex(x => x.Mine == null && x.Is(s));
            if (at < 0)
            {
                items.Add(new FxPresetItem(Loc.T(comp ? "insp.comp.manual" : "mix.custom.m"), Loc.T(comp ? "mix.comp.custom.tip" : "fx.preset.custom.tip"), null, () => { }, _ => true));
                at = items.Count - 1;
            }
            select.SetItems([.. items.Select(x => x.Name)], [.. items.Select(x => x.Hint)], [.. items.Select(x => x.Group)]);
            select.SelectedIndex = at;
            ToolTip.SetTip(select, items[at].Hint);
            delete.IsVisible = items[at].Mine != null;
        });
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*" + string.Concat(Enumerable.Repeat(",Auto", (extra?.Length ?? 0) + 2))) };
        row.Children.Add(select);
        int col = 1;
        foreach (var c in (Control[])[.. extra ?? [], save, delete])
        {
            c.Margin = new Thickness(4, 0, 0, 0);
            Grid.SetColumn(c, col++);
            row.Children.Add(c);
        }
        presetBarRefresh.Add(new(refresh));
        return row;
    }

    /// <summary>The open panels with a preset bar: a save or a delete reaches all of them (the list is the machine's).</summary>
    readonly List<WeakReference<List<Action>>> presetBarRefresh = [];

    void RefreshPresetBars()
    {
        bool was = syncing;
        syncing = true;
        try
        {
            presetBarRefresh.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var w in presetBarRefresh.ToArray())
                if (w.TryGetTarget(out var list)) foreach (var r in list.ToArray()) r();
        }
        finally { syncing = was; }
    }

    /// <summary>The compressor's own presets, by category (its Default goes first, outside them).</summary>
    FxPresetItem[] CompPresetItems(SlotOwner o, string id)
    {
        string Group(CompPresetDef p) => Loc.Or("insp.comp.group." + p.Group, Array.Find(CompPresets.Groups, g => g.Id == p.Group)?.Label ?? p.Group);
        return [.. CompPresets.All.Where(p => p.Id != CompPresets.DefaultId).Select(p => new FxPresetItem(CompPresetName(p), CompPresetHint(p), Group(p),
            () => StripComp(o, id, c => { c.Preset = p.Id; c.On = true; }, "preset"), s => s.Comp?.Preset == p.Id))];
    }

    /// <summary>The EQ's own presets ("Plano" is its Default).</summary>
    FxPresetItem[] EqPresetItems(SlotOwner o, string id) =>
    [
        .. TrackEq.Presets.Select((p, i) => (p, i)).Where(x => x.p.Id != "flat").Select(x => new FxPresetItem(Loc.Or("mix.eq." + x.p.Id, x.p.Label),
            Loc.Or("mix.eq." + x.p.Id + ".hint", x.p.Hint), null, () => SlotEdit(o, id, s => s.Eq = EqEditor.FromPreset(x.i, s.Eq ?? new()), "eq.preset"),
            s => s.Eq != null && EqEditor.PresetIndex(s.Eq) == x.i)),
    ];

    /// <summary>The reverb's own presets.</summary>
    FxPresetItem[] ReverbPresetItems(SlotOwner o, string id) =>
    [
        .. ReverbBus.Presets.Select(x => new FxPresetItem(BusPresetName("reverb", x.Label), BusPresetHint("reverb", x.Label, x.Hint), null, () => SlotEdit(o, id, s =>
        {
            var r = s.Reverb!;
            var p = ReverbBus.ApplyPreset(ToParams(r), x.Label);
            r.Decay = p.Decay; r.PreDelayMs = p.PreDelayMs; r.Tone = p.Tone; r.LowCut = p.LowCut; r.Width = p.Width;
        }, "reverb.preset"), s => s.Reverb != null && ReverbBus.PresetOf(ToParams(s.Reverb)) == x.Label)),
    ];

    /// <summary>The delay's own presets.</summary>
    FxPresetItem[] DelayPresetItems(SlotOwner o, string id) =>
    [
        .. DelayBus.Presets.Select(x => new FxPresetItem(BusPresetName("delay", x.Label), BusPresetHint("delay", x.Label, x.Hint), null, () => SlotEdit(o, id, s =>
        {
            var d = s.Delay!;
            var p = DelayBus.ApplyPreset(ToParams(d), x.Label);
            d.Mode = DelayParams.ModeToString(p.Mode); d.Sync = p.Sync; d.Division = p.Division; d.TimeMs = p.TimeMs;
            d.Feedback = p.Feedback; d.Tone = p.Tone; d.LowCut = p.LowCut; d.Width = p.Width; d.Duck = p.Duck;
        }, "delay.preset"), s => s.Delay != null && DelayBus.PresetOf(ToParams(s.Delay)) == x.Label)),
    ];

    /// <summary>The compressor's choices: its presets, measuring the take and peak limiting (its Default is the reset).</summary>
    void CompChoices(StackPanel body, SlotOwner o, string id, List<Action> refresh)
    {
        CompSettings C() => o.Slot(id)?.Comp ?? GoneComp;
        // the take goes off the UI thread: a long one takes a moment the first time, and then the result is kept
        var analyze = new Button { Classes = { "chip" }, Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12 }, Height = 30, MinHeight = 30, Padding = new Thickness(6, 0), Focusable = false };
        ToolTip.SetShowOnDisabled(analyze, true);
        analyze.Click += async (_, _) =>
        {
            if (o.Take() is not { } take) return;
            analyze.IsEnabled = false;
            var env = await Task.Run(() => take.Envelope);
            analyze.IsEnabled = true;
            SlotEdit(o, id, s =>
            {
                if (s.Comp is not { } c) return;
                o.CompEdited?.Invoke(c);
                ChannelDsp.AnalyzeComp(c, env);
            }, $"comp.analyze.{++slotEdits}");
        };
        var lim = new Button { Content = "LIM", Classes = { "chip", "mono" }, FontSize = 10, FontWeight = FontWeight.Bold, Height = 30, MinHeight = 30, Padding = new Thickness(6, 0), Focusable = false };
        lim.Click += (_, _) => StripComp(o, id, c => c.Limit = !c.Limit, "limit");
        refresh.Add(() =>
        {
            var c = C();
            bool custom = c.Preset == "custom";
            bool take = o.Take() != null;
            analyze.IsEnabled = take && !custom;
            analyze.Classes.Set("on", c.Analyzed && !custom);
            ToolTip.SetTip(analyze, Loc.T(custom ? "insp.comp.analyze.manual" : !take ? "insp.comp.analyze.none" : c.Analyzed ? "insp.comp.analyze.on" : "insp.comp.analyze.tip"));
            lim.Classes.Set("on", c.Limit);
            ToolTip.SetTip(lim, Loc.T(c.Limit ? "mix.lim.on" : "mix.lim.off"));
        });
        body.Children.Add(PresetBar(o, id, InsertSlot.CompKind, refresh, CompPresetItems(o, id), [analyze, lim]));
    }

    /// <summary>The delay's choices: its preset, stereo or ping-pong, and the note value (or "Libre (ms)").</summary>
    void DelayChoices(StackPanel body, SlotOwner o, string id, List<Action> refresh)
    {
        DelaySettings V() => o.Slot(id)?.Delay ?? GoneDelay;
        double Bpm() => store?.Project.Settings.Bpm ?? 110;
        body.Children.Add(PresetBar(o, id, InsertSlot.DelayKind, refresh, DelayPresetItems(o, id)));
        body.Children.Add(FxSegments([("stereo", Loc.T("mix.delay.stereo.short"), Loc.T("mix.delay.stereo.tip")), ("pingpong", Loc.T("mix.delay.pingpong.short"), Loc.T("mix.delay.pingpong.tip"))],
            () => V().Mode, m => SlotEdit(o, id, s => s.Delay!.Mode = m, "delay.mode"), refresh));
        var division = StripSelect(DelayBus.Divisions.Select(d => Loc.Or("mix.div." + d.Id, d.Label)).Append(Loc.T("mix.delay.free")).ToArray());
        division.Selected += i =>
        {
            if (i == DelayBus.Divisions.Length)
                SlotEdit(o, id, s => { var d = s.Delay!; d.TimeMs = Math.Round(DelayBus.DelayTimeSec(ToParams(d), Bpm()) * 1000); d.Sync = false; }, "delay.sync");
            else
            {
                string div = DelayBus.Divisions[i].Id;
                SlotEdit(o, id, s => { s.Delay!.Sync = true; s.Delay.Division = div; }, "delay.division");
            }
        };
        refresh.Add(() =>
        {
            var d = V();
            division.SelectedIndex = d.Sync ? Math.Max(0, Array.FindIndex(DelayBus.Divisions, x => x.Id == d.Division)) : DelayBus.Divisions.Length;
            ToolTip.SetTip(division, Loc.T("mix.delay.time.tip", $"{Math.Round(DelayBus.DelayTimeSec(ToParams(d), Bpm()) * 1000):0}", d.Sync ? Loc.T("mix.delay.at", $"{Bpm():0.##}") : ""));
        });
        body.Children.Add(division);
    }

    /// <summary>
    /// The master's chain (<see cref="MasterSettings.Slots"/>), for the panels: the same list in its strip and in the
    /// Mastering view. A first effect turns the master on, and its "+ Efecto" also offers the whole chain.
    /// </summary>
    SlotOwner MasterSlots() => new()
    {
        OwnerId = MasterOwner,
        Slots = () => MasterSet.Slots,
        Edit = (change, key) => MasterEdit(m =>
        {
            int before = m.Slots.Count;
            change(m.Slots);
            if (m.Slots.Count > before) m.On = true;
        }, key),
        Recalculate = ChannelDsp.RecalculateComp,
        Take = () => BusTake(MasterOwner),
        Insert = engine.MasterInsert,
        AddTip = Loc.T("mix.slot.add.master.tip"),
        Master = true,
    };

    const string MasterOwner = "master";

    /// <summary>"Cadena completa": the stages missing from the list, at its end (one undo step).</summary>
    void AddMasterChain()
    {
        MasterEdit(m => ChannelInserts.AddMasterChain(m), "chain." + ++slotEdits);
    }

}
