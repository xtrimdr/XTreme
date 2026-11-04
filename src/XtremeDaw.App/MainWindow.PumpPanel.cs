using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using XtremeDaw.App.Controls;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The pump's panel, laid out like KickStart's (asked by the user): the mode (on the beat or on a track's hits) and the
/// division on top, the depth as a big knob beside a big display of the curve over the sound, and the shapes under it as
/// buttons. With a sidechain its knobs read like a compressor's (threshold, attack, hold, release), and the division and
/// the shapes go away: the hits set when it ducks. It is still <see cref="FxBody"/>'s panel, the same everywhere.
/// </summary>
public partial class MainWindow
{
    /// <summary>The depth knob's size, wide and in a narrow panel, and the width under which it goes small.</summary>
    const double PumpKnobW = 64, PumpKnobSmallW = 48, PumpNarrowW = 320;
    /// <summary>The narrowest a shape button gets before the row breaks in two.</summary>
    const double PumpShapeMinW = 34;

    /// <summary>The shapes under the display, as length and curve, from a long soft swell to a short tight duck.</summary>
    static readonly (double Length, double Curve)[] PumpShapes =
        [(1, -0.5), (0.8, 0), (0.6, 0.4), (0.45, 0.7), (0.3, 0.3), (0.2, 0), (0.9, 1), (0.6, -0.8)];

    /// <summary>Names a kick usually goes by, for the track a sidechain starts listening to.</summary>
    static readonly string[] KickNames = ["kick", "bombo", "bd", "bass drum"];

    void PumpBody(StackPanel body, SlotOwner o, string id, List<Action> refresh)
    {
        var all = FxDefs.Params(TrackInserts.Pump);
        FxParam P(string pid) => Array.Find(all, p => p.Id == pid)!;
        bool IsKeyed() => o.Slot(id) is { } s && Keyed(s);
        bool IsBeat() => !IsKeyed();
        bool Listens() => o.Slot(id)?.Sidechain != null;

        // the mode: picking Sidechain starts on the kick (or the first other track); the picker under it changes it
        var mode = FxSegments([("beat", Loc.T("fx.pump.mode.beat"), Loc.T("fx.pump.mode.beat.tip")), ("key", Loc.T("fx.key"), Loc.T("fx.pump.mode.key.tip"))],
            () => Listens() ? "key" : "beat",
            m =>
            {
                if (m == "beat" && Listens()) SlotEdit(o, id, s => s.Sidechain = null, "sidechain");
                else if (m == "key" && !Listens() && KickGuess(o) is { } k) SlotEdit(o, id, s => s.Sidechain = k, "sidechain");
                foreach (var r in refresh) r();
            }, refresh);
        body.Children.Add(mode);

        var division = P("division");
        var divisions = FxSegments([.. Enumerable.Range(0, PumpParams.Names.Length).Reverse().Select(i => ($"{i}", PumpParams.Names[i], (string?)Loc.T("fx.pump.division.tip")))],
            () => o.Slot(id) is { } s ? $"{(int)Math.Round(division.Get(s))}" : "",
            m => FxEdit(o, id, division, int.Parse(m)), refresh);
        body.Children.Add(divisions);
        var key = KeyChoice(o, id, refresh);
        body.Children.Add(key);
        refresh.Add(() =>
        {
            divisions.IsVisible = IsBeat();
            key.IsVisible = Listens();
        });

        // the depth, big, beside the display
        var (depthCell, depthKnob) = FxKnobCell(o, id, P("depth"), refresh);
        depthKnob.Resize(PumpKnobW);
        depthCell.VerticalAlignment = VerticalAlignment.Center;
        depthCell.MinWidth = PumpKnobW + 16;
        var view = new PumpView { Live = () => o.Insert(id) is FxShell { Inner: PumpInsert p } ? p : null, Margin = new Thickness(10, 0, 0, 0) };
        refresh.Add(() =>
        {
            if (o.Slot(id) is not { } s) return;
            view.Set(FxDefs.ToPump(s), store?.Project.Settings.Bpm ?? 120, Keyed(s), s.On);
        });
        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 4, 0, 0) };
        main.Children.Add(depthCell);
        Grid.SetColumn(view, 1);
        main.Children.Add(view);
        main.SizeChanged += (_, e) => depthKnob.Resize(e.NewSize.Width < PumpNarrowW ? PumpKnobSmallW : PumpKnobW);
        body.Children.Add(main);

        // the shapes: one click sets the length and the curve together
        var shapes = new UniformGrid { Columns = PumpShapes.Length, Margin = new Thickness(0, 2, 0, 0) };
        foreach (var (length, curve) in PumpShapes)
        {
            var icon = new PumpShapeIcon(length, curve);
            var b = new Button { Classes = { "chip" }, Content = icon, Padding = new Thickness(0), Height = 34, MinHeight = 34, Margin = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, Focusable = false };
            ToolTip.SetTip(b, Loc.T("fx.pump.shape.tip", $"{length * 100:0}%", $"{curve * 100:+0;-0;0}%"));
            b.Click += (_, _) => SlotEdit(o, id, s =>
            {
                P("length").Set(s, length);
                P("curve").Set(s, curve);
            }, $"pump.shape.{++slotEdits}");
            refresh.Add(() =>
            {
                if (o.Slot(id) is not { } s) return;
                bool lit = Math.Abs(P("length").Get(s) - length) < 0.005 && Math.Abs(P("curve").Get(s) - curve) < 0.005;
                icon.Lit = lit;
                b.Classes.Set("on", lit);
            });
            shapes.Children.Add(b);
        }
        shapes.SizeChanged += (_, e) =>
        {
            int cols = e.NewSize.Width / PumpShapes.Length < PumpShapeMinW ? PumpShapes.Length / 2 : PumpShapes.Length;
            if (cols != shapes.Columns) shapes.Columns = cols;
        };
        refresh.Add(() => shapes.IsVisible = IsBeat());
        body.Children.Add(shapes);

        // the knobs: on the beat the curve's; with a sidechain a compressor's, its attack out of the fine controls
        var knobs = new List<Knob>();
        var grid = FxGrid(knobs, 6);
        void Add(Panel to, List<Knob> list, string pid, Func<bool>? shown)
        {
            var (cell, knob) = FxKnobCell(o, id, P(pid), refresh, shown: shown);
            to.Children.Add(cell);
            list.Add(knob);
        }
        foreach (var pid in new[] { "threshold", "attack", "hold", "keyMs" }) Add(grid, knobs, pid, IsKeyed);
        Add(grid, knobs, "length", IsBeat);
        Add(grid, knobs, "curve", null);
        foreach (var p in FxDefs.Common(TrackInserts.Pump))
        {
            var (cell, knob) = FxKnobCell(o, id, p, refresh);
            grid.Children.Add(cell);
            knobs.Add(knob);
        }
        body.Children.Add(grid);

        var fineKnobs = new List<Knob>();
        var fineGrid = FxGrid(fineKnobs, 6);
        Add(fineGrid, fineKnobs, "attack", null);
        Add(fineGrid, fineKnobs, "offset", null);
        fineGrid.IsVisible = false;
        var button = new Button { Classes = { "ghost" }, FontSize = 11, Padding = new Thickness(8, 6), Focusable = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        bool open = false;
        void Label() => button.Content = (open ? "▾ " : "▸ ") + Loc.T("insp.fine");
        Label();
        button.Click += (_, _) => { open = !open; Label(); fineGrid.IsVisible = open && IsBeat(); };
        refresh.Add(() =>
        {
            button.IsVisible = IsBeat();
            fineGrid.IsVisible = open && IsBeat();
        });
        body.Children.Add(button);
        body.Children.Add(fineGrid);
    }

    /// <summary>The track a sidechain starts on: one named like a kick, or the first other track; null when there is none.</summary>
    string? KickGuess(SlotOwner o)
    {
        var others = (store?.Project.Tracks ?? []).Where(t => t.Id != o.OwnerId).ToList();
        return (others.Find(t => KickNames.Any(n => t.Name.Contains(n, StringComparison.OrdinalIgnoreCase))) ?? others.FirstOrDefault())?.Id;
    }
}
