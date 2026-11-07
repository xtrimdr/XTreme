using System.Globalization;
using Avalonia;
using Avalonia.Controls;
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
/// The tempo track's edits (#214): what the timeline's tempo strip asks (<see cref="TimelineView.TempoPointAdded"/> and
/// the rest) becomes a new <see cref="TempoMap"/> put in the project through <see cref="TempoFollow.SetMap"/>, in one
/// undo step (<see cref="EditTimeline"/>, which also reloads what moved: MIDI clips, follow-tempo clips, lanes, markers).
/// The playhead keeps its beat. The menus of a point and of the meter row, the typed tempo and the meter's little card
/// live here. The transport's tempo box stays the whole song's speed (<see cref="SetTempo"/>, its own undo step).
/// </summary>
public partial class MainWindow
{
    /// <summary>The tool strip's button that shows the tempo track.</summary>
    ToggleButton tempoStripButton = null!;

    void InitTempo()
    {
        Timeline.TempoPointAdded += (beat, bpm) =>
        {
            var map = TempoNow();
            if (map == null) return;
            // a point between two of a ramp keeps the ramp going on through it
            _ = ApplyTempoMap(map.WithPoint(beat, bpm, RampAt(map, beat)));
        };
        Timeline.TempoPointMoved += (from, to, bpm) =>
        {
            if (TempoNow() is not { } map) return;
            bool ramp = map.Points.Find(x => Math.Abs(x.Beat - from) < 1e-9)?.Ramp ?? false;
            _ = ApplyTempoMap(map.WithoutPoint(from).WithPoint(to, bpm, ramp));
        };
        Timeline.TempoPointMenuRequested += ShowTempoPointMenu;
        Timeline.TempoStripMenuRequested += ShowTempoStripMenu;
        Timeline.MeterMenuRequested += ShowMeterMenu;
        Timeline.TempoErased += (beats, bars) =>
        {
            if (TempoNow() is not { } map) return;
            foreach (double beat in beats)
                if (beat > 0) map = map.WithoutPoint(beat);
            foreach (int bar in bars)
                if (bar > 0) map = map.WithoutMeter(bar);
            _ = ApplyTempoMap(map);
        };
        Timeline.MeterEditRequested += (bar, point) => AskMeter(bar, point);
        // the curve's height, as the user left it dragging the strip's bottom edge
        try
        {
            if (double.TryParse(AppConfig.Get(TempoHeightKey), NumberStyles.Float, CultureInfo.InvariantCulture, out double h))
                Timeline.TempoCurveHeight = h;
        }
        catch { }
        Timeline.TempoStripResized += h =>
        {
            try { AppConfig.Set(TempoHeightKey, h.ToString(CultureInfo.InvariantCulture)); }
            catch { }
        };
    }

    const string TempoHeightKey = "timeline.tempoStripHeight";

    /// <summary>The project's map when it can be edited, else null (and the strip drops a preview it was holding).</summary>
    TempoMap? TempoNow()
    {
        if (store != null && !store.ReadOnly && !restoring && !RecordingBusy) return store.Project.Settings.TempoMap;
        if (store != null) Timeline.Tempo = store.Project.Settings.TempoMap;
        if (RecordingBusy) Toast(Loc.T("tempo.recording"));
        return null;
    }

    /// <summary>Whether a point added at <paramref name="beat"/> falls inside a ramp (it then ramps on to the next point).</summary>
    static bool RampAt(TempoMap map, double beat)
    {
        TempoPoint? before = null;
        foreach (var x in map.Points)
            if (x.Beat < beat) before = x;
        return before?.Ramp == true && map.Points.Any(x => x.Beat > beat);
    }

    /// <summary>
    /// Puts <paramref name="map"/> in the project as one undo step: what is musical keeps its beat (<see cref="TempoFollow.SetMap"/>),
    /// the timeline, the engine and the clips that follow the tempo are reloaded, and the playhead stays on its beat.
    /// </summary>
    async Task ApplyTempoMap(TempoMap map)
    {
        if (store is not { } st || TempoNow() is not { } old) return;
        map.Normalize();
        if (map.SameAs(old))
        {
            Timeline.Tempo = old;
            return;
        }
        double at = engine.PositionSec;
        await EditTimeline(p => { TempoFollow.SetMap(p, map); return sel?.Track.Id; }, "tempo-edited");
        if (store != st) return;
        var now = st.Project.Settings.TempoMap;
        // the strip may still hold its drag's preview if nothing was applied
        Timeline.Tempo = now;
        if (!now.SameAs(old)) engine.Seek(Math.Max(0, now.SecAt(old.BeatAt(at))));
    }

    void ShowTempoPointMenu(double beat, Point point)
    {
        if (store == null) return;
        var map = store.Project.Settings.TempoMap;
        int i = map.Points.FindIndex(x => Math.Abs(x.Beat - beat) < 1e-9);
        if (i < 0) return;
        var pt = map.Points[i];
        bool enabled = !store.ReadOnly && !RecordingBusy;
        bool hasNext = i + 1 < map.Points.Count;
        var refresh = new List<Action>();
        var list = new List<object>
        {
            new MenuItem { Header = Loc.T("tempo.point.title", BpmLabel(pt.Bpm), BarBeatLabel(map, beat)), IsEnabled = false, FontSize = 11 },
            MenuCheck(Loc.T("tempo.ramp"), () => store?.Project.Settings.TempoMap.Points.Find(x => Math.Abs(x.Beat - beat) < 1e-9)?.Ramp == true, () =>
            {
                if (TempoNow() is not { } m || m.Points.Find(x => Math.Abs(x.Beat - beat) < 1e-9) is not { } p) return;
                _ = ApplyTempoMap(m.WithPoint(beat, p.Bpm, !p.Ramp));
            }, refresh, tip: Loc.T("tempo.ramp.tip"), enabled: enabled && hasNext),
            MenuAction(Loc.T("tempo.type"), () => { AskTempoPoint(beat, point); return Task.CompletedTask; }, enabled: enabled, icon: ""),
            new Separator(),
            MenuAction(Loc.T("tempo.remove"), () => RemoveTempoPoint(beat), Shortcuts.Label("delete"), enabled && beat > 0, icon: "",
                tip: beat > 0 ? null : Loc.T("tempo.remove.first")),
        };
        OpenTimelineMenu(Popup(list, 260), point, MenuHeight(list));
    }

    void ShowTempoStripMenu(double beat, double bpm, Point point)
    {
        if (store == null) return;
        var map = store.Project.Settings.TempoMap;
        bool enabled = !store.ReadOnly && !RecordingBusy;
        int bar = Math.Max(0, map.BarAt(beat));
        var list = new List<object>
        {
            MenuAction(Loc.T("tempo.add", BarBeatLabel(map, beat)), () => TempoNow() is { } m ? ApplyTempoMap(m.WithPoint(beat, bpm, RampAt(m, beat))) : Task.CompletedTask,
                BpmLabel(bpm), enabled, icon: ""),
            new Separator(),
        };
        list.AddRange(MeterItems(bar, point, enabled));
        OpenTimelineMenu(Popup(list, 280), point, MenuHeight(list));
    }

    void ShowMeterMenu(int bar, Point point)
    {
        if (store == null) return;
        var list = MeterItems(bar, point, !store.ReadOnly && !RecordingBusy);
        OpenTimelineMenu(Popup(list, 280), point, MenuHeight(list));
    }

    /// <summary>"Cambio de compás…" at a bar, and taking out the change that is there (the first bar's always stays).</summary>
    List<object> MeterItems(int bar, Point point, bool enabled)
    {
        var map = store!.Project.Settings.TempoMap;
        var (num, den) = map.MeterAt(bar);
        bool own = bar > 0 && map.Meters.Any(m => m.Bar == bar);
        var list = new List<object>
        {
            MenuAction(Loc.T("tempo.meter.change", bar + 1), () => { AskMeter(bar, point); return Task.CompletedTask; }, $"{num}/{den}", enabled, icon: ""),
        };
        if (own)
            list.Add(MenuAction(Loc.T("tempo.meter.remove"), () => TempoNow() is { } m ? ApplyTempoMap(m.WithoutMeter(bar)) : Task.CompletedTask, enabled: enabled, icon: ""));
        return list;
    }

    /// <summary>The ruler's menu: the meter change at the bar under the pointer, before the grid's options.</summary>
    List<object> RulerMeterItems(Point point) =>
        store == null || !Timeline.RulerInBars ? [] : [.. MeterItems(Timeline.BarAtX(point.X), point, !store.ReadOnly && !RecordingBusy), new Separator()];

    /// <summary>Supr on the picked point, or "Borrar" in its menu: the first one, at the song's start, stays.</summary>
    Task RemoveTempoPoint(double beat)
    {
        if (beat <= 0 || TempoNow() is not { } map) return Task.CompletedTask;
        return ApplyTempoMap(map.WithoutPoint(beat));
    }

    /// <summary>"Escribir tempo…": the point's tempo typed, with decimals.</summary>
    void AskTempoPoint(double beat, Point point)
    {
        if (TempoNow() is not { } map || map.Points.Find(x => Math.Abs(x.Beat - beat) < 1e-9) is not { } pt) return;
        AskName(Timeline, point, Loc.T("tempo.type.title"), pt.Bpm.ToString("0.##", CultureInfo.CurrentCulture), Loc.T("common.ok"), text =>
        {
            if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double bpm) || !double.IsFinite(bpm)
                || bpm < TempoMap.MinBpm || bpm > TempoMap.MaxBpm)
            {
                Toast(Loc.T("tempo.type.range", TempoMap.MinBpm, TempoMap.MaxBpm));
                return;
            }
            if (TempoNow() is { } m && m.Points.Find(x => Math.Abs(x.Beat - beat) < 1e-9) is { } p)
                _ = ApplyTempoMap(m.WithPoint(beat, Math.Round(bpm, 2), p.Ramp));
        });
    }

    /// <summary>
    /// The meter of bar <paramref name="bar"/> on a small card: the beats (1 to <see cref="TempoMap.MaxNum"/>) and the
    /// note value they count in. It holds from that bar until the next change.
    /// </summary>
    void AskMeter(int bar, Point point)
    {
        if (TempoNow() is not { } map) return;
        var (num, den) = map.MeterAt(bar);
        var box = new TextBox { Classes = { "field", "mono" }, Text = num.ToString(CultureInfo.InvariantCulture), Width = 56, HorizontalContentAlignment = HorizontalAlignment.Right };
        var dens = new SelectButton { MinWidth = 64 };
        dens.SetItems([.. TempoMap.Dens.Select(d => d.ToString(CultureInfo.InvariantCulture))]);
        dens.SelectedIndex = Math.Max(0, Array.IndexOf(TempoMap.Dens, den));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(box);
        row.Children.Add(new TextBlock { Text = "/", FontSize = 14, Foreground = Palette.Dim, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(dens);
        var wrong = new TextBlock { Classes = { "notice" }, Text = Loc.T("tempo.meter.wrong", TempoMap.MaxNum), TextWrapping = TextWrapping.Wrap, MaxWidth = 220, IsVisible = false };
        var ok = new Button { Classes = { "btn", "sm", "primary" }, Content = Loc.T("common.ok"), HorizontalAlignment = HorizontalAlignment.Right };
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Classes = { "label" }, Text = Loc.T("tempo.meter.title", bar + 1).ToUpperInvariant() });
        body.Children.Add(row);
        body.Children.Add(wrong);
        body.Children.Add(ok);
        var card = new Border
        {
            Padding = new Thickness(12, 10), Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = body,
        };
        var flyout = new Flyout
        {
            Content = card, FlyoutPresenterClasses = { "bare" }, Placement = PlacementMode.AnchorAndGravity, PlacementAnchor = PopupAnchor.TopLeft,
            PlacementGravity = PopupGravity.BottomRight, HorizontalOffset = point.X, VerticalOffset = point.Y,
        };
        void Commit()
        {
            if (!int.TryParse(box.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > TempoMap.MaxNum)
            {
                wrong.IsVisible = true;
                return;
            }
            flyout.Hide();
            if (TempoNow() is { } m) _ = ApplyTempoMap(m.WithMeter(bar, n, TempoMap.Dens[Math.Max(0, dens.SelectedIndex)]));
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { flyout.Hide(); e.Handled = true; }
        };
        box.TextChanged += (_, _) => wrong.IsVisible = false;
        ok.Click += (_, _) => Commit();
        flyout.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        flyout.ShowAt(Timeline);
    }

    /// <summary>"Seguir tempo" on an audio track: its clips move and stretch with the tempo map from now on. Nothing moves by itself.</summary>
    MenuItem FollowTempoItem(string trackId, bool enabled, List<Action> refresh)
    {
        LiveTrack? Now() => tracks.Find(t => t.Track.Id == trackId);
        return MenuCheck(Loc.T("tempo.follow"), () => Now()?.Track.FollowTempo == true, () =>
        {
            if (Now() is not { } x) return;
            bool on = !x.Track.FollowTempo;
            EditTrack(x, t => t.Track.FollowTempo = on, "followTempo");
            Status(Loc.T(on ? "tempo.follow.on" : "tempo.follow.off", x.Track.Name));
        }, refresh, tip: Loc.T("tempo.follow.tip"), enabled: enabled);
    }

    static string BpmLabel(double bpm) => Loc.T("tempo.bpm", bpm.ToString("0.##", CultureInfo.CurrentCulture));

    /// <summary>Where a beat falls as "bar.unit" (from 1), like the ruler.</summary>
    static string BarBeatLabel(TempoMap map, double beat)
    {
        map.BarBeatAt(map.SecAt(beat), out int bar, out double unit);
        return $"{bar + 1}.{(int)Math.Floor(unit + 1e-6) + 1}";
    }
}
