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

    /// <summary>A knob with its label on top and its value under it, in the effect panels and the Mastering view.</summary>
    static StackPanel FxCell(string label, Control knob, TextBlock value) => new()
    {
        Spacing = 5,
        Children =
        {
            new TextBlock { Text = label.ToUpperInvariant(), FontSize = 10, LetterSpacing = 0.8, Foreground = Palette.Faint, HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis },
            knob, value,
        },
    };

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
        var knob = new Knob(FxKnobBigW) { Min = 0, Max = 1, ResetTo = p.ToKnob(p.Default), HorizontalAlignment = HorizontalAlignment.Center };
        knob.Changed += k => { if (!syncing) FxEdit(o, id, p, p.FromKnob(k)); };
        var value = FxValue();
        string label = Loc.T(p.Label);
        string tip = p.Tip != null ? Loc.T(p.Tip) : label;
        var cell = FxCell(label, knob, value);
        refresh.Add(() =>
        {
            if (o.Slot(id) is not { } s) return;
            double v = p.Get(s);
            knob.Value = p.ToKnob(v);
            value.Text = FxText(p, v);
            knob.Tip = $"{label}: {value.Text}" + (tip == label ? "" : $"\n{tip}");
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

    /// <summary>
    /// A grid of knobs that deals out its width: as many columns as fit, each knob growing with its cell, so the same
    /// panel works in a narrow window or a wide inspector.
    /// </summary>
    static UniformGrid FxGrid(List<Knob> knobs, double top = 2)
    {
        var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, top, 0, 0) };
        grid.SizeChanged += (_, e) =>
        {
            double w = e.NewSize.Width;
            int visible = Math.Max(1, grid.Children.Count(c => c.IsVisible));
            int cols = Math.Clamp((int)Math.Ceiling(w / FxCellW), 1, Math.Max(1, (int)(w / FxCellMinW)));
            cols = Math.Min(cols, visible);
            if (cols != grid.Columns) grid.Columns = cols;
            double size = w / Math.Max(1, grid.Columns) >= FxBigCellW ? FxKnobBigW : FxKnobW;
            foreach (var k in knobs) k.Resize(size);
        };
        return grid;
    }

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
    /// </summary>
    void FxBody(StackPanel body, SlotOwner o, InsertSlot slot, List<Action> refresh, Action<EqEditor>? onEq = null, string[]? hide = null)
    {
        string id = slot.Id, kind = slot.Type;
        if (kind == TrackInserts.Pump)
        {
            PumpBody(body, o, id, refresh);
            return;
        }
        var knobs = new List<Knob>();
        var grid = FxGrid(knobs);
        void Add(FxParam p, UniformGrid to, List<Knob> list, Func<bool>? enabled = null, Func<bool>? shown = null)
        {
            var (cell, knob) = FxKnobCell(o, id, p, refresh, enabled, shown);
            to.Children.Add(cell);
            list.Add(knob);
        }
        switch (kind)
        {
            case InsertSlot.SatKind:
                SatSettings S() => o.Slot(id)?.Sat ?? GoneSat;
                body.Children.Add(FxSegments([("tube", Loc.T("insp.sat.tube"), Loc.T("insp.sat.tube.hint")), ("tape", Loc.T("insp.sat.tape"), Loc.T("insp.sat.tape.hint"))],
                    () => S().Mode, m => SlotEdit(o, id, s => { s.Sat!.Mode = m; s.Sat.On = true; }, "sat.mode"), refresh));
                break;
            case InsertSlot.GateKind:
                GateSettings G() => o.Slot(id)?.Gate ?? GoneGate;
                body.Children.Add(FxSegments(GateModes.Select(m => (m, Loc.T("gate.mode." + m), (string?)Loc.T($"gate.mode.{m}.hint"))).ToArray(),
                    () => G().Mode, m => SlotEdit(o, id, s => { s.Gate!.Mode = m; s.Gate.On = true; }, "gate.mode"), refresh));
                break;
            case InsertSlot.CompKind:
                CompChoices(body, o, id, refresh);
                break;
            case InsertSlot.ReverbKind:
                body.Children.Add(ReverbPresets(o, id, refresh));
                break;
            case InsertSlot.DelayKind:
                DelayChoices(body, o, id, refresh);
                break;
            case InsertSlot.EqKind:
                var eq = new EqEditor { Live = () => LiveEq(o, id) };
                onEq?.Invoke(eq);
                eq.Changed += value => SlotEdit(o, id, s => s.Eq = value, "eq");
                eq.BigRequested += () => ShowEqBig(o, id, ReferenceEquals(eq, eqEditor));
                refresh.Add(() => eq.Set(o.Slot(id)?.Eq ?? GoneEq));
                body.Children.Add(eq);
                break;
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
    /// What a compressor, a gate or the pump listens to (its sidechain): its own input or another track, by name. A track that
    /// was removed shows as such and it listens to its own input meanwhile (an undo brings it back).
    /// </summary>
    Control KeyChoice(SlotOwner o, string id, List<Action> refresh)
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
        var label = new TextBlock { Text = Loc.T("fx.key"), Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
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

    /// <summary>What the compressor's menu lists, by category; "Manual" goes at the end, outside them, only while it is in use.</summary>
    sealed record CompList(string[] Names, string?[] Hints, string?[] Groups);

    static CompList MakeCompList(bool custom)
    {
        var all = CompPresets.All;
        string Group(CompPresetDef p) => Loc.Or("insp.comp.group." + p.Group, Array.Find(CompPresets.Groups, g => g.Id == p.Group)?.Label ?? p.Group);
        return new([.. all.Select(CompPresetName), .. custom ? new[] { Loc.T("insp.comp.manual") } : []],
            [.. all.Select(CompPresetHint), .. custom ? new string?[] { null } : []],
            [.. all.Select(Group), .. custom ? new string?[] { null } : []]);
    }

    // made once: every edit refreshes the panels, and the language only changes on restart
    static readonly Lazy<CompList> compList = new(() => MakeCompList(false)), compListManual = new(() => MakeCompList(true));

    /// <summary>The compressor's choices: its style, measuring the take, peak limiting and going back to the style's defaults.</summary>
    void CompChoices(StackPanel body, SlotOwner o, string id, List<Action> refresh)
    {
        CompSettings C() => o.Slot(id)?.Comp ?? GoneComp;
        var preset = StripSelect([]);
        preset.Selected += i =>
        {
            if (i >= CompPresets.All.Length) return;
            string p = CompPresets.All[i].Id;
            StripComp(o, id, c => { c.Preset = p; c.On = true; }, "preset");
        };
        // the take goes off the UI thread: a long one takes a moment the first time, and then the result is kept
        var analyze = new Button { Classes = { "chip" }, Content = new TextBlock { Classes = { "icon" }, Text = "\uE9D9", FontSize = 12 }, Height = 30, MinHeight = 30, Padding = new Thickness(6, 0), Margin = new Thickness(4, 0, 0, 0), Focusable = false };
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
        var lim = new Button { Content = "LIM", Classes = { "chip", "mono" }, FontSize = 10, FontWeight = FontWeight.Bold, Height = 30, MinHeight = 30, Padding = new Thickness(6, 0), Margin = new Thickness(4, 0, 0, 0), Focusable = false };
        lim.Click += (_, _) => StripComp(o, id, c => c.Limit = !c.Limit, "limit");
        var reset = InspectorIcon("", Loc.T("insp.comp.reset"), () => SlotEdit(o, id, s =>
        {
            bool on = s.Comp!.On;
            s.Comp = new CompSettings { On = on, Preset = o.DefaultPreset() };
            o.Recalculate(s.Comp);
        }, "comp.reset"));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        row.Children.Add(preset);
        Grid.SetColumn(analyze, 1); row.Children.Add(analyze);
        Grid.SetColumn(lim, 2); row.Children.Add(lim);
        Grid.SetColumn(reset, 3); row.Children.Add(reset);
        refresh.Add(() =>
        {
            var c = C();
            bool custom = c.Preset == "custom";
            var list = (custom ? compListManual : compList).Value;
            preset.SetItems(list.Names, list.Hints, list.Groups);
            int i = Array.FindIndex(CompPresets.All, p => p.Id == c.Preset);
            preset.SelectedIndex = custom ? CompPresets.All.Length : i;
            ToolTip.SetTip(preset, i >= 0 ? CompPresetHint(CompPresets.All[i]) : Loc.T("mix.comp.custom.tip"));
            bool take = o.Take() != null;
            analyze.IsEnabled = take && !custom;
            analyze.Classes.Set("on", c.Analyzed && !custom);
            ToolTip.SetTip(analyze, Loc.T(custom ? "insp.comp.analyze.manual" : !take ? "insp.comp.analyze.none" : c.Analyzed ? "insp.comp.analyze.on" : "insp.comp.analyze.tip"));
            lim.Classes.Set("on", c.Limit);
            ToolTip.SetTip(lim, Loc.T(c.Limit ? "mix.lim.on" : "mix.lim.off"));
        });
        body.Children.Add(row);
    }

    Control ReverbPresets(SlotOwner o, string id, List<Action> refresh)
    {
        ReverbSettings V() => o.Slot(id)?.Reverb ?? GoneReverb;
        var presets = StripSelect([]);
        ToolTip.SetTip(presets, Loc.T("mix.reverb.presets.tip"));
        presets.Selected += i =>
        {
            int k = i - (ReverbBus.PresetOf(ToParams(V())) == "" ? 1 : 0);
            if (k < 0) return;
            string label = ReverbBus.Presets[k].Label;
            SlotEdit(o, id, s =>
            {
                var r = s.Reverb!;
                var p = ReverbBus.ApplyPreset(ToParams(r), label);
                r.Decay = p.Decay; r.PreDelayMs = p.PreDelayMs; r.Tone = p.Tone; r.LowCut = p.LowCut; r.Width = p.Width;
            }, "reverb.preset");
        };
        refresh.Add(() =>
        {
            string p = ReverbBus.PresetOf(ToParams(V()));
            presets.SetItems((p == "" ? new[] { Loc.T("mix.custom.f") } : []).Concat(ReverbBus.Presets.Select(x => BusPresetName("reverb", x.Label))).ToArray(),
                (p == "" ? new string?[] { null } : []).Concat(ReverbBus.Presets.Select(x => (string?)BusPresetHint("reverb", x.Label, x.Hint))).ToArray());
            presets.SelectedIndex = p == "" ? 0 : Array.FindIndex(ReverbBus.Presets, x => x.Label == p);
        });
        return presets;
    }

    /// <summary>The delay's choices: its preset, stereo or ping-pong, and the note value (or "Libre (ms)").</summary>
    void DelayChoices(StackPanel body, SlotOwner o, string id, List<Action> refresh)
    {
        DelaySettings V() => o.Slot(id)?.Delay ?? GoneDelay;
        double Bpm() => store?.Project.Settings.Bpm ?? 110;
        var presets = StripSelect([]);
        ToolTip.SetTip(presets, Loc.T("mix.delay.presets.tip"));
        presets.Selected += i =>
        {
            int k = i - (DelayBus.PresetOf(ToParams(V())) == "" ? 1 : 0);
            if (k < 0) return;
            string label = DelayBus.Presets[k].Label;
            SlotEdit(o, id, s =>
            {
                var d = s.Delay!;
                var p = DelayBus.ApplyPreset(ToParams(d), label);
                d.Mode = DelayParams.ModeToString(p.Mode); d.Sync = p.Sync; d.Division = p.Division; d.TimeMs = p.TimeMs;
                d.Feedback = p.Feedback; d.Tone = p.Tone; d.LowCut = p.LowCut; d.Width = p.Width; d.Duck = p.Duck;
            }, "delay.preset");
        };
        refresh.Add(() =>
        {
            string p = DelayBus.PresetOf(ToParams(V()));
            presets.SetItems((p == "" ? new[] { Loc.T("mix.custom.m") } : []).Concat(DelayBus.Presets.Select(x => BusPresetName("delay", x.Label))).ToArray(),
                (p == "" ? new string?[] { null } : []).Concat(DelayBus.Presets.Select(x => (string?)BusPresetHint("delay", x.Label, x.Hint))).ToArray());
            presets.SelectedIndex = p == "" ? 0 : Array.FindIndex(DelayBus.Presets, x => x.Label == p);
        });
        body.Children.Add(presets);
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
        DefaultPreset = () => "bus-mix",
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
