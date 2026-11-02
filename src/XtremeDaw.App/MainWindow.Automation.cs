using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using XtremeDaw.App.Audio;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Model;
using XtremeDaw.Audio.Vst;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// Automation (asked by the user: anything a channel has, audio or MIDI): a right click on a control, or the track's
/// menu, gives that parameter a lane, a child of the track in the timeline (<c>TimelineView.Automation.cs</c>). Lanes live
/// on the track (<see cref="Automations"/>) and never change the channel: the controls follow them while they play
/// (<see cref="FollowLanes"/>), and what the channel holds comes back when a lane goes off.
/// </summary>
public partial class MainWindow
{
    /// <summary>A control that shows a lane: its track, its target and how it shows a position.</summary>
    sealed class LaneView(Func<LiveTrack?> track, string target, Action<double> show)
    {
        public readonly Func<LiveTrack?> Track = track;
        public readonly string Target = target;
        public readonly Action<double> Show = show;
        public double Last = -1;
    }

    readonly List<LaneView> laneViews = [];
    /// <summary>What each running plugin said it can automate: asked once per process.</summary>
    readonly ConditionalWeakTable<VstBridge, IReadOnlyList<VstParamInfo>> pluginParams = [];

    void InitAutomation()
    {
        Timeline.AutomationName = LaneName;
        Timeline.AutomationValue = LaneValue;
        Timeline.AutomationBlockEdited += (t, id, block) => _ = LaneEdit(t, id, l =>
        {
            int at = l.Blocks.FindIndex(b => b.Id == block.Id);
            if (at >= 0) l.Blocks[at] = block;
            else l.Blocks.Add(block);
        });
        Timeline.AutomationBlockCreated += (t, id, sec) => _ = LaneEdit(t, id, l => Automations.AddBlock(l, sec, BarSec, Automations.PositionAt(l, Automations.Stepped(t.Track, l.Target), sec)));
        Timeline.AutomationToggled += (t, id) => _ = LaneEdit(t, id, l => l.On = !l.On);
        Timeline.AutomationFoldRequested += t =>
        {
            t.Track.AutomationFolded = !t.Track.AutomationFolded;
            MarkDirty();
            Timeline.UpdateTracks(tracks, store!.Project.Folders);
        };
        Timeline.AutomationContextRequested += ShowLaneMenu;
        Timeline.AutomationAsked += (t, target, point) =>
        {
            var list = new List<object> { AutomateItem(t, target) };
            OpenTimelineMenu(Popup(list, 240), point, MenuHeight(list));
        };
    }

    /// <summary>A lane's edit: one undo step, heard at once.</summary>
    Task LaneEdit(LiveTrack track, string id, Action<AutomationLane> change) => EditTimeline(p =>
    {
        if (p.Tracks.Find(t => t.Id == track.Track.Id) is { } t && Automations.ById(t, id) is { } lane)
        {
            change(lane);
            lane.Touch();
        }
        return track.Track.Id;
    }, null);


    /// <summary>Gives <paramref name="target"/> its lane under the track, or shows the one it has.</summary>
    Task Automate(LiveTrack track, string target, string? name = null, double? position = null) => EditTimeline(p =>
    {
        if (p.Tracks.Find(t => t.Id == track.Track.Id) is { } t) Automations.Show(t, target, position ?? Automations.Position(t, target), name, engine.LengthSec);
        return track.Track.Id;
    }, null);

    /// <summary>The name of a lane, as its control is called.</summary>
    string LaneName(LiveTrack t, AutomationLane l)
    {
        string target = l.Target;
        var slots = t.Channel.Slots;
        if (target == AutoTarget.Volume) return Loc.T("insp.volume");
        if (target == AutoTarget.Pan) return Loc.T("insp.pan");
        if (target == AutoTarget.Mute) return Loc.T("auto.mute");
        if (AutoTarget.BusOf(target) is { } bus) return Loc.T("auto.send", BusById(bus)?.Name ?? "");
        if (AutoTarget.VstOf(target) is { } v)
            return slots.Find(s => s.Id == v.Slot) is { } slot ? $"{slot.Vst?.Plugin.Name} · {l.Name}" : Loc.T("auto.gone");
        if (AutoTarget.InstrumentOf(target) != null) return $"{t.Track.Instrument?.Name ?? Loc.T("auto.gone")} · {l.Name}";
        return Automations.FxParamOf(slots, target) is { } f ? $"{SlotName(slots, f.Slot)} · {ParamName(f.Param)}" : Loc.T("auto.gone");
    }

    static string ParamName(FxParam p) =>
        p.Id is ['b', var b, '.', ..] ? Loc.T("auto.eq.band", b - '0' + 1) + " " + Loc.T(p.Label) : Loc.T(p.Label);

    static string SlotName(List<InsertSlot> slots, InsertSlot slot)
    {
        int nth = 1;
        foreach (var s in slots)
        {
            if (s == slot) break;
            if (s.Type == slot.Type) nth++;
        }
        return SlotLabel(slots, slot, nth);
    }

    /// <summary>A position (0..1) as its parameter reads.</summary>
    string LaneValue(LiveTrack t, AutomationLane l, double k)
    {
        if (l.Target == AutoTarget.Volume) return $"{Fader.Db(Fader.ToVol(k))} dB";
        if (l.Target == AutoTarget.Pan)
        {
            double pan = k * 2 - 1;
            return Math.Abs(pan) < 0.01 ? "C" : pan < 0 ? $"L{-pan * 100:0}" : $"R{pan * 100:0}";
        }
        if (l.Target == AutoTarget.Mute) return Loc.T(k >= 0.5 ? "auto.mute.on" : "auto.mute.off");
        if (Automations.FxParamOf(t.Channel.Slots, l.Target) is { } f)
            return f.Param.Toggle ? Loc.T(k >= 0.5 ? "auto.fx.onValue" : "auto.fx.offValue") : FxText(f.Param, f.Param.FromKnob(k));
        return $"{k * 100:0}%";
    }

    /// <summary>The entry that gives a parameter its lane; a tick says it has one.</summary>
    MenuItem AutomateItem(LiveTrack t, string target, string? label = null)
    {
        var shown = new AutomationLane { Target = target };
        string name = label ?? LaneName(t, shown);
        bool has = Automations.Find(t.Track, target) != null;
        return MenuAction(label != null ? name : Loc.T(has ? "auto.show" : "auto.add", name), () => Automate(t, target), icon: has ? "" : null);
    }

    /// <summary>Everything of the track that can have a lane, for its menu (the timeline's and the mixer's).</summary>
    List<object> AutomationItems(LiveTrack t)
    {
        var items = new List<object>
        {
            AutomateItem(t, AutoTarget.Volume, Loc.T("insp.volume")),
            AutomateItem(t, AutoTarget.Pan, Loc.T("insp.pan")),
            AutomateItem(t, AutoTarget.Mute, Loc.T("auto.mute")),
        };
        var sends = t.Channel.Sends.Where(s => !s.Hidden).ToList();
        if (sends.Count > 0)
        {
            items.Add(new Separator());
            foreach (var s in sends) items.Add(AutomateItem(t, AutoTarget.Send(s.BusId), Loc.T("auto.send", BusById(s.BusId)?.Name ?? "")));
        }
        var slots = t.Channel.Slots;
        if (slots.Exists(s => s.Known)) items.Add(new Separator());
        foreach (var slot in slots)
        {
            if (!slot.Known) continue;
            var sub = slot.Type == InsertSlot.VstKind
                ? PluginItems(t, LiveVst.Find?.Invoke(t.Track.Id, slot.Id), id => AutoTarget.Vst(slot.Id, id))
                : FxDefs.Automatable(slot.Type).Select(p => (object)AutomateItem(t, AutoTarget.Fx(slot.Id, p.Id), ParamName(p))).ToList();
            items.Add(new MenuItem { Header = SlotName(slots, slot), ItemsSource = sub });
        }
        if (t.Track.IsMidi && t.Track.Instrument != null)
        {
            items.Add(new Separator());
            items.Add(new MenuItem { Header = t.Track.Instrument.Name, ItemsSource = PluginItems(t, t.Instrument?.Bridge, AutoTarget.Instrument) });
        }
        return items;
    }

    /// <summary>A plugin's entries: the parameter last moved in its window and the whole list, which it has to be running to say.</summary>
    List<object> PluginItems(LiveTrack t, VstBridge? bridge, Func<uint, string> target)
    {
        if (bridge == null) return [new MenuItem { Header = Loc.T("auto.plugin.loading"), IsEnabled = false }];
        _ = ParamsOf(bridge);
        var items = new List<object>();
        if (bridge.Touched is { } id && pluginParams.TryGetValue(bridge, out var known) && known.FirstOrDefault(p => p.Id == id) is { } touched)
            items.Add(MenuAction(Loc.T("auto.plugin.touched", touched.Title), () => Automate(t, target(id), touched.Title, touched.Value)));
        items.Add(MenuAction(Loc.T("auto.plugin.pick") + "…", () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = PickPluginParam(t, bridge, target));
            return Task.CompletedTask;
        }));
        return items;
    }

    async Task<IReadOnlyList<VstParamInfo>> ParamsOf(VstBridge bridge)
    {
        if (pluginParams.TryGetValue(bridge, out var known)) return known;
        var list = await bridge.GetParamsAsync() ?? [];
        if (list.Count > 0) pluginParams.AddOrUpdate(bridge, list);
        return list;
    }

    /// <summary>A plugin's parameters can be thousands: a list with a search box instead of a menu.</summary>
    async Task PickPluginParam(LiveTrack t, VstBridge bridge, Func<uint, string> target)
    {
        var all = await ParamsOf(bridge);
        if (all.Count == 0)
        {
            Status(Loc.T("auto.plugin.none", bridge.Name));
            return;
        }
        var search = new TextBox { Classes = { "field" }, Watermark = Loc.T("auto.plugin.search"), Width = 280 };
        var list = new ListBox { Height = 320, Width = 280, ItemsSource = all.Select(p => p.Title).ToList() };
        var shown = all;
        var panel = new StackPanel { Spacing = 6, Children = { search, list } };
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Pointer };
        search.TextChanged += (_, _) =>
        {
            string q = search.Text?.Trim() ?? "";
            shown = q.Length == 0 ? all : [.. all.Where(p => p.Title.Contains(q, StringComparison.OrdinalIgnoreCase))];
            list.ItemsSource = shown.Select(p => p.Title).ToList();
        };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= shown.Count) return;
            var p = shown[list.SelectedIndex];
            flyout.Hide();
            _ = Automate(t, target(p.Id), p.Title, p.Value);
        };
        flyout.ShowAt(Timeline, true);
        search.Focus();
    }

    /// <summary>A lane's menu: the point under the pointer, then the lane.</summary>
    void ShowLaneMenu(LiveTrack t, AutomationLane lane, AutomationBlock? block, int point, double sec, Point at)
    {
        if (store == null || restoring) return;
        bool enabled = !store.ReadOnly;
        string id = lane.Id;
        var list = new List<object> { new MenuItem { Header = LaneName(t, lane), IsEnabled = false, FontSize = 11 } };
        bool stepped = Automations.Stepped(t.Track, lane.Target);
        if (block != null)
        {
            string blockId = block.Id;
            if (point >= 0)
            {
                list.Add(MenuAction(Loc.T("auto.point.remove"), () => LaneEdit(t, id, l =>
                {
                    if (Automations.BlockById(l, blockId) is { Points.Count: > 1 } b && point < b.Points.Count) b.Points.RemoveAt(point);
                }), enabled: enabled && block.Points.Count > 1));
                list.Add(new Separator());
            }
            list.Add(MenuAction(Loc.T("auto.block.copy"), () => { CopyBlock(block); return Task.CompletedTask; }, Shortcuts.Label("copy")));
            list.Add(MenuAction(Loc.T("menu.duplicate"), () => LaneEdit(t, id, l => Automations.Repeat(l, blockId, 1)), Shortcuts.Label("duplicate"), enabled));
            var repeats = new List<object>();
            foreach (int n in new[] { 2, 3, 4, 8, 16 })
                repeats.Add(MenuAction(Loc.T("auto.block.times", n), () => LaneEdit(t, id, l => Automations.Repeat(l, blockId, n)), enabled: enabled));
            repeats.Add(new Separator());
            repeats.Add(MenuAction(Loc.T("auto.block.timesOther") + "…", () =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => AskRepeat(t, id, blockId));
                return Task.CompletedTask;
            }, enabled: enabled));
            list.Add(new MenuItem { Header = Loc.T("auto.block.repeat"), ItemsSource = repeats, IsEnabled = enabled });
            list.Add(MenuAction(Loc.T("auto.block.split"), () => LaneEdit(t, id, l => Automations.Split(l, blockId, sec, stepped)),
                enabled: enabled && sec > block.AtSec + AutomationBlock.MinSec && sec < block.EndSec - AutomationBlock.MinSec));
            list.Add(MenuAction(Loc.T("auto.flat"), () => LaneEdit(t, id, l =>
            {
                if (Automations.BlockById(l, blockId) is { } b) b.Points = [new AutomationPoint { Sec = 0, Value = b.PositionAt(sec - b.AtSec, stepped) }];
            }), enabled: enabled, tip: Loc.T("auto.flat.tip")));
            list.Add(MenuAction(Loc.T("auto.block.remove"), () => LaneEdit(t, id, l => l.Blocks.RemoveAll(b => b.Id == blockId)), "Supr", enabled, danger: true));
        }
        else list.Add(MenuAction(Loc.T("auto.block.new"), () => LaneEdit(t, id, l => Automations.AddBlock(l, sec, BarSec, Automations.PositionAt(l, stepped, sec))), enabled: enabled, tip: Loc.T("auto.block.new.tip")));
        list.Add(MenuAction(Loc.T("auto.block.paste"), () => PasteBlock(t, id, sec), Shortcuts.Label("paste"), enabled && blockClipboard != null));
        list.Add(new Separator());
        list.Add(MenuAction(Loc.T(lane.On ? "auto.off" : "auto.on"), () => LaneEdit(t, id, l => l.On = !l.On), enabled: enabled));
        list.Add(MenuAction(Loc.T("auto.hide"), () => LaneEdit(t, id, l => l.Hidden = true), enabled: enabled, tip: Loc.T("auto.hide.tip")));
        if (lane.Blocks.Exists(b => b.Points.Exists(p => p.Curve != 0)))
            list.Add(MenuAction(Loc.T("auto.straight"), () => LaneEdit(t, id, l =>
            {
                foreach (var b in l.Blocks)
                    foreach (var p in b.Points) p.Curve = 0;
            }), enabled: enabled, tip: Loc.T("auto.straight.tip")));
        list.Add(new Separator());
        list.Add(MenuAction(Loc.T("auto.remove"), () => EditTimeline(p =>
        {
            if (p.Tracks.Find(x => x.Id == t.Track.Id) is { } x2) Automations.Remove(x2, id);
            return t.Track.Id;
        }, null), enabled: enabled, danger: true));
        OpenTimelineMenu(Popup(list, 240), at, MenuHeight(list));
    }

    /// <summary>A copied block: it pastes on any lane, as positions of that lane's control.</summary>
    AutomationBlock? blockClipboard;

    void CopyBlock(AutomationBlock block) => blockClipboard = block.Copy(block.AtSec);

    Task PasteBlock(LiveTrack t, string laneId, double sec) => blockClipboard is { } b ? LaneEdit(t, laneId, l => l.Blocks.Add(b.Copy(sec))) : Task.CompletedTask;

    /// <summary>How many copies, for a number the menu doesn't offer.</summary>
    void AskRepeat(LiveTrack t, string laneId, string blockId)
    {
        var box = new TextBox { Classes = { "field" }, Text = "32", Width = 120 };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 6, Children = { new TextBlock { Classes = { "dim" }, Text = Loc.T("auto.block.howMany") }, box } }, Placement = PlacementMode.Pointer };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            flyout.Hide();
            if (int.TryParse(box.Text, out int n) && n > 0) _ = LaneEdit(t, laneId, l => Automations.Repeat(l, blockId, n));
        };
        flyout.ShowAt(Timeline, true);
        box.SelectAll();
        box.Focus();
    }

    /// <summary>The shortcuts on the block picked in a lane. True when one took the key.</summary>
    bool BlockShortcut(string action)
    {
        if (store is not { ReadOnly: false } || Timeline.SelectedBlock is not { } s) return false;
        string lane = s.Lane.Id, id = s.Block.Id;
        switch (action)
        {
            case "duplicate": _ = LaneEdit(s.Track, lane, l => Automations.Repeat(l, id, 1)); return true;
            case "delete": _ = LaneEdit(s.Track, lane, l => l.Blocks.RemoveAll(b => b.Id == id)); return true;
            case "copy": CopyBlock(s.Block); return true;
            case "paste" when blockClipboard != null: _ = PasteBlock(s.Track, lane, engine.PositionSec); return true;
            default: return false;
        }
    }

    /// <summary>
    /// A control of a track: a right click offers its lane, and with <paramref name="show"/> it follows that lane while
    /// it is on screen. Does nothing for an owner that isn't a track (lanes are the tracks').
    /// </summary>
    void AutomateOn(Control c, Func<LiveTrack?> track, string target, Action<double>? show = null)
    {
        // not ContextRequested: knobs and faders take their release and it never gets raised
        c.AddHandler(PointerReleasedEvent, (object? _, PointerReleasedEventArgs e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Right || track() is not { } t || store is not { ReadOnly: false }) return;
            e.Handled = true;
            OpenAnchoredMenu(Popup(new List<object> { AutomateItem(t, target) }, 240), c);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        if (show != null) FollowLane(c, track, target, show);
    }

    /// <summary>A control that shows a lane while it is on screen, without touching the channel.</summary>
    void FollowLane(Control c, Func<LiveTrack?> track, string target, Action<double> show)
    {
        var view = new LaneView(track, target, show);
        c.AttachedToVisualTree += (_, _) => laneViews.Add(view);
        c.DetachedFromVisualTree += (_, _) => laneViews.Remove(view);
        if (c.GetVisualRoot() != null) laneViews.Add(view);
    }

    LiveTrack? TrackOf(SlotOwner o)
    {
        foreach (var t in tracks) if (t.Track.Id == o.OwnerId) return t;
        return null;
    }

    /// <summary>Every tick: the native effects' lanes at the playhead, and the controls that show a lane. Allocates nothing while nothing moves.</summary>
    void FollowLanes(double pos)
    {
        bool playing = engine.Playing, moved = false;
        foreach (var t in tracks) moved |= t.TickAutomation(pos, playing);
        if (moved) Timeline.InvalidateVisual();
        for (int i = 0; i < laneViews.Count; i++)
        {
            var v = laneViews[i];
            if (v.Track() is not { Track.Automation: not null } t || Automations.Find(t.Track, v.Target) is not { On: true, Blocks.Count: > 0 } lane) continue;
            double k = Automations.PositionAt(lane, false, pos);
            if (Math.Abs(k - v.Last) < 0.002) continue;
            v.Last = k;
            v.Show(k);
        }
    }
}
