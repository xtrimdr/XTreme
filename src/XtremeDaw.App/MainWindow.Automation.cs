using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using XtremeDaw.App.Audio;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Dialogs;
using XtremeDaw.App.Model;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Audio.Vst;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// Automation (asked by the user: anything a channel has, audio or MIDI): a right click on a control, or the track's
/// menu, gives that parameter a lane, a child of the track in the timeline (<c>TimelineView.Automation.cs</c>). A folder
/// is a channel too (#136): its lanes go the same way, under its row, and so are a bus's (#267), under its row at the end
/// of the timeline. Lanes live on their owner, the track, the folder or the bus
/// (<see cref="IAutomated"/>, <see cref="Automations"/>), and never change the channel: the controls follow them while
/// they play (<see cref="FollowLanes"/>), and what the channel holds comes back when a lane goes off.
/// </summary>
public partial class MainWindow
{
    /// <summary>A control that shows a lane: its track or folder, its target and how it shows a position.</summary>
    sealed class LaneView(Func<IAutomated?> owner, string target, Action<double> show)
    {
        public readonly Func<IAutomated?> Owner = owner;
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
        Timeline.AutomationBlockCreated +=(t, id, sec) => _ = LaneEdit(t, id, l => Automations.AddBlock(l, sec, BarSec(sec), Automations.PositionAt(l, Automations.Stepped(t, l.Target), sec)));
        Timeline.AutomationToggled += (t, id) => _ = LaneEdit(t, id, l => l.On = !l.On);
        Timeline.AutomationFoldRequested += FoldLanes;
        Timeline.AutomationContextRequested += ShowLaneMenu;
        Timeline.AutomationShapeRequested += ShowShapeMenu;
        Timeline.AutomationRemoveRequested += AskRemoveLane;
        Timeline.AutomationSplitRequested += (t, laneId, blockId, sec) => _ = SplitBlock(t, laneId, blockId, sec);
        Timeline.AutomationPainted += (t, laneId, brush, starts) => _ = PaintBlocks(t, laneId, brush, starts);
        Timeline.AutomationAsked += (t, target, point) =>
        {
            var list = new List<object> { AutomateItem(t, target) };
            OpenTimelineMenu(Popup(list, 240), point, MenuHeight(list));
        };
    }

    /// <summary>The project's track, folder or bus by id (the objects change with every undo).</summary>
    /// <summary>Every lane of the project: the tracks', the folders' and the buses'.</summary>
    static IEnumerable<AutomationLane> LanesIn(Project p) =>
        p.Tracks.Cast<IAutomated>().Concat(p.Folders).Concat(p.Settings.Buses).SelectMany(o => o.Automation ?? []);

    /// <summary>The ids of every automation block, for Seleccionar todo (#281).</summary>
    static IEnumerable<string> AllBlockIds(Project p) => LanesIn(p).SelectMany(l => l.Blocks.Select(b => b.Id));

    /// <summary>Boxed blocks dragged with the clips (#281): each one to its new start, in its own lane.</summary>
    static void MoveBlocks(Project p, IReadOnlyList<(string Id, double AtSec)> moves)
    {
        if (moves.Count == 0) return;
        foreach (var lane in LanesIn(p))
        {
            bool touched = false;
            foreach (var b in lane.Blocks)
                foreach (var (id, at) in moves)
                    if (b.Id == id) { b.AtSec = Math.Max(0, at); touched = true; }
            if (touched) lane.Touch();
        }
    }

    /// <summary>
    /// Shift+drag of a box (#281): a copy of each carried block stays where it was, in its lane, as a clip's does
    /// (<see cref="MidiEditing.LeaveCopies"/>). Gives back the ids that are not blocks, the clips' to copy.
    /// </summary>
    static List<string> LeaveBlockCopies(Project p, List<string> ids)
    {
        if (ids.Count == 0) return ids;
        var rest = new HashSet<string>(ids);
        foreach (var lane in LanesIn(p))
        {
            bool touched = false;
            for (int i = lane.Blocks.Count - 1; i >= 0; i--)
            {
                var b = lane.Blocks[i];
                if (!rest.Remove(b.Id)) continue;
                lane.Blocks.Insert(i, b.Copy(b.AtSec));
                touched = true;
            }
            if (touched) lane.Touch();
        }
        return [.. ids.Where(rest.Contains)];
    }

    /// <summary>Boxed blocks edited with the clips (#281): each one in place of the block with its id.</summary>
    static void ReplaceBlocks(Project p, IReadOnlyList<AutomationBlock> blocks)
    {
        foreach (var b in blocks)
            if (Automations.FindBlock(p, b.Id) is var (lane, old))
            {
                lane.Blocks[lane.Blocks.IndexOf(old)] = b;
                lane.Touch();
            }
    }

    /// <summary>Boxed blocks deleted with the clips (#281).</summary>
    static void RemoveBlocks(Project p, IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return;
        foreach (var lane in LanesIn(p))
            if (lane.Blocks.RemoveAll(b => ids.Contains(b.Id)) > 0) lane.Touch();
    }

    static IAutomated? OwnerIn(Project p, string id) => (IAutomated?)p.Tracks.Find(t => t.Id == id) ?? (IAutomated?)p.Folders.Find(f => f.Id == id) ?? p.Settings.Buses.Find(b => b.Id == id);

    /// <summary>A track's live side (its instrument's process); null for a folder.</summary>
    LiveTrack? LiveOf(IAutomated o) => o is Projects.Track tr ? tracks.FirstOrDefault(t => t.Track.Id == tr.Id) : null;

    /// <summary>Folds or shows a track's, a folder's or a bus's lanes (a view setting: no undo step).</summary>
    void FoldLanes(IAutomated owner)
    {
        if (store == null || OwnerIn(store.Project, owner.Id) is not { } o) return;
        o.AutomationFolded = !o.AutomationFolded;
        MarkDirty();
        SyncBusRows();
        Timeline.UpdateTracks(tracks, store.Project.Folders);
    }

    /// <summary>A lane's edit: one undo step, heard at once.</summary>
    Task LaneEdit(IAutomated owner, string id, Action<AutomationLane> change) => EditTimeline(p =>
    {
        if (OwnerIn(p, owner.Id) is { } o && Automations.ById(o, id) is { } lane)
        {
            change(lane);
            lane.Touch();
        }
        return owner.Id;
    }, null);


    /// <summary>Gives <paramref name="target"/> its lane under the track (or folder), or shows the one it has.</summary>
    Task Automate(IAutomated owner, string target, string? name = null, double? position = null) => EditTimeline(p =>
    {
        if (OwnerIn(p, owner.Id) is { } o) Automations.Show(o, target, position ?? Automations.Position(o, target), name, engine.LengthSec);
        return owner.Id;
    }, null);

    /// <summary>The name of a lane, as its control is called.</summary>
    string LaneName(IAutomated t, AutomationLane l)
    {
        string target = l.Target;
        var slots = t.AutoSlots;
        var track = t as Projects.Track;
        if (target == AutoTarget.Volume) return Loc.T("insp.volume");
        if (target == AutoTarget.Pan) return Loc.T("insp.pan");
        if (target == AutoTarget.Mute) return Loc.T("auto.mute");
        if (AutoTarget.BusOf(target) is { } bus) return Loc.T("auto.send", BusById(bus)?.Name ?? "");
        if (AutoTarget.VstOf(target) is { } v)
            return slots.FirstOrDefault(s => s.Id == v.Slot) is { } slot ? $"{slot.Vst?.Plugin.Name} · {l.Name}" : Loc.T("auto.gone");
        if (AutoTarget.InstrumentOf(target) != null) return $"{track?.Instrument?.Name ?? Loc.T("auto.gone")} · {l.Name}";
        if (AutoTarget.SamplerOf(target) is { } knob) return track?.Sampler != null ? $"{Loc.T("sampler.name")} · {SamplerKnobName(knob)}" : Loc.T("auto.gone");
        if (AutoTarget.GranularOf(target) is { } grain) return track?.Granular != null ? $"{Loc.T("granular.name")} · {GranularKnobName(grain)}" : Loc.T("auto.gone");
        return Automations.FxParamOf(slots, target) is { } f ? $"{SlotName(slots, f.Slot)} · {ParamName(f.Param)}" : Loc.T("auto.gone");
    }

    static string SamplerKnobName(SamplerKnob knob) => Loc.T("auto.sampler." + SamplerKnobs.Ids[(int)knob]);

    static string GranularKnobName(GranularKnob knob) => Loc.T("auto.granular." + GranularKnobs.Ids[(int)knob]);

    static string ParamName(FxParam p) =>
        p.Id is ['b', var b, '.', ..] ? Loc.T("auto.eq.band", b - '0' + 1) + " " + Loc.T(p.Label) : Loc.T(p.Label);

    static string SlotName(IReadOnlyList<InsertSlot> slots, InsertSlot slot)
    {
        int nth = 1;
        foreach (var s in slots)
        {
            if (s == slot) break;
            if (s.Type == slot.Type) nth++;
        }
        return SlotLabel(slots as List<InsertSlot> ?? [.. slots], slot, nth);
    }

    /// <summary>A position (0..1) as its parameter reads.</summary>
    string LaneValue(IAutomated t, AutomationLane l, double k)
    {
        if (l.Target == AutoTarget.Volume) return ValueFormat.Db(Fader.ToVol(k));
        if (l.Target == AutoTarget.Pan) return ValueFormat.PanShort(k * 2 - 1);
        if (l.Target == AutoTarget.Mute) return Loc.T(k >= 0.5 ? "auto.mute.on" : "auto.mute.off");
        if (AutoTarget.SamplerOf(l.Target) is { } knob) return SamplerWindow.KnobText(knob, SamplerKnobs.Value(knob, k));
        if (AutoTarget.GranularOf(l.Target) is { } grain) return GranularWindow.KnobText(grain, GranularKnobs.Value(grain, k));
        if (Automations.FxParamOf(t.AutoSlots, l.Target) is { } f)
            return f.Param.Toggle ? Loc.T(k >= 0.5 ? "auto.fx.onValue" : "auto.fx.offValue") : FxText(f.Param, f.Param.FromKnob(k));
        return $"{k * 100:0}%";
    }

    /// <summary>The entry that gives a parameter its lane; a tick says it has one.</summary>
    MenuItem AutomateItem(IAutomated t, string target, string? label = null)
    {
        var shown = new AutomationLane { Target = target };
        string name = label ?? LaneName(t, shown);
        bool has = Automations.Find(t, target) != null;
        return MenuAction(label != null ? name : Loc.T(has ? "auto.show" : "auto.add", name), () => Automate(t, target), icon: has ? "" : null);
    }

    /// <summary>Everything of the track (or folder) that can have a lane, for its menu (the timeline's and the mixer's).</summary>
    List<object> AutomationItems(IAutomated t)
    {
        var track = t as Projects.Track;
        var items = new List<object>
        {
            AutomateItem(t, AutoTarget.Volume, Loc.T("insp.volume")),
            AutomateItem(t, AutoTarget.Pan, Loc.T("insp.pan")),
            AutomateItem(t, AutoTarget.Mute, Loc.T("auto.mute")),
        };
        var sends = t.AutoRouted.Sends.Where(s => !s.Hidden).ToList();
        if (sends.Count > 0)
        {
            items.Add(new Separator());
            foreach (var s in sends) items.Add(AutomateItem(t, AutoTarget.Send(s.BusId), Loc.T("auto.send", BusById(s.BusId)?.Name ?? "")));
        }
        var slots = t.AutoSlots;
        if (slots.Any(s => s.Known)) items.Add(new Separator());
        foreach (var slot in slots)
        {
            if (!slot.Known) continue;
            var sub = slot.Type == InsertSlot.VstKind
                ? PluginItems(t, LiveVst.Find?.Invoke(t.Id, slot.Id), id => AutoTarget.Vst(slot.Id, id))
                : FxDefs.Automatable(slot.Type).Select(p => (object)AutomateItem(t, AutoTarget.Fx(slot.Id, p.Id), ParamName(p))).ToList();
            items.Add(new MenuItem { Header = SlotName(slots, slot), ItemsSource = sub });
        }
        if (track is { IsMidi: true, Instrument: { } inst })
        {
            items.Add(new Separator());
            items.Add(new MenuItem { Header = inst.Name, ItemsSource = PluginItems(t, LiveOf(t)?.Instrument?.Bridge, AutoTarget.Instrument) });
        }
        if (track?.Sampler != null)
        {
            items.Add(new Separator());
            items.Add(new MenuItem { Header = Loc.T("sampler.name"), ItemsSource = Enum.GetValues<SamplerKnob>().Select(k => (object)AutomateItem(t, AutoTarget.Sampler(k), SamplerKnobName(k))).ToList() });
        }
        if (track?.Granular != null)
        {
            items.Add(new Separator());
            items.Add(new MenuItem { Header = Loc.T("granular.name"), ItemsSource = Enum.GetValues<GranularKnob>().Select(k => (object)AutomateItem(t, AutoTarget.Granular(k), GranularKnobName(k))).ToList() });
        }
        return items;
    }

    /// <summary>A plugin's entries: the parameter last moved in its window and the whole list, which it has to be running to say.</summary>
    List<object> PluginItems(IAutomated t, VstBridge? bridge, Func<uint, string> target)
    {
        if (bridge == null) return [new MenuItem { Header = Loc.T("auto.plugin.loading"), IsEnabled = false }];
        _ = ParamsOf(bridge);
        var items = new List<object>();
        if (bridge.Touched is { } id && pluginParams.TryGetValue(bridge, out var known) && known.FirstOrDefault(p => p.Id == id) is { } touched)
            items.Add(MenuAction(Loc.T("auto.plugin.touched", touched.Title), () => Automate(t, target(id), touched.Title, touched.Value)));
        items.Add(MenuAction(Loc.T("auto.plugin.pick"), () =>
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
    async Task PickPluginParam(IAutomated t, VstBridge bridge, Func<uint, string> target)
    {
        var all = await ParamsOf(bridge);
        if (all.Count == 0)
        {
            Toast(Loc.T("auto.plugin.none", bridge.Name));
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
    void ShowLaneMenu(IAutomated t, AutomationLane lane, AutomationBlock? block, int point, double sec, Point at)
    {
        if (store == null || restoring) return;
        bool enabled = !store.ReadOnly;
        string id = lane.Id;
        var list = new List<object> { new MenuItem { Header = LaneName(t, lane), IsEnabled = false, FontSize = 11 } };
        bool stepped = Automations.Stepped(t, lane.Target);
        if (block != null)
        {
            string blockId = block.Id;
            if (point >= 0)
            {
                list.Add(PointValueItem(t, lane, block, point, enabled));
                list.Add(MenuAction(Loc.T("auto.point.remove"), () => LaneEdit(t, id, l =>
                {
                    if (Automations.BlockById(l, blockId) is { Points.Count: > 1 } b && point < b.Points.Count) b.Points.RemoveAt(point);
                }), enabled: enabled && block.Points.Count > 1));
                list.Add(new Separator());
            }
            list.Add(MenuAction(Loc.T("menu.cut"), () => CutBlock(t, id, block), Shortcuts.Label("cut"), enabled));
            list.Add(MenuAction(Loc.T("auto.block.copy"), () => { CopyBlock(block); return Task.CompletedTask; }, Shortcuts.Label("copy")));
            list.Add(MenuAction(Loc.T("menu.duplicate"), () => LaneEdit(t, id, l => Automations.Repeat(l, blockId, 1)), Shortcuts.Label("duplicate"), enabled));
            var repeats = new List<object>();
            foreach (int n in new[] { 2, 3, 4, 8, 16 })
                repeats.Add(MenuAction(Loc.T("auto.block.times", n), () => LaneEdit(t, id, l => Automations.Repeat(l, blockId, n)), enabled: enabled));
            repeats.Add(new Separator());
            repeats.Add(MenuAction(Loc.T("auto.block.timesOther"), () =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => AskRepeat(t, id, blockId));
                return Task.CompletedTask;
            }, enabled: enabled));
            list.Add(new MenuItem { Header = Loc.T("auto.block.repeat"), ItemsSource = repeats, IsEnabled = enabled });
            list.Add(MenuAction(Loc.T("auto.block.split"), () => SplitBlock(t, id, blockId, sec),
                enabled: enabled && Automations.CanSplit(block, sec)));
            double grid = Timeline.SnapSec;
            list.Add(MenuAction(Loc.T("menu.alignGrid"), () => AlignBlock(t, id, blockId), Shortcuts.Label("alignGrid"), enabled && grid > 0,
                tip: grid > 0 ? Loc.T("menu.alignGrid.tip", SnapChoices.Label(Timeline.SnapMode)) : Loc.T("menu.alignGrid.off")));
            list.Add(MenuAction(Loc.T("auto.flat"), () => LaneEdit(t, id, l =>
            {
                if (Automations.BlockById(l, blockId) is { } b) b.Points = [new AutomationPoint { Sec = 0, Value = b.PositionAt(sec - b.AtSec, stepped) }];
            }), enabled: enabled, tip: Loc.T("auto.flat.tip")));
        }
        else list.Add(MenuAction(Loc.T("auto.block.new"), () => LaneEdit(t, id, l => Automations.AddBlock(l, sec, BarSec(sec), Automations.PositionAt(l, stepped, sec))), enabled: enabled, tip: Loc.T("auto.block.new.tip")));
        list.Add(MenuAction(Loc.T("auto.block.paste"), () => PasteBlock(t, id, sec), Shortcuts.Label("paste"), enabled && blockClipboard != null));
        list.Add(new Separator());
        // looked up by id at each click: a right click keeps the menu open while the lane changes
        string trackId = t.Id;
        bool LaneOn() => store != null && OwnerIn(store.Project, trackId) is { } tr && Automations.ById(tr, id)?.On == true;
        list.Add(MenuCheck(Loc.T("auto.active"), LaneOn, () => _ = LaneEdit(t, id, l => l.On = !l.On), [], tip: Loc.T("auto.active.tip"), enabled: enabled));
        list.Add(MenuAction(Loc.T("auto.hide"), () => LaneEdit(t, id, l => l.Hidden = true), enabled: enabled, tip: Loc.T("auto.hide.tip")));
        if (lane.Blocks.Exists(b => b.Points.Exists(p => p.Shape != AutoShape.Curve || p.Tension != 0)))
            list.Add(MenuAction(Loc.T("auto.straight"), () => LaneEdit(t, id, l =>
            {
                foreach (var b in l.Blocks)
                    foreach (var p in b.Points) (p.Shape, p.Tension) = (AutoShape.Curve, 0);
            }), enabled: enabled, tip: Loc.T("auto.straight.tip")));
        list.Add(new Separator());
        if (block != null)
        {
            string blockId = block.Id;
            list.Add(MenuAction(Loc.T("auto.block.remove"), () => LaneEdit(t, id, l => l.Blocks.RemoveAll(b => b.Id == blockId)), Shortcuts.First("delete"), enabled, danger: true));
        }
        list.Add(MenuAction(Loc.T("auto.remove"), () =>
        {
            AskRemoveLane(t, lane, at);
            return Task.CompletedTask;
        }, enabled: enabled, danger: true));
        OpenTimelineMenu(Popup(list, 240), at, MenuHeight(list));
    }

    /// <summary>Removing a lane asks first, under its trash can or where its menu was (asked by the user, #152): its curve goes with it.</summary>
    void AskRemoveLane(IAutomated t, AutomationLane lane, Point at)
    {
        if (store == null || restoring || store.ReadOnly) return;
        string id = lane.Id;
        var list = new List<object>
        {
            new MenuItem { Header = Loc.T("auto.remove.ask", LaneName(t, lane)), IsEnabled = false, FontSize = 11 },
            MenuAction(Loc.T("auto.remove.yes"), () => RemoveLane(t, id), danger: true, tip: Loc.T("auto.remove.yes.tip")),
            MenuAction(Loc.T("common.cancel"), () => Task.CompletedTask),
        };
        OpenAsk(Popup(list, 240), at, null);
    }

    /// <summary>Takes a lane out of its track or folder, one step to undo.</summary>
    Task RemoveLane(IAutomated t, string id) => EditTimeline(p =>
    {
        if (OwnerIn(p, t.Id) is { } x) Automations.Remove(x, id);
        return t.Id;
    }, null);

    /// <summary>
    /// A stretch's menu, from a right click on a point or on the stretch: only its shape (asked by the user, like FL
    /// Studio), each with how it looks; a right click on a shape tries it and leaves the menu open. Picking another shape
    /// starts it at tension 0 (the steps and cycles of one are not the bend of another).
    /// </summary>
    void ShowShapeMenu(IAutomated t, AutomationLane lane, AutomationBlock block, int stretch, int point, Point at)
    {
        if (store == null || restoring) return;
        bool enabled = !store.ReadOnly, onPoint = point >= 0;
        string trackId = t.Id, id = lane.Id, blockId = block.Id;
        // looked up at each click: the menu can stay open while the stretch changes
        AutomationPoint? Now() => store != null && OwnerIn(store.Project, trackId) is { } tr && Automations.ById(tr, id) is { } l
            && Automations.BlockById(l, blockId) is { } b && stretch + 1 < b.Points.Count ? b.Points[stretch] : null;
        Task Edit(Action<AutomationBlock, AutomationPoint> change) => LaneEdit(t, id, l =>
        {
            if (Automations.BlockById(l, blockId) is { } b && stretch + 1 < b.Points.Count) change(b, b.Points[stretch]);
        });
        var list = new List<object>();
        if (onPoint)
        {
            list.Add(PointValueItem(t, lane, block, point, enabled));
            list.Add(new Separator());
        }
        list.Add(new MenuItem { Header = Loc.T(onPoint ? "auto.shape.point" : "auto.shape.stretch"), IsEnabled = false, FontSize = 11 });
        var refresh = new List<Action>();
        foreach (var shape in Enum.GetValues<AutoShape>())
            list.Add(ShapeItem(shape, () => Now()?.Shape == shape, () => _ = Edit((_, p) =>
            {
                if (p.Shape != shape) (p.Shape, p.Tension) = (shape, 0);
            }), refresh, enabled));
        list.Add(new Separator());
        list.Add(MenuAction(Loc.T("auto.tension.reset"), () => Edit((_, p) => p.Tension = 0),
            enabled: enabled && block.Points[stretch].Tension != 0, tip: Loc.T("auto.tension.reset.tip")));
        list.Add(MenuAction(Loc.T("auto.shape.block"), () => Edit((b, p) =>
        {
            foreach (var x in b.Points) (x.Shape, x.Tension) = (p.Shape, p.Tension);
        }), enabled: enabled && block.Points.Count > 2, tip: Loc.T("auto.shape.block.tip")));
        OpenTimelineMenu(Popup(list, 220), at, MenuHeight(list));
    }

    /// <summary>"Escribir valor…" on a point (#296): its value by keyboard, in the parameter's own units.</summary>
    MenuItem PointValueItem(IAutomated t, AutomationLane lane, AutomationBlock block, int point, bool enabled) =>
        MenuAction(Loc.T("auto.point.value"), () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => AskPointValue(t, lane, block, point));
            return Task.CompletedTask;
        }, enabled: enabled && point < block.Points.Count);

    /// <summary>A box with the point's value as its lane reads it; Enter sets what was typed, Esc leaves it.</summary>
    void AskPointValue(IAutomated t, AutomationLane lane, AutomationBlock block, int point)
    {
        if (point >= block.Points.Count) return;
        string id = lane.Id, blockId = block.Id;
        var box = new TextBox { Classes = { "field", "mono" }, Text = LaneValue(t, lane, block.Points[point].Value), Width = 160 };
        var flyout = new Flyout
        {
            Content = new StackPanel { Spacing = 6, Children = { new TextBlock { Classes = { "dim" }, Text = LaneName(t, lane) }, box } },
            Placement = PlacementMode.Pointer,
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; flyout.Hide(); return; }
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            flyout.Hide();
            if (PositionOf(t, lane, box.Text ?? "") is not { } k)
            {
                Toast(Loc.T("auto.point.value.bad", box.Text ?? ""));
                return;
            }
            _ = LaneEdit(t, id, l =>
            {
                if (Automations.BlockById(l, blockId) is { } b && point < b.Points.Count) b.Points[point].Value = k;
            });
        };
        flyout.ShowAt(Timeline, true);
        box.SelectAll();
        box.Focus();
    }

    /// <summary>
    /// What was typed as a position (0..1) of the lane, or null: a number in the units its values show (dB, Hz with "k",
    /// ms, %, L/R for pan), or on/off for a switch. The position is the one whose reading comes nearest, so any curve works.
    /// </summary>
    double? PositionOf(IAutomated t, AutomationLane l, string text)
    {
        string s = text.Trim().ToLowerInvariant();
        if (s.Length == 0) return null;
        if (Automations.Stepped(t, l.Target))
        {
            string on = LaneValue(t, l, 1).ToLowerInvariant(), off = LaneValue(t, l, 0).ToLowerInvariant();
            if (s == on || s is "on" or "1" or "si" or "sí" or "yes") return 1;
            if (s == off || s is "off" or "0" or "no") return 0;
            return null;
        }
        bool pan = l.Target == AutoTarget.Pan || AutoTarget.SamplerOf(l.Target) == SamplerKnob.Pan || AutoTarget.GranularOf(l.Target) == GranularKnob.Pan;
        if (pan && (s == "c" || s == LaneValue(t, l, 0.5).ToLowerInvariant() || s.StartsWith(Loc.T("value.pan.center").ToLowerInvariant()))) return 0.5;
        if (l.Target == AutoTarget.Volume && (s.Contains('∞') || s.Contains("inf"))) return 0;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"[-+]?\d+(?:[.,]\d+)?|[-+]?[.,]\d+");
        if (!m.Success || !double.TryParse(m.Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v))
        {
            // no number: a reading by its name (an effect's mode, "Apagado")
            for (int i = 0; i <= NamedSamples; i++)
                if (LaneValue(t, l, i / (double)NamedSamples).Trim().ToLowerInvariant() == s) return Math.Round(i / (double)NamedSamples, 4);
            return null;
        }
        string unit = s[(m.Index + m.Length)..].Trim();
        // a pan's side: L (or I, izquierda) to the left, R (or D, derecha) to the right, before or after the number
        if (pan && (s.StartsWith('l') || s.StartsWith('i') || unit.StartsWith('l') || unit.StartsWith('i'))) v = -Math.Abs(v);
        Func<double, double> reading;
        if (l.Target == AutoTarget.Volume) reading = k => 20 * Math.Log10(Math.Max(1e-9, Fader.ToVol(k)));
        else if (pan) reading = k => (k * 2 - 1) * 100;
        else if (AutoTarget.SamplerOf(l.Target) is { } knob)
        {
            if (knob == SamplerKnob.Cutoff && unit.StartsWith('k')) v *= 1000;
            reading = knob is SamplerKnob.Cutoff or SamplerKnob.Gain or SamplerKnob.LfoPitch or SamplerKnob.LfoFilter
                ? k => SamplerKnobs.Value(knob, k) : k => SamplerKnobs.Value(knob, k) * 100;
        }
        else if (AutoTarget.GranularOf(l.Target) is { } grain)
        {
            if (grain is GranularKnob.Size or GranularKnob.Spray && unit.StartsWith("ms")) v /= 1000;
            reading = grain is GranularKnob.Density or GranularKnob.Size or GranularKnob.Spray or GranularKnob.Gain
                ? k => GranularKnobs.Value(grain, k) : k => GranularKnobs.Value(grain, k) * 100;
        }
        else if (Automations.FxParamOf(t.AutoSlots, l.Target) is { } f)
        {
            var param = f.Param;
            if (param.Unit == FxUnit.Hz && unit.StartsWith('k')) v *= 1000;
            if (param.Unit == FxUnit.Ms && (unit.StartsWith('µ') || unit.StartsWith("us"))) v /= 1000;
            reading = param.Unit == FxUnit.Percent ? k => param.FromKnob(k) * 100 : k => param.FromKnob(k);
        }
        else reading = k => k * 100;
        // the nearest reading over the whole travel: steps, logs and squares alike
        const int Samples = 10000;
        double best = 0, gap = double.MaxValue;
        for (int i = 0; i <= Samples; i++)
        {
            double k = i / (double)Samples, d = Math.Abs(reading(k) - v);
            if (d < gap) (best, gap) = (k, d);
        }
        return Math.Round(best, 4);
    }

    /// <summary>Positions looked at for a reading typed by its name.</summary>
    const int NamedSamples = 200;

    /// <summary>A shape's entry: its check, a drawing of how it goes up, and its name.</summary>
    static MenuItem ShapeItem(AutoShape shape, Func<bool> isOn, Action apply, List<Action> refresh, bool enabled)
    {
        var item = MenuCheck(Loc.T(ShapeKey(shape)), isOn, apply, refresh, enabled: enabled);
        // MenuAction puts the check and the label in the header's first row: the drawing goes between them
        var row = (Grid)((Grid)item.Header!).Children[0];
        row.ColumnDefinitions = new ColumnDefinitions("20,28,*");
        Grid.SetColumn(row.Children[1], 2);
        var line = new Avalonia.Controls.Shapes.Polyline
        {
            Stroke = Palette.Accent, StrokeThickness = 1.3, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            StrokeJoin = Avalonia.Media.PenLineJoin.Round, Points = ShapeDrawing(shape),
        };
        Grid.SetColumn(line, 1);
        row.Children.Add(line);
        return item;
    }

    /// <summary>The menu's drawing of a shape, rising over its box, at a tension that shows it (few steps, a soft bend).</summary>
    static Avalonia.Collections.AvaloniaList<Point> ShapeDrawing(AutoShape shape)
    {
        const double W = 18, H = 11;
        const int Samples = 72;
        double tension = shape is AutoShape.Curve or AutoShape.Double ? 0.45 : shape == AutoShape.Stairs ? -0.5 : -1;
        var pts = new Avalonia.Collections.AvaloniaList<Point>();
        for (int i = 0; i <= Samples; i++)
        {
            double u = i / (double)Samples;
            pts.Add(new Point(W * u, H * (1 - AutoShapes.Along(shape, Math.Min(u, 0.9999), tension))));
        }
        if (shape == AutoShape.Hold) pts.Add(new Point(W, 0));
        return pts;
    }

    static string ShapeKey(AutoShape shape) => shape switch
    {
        AutoShape.Double => "auto.shape.double",
        AutoShape.Hold => "auto.shape.hold",
        AutoShape.Stairs => "auto.shape.stairs",
        AutoShape.SmoothStairs => "auto.shape.smoothStairs",
        AutoShape.Pulse => "auto.shape.pulse",
        AutoShape.Wave => "auto.shape.wave",
        _ => "auto.shape.curve",
    };

    /// <summary>A copied block: it pastes on any lane, as positions of that lane's control.</summary>
    AutomationBlock? blockClipboard;

    void CopyBlock(AutomationBlock block) => blockClipboard = block.Copy(block.AtSec);

    Task PasteBlock(IAutomated t, string laneId, double sec) => blockClipboard is { } b ? LaneEdit(t, laneId, l => l.Blocks.Add(b.Copy(sec))) : Task.CompletedTask;

    /// <summary>How many copies, for a number the menu doesn't offer.</summary>
    void AskRepeat(IAutomated t, string laneId, string blockId)
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
        // with several boxed (#281), the keys act on all of them, not on this block alone
        if (Timeline.BoxedIds.Count > 1) return false;
        string lane = s.Lane.Id, id = s.Block.Id;
        switch (action)
        {
            case "duplicate": _ = LaneEdit(s.Owner, lane, l => Automations.Repeat(l, id, 1)); return true;
            case "delete": _ = LaneEdit(s.Owner, lane, l => l.Blocks.RemoveAll(b => b.Id == id)); return true;
            case "copy": CopyBlock(s.Block); return true;
            case "cut": _ = CutBlock(s.Owner, lane, s.Block); return true;
            case "alignGrid": _ = AlignBlock(s.Owner, lane, id); return true;
            case "paste" when blockClipboard != null: _ = PasteBlock(s.Owner, lane, engine.PositionSec); return true;
            default: return false;
        }
    }

    /// <summary>Ctrl+X on a block (#281, as on a clip): copied, then taken out (one step to undo).</summary>
    Task CutBlock(IAutomated t, string laneId, AutomationBlock block)
    {
        CopyBlock(block);
        string id = block.Id;
        return LaneEdit(t, laneId, l => l.Blocks.RemoveAll(b => b.Id == id));
    }

    /// <summary>"Alinear a la grilla" on a block (#281, as on a clip): its start on the nearest line of the magnet.</summary>
    Task AlignBlock(IAutomated t, string laneId, string id)
    {
        var grid = Timeline.SnapLines;
        if (grid.Step <= 0) { Toast(Loc.T("menu.alignGrid.off")); return Task.CompletedTask; }
        return LaneEdit(t, laneId, l => Automations.AlignToGrid(l, id, grid));
    }

    /// <summary>Dividir on a block (#281, as on a clip): cut where it was clicked, the right piece selected.</summary>
    async Task SplitBlock(IAutomated t, string laneId, string blockId, double sec)
    {
        if (store is not { ReadOnly: false } || OwnerIn(store.Project, t.Id) is not { } o || Automations.ById(o, laneId) is not { } lane
            || Automations.BlockById(lane, blockId) is not { } block) return;
        if (!Automations.CanSplit(block, sec)) { Toast(Loc.T("split.edge")); return; }
        bool stepped = Automations.Stepped(o, lane.Target);
        string? right = null;
        await LaneEdit(t, laneId, l => right = Automations.Split(l, blockId, sec, stepped));
        Timeline.SelectBlock(right);
    }

    /// <summary>
    /// Alt+X on blocks (#281, as on the clip under the playhead): the selected block, or every boxed one, the playhead
    /// crosses is cut there; the right piece of the last is selected. False when no block is picked.
    /// </summary>
    async Task<bool> SplitBlocksAtPlayhead()
    {
        if (store is not { ReadOnly: false } || Timeline.SelectedBlock is not { } picked) return false;
        double at = engine.PositionSec;
        var ids = Timeline.BoxedBlockIds.Count > 0 ? Timeline.BoxedBlockIds.ToHashSet() : [picked.Block.Id];
        string? right = null;
        int cut = 0;
        await EditTimeline(p =>
        {
            foreach (var (owner, lane) in Automations.Lanes(p))
            {
                bool stepped = Automations.Stepped(owner, lane.Target);
                foreach (var b in lane.Blocks.ToList())
                    if (ids.Contains(b.Id) && Automations.CanSplit(b, at) && Automations.Split(lane, b.Id, at, stepped) is { } r)
                    {
                        right = r;
                        cut++;
                    }
            }
            return picked.Owner.Id;
        }, null);
        if (cut == 0) Toast(Loc.T("timeline.split.nowhere"));
        else Timeline.SelectBlock(right);
        return true;
    }

    /// <summary>
    /// Lápiz and Pincel on a lane (#281): a copy of the brush block at each start, one step to undo. A copy that would land
    /// on a block already there is left out, as the pencil does with clips.
    /// </summary>
    async Task PaintBlocks(IAutomated t, string laneId, AutomationBlock brush, IReadOnlyList<double> starts)
    {
        if (store is not { ReadOnly: false } || OwnerIn(store.Project, t.Id) is not { } o || Automations.ById(o, laneId) is not { } lane) return;
        bool stepped = Automations.Stepped(o, lane.Target);
        var free = starts.Where(at => !lane.Blocks.Any(b => b.AtSec < at + brush.LengthSec - 1e-9 && b.EndSec > at + 1e-9)).ToList();
        if (free.Count == 0) { Toast(Loc.T("pencil.full")); return; }
        string? last = null;
        await LaneEdit(t, laneId, l =>
        {
            foreach (double at in free)
            {
                var copy = brush.Copy(at);
                // a copy into an on/off or stepped parameter takes whole values, as a point drawn there would
                if (stepped) foreach (var pt in copy.Points) pt.Value = Math.Round(pt.Value);
                l.Blocks.Add(copy);
                last = copy.Id;
            }
        });
        Timeline.SelectBlock(last);
    }

    /// <summary>
    /// A control of a track, a folder or a bus: a right click offers its lane, and with <paramref name="show"/> it follows that
    /// lane while it is on screen. Does nothing for an owner that has no lanes (the master).
    /// </summary>
    void AutomateOn(Control c, Func<IAutomated?> track, string target, Action<double>? show = null)
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
    void FollowLane(Control c, Func<IAutomated?> track, string target, Action<double> show)
    {
        var view = new LaneView(track, target, show);
        c.AttachedToVisualTree += (_, _) => laneViews.Add(view);
        c.DetachedFromVisualTree += (_, _) => laneViews.Remove(view);
        if (c.GetVisualRoot() != null) laneViews.Add(view);
    }

    /// <summary>The track, folder or bus a strip's slots belong to; null for the master.</summary>
    IAutomated? TrackOf(SlotOwner o)
    {
        foreach (var t in tracks) if (t.Track.Id == o.OwnerId) return t.Track;
        if (store == null) return null;
        foreach (var f in store.Project.Folders) if (f.Id == o.OwnerId) return f;
        foreach (var b in store.Project.Settings.Buses) if (b.Id == o.OwnerId) return b;
        return null;
    }

    /// <summary>A folder's volume and pan as its lanes have them at the playhead (null = its own), for its header in the timeline.</summary>
    readonly Dictionary<string, (double? Volume, double? Pan)> folderShown = [];

    /// <summary>Updates a folder's entry in <see cref="folderShown"/>; true when its header has to redraw. Allocates nothing once the folder has its entry.</summary>
    bool FollowFolder(Folder f, double pos)
    {
        if (f.Separator) return false;
        double? volume = Automations.Find(f, AutoTarget.Volume) is { On: true, Blocks.Count: > 0 } vl ? Fader.ToVol(Automations.PositionAt(vl, false, pos)) : null;
        double? pan = Automations.Find(f, AutoTarget.Pan) is { On: true, Blocks.Count: > 0 } pl ? Automations.PositionAt(pl, false, pos) * 2 - 1 : null;
        folderShown.TryGetValue(f.Id, out var old);
        if (!FolderFar(volume, old.Volume) && !FolderFar(pan, old.Pan)) return false;
        folderShown[f.Id] = (volume, pan);
        return true;
    }

    static bool FolderFar(double? a, double? b) => a.HasValue != b.HasValue || a is { } x && b is { } y && Math.Abs(x - y) >= 0.01;

    /// <summary>Every tick: the native effects' lanes at the playhead, and the controls that show a lane. Allocates nothing while nothing moves.</summary>
    void FollowLanes(double pos)
    {
        bool playing = engine.Playing, moved = false;
        foreach (var t in tracks) moved |= t.TickAutomation(pos, playing);
        engine.TickNodeAutomation(pos);
        if (store != null) foreach (var f in store.Project.Folders) moved |= FollowFolder(f, pos);
        if (moved) Timeline.InvalidateVisual();
        for (int i = 0; i < laneViews.Count; i++)
        {
            var v = laneViews[i];
            if (v.Owner() is not { Automation: not null } t || Automations.Find(t, v.Target) is not { On: true, Blocks.Count: > 0 } lane) continue;
            double k = Automations.PositionAt(lane, false, pos);
            if (Math.Abs(k - v.Last) < 0.002) continue;
            v.Last = k;
            v.Show(k);
        }
    }
}
