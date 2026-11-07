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
    /// <summary>A hero knob (#187: what carries the effect, as on a hardware unit), and its picture's height in each view.</summary>
    const double FxHeroW = 68, FxScopeCompactH = 64, FxScopeFullH = 96;

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
            if (p.Manual)
            {
                if (c.Preset != "custom") c.ManualFrom = c.Preset;
                c.Preset = "custom";
            }
            else if (p.Id == "amount")
            {
                // Amount only drives a preset: in Manual it drops the hand-set values and goes back to the last one
                if (c.Preset == "custom")
                    c.Preset = CompPresets.All.Any(x => x.Id == c.ManualFrom) ? c.ManualFrom! : CompPresets.DefaultId;
                o.Recalculate(c);
            }
        }
    }, $"{id}.{p.Id}");

    /// <summary>
    /// One knob of the list; hidden and disabled as <paramref name="shown"/> and <paramref name="enabled"/> say.
    /// <paramref name="tint"/>: its lit arc in the effect family's color; <paramref name="hero"/>: one of the knobs that
    /// carry the effect (#187), big and with its value larger, out of any grid.
    /// </summary>
    (Control Cell, Knob Knob) FxKnobCell(SlotOwner o, string id, FxParam p, List<Action> refresh, Func<bool>? enabled = null, Func<bool>? shown = null,
        Color? tint = null, bool hero = false)
    {
        string label = Loc.T(p.Label);
        // Dry and Wet go back to where the effect starts here (#187): a reverb on a channel at 25 %, on a bus at 100 %
        double reset = p.Default;
        if ((p == FxDefs.Dry || p == FxDefs.Wet) && o.Slot(id)?.Type is { } kind) reset = p.Get(DefaultSlot(o, kind));
        // a compressor's threshold lights from 0 dB down: the more arc, the more it compresses (#187)
        double? arcFrom = p.Label == "insp.comp.threshold" && double.IsNegativeInfinity(p.OffBelow) ? 1 : null;
        var knob = new Knob(hero ? FxHeroW : FxKnobBigW, label) { Min = 0, Max = 1, ResetTo = p.ToKnob(reset), ArcFrom = arcFrom, HorizontalAlignment = HorizontalAlignment.Center, Tint = tint };
        knob.Changed += k => { if (!syncing) FxEdit(o, id, p, p.FromKnob(k)); };
        var value = FxValue();
        var cell = FxCell(label, knob, value, p.Tip != null ? Loc.T(p.Tip) : null);
        if (hero)
        {
            value.FontSize = 13;
            value.Foreground = Palette.Fg;
            cell.Spacing = 6;
            cell.Margin = new Thickness(4, 0, 10, 0);
            if (cell.Children[0] is TextBlock name)
            {
                name.FontSize = 10.5;
                name.FontWeight = FontWeight.SemiBold;
                if (tint is { } t) name.Foreground = Palette.Alpha(t, 0.9);
            }
        }
        refresh.Add(() =>
        {
            if (o.Slot(id) is not { } s) return;
            double v = p.Get(s);
            knob.Value = p.ToKnob(v);
            value.Text = FxText(p, v);
            if (enabled != null) { bool e = enabled(); knob.IsEnabled = e; cell.Opacity = e ? 1 : 0.4; }
            if (shown != null) cell.IsVisible = shown();
        });
        // an analyzer's knobs only change how it reads: no lanes (FxDefs.Automatable)
        if (!TrackInserts.IsMeter(o.Slot(id)?.Type))
            AutomateOn(cell, () => TrackOf(o), AutoTarget.Fx(id, p.Id), k =>
            {
                knob.Value = k;
                value.Text = FxText(p, p.FromKnob(k));
            });
        return (cell, knob);
    }

    /// <summary>
    /// The most values a stepped parameter can have and still show as buttons (asked by the user, #261: a handful of
    /// fixed values reads and picks faster all in sight than on a knob); more than that stays a knob.
    /// </summary>
    const int FxChoiceMaxSteps = 8;

    static bool FxIsChoice(FxParam p) => p.Steps is { Length: >= 2 and <= FxChoiceMaxSteps };

    /// <summary>
    /// A stepped parameter as a row of buttons, one per value, with its name on top as a knob's (#261). A value that reads
    /// long ("1 · 0,3 s") shows its first part and the rest in its tooltip. It follows its lane like a knob.
    /// </summary>
    Control FxStepChoice(SlotOwner o, string id, FxParam p, List<Action> refresh)
    {
        var steps = p.Steps!;
        var buttons = new ToggleButton[steps.Length];
        var row = new UniformGrid { Rows = 1 };
        void Show(double v)
        {
            double at = p.Snap(v);
            for (int i = 0; i < buttons.Length; i++) buttons[i].IsChecked = steps[i] == at;
        }
        for (int i = 0; i < steps.Length; i++)
        {
            double v = steps[i];
            string text = FxText(p, v);
            int cut = text.IndexOf(" · ", StringComparison.Ordinal);
            var b = new ToggleButton { Content = cut > 0 ? text[..cut] : text, Classes = { "seg" }, Padding = new Thickness(2, 0), FontSize = 11, Focusable = false };
            if (cut > 0) ToolTip.SetTip(b, text);
            b.Click += (_, _) =>
            {
                if (!syncing) FxEdit(o, id, p, v);
                if (o.Slot(id) is { } s) Show(p.Get(s));
            };
            buttons[i] = b;
            row.Children.Add(b);
        }
        var name = new TextBlock { Text = Loc.T(p.Label).ToUpperInvariant(), FontSize = 10, LetterSpacing = 0.8, Foreground = Palette.Faint, TextTrimming = TextTrimming.CharacterEllipsis,
            Background = Brushes.Transparent, Margin = new Thickness(2, 0, 0, 0) };
        if (p.Tip != null) ToolTip.SetTip(name, Loc.T(p.Tip));
        var cell = new StackPanel { Spacing = 4, Children = { name, new Border { Classes = { "seg" }, Child = row } } };
        refresh.Add(() => { if (o.Slot(id) is { } s) Show(p.Get(s)); });
        if (!TrackInserts.IsMeter(o.Slot(id)?.Type))
            AutomateOn(cell, () => TrackOf(o), AutoTarget.Fx(id, p.Id), k => Show(p.FromKnob(k)));
        return cell;
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
        // the effect's own choices: the full view puts each in its panel, the other views in a row under the preset (#261)
        var parts = new Dictionary<string, Control>();
        switch (kind)
        {
            case InsertSlot.SatKind:
                body.Children.Add(PresetBar(o, id, kind, refresh, []));
                SatSettings S() => o.Slot(id)?.Sat ?? GoneSat;
                parts[FxDefs.ModePart] = FxSegments([("tube", Loc.T("insp.sat.tube"), Loc.T("insp.sat.tube.hint")), ("tape", Loc.T("insp.sat.tape"), Loc.T("insp.sat.tape.hint"))],
                    () => S().Mode, m => SlotEdit(o, id, s => { s.Sat!.Mode = m; s.Sat.On = true; }, "sat.mode"), refresh);
                break;
            case InsertSlot.GateKind:
                body.Children.Add(PresetBar(o, id, kind, refresh, []));
                GateSettings G() => o.Slot(id)?.Gate ?? GoneGate;
                parts[FxDefs.ModePart] = FxSegments(GateModes.Select(m => (m, Loc.T("gate.mode." + m), (string?)Loc.T($"gate.mode.{m}.hint"))).ToArray(),
                    () => G().Mode, m => SlotEdit(o, id, s => { s.Gate!.Mode = m; s.Gate.On = true; }, "gate.mode"), refresh);
                break;
            case InsertSlot.CompKind:
                parts[FxDefs.LimitPart] = CompChoices(body, o, id, refresh);
                break;
            case InsertSlot.ReverbKind:
                body.Children.Add(PresetBar(o, id, kind, refresh, ReverbPresetItems(o, id)));
                break;
            case InsertSlot.DelayKind:
                var (mode, division) = DelayChoices(body, o, id, refresh, full == true);
                parts[FxDefs.ModePart] = mode;
                parts[FxDefs.DivisionPart] = division;
                break;
            case InsertSlot.EqKind:
                var bigEq = InspectorIcon("", Loc.T("eq.big.tip"), () => ShowEqBig(o, id, o.OwnerId == sel?.Track.Id));
                body.Children.Add(PresetBar(o, id, kind, refresh, EqPresetItems(o, id), [bigEq]));
                // in an effect window the band's controls are knobs in the family's color, side by side in the full view (#187)
                var eq = new EqEditor(knobs: full != null, wide: full == true, tint: Palette.FamilyColor(FxDefs.Family(kind))) { Live = () => LiveEq(o, id), ShowsPresets = false };
                onEq?.Invoke(eq);
                eq.Changed += value => SlotEdit(o, id, s => s.Eq = value, "eq");
                eq.BigRequested += () => ShowEqBig(o, id, o.OwnerId == sel?.Track.Id);
                refresh.Add(() => eq.Set(o.Slot(id)?.Eq ?? GoneEq));
                body.Children.Add(eq);
                break;
            case TrackInserts.Tonal:
                // an analyzer has no fine controls: its compact or full view everywhere, the full one in Mastering (#210)
                TonalChoices(body, o, id, refresh);
                FxSized(body, o, id, kind, refresh, FxDefs.Layout(kind)!, full ?? true, []);
                return;
            default:
                body.Children.Add(PresetBar(o, id, kind, refresh, []));
                break;
        }

        if (full is { } big && FxDefs.Layout(kind) is { } layout)
        {
            if (!big && parts.Count > 0) body.Children.Add(FxChoiceRow(parts));
            FxSized(body, o, id, kind, refresh, layout, big, parts);
            return;
        }
        if (parts.Count > 0) body.Children.Add(FxChoiceRow(parts));

        if (TrackInserts.HasCompGraph(kind)) body.Children.Add(CompGraphOf(o, id, refresh));
        if (TrackInserts.TakesKey(kind)) body.Children.Add(KeyChoice(o, id, refresh));

        bool Manual() => o.Slot(id)?.Comp?.Preset == "custom";
        bool Free() => o.Slot(id)?.Delay?.Sync != true;
        var all = FxDefs.Params(kind);
        Func<bool>? Shown(FxParam p) => kind == InsertSlot.DelayKind && p.Id == "time" ? Free : null;
        foreach (var p in all.Where(p => !p.Fine && hide?.Contains(p.Id) != true))
        {
            if (FxIsChoice(p)) body.Children.Add(FxStepChoice(o, id, p, refresh));
            else Add(p, grid, knobs, null, Shown(p));
        }
        foreach (var p in FxDefs.Common(kind)) Add(p, grid, knobs);
        body.Children.Add(grid);

        // the fine controls wait behind a button (asked by the user: the simple way stays in front), open when set by hand
        var fine = all.Where(p => p.Fine).ToArray();
        if (fine.Length == 0) return;
        var fineKnobs = new List<Knob>();
        var fineKnobGrid = FxGrid(fineKnobs, 0);
        var fineGrid = new StackPanel { Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var p in fine)
        {
            if (FxIsChoice(p)) fineGrid.Children.Add(FxStepChoice(o, id, p, refresh));
            else Add(p, fineKnobGrid, fineKnobs, shown: Shown(p));
        }
        fineGrid.Children.Add(fineKnobGrid);
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
    void FxSized(StackPanel body, SlotOwner o, string id, string kind, List<Action> refresh, FxLayout layout, bool full, Dictionary<string, Control> parts)
    {
        FxParam[] all = [.. FxDefs.Params(kind), .. FxDefs.Common(kind)];
        var family = FxDefs.Family(kind);
        Color? tint = family == FxFamily.None ? null : Palette.FamilyColor(family);
        bool Free() => o.Slot(id)?.Delay?.Sync != true;
        (Control Cell, Knob Knob) Cell(FxParam p, bool hero) =>
            FxKnobCell(o, id, p, refresh, null, kind == InsertSlot.DelayKind && p.Id == "time" ? Free : null, tint, hero);
        // the hero knobs first and big (#187), the rest in a grid that takes what is left of the width
        // a stepped parameter with a handful of values goes as buttons over the knobs, the panel's width (#261)
        Control Knobs(IEnumerable<string> ids, double top)
        {
            var choices = new StackPanel { Spacing = 8 };
            foreach (string pid in ids)
                if (Array.Find(all, p => p.Id == pid) is { } p && FxIsChoice(p)) choices.Children.Add(FxStepChoice(o, id, p, refresh));
            string[] turned = [.. ids.Where(pid => Array.Find(all, p => p.Id == pid) is { } p && !FxIsChoice(p))];
            if (choices.Children.Count == 0) return KnobRow(turned, top);
            choices.Margin = new Thickness(0, top, 0, 0);
            if (turned.Length > 0)
            {
                var knobsRow = KnobRow(turned, 0);
                knobsRow.Margin = new Thickness(0, 4, 0, 0);
                choices.Children.Add(knobsRow);
            }
            return choices;
        }
        Control KnobRow(string[] ids, double top)
        {
            var knobs = new List<Knob>();
            var grid = FxGrid(knobs, 0);
            var heroes = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (string pid in ids)
            {
                if (Array.Find(all, p => p.Id == pid) is not { } p) continue;
                bool hero = layout.Hero.Contains(pid);
                var (cell, knob) = Cell(p, hero);
                if (hero) heroes.Children.Add(cell);
                else
                {
                    grid.Children.Add(cell);
                    knobs.Add(knob);
                }
            }
            if (heroes.Children.Count == 0) { grid.Margin = new Thickness(0, top, 0, 0); return grid; }
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, top, 0, 0) };
            row.Children.Add(heroes);
            if (grid.Children.Count == 0) heroes.HorizontalAlignment = HorizontalAlignment.Center;
            else
            {
                grid.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(grid, 1);
                row.Children.Add(grid);
            }
            if (grid.Children.Count == 0) Grid.SetColumnSpan(heroes, 2);
            return row;
        }
        Control? Part(string part, double scopeH) => part switch
        {
            FxDefs.GraphPart when TrackInserts.HasCompGraph(kind) => CompGraphOf(o, id, refresh),
            FxDefs.KeyPart when TrackInserts.TakesKey(kind) => KeyChoice(o, id, refresh, labeled: false),
            FxDefs.ScopePart when FxScope.Has(kind) => ScopeOf(o, id, kind, scopeH, tint ?? Palette.AccentColor, refresh),
            FxDefs.TonalPart when kind == TrackInserts.Tonal => TonalOf(o, id, full, tint ?? Palette.AccentColor, refresh),
            _ => parts.GetValueOrDefault(part),
        };

        if (!full)
        {
            if ((Part(FxDefs.GraphPart, 0) ?? Part(FxDefs.ScopePart, FxScopeCompactH) ?? Part(FxDefs.TonalPart, 0)) is { } graph) body.Children.Add(graph);
            body.Children.Add(Knobs(layout.Compact, 2));
            return;
        }
        foreach (var row in layout.Rows)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", row.Select(g => g.Weight.ToString(CultureInfo.InvariantCulture) + "*"))) };
            for (int i = 0; i < row.Length; i++)
            {
                var g = row[i];
                var title = new TextBlock { Classes = { "label" }, Text = Loc.T(g.Label) };
                if (tint is { } t) title.Foreground = Palette.Alpha(t, 0.85);
                // what goes in the panel in the order its list says, the knobs next to each other in one row; under the
                // title, centered in the height the row gives it (#261: a short panel beside a tall one was left with a hole)
                var stack = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                var run = new List<string>();
                void Flush()
                {
                    if (run.Count > 0) stack.Children.Add(Knobs(run, 0));
                    run = [];
                }
                foreach (string pid in g.Ids)
                {
                    if (!pid.StartsWith('@')) { run.Add(pid); continue; }
                    Flush();
                    if (Part(pid, FxScopeFullH) is { } c) stack.Children.Add(c);
                }
                Flush();
                var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
                content.Children.Add(title);
                Grid.SetRow(stack, 1);
                stack.Margin = new Thickness(0, 6, 0, 0);
                content.Children.Add(stack);
                var panel = new Border { Child = content, Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                    BorderBrush = tint is { } b ? Palette.Alpha(b, FxInnerTintLine) : Palette.Line, Background = Palette.Alpha(Palette.AppColor, FxInnerAlpha),
                    Margin = new Thickness(i == 0 ? 0 : FxInnerGap, 0, 0, 0) };
                Grid.SetColumn(panel, i);
                grid.Children.Add(panel);
            }
            body.Children.Add(grid);
        }
    }

    /// <summary>An effect's own choices side by side, sharing the width (#261): where they go when there are no inner panels.
    /// A delay's note values take a line of their own under them: nine buttons need the whole width.</summary>
    static Control FxChoiceRow(Dictionary<string, Control> parts)
    {
        var side = parts.Where(x => x.Key != FxDefs.DivisionPart).Select(x => x.Value).ToArray();
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", side.Select(_ => "*"))) };
        if (parts.GetValueOrDefault(FxDefs.DivisionPart) is { } division)
            return side.Length == 0 ? division : new StackPanel { Spacing = 8, Children = { WithRow(row, side), division } };
        return WithRow(row, side);
    }

    static Grid WithRow(Grid row, Control[] side)
    {
        int col = 0;
        foreach (var c in side)
        {
            c.Margin = new Thickness(col == 0 ? 0 : FxInnerGap, 0, 0, 0);
            c.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(c, col++);
            row.Children.Add(c);
        }
        return row;
    }

    /// <summary>An inner panel of a full view: a step down from the effect's box, the room between two of them and how
    /// much of the family's color its border takes.</summary>
    const double FxInnerAlpha = 0.35, FxInnerGap = 8, FxInnerTintLine = 0.3;

    /// <summary>
    /// An effect's picture (<see cref="FxScope"/>, #187): it reads the slot's values on every edit; a synced delay's time
    /// comes from the tempo, and a modulation moves while the song plays.
    /// </summary>
    FxScope ScopeOf(SlotOwner o, string id, string kind, double height, Color tint, List<Action> refresh)
    {
        var scope = new FxScope(kind, height) { Tint = tint, Running = () => engine.Playing, Margin = new Thickness(0, 2, 0, 2) };
        FxParam?[] inputs = [.. FxScope.Inputs(kind).Select(pid => Array.Find(FxDefs.Params(kind), p => p.Id == pid))];
        refresh.Add(() =>
        {
            if (o.Slot(id) is not { } s) return;
            for (int i = 0; i < inputs.Length; i++) scope.Values[i] = inputs[i]?.Get(s) ?? 0;
            if (s.Delay is { } d)
            {
                scope.Values[0] = DelayBus.DelayTimeSec(ToParams(d), store?.Project.Settings.Bpm ?? 110) * 1000;
                scope.PingPong = d.Mode == "pingpong";
            }
            if (s.Sat is { } sat) scope.TapeMode = sat.Mode == "tape";
            scope.InvalidateVisual();
        });
        return scope;
    }

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
        select.Emphasize(PresetBarH);
        var items = new List<FxPresetItem>();
        // the last entry is "Personalizado" while the settings are on no preset: the arrows step over it
        bool custom = false;
        void Step(int d)
        {
            int real = items.Count - (custom ? 1 : 0);
            if (real == 0) return;
            int at = select.SelectedIndex;
            int to = at < 0 || at >= real ? (d > 0 ? 0 : real - 1) : ((at + d) % real + real) % real;
            items[to].Pick();
        }
        var prev = InspectorIcon("", Loc.T("fx.preset.prev.tip"), () => Step(-1));
        var next = InspectorIcon("", Loc.T("fx.preset.next.tip"), () => Step(1));
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
            custom = at < 0;
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
        // a band of its own, the same in every native effect (asked by the user, #261: it has to read as the preset
        // system): its name, the arrows, the menu, the effect's own buttons, save and delete; in the family's color
        var family = FxDefs.Family(kind);
        Color? tint = family == FxFamily.None ? null : Palette.FamilyColor(family);
        var name = new TextBlock { Text = Loc.T("fx.preset.label").ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.8,
            Foreground = tint is { } t ? Palette.Alpha(t, 0.9) : Palette.Faint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) };
        Control[] cells = [name, prev, next, select, .. extra ?? [], save, delete];
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*" + string.Concat(Enumerable.Repeat(",Auto", cells.Length - 4))) };
        for (int i = 0; i < cells.Length; i++)
        {
            var c = cells[i];
            if (i > 0) c.Margin = new Thickness(i == 3 ? 6 : i < 3 ? 0 : 4, 0, 0, 0);
            c.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(c, i);
            row.Children.Add(c);
        }
        presetBarRefresh.Add(new(refresh));
        return new Border
        {
            Child = row, Padding = new Thickness(6, 5), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = tint is { } b ? Palette.Alpha(b, PresetBandLine) : Palette.Line,
            Background = tint is { } f ? Palette.Alpha(f, PresetBandFill) : Palette.Raised,
        };
    }

    /// <summary>The preset band (#261): its menu's height, and how much of the family's color its fill and border take.</summary>
    const double PresetBarH = 30, PresetBandFill = 0.07, PresetBandLine = 0.35;

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

    /// <summary>The compressor's choices: its presets with measuring the take beside them (its Default is the reset), and
    /// peak limiting, returned for its place in the panel (#261: it is part of the output).</summary>
    Control CompChoices(StackPanel body, SlotOwner o, string id, List<Action> refresh)
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
        body.Children.Add(PresetBar(o, id, InsertSlot.CompKind, refresh, CompPresetItems(o, id), [analyze]));
        lim.HorizontalAlignment = HorizontalAlignment.Center;
        return lim;
    }

    /// <summary>
    /// The delay's choices: its preset, and returned for their panels (#261) stereo or ping-pong and the note value (or
    /// "Libre (ms)"), named in full where there is room (<paramref name="wide"/>).
    /// </summary>
    (Control Mode, Control Division) DelayChoices(StackPanel body, SlotOwner o, string id, List<Action> refresh, bool wide)
    {
        DelaySettings V() => o.Slot(id)?.Delay ?? GoneDelay;
        double Bpm() => store?.Project.Settings.Bpm ?? 110;
        body.Children.Add(PresetBar(o, id, InsertSlot.DelayKind, refresh, DelayPresetItems(o, id)));
        string sfx = wide ? "" : ".short";
        var mode = FxSegments([("stereo", Loc.T("mix.delay.stereo" + sfx), Loc.T("mix.delay.stereo.tip")), ("pingpong", Loc.T("mix.delay.pingpong" + sfx), Loc.T("mix.delay.pingpong.tip"))],
            () => V().Mode, m => SlotEdit(o, id, s => s.Delay!.Mode = m, "delay.mode"), refresh);
        // the note value as buttons, all in sight (asked by the user, #261: a handful of values picks faster than a menu),
        // in its musical short form ("1/8.", "1/8T"), its full name and time in the tooltip; "Libre" frees the time knob
        const string free = "free";
        var division = FxSegments([.. DelayBus.Divisions.Select(d => (d.Id, d.Id.Replace('t', 'T'), (string?)null)), (free, Loc.T("fx.delay.free"), Loc.T("mix.delay.free"))],
            () => V().Sync ? V().Division : free,
            pick =>
            {
                if (pick == free)
                {
                    if (V().Sync) SlotEdit(o, id, s => { var d = s.Delay!; d.TimeMs = Math.Round(DelayBus.DelayTimeSec(ToParams(d), Bpm()) * 1000); d.Sync = false; }, "delay.sync");
                }
                else SlotEdit(o, id, s => { s.Delay!.Sync = true; s.Delay.Division = pick; }, "delay.division");
            }, refresh);
        var cells = ((UniformGrid)((Border)division).Child!).Children.OfType<ToggleButton>().ToArray();
        foreach (var b in cells) { b.Padding = new Thickness(2, 0); b.FontSize = 11; }
        refresh.Add(() =>
        {
            for (int i = 0; i < DelayBus.Divisions.Length; i++)
            {
                var x = DelayBus.Divisions[i];
                var at = ToParams(V()) with { Sync = true, Division = x.Id };
                ToolTip.SetTip(cells[i], $"{Loc.Or("mix.div." + x.Id, x.Label)} · {Math.Round(DelayBus.DelayTimeSec(at, Bpm()) * 1000):0} ms");
            }
        });
        var name = new TextBlock { Text = Loc.T("fx.delay.note").ToUpperInvariant(), FontSize = 10, LetterSpacing = 0.8, Foreground = Palette.Faint, Margin = new Thickness(2, 0, 0, 0) };
        return (mode, new StackPanel { Spacing = 4, Children = { name, division } });
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
