using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Threading;
using XtremeDaw.App.Audio;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Model;
using XtremeDaw.Audio.Vst;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// Instrument plugins on MIDI tracks (native only, stage 3 of the MIDI editor, the user's order: instruments first):
/// each track's <see cref="Track.Instrument"/> runs in its own xtvsthost.exe (<see cref="VstBridge"/>), loaded in the
/// background after every edit that may change them (<see cref="SyncInstruments"/>), and plays through the track's
/// <see cref="LiveTrack.Instrument"/>; until it is up and sounding (<see cref="InstrumentWarmup"/>), or if it can't be
/// opened, the track plays the test tone and a toast says why. What the user changes in the plugin's window is saved in the project: when it says "dirty" or its
/// window closes, its state is asked for and kept in <see cref="PluginRef.State"/> (outside the undo, like the tempo).
/// The list of plugins comes from <see cref="PluginScan"/> (scanned once, cached; managed in Configuración → Plugins).
/// </summary>
public partial class MainWindow
{
    sealed class LoadedInstrument(VstBridge bridge, InstrumentPlayer player, PluginRef plugin)
    {
        public readonly VstBridge Bridge = bridge;
        public readonly InstrumentPlayer Player = player;
        public readonly PluginRef Plugin = plugin;
        /// <summary>Its state has to be asked for and saved (it said "dirty", or its window closed).</summary>
        public bool StateDue;
    }

    readonly Dictionary<string, LoadedInstrument> instruments = [];
    /// <summary>Tracks whose plugin is loading, with which plugin (so a change meanwhile drops the old load).</summary>
    readonly Dictionary<string, PluginRef> instrumentLoads = [];
    /// <summary>Plugins that already failed to open, by track and file: not retried (nor toasted) on every edit.</summary>
    readonly HashSet<string> instrumentFailed = [];
    DispatcherTimer? instrumentStateTimer;
    /// <summary>How long after a "dirty" its state is asked for: one ask for a whole knob turn.</summary>
    static readonly TimeSpan InstrumentStateDelay = TimeSpan.FromSeconds(1.5);
    /// <summary>How long the piano roll's keys sound through an instrument.</summary>
    const double AuditionSec = 0.35;
    /// <summary>How long a plugin that just opened may stay "loading" until it sounds (<see cref="InstrumentWarmup"/>):
    /// past that it goes on the track anyway (the key tried may mean nothing on it).</summary>
    const double InstrumentWarmSec = 5;

    void InitInstruments()
    {
        Timeline.InstrumentStateOf = InstrumentStateOf;
        Timeline.InstrumentNoteOf = id => lackNotes.TryGetValue(id, out var note) ? note : null;
        Timeline.InstrumentMenuRequested += (t, at) => ShowInstrumentMenu(t, at);
        Timeline.InstrumentWindowRequested += OpenInstrumentWindow;
        // a scan (here or in Configuración → Plugins) or a plugin hidden there: the lists catch up
        PluginScan.Changed += InstrumentsChanged;
        // in a shared session the others learn what this copy has now (#291)
        PluginScan.Changed += ShareLiveCatalog;
    }

    static string FailKey(string trackId, PluginRef p) => trackId + "|" + p.Path + "|" + p.ClassId;

    TimelineView.InstrumentState InstrumentStateOf(string trackId)
    {
        if (instruments.ContainsKey(trackId)) return TimelineView.InstrumentState.Ready;
        if (tracks.FirstOrDefault(t => t.Track.Id == trackId)?.Track.Sampler != null)
            return !samplers.TryGetValue(trackId, out var sampler) || sampler.Loading ? TimelineView.InstrumentState.Loading
                : sampler.Missing ? TimelineView.InstrumentState.Failed : TimelineView.InstrumentState.Ready;
        if (tracks.FirstOrDefault(t => t.Track.Id == trackId)?.Track.Granular != null)
            return !granulars.TryGetValue(trackId, out var grains) || grains.Loading ? TimelineView.InstrumentState.Loading
                : grains.Missing ? TimelineView.InstrumentState.Failed : TimelineView.InstrumentState.Ready;
        if (tracks.FirstOrDefault(t => t.Track.Id == trackId)?.Track.Gm is { } gm) return GmState(gm);
        var plugin = tracks.FirstOrDefault(t => t.Track.Id == trackId)?.Track.Instrument;
        if (plugin == null) return TimelineView.InstrumentState.TestTone;
        if (PluginOff(plugin)) return TimelineView.InstrumentState.Failed;
        return instrumentFailed.Contains(FailKey(trackId, plugin)) ? TimelineView.InstrumentState.Failed : TimelineView.InstrumentState.Loading;
    }

    /// <summary>Something about the instruments changed (one loaded, failed, closed, was chosen): every place that shows them catches up.</summary>
    void InstrumentsChanged()
    {
        Timeline.InvalidateVisual();
        RefreshPianoInstrument();
        RefreshMidiInspector();
    }

    /// <summary>The instrument list as a menu under the timeline's chip (the first time, it looks for plugins on its own).</summary>
    void ShowInstrumentMenu(LiveTrack track, Avalonia.Point at)
    {
        if (store == null) return;
        EnsurePlugins();
        var items = InstrumentItems(track, !store.ReadOnly);
        OpenTimelineMenu(Popup(items, 280), at, MenuHeight(items));
    }

    bool pluginsAsked;

    /// <summary>The first time a list of instruments is shown and none were ever found, it looks for them (no extra
    /// click); same if an older scan didn't tell instruments from effects (it only opens those files again). Never while recording.</summary>
    void EnsurePlugins()
    {
        if (pluginsAsked || PluginScan.Running || PluginScan.Entries.Count > 0 && !PluginScan.Outdated || VstHost.Find() == null || RecordingBusy) return;
        pluginsAsked = true;
        _ = ScanPlugins();
    }

    /// <summary>
    /// Brings the running plugins in line with the project: starts the ones a track wants (in the background), stops the
    /// ones no track wants any more and hands each live track its player. After every <see cref="ApplyTracks"/> and on
    /// opening a project.
    /// </summary>
    void SyncInstruments()
    {
        if (store == null) return;
        var wanted = new Dictionary<string, PluginRef>();
        foreach (var t in tracks)
            // a frozen track's instrument is in its render: its process closes (#240)
            // and one someone in a shared session lacks stays off for everybody (#291)
            if (t.Track.IsMidi && t.Track.Instrument is { } plugin && !FreezeHolds(t.Track) && !PluginOff(plugin)) wanted[t.Track.Id] = plugin;
        foreach (var (id, loaded) in instruments.ToList())
            if (!wanted.TryGetValue(id, out var w) || !w.SamePlugin(loaded.Plugin)) DropInstrument(id);
        foreach (var t in tracks)
        {
            string id = t.Track.Id;
            if (instruments.TryGetValue(id, out var loaded)) { if (t.Instrument != loaded.Player) t.Instrument = loaded.Player; continue; }
            t.Instrument = null;
            if (!wanted.TryGetValue(id, out var plugin)) continue;
            if (instrumentLoads.TryGetValue(id, out var loading) && loading.SamePlugin(plugin)) continue;
            if (instrumentFailed.Contains(FailKey(id, plugin))) continue;
            LoadInstrument(id, plugin);
        }
    }

    void LoadInstrument(string trackId, PluginRef plugin)
    {
        if (VstHost.Find() == null)
        {
            instrumentFailed.Add(FailKey(trackId, plugin));
            Toast(Loc.T("vst.noHost"));
            return;
        }
        instrumentLoads[trackId] = plugin;
        string path = plugin.Path, cid = plugin.ClassId, name = plugin.Name;
        string? state = plugin.State;
        int pitch = InstrumentWarmup.Pitch(tracks.FirstOrDefault(x => x.Track.Id == trackId)?.Notes);
        double tempo = store?.Project.Settings.Bpm ?? 120;
        _ = Task.Run(() =>
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException(Loc.T("vst.missing", path));
                var bridge = VstBridge.Start(path, cid, name, PlaybackEngine.SampleRate, offline: false, VstInsert.LiveBlock);
                if (state != null && !bridge.SetState(state))
                    Dispatcher.UIThread.Post(() => Toast(Loc.T("vst.stateFailed", name)));
                InstrumentWarmup.Run(bridge, pitch, tempo, PlaybackEngine.SampleRate, InstrumentWarmSec);
                return (Bridge: bridge, Error: (string?)null);
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                return (Bridge: (VstBridge?)null, Error: e.Message);
            }
        }).ContinueWith(t =>
        {
            var (bridge, error) = t.Result;
            bool still = instrumentLoads.TryGetValue(trackId, out var p) && p == plugin;
            if (still) instrumentLoads.Remove(trackId);
            var live = tracks.FirstOrDefault(x => x.Track.Id == trackId);
            if (bridge == null)
            {
                if (still)
                {
                    instrumentFailed.Add(FailKey(trackId, plugin));
                    Toast(Loc.T("vst.loadFailed", name, error ?? ""), sticky: true);
                    InstrumentsChanged();
                }
                return;
            }
            // the track (or its plugin) changed while it loaded
            if (!still || live == null || live.Track.Instrument is not { } now || !now.SamePlugin(plugin))
            {
                _ = Task.Run(bridge.Dispose);
                return;
            }
            var loaded = new LoadedInstrument(bridge, new InstrumentPlayer(bridge), plugin);
            instruments[trackId] = loaded;
            bridge.Message += line => Dispatcher.UIThread.Post(() => InstrumentSaid(trackId, loaded, line));
            bridge.Exited += () => Dispatcher.UIThread.Post(() => InstrumentExited(trackId, loaded));
            live.Instrument = loaded.Player;
            InstrumentsChanged();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Takes a plugin off its track and closes its process (a moment later: the audio thread may hold it one more block).</summary>
    void DropInstrument(string trackId)
    {
        if (!instruments.Remove(trackId, out var loaded)) return;
        StopLaneListen(loaded.Bridge);
        ForgetPluginWindow(trackId);
        if (tracks.FirstOrDefault(t => t.Track.Id == trackId) is { } live && live.Instrument == loaded.Player) live.Instrument = null;
        _ = Task.Run(async () => { await Task.Delay(200); loaded.Bridge.Dispose(); });
        InstrumentsChanged();
    }

    /// <summary>Closes every plugin (another project, or the window closes).</summary>
    void DropAllInstruments()
    {
        foreach (var id in instruments.Keys.ToList()) DropInstrument(id);
        instrumentLoads.Clear();
        instrumentFailed.Clear();
    }

    void InstrumentSaid(string trackId, LoadedInstrument loaded, string line)
    {
        if (!instruments.TryGetValue(trackId, out var current) || current != loaded || PluginKeySaid(line) || PluginThumbSaid(loaded.Bridge, loaded.Plugin, line)) return;
        VirtualEditorSaid(trackId, loaded.Bridge.ProcessId, line);
        if (line == "editor-closed") StopLaneListen(loaded.Bridge);
        if (line is "dirty" or "editor-closed")
        {
            loaded.StateDue = true;
            instrumentStateTimer ??= new DispatcherTimer(InstrumentStateDelay, DispatcherPriority.Background, async (_, _) => await SaveInstrumentStates());
            instrumentStateTimer.Stop();
            instrumentStateTimer.Start();
            if (line == "editor-closed") _ = SaveInstrumentStates();
        }
        else if (line.StartsWith("error ", StringComparison.Ordinal)) Toast(Loc.T("vst.said", loaded.Plugin.Name, line[6..]));
    }

    void InstrumentExited(string trackId, LoadedInstrument loaded)
    {
        if (!instruments.TryGetValue(trackId, out var current) || current != loaded) return;
        DropInstrument(trackId);
        instrumentFailed.Add(FailKey(trackId, loaded.Plugin));
        Toast(Loc.T("vst.crashed", loaded.Plugin.Name), sticky: true, action: (Loc.T("vst.reopen"), () =>
        {
            instrumentFailed.Remove(FailKey(trackId, loaded.Plugin));
            SyncInstruments();
        }));
    }

    bool InstrumentStatesDue => instruments.Values.Any(i => i.StateDue);

    /// <summary>Asks the plugins that changed for their state and keeps it in the project (one save for all).</summary>
    async Task SaveInstrumentStates()
    {
        instrumentStateTimer?.Stop();
        bool changed = false;
        foreach (var (id, loaded) in instruments.ToList())
        {
            if (!loaded.StateDue) continue;
            loaded.StateDue = false;
            string? state = await loaded.Bridge.GetStateAsync();
            // the project's track now (an undo may have put another object there), if it still has this plugin
            if (state == null || store?.Project.Tracks.Find(t => t.Id == id)?.Instrument is not { } plugin || !plugin.SamePlugin(loaded.Plugin)) continue;
            if (plugin.State == state) continue;
            plugin.State = state;
            changed = true;
        }
        if (changed && store is { ReadOnly: false }) MarkDirty();
    }

    /// <summary>Sets (or takes away, with null) a MIDI track's instrument: one undo step; the plugin loads right after.</summary>
    Task SetInstrument(LiveTrack track, VstPluginInfo? plugin)
    {
        if (store == null || store.ReadOnly || !track.Track.IsMidi) return Task.CompletedTask;
        if (track.Track.Frozen)
        {
            ToastLocked(track.Track);
            return Task.CompletedTask;
        }
        string id = track.Track.Id;
        return EditTimeline(p =>
        {
            if (p.Tracks.Find(t => t.Id == id) is not { } t) return null;
            t.Instrument = plugin == null ? null : PluginCatalog.Ref(plugin);
            t.Sampler = null;
            t.Granular = null;
            t.Gm = null;
            return id;
        }, "instrument-set");
    }

    void OpenInstrumentWindow(LiveTrack track)
    {
        if (track.Track.Sampler != null) OpenSamplerWindow(track.Track.Id);
        else if (track.Track.Granular != null) OpenGranularWindow(track.Track.Id);
        else if (track.Track.Gm != null) OpenGmWindow(track.Track.Id);
        else if (instruments.TryGetValue(track.Track.Id, out var loaded))
        {
            DressPluginWindow(loaded.Bridge, loaded.Plugin.Name);
            string listen = ListenBarButton;
            loaded.Bridge.Command($"toolbar {(listen.Length > 0 ? listen + "|-|" : "")}keys=\uE765:{Loc.T("vst.bar.keys")}|-|mute=:M|solo=:S|-|panic=\uE71A:{Loc.T("vst.bar.panic")}|-|{ThumbBarButton}|-|{KeyRouteBarButtons}");
            TellKeyRoute(loaded.Bridge);
            TellPluginTrack(loaded.Bridge, loaded.Plugin.Name, track.Track);
            pluginBars.Remove(track.Track.Id);
            TellPluginPanel(loaded.Bridge);
            loaded.Bridge.OpenEditor(TryGetPlatformHandle()?.Handle ?? 0);
        }
        else if (track.Track.Instrument is { } plugin) Toast(Loc.T(instrumentLoads.ContainsKey(track.Track.Id) ? "vst.stillLoading" : "vst.notLoaded", plugin.Name));
    }

    /// <summary>The piano roll's key through the track's instrument, or the test tone without one: always through its chain.</summary>
    void Audition(string? trackId, int pitch, int velocity)
    {
        if (trackId != null && gms.TryGetValue(trackId, out var gm))
            gm.Audition(pitch, velocity, (int)(AuditionSec * PlaybackEngine.SampleRate));
        else if (trackId != null && samplers.TryGetValue(trackId, out var sampler))
            sampler.Player.Audition(pitch, velocity, (int)(AuditionSec * PlaybackEngine.SampleRate));
        else if (trackId != null && instruments.TryGetValue(trackId, out var loaded))
            loaded.Player.Audition(pitch, velocity, (int)(AuditionSec * PlaybackEngine.SampleRate));
        else if (tracks.Find(t => t.Track.Id == trackId) is { } track)
            track.Preview.Post(XtremeDaw.Audio.Dsp.NoteSynth.Audition(pitch + track.Track.Transpose, velocity, PlaybackEngine.SampleRate));
    }

    /// <summary>The track of the clip open in the piano roll, if any.</summary>
    string? PianoTrackId() =>
        store != null && MidiEditing.TryFind(store.Project, pianoClipId) != null ? MidiEditing.Find(store.Project, pianoClipId!).Track.Id : null;

    /// <summary>Where each held key of the piano roll went (#276), so its lift reaches the same instrument.</summary>
    readonly Dictionary<int, string> auditionHeld = [];

    /// <summary>
    /// A piano roll key held with the mouse (#276: the instrument's sustain lasts while it is held): the GM instrument, the
    /// sampler or the plugin keep it until <see cref="AuditionLift"/>; the test tone has no sustain and plays its blip.
    /// </summary>
    void AuditionPress(string? trackId, int pitch, int velocity)
    {
        AuditionLift(pitch);
        if (trackId != null && gms.TryGetValue(trackId, out var gm)) gm.Press(pitch, velocity);
        else if (trackId != null && samplers.TryGetValue(trackId, out var sampler)) sampler.Player.Press(pitch, velocity);
        else if (trackId != null && instruments.TryGetValue(trackId, out var loaded)) loaded.Player.Press(pitch, velocity);
        else
        {
            Audition(trackId, pitch, velocity);
            return;
        }
        auditionHeld[pitch] = trackId;
    }

    void AuditionLift(int pitch)
    {
        if (!auditionHeld.Remove(pitch, out var trackId)) return;
        if (gms.TryGetValue(trackId, out var gm)) gm.Lift(pitch);
        else if (samplers.TryGetValue(trackId, out var sampler)) sampler.Player.Lift(pitch);
        else if (instruments.TryGetValue(trackId, out var loaded)) loaded.Player.Lift(pitch);
    }

    /// <summary>Looks for new or changed plugins (in the background, with a toast that says what it found).</summary>
    async Task ScanPlugins()
    {
        if (PluginScan.Running) return;
        if (VstHost.Find() == null) { Toast(Loc.T("vst.noHost")); return; }
        Toast(Loc.T("vst.scanning"));
        var found = await PluginScan.Run(error: e => Toast(Loc.T("vst.scanFailed", e)));
        if (found == null) return;
        var all = PluginCatalog.Plugins(found);
        int n = all.Count(p => p.IsInstrument);
        Toast(Loc.T("vst.scanned", n, all.Count - n), action: (Loc.T("vst.manage"), () => Settings.Show("plugins")));
    }

    static bool SamePlugin(PluginRef? r, VstPluginInfo p) => r != null && r.Path.Equals(p.Path, StringComparison.OrdinalIgnoreCase) && r.ClassId == p.ClassId;

    /// <summary>A plugin's entry in a menu: a VST2 or a 32-bit one says so, since the same plugin often comes in two formats.</summary>
    static string MenuName(VstPluginInfo p, string label) => p.FormatText == "VST3" ? label : $"{label} · {p.FormatText}";

    /// <summary>The instruments a track can pick: the ones not hidden in Configuración → Plugins, plus the one it uses even if hidden.</summary>
    static List<VstPluginInfo> InstrumentChoices(PluginRef? current)
    {
        var found = PluginScan.Visible.Where(p => p.IsInstrument).ToList();
        if (current != null && !found.Any(p => SamePlugin(current, p))
            && PluginCatalog.Plugins(PluginScan.Entries).FirstOrDefault(p => SamePlugin(current, p)) is { } used)
            found.Add(used);
        return found;
    }

    /// <summary>A group's title inside a menu ("NATIVOS", "VST3"): a section label, not an entry (it can't be picked).</summary>
    static MenuItem MenuGroup(string text) => new()
    {
        Header = new TextBlock { Text = text, Classes = { "label" } },
        IsHitTestVisible = false, Focusable = false, MinHeight = 0, Padding = new Avalonia.Thickness(12, 6, 12, 2),
    };

    /// <summary>
    /// The "Instrumento" entries of a MIDI track's menu, in two groups (asked by the user): the native ones (today the
    /// test tone) and the VST3 ones found, grouped as Configuración → Plugins says (<see cref="AddPluginItems"/>); then look again,
    /// manage the plugins (Configuración → Plugins), open its window. <paramref name="enabled"/> false (read only) locks the choice.
    /// </summary>
    AvaloniaList<object> InstrumentItems(LiveTrack track, bool enabled)
    {
        var refresh = new List<Action>();
        var current = track.Track.Instrument;
        MenuItem Pick(VstPluginInfo? p, string label, string? tip = null)
        {
            var item = MenuCheck(label, () => p == null ? track.Track.Instrument == null && track.Track.Sampler == null && track.Track.Granular == null && track.Track.Gm == null : SamePlugin(track.Track.Instrument, p), () => _ = SetInstrument(track, p), refresh, tip: tip);
            item.IsEnabled = enabled;
            if (p != null)
            {
                PluginThumbView.AddTip(item, p);
                MarkLacking(item, p);
            }
            return item;
        }
        MenuItem Native()
        {
            var item = MenuCheck(Loc.T("sampler.name"), () => track.Track.Sampler != null, () => _ = SetSampler(track), refresh, tip: Loc.T("sampler.tip"));
            item.IsEnabled = enabled;
            return item;
        }
        MenuItem Grains()
        {
            var item = MenuCheck(Loc.T("granular.name"), () => track.Track.Granular != null, () => _ = SetGranular(track), refresh, tip: Loc.T("granular.tip"));
            item.IsEnabled = enabled;
            return item;
        }
        var items = new AvaloniaList<object>
        {
            MenuGroup(Loc.T("vst.group.native")),
            Pick(null, Loc.T("vst.testTone"), Loc.T("vst.testTone.tip")),
            Native(),
            Grains(),
            GmMenu(() => tracks.FirstOrDefault(x => x.Track.Id == track.Track.Id)?.Track.Gm, (n, drums) => _ = SetGm(track, n, drums), enabled, refresh),
            new Separator(),
            MenuGroup(Loc.T("vst.group.vst")),
        };
        var found = InstrumentChoices(current);
        // the maker of the one in use carries the check, so it is found without opening every submenu
        AddPluginItems(items, found, (p, label) => Pick(p, label), maker => maker.Any(p => SamePlugin(track.Track.Instrument, p)), refresh);
        items.Add(new Separator());
        items.Add(MenuAction(Loc.T(found.Count == 0 ? "vst.scan.first" : "vst.scan"), ScanPlugins,
            enabled: !PluginScan.Running, tip: Loc.T("vst.scan.tip")));
        items.Add(MenuAction(Loc.T("vst.manage.menu"), () => { Settings.Show("plugins"); return Task.CompletedTask; }, tip: Loc.T("vst.manage.tip")));
        if (current != null || track.Track.Sampler != null || track.Track.Granular != null || track.Track.Gm != null)
            items.Add(MenuAction(Loc.T("vst.open", InstrumentName(track.Track)), () => { OpenInstrumentWindow(track); return Task.CompletedTask; },
                enabled: track.Track.Sampler != null || track.Track.Granular != null || track.Track.Gm != null || instruments.ContainsKey(track.Track.Id), icon: ""));
        return items;
    }

    /// <summary>
    /// The plugins of a menu (instruments or effects) as Configuración → Plugins says (<see cref="PluginCatalog.Grouping"/>):
    /// by maker or by category, a submenu each, or one list. <paramref name="holds"/> marks the group of the one in use.
    /// First, "Mostrar en los menús" changes that grouping right there (asked by the user): the plugins regroup in the
    /// open menu, so <paramref name="items"/> is an <see cref="AvaloniaList{T}"/> the menu watches.
    /// </summary>
    static void AddPluginItems(AvaloniaList<object> items, List<VstPluginInfo> found, Func<VstPluginInfo, string, MenuItem> pick,
        Func<IEnumerable<VstPluginInfo>, bool>? holds = null, List<Action>? refresh = null, string none = "vst.group.none")
    {
        if (found.Count == 0)
        {
            items.Add(new MenuItem { IsEnabled = false, Header = new TextBlock { Text = Loc.T(none), FontSize = 11 } });
            return;
        }
        var checks = new List<Action>();
        int start = items.Count + 1, count = 0;
        void Fill()
        {
            items.RemoveRange(start, count);
            var built = new List<object>();
            AddPluginGroups(built, found, pick, holds, refresh);
            items.InsertRange(start, built);
            count = built.Count;
        }
        MenuItem Way(string id, string key) => MenuCheck(Loc.T(key), () => PluginCatalog.Grouping == id,
            () => { PluginScan.SetGrouping(id); Fill(); }, checks, tip: Loc.T(key + ".tip"));
        var ways = new MenuItem
        {
            Header = MenuHeader(Loc.T("set.vst.group"), new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12, Width = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }),
            ItemsSource = new List<object>
            {
                Way(PluginCatalog.ByVendor, "set.vst.group.vendor"),
                Way(PluginCatalog.ByCategory, "set.vst.group.category"),
                Way(PluginCatalog.AsList, "set.vst.group.list"),
            },
        };
        items.Add(ways);
        Fill();
    }

    /// <summary>The plugins themselves, grouped as <see cref="PluginCatalog.Grouping"/> says now (<see cref="AddPluginItems"/>).</summary>
    static void AddPluginGroups(List<object> items, List<VstPluginInfo> found, Func<VstPluginInfo, string, MenuItem> pick,
        Func<IEnumerable<VstPluginInfo>, bool>? holds, List<Action>? refresh)
    {
        string grouping = PluginCatalog.Grouping;
        var custom = PluginCatalog.CustomCategories();
        var all = PluginCatalog.Plugins(PluginScan.Entries);
        string WithVendor(VstPluginInfo p) => MenuName(p, p.Vendor.Length > 0 ? $"{p.Name} · {p.Vendor}" : p.Name);
        var groups = found.GroupBy(p => PluginCatalog.GroupOf(p, grouping, custom, all) ?? "").ToList();
        // one list, or everything in a single group: no submenu to open for nothing
        if (grouping == PluginCatalog.AsList || groups.Count == 1)
        {
            foreach (var p in found) items.Add(pick(p, WithVendor(p)));
            return;
        }
        foreach (var group in groups.OrderBy(g => g.Key.Length == 0).ThenBy(g => PluginScan.GroupName(g.Key, grouping), StringComparer.CurrentCultureIgnoreCase))
        {
            var entries = group.Select(p => pick(p, grouping == PluginCatalog.ByCategory ? WithVendor(p) : MenuName(p, p.Name))).ToList();
            string title = $"{PluginScan.GroupName(group.Key, grouping)} ({group.Count()})";
            if (holds == null)
            {
                items.Add(new MenuItem { Header = new TextBlock { Text = title, FontSize = 12 }, ItemsSource = entries });
                continue;
            }
            string Check() => holds(group) ? "" : " ";
            var lead = new TextBlock { Classes = { "icon" }, Text = Check(), FontSize = 12, Width = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            refresh?.Add(() => lead.Text = Check());
            items.Add(new MenuItem { Header = MenuHeader(title, lead), ItemsSource = entries });
        }
    }

    /// <summary>
    /// The new track menu's "Pista MIDI con instrumento" (asked by the user): the instruments of a track's menu, native
    /// and plugins, and picking one makes the track already playing with it (<see cref="NewInstrumentTrack"/>).
    /// </summary>
    MenuItem NewInstrumentTrackMenu(bool enabled)
    {
        EnsurePlugins();
        MenuItem Native(string name, string tip, Action<Track> setup) =>
            MenuAction(name, () => NewInstrumentTrack(name, setup), enabled: enabled, tip: tip);
        var items = new AvaloniaList<object>
        {
            MenuGroup(Loc.T("vst.group.native")),
            Native(Loc.T("sampler.name"), Loc.T("sampler.tip"), t => t.Sampler = new SamplerSettings()),
            Native(Loc.T("granular.name"), Loc.T("granular.tip"), t => t.Granular = new GranularSettings()),
            GmMenu(() => null, (n, drums) =>
            {
                var gm = new GmSettings { Program = n, Drums = drums };
                _ = NewInstrumentTrack(gm.Name, t => t.Gm = gm);
            }, enabled, null),
            new Separator(),
            MenuGroup(Loc.T("vst.group.vst")),
        };
        AddPluginItems(items, InstrumentChoices(null), (p, label) =>
        {
            var item = MenuAction(label, () => NewInstrumentTrack(p.Name, t => t.Instrument = PluginCatalog.Ref(p)), enabled: enabled);
            PluginThumbView.AddTip(item, p);
            MarkLacking(item, p);
            return item;
        });
        items.Add(new Separator());
        items.Add(MenuAction(Loc.T(PluginScan.Visible.Any(p => p.IsInstrument) ? "vst.scan" : "vst.scan.first"), ScanPlugins,
            enabled: !PluginScan.Running, tip: Loc.T("vst.scan.tip")));
        items.Add(MenuAction(Loc.T("vst.manage.menu"), () => { Settings.Show("plugins"); return Task.CompletedTask; }, tip: Loc.T("vst.manage.tip")));
        var menu = new MenuItem { Header = MenuHeader(Loc.T("menu.trackInstrument"), AddIcon("kind:midi")), ItemsSource = items };
        ToolTip.SetTip(menu, Loc.T("menu.trackInstrument.hint"));
        ToolTip.SetShowDelay(menu, (int)MenuTipDelay.TotalMilliseconds);
        return menu;
    }
}

/// <summary>
/// The inspector of a MIDI track (asked by the user: choosing its instrument has to be easy and in sight, but not as a
/// list that fills the panel once there are many): the instrument it plays with as a dropdown (the same menu as the
/// timeline's chip, native and VST3 apart), how it is going and its window, in the Pista tab; its fader, effects and
/// sends go in the strip like an audio track's (MainWindow.InspectorStrip.cs).
/// </summary>
public partial class MainWindow
{
    readonly StackPanel midiPanel = new() { Spacing = 10, IsVisible = false };
    readonly TextBlock midiNow = new() { FontSize = 12.5, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
    readonly TextBlock midiNowKind = new() { FontSize = 11, Foreground = Palette.Faint, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
    readonly TextBlock midiNowInfo = new() { FontSize = 12, Foreground = Palette.Dim, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    readonly Button midiPick = new()
    {
        Classes = { "chip" }, MinHeight = 40, Padding = new Avalonia.Thickness(10, 4), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
    };
    readonly Button midiOpen = new() { Classes = { "chip" }, Height = 30, Padding = new Avalonia.Thickness(12, 0), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };

    void InitMidiInspector()
    {
        midiPanel.Children.Add(new TextBlock { Text = Loc.T("vst.insp.title"), Classes = { "label" } });
        var pick = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        pick.Children.Add(new StackPanel { Spacing = 1, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Children = { midiNow, midiNowKind } });
        var chevron = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 10, Foreground = Palette.Dim, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Avalonia.Thickness(8, 0, 0, 0) };
        Grid.SetColumn(chevron, 1);
        pick.Children.Add(chevron);
        midiPick.Content = pick;
        ToolTip.SetTip(midiPick, Loc.T("vst.insp.pick.tip"));
        midiPick.Click += (_, _) =>
        {
            if (sel is not { Track.IsMidi: true } t || store == null) return;
            EnsurePlugins();
            OpenAnchoredMenu(Popup(InstrumentItems(t, !store.ReadOnly), Math.Max(240, midiPick.Bounds.Width)), midiPick);
        };
        var card = new Border
        {
            Background = Palette.Raised, BorderBrush = Palette.Line, BorderThickness = new Avalonia.Thickness(1), CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(12, 10),
            Child = new StackPanel { Spacing = 8, Children = { midiPick, midiNowInfo, midiOpen } },
        };
        midiPanel.Children.Add(card);
        midiOpen.Content = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6,
            Children = { new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12 }, new TextBlock { Text = Loc.T("vst.insp.open"), FontSize = 12 } },
        };
        ToolTip.SetTip(midiOpen, Loc.T("vst.openWindow.tip"));
        midiOpen.Click += (_, _) => { if (sel is { Track.IsMidi: true } t) OpenInstrumentWindow(t); };
        // the instrument first, then the channel's sections, as for any track (decisión del usuario, 2026-11-03)
        int at = Inspector.Children.IndexOf(takeSections);
        Inspector.Children.Insert(at >= 0 ? at : Inspector.Children.Count, midiPanel);
    }

    /// <summary>Fills the panel for the selected MIDI track: what it plays with (and whether it is native or VST3) and how that is going.</summary>
    void RefreshMidiInspector()
    {
        if (sel is not { Track.IsMidi: true } t) return;
        var plugin = t.Track.Instrument;
        var state = InstrumentStateOf(t.Track.Id);
        midiNow.Text = InstrumentName(t.Track);
        midiNow.Foreground = state == TimelineView.InstrumentState.Ready ? Palette.Accent : Palette.Fg;
        midiNowKind.Text = Loc.T(plugin == null ? "vst.kind.native" : "vst.group.vst");
        midiNowInfo.Text = state switch
        {
            TimelineView.InstrumentState.Ready => Loc.T("vst.insp.ready"),
            TimelineView.InstrumentState.Loading => Loc.T("vst.insp.loading"),
            TimelineView.InstrumentState.Failed => Loc.T("vst.insp.failed"),
            _ => Loc.T("vst.testTone.tip"),
        };
        midiNowInfo.Text = PluginScan.Running ? Loc.T("vst.scanning") : midiNowInfo.Text;
        if (plugin != null && LackNote(plugin) is { } off) midiNowInfo.Text = off;
        if (t.Track.Sampler != null && store != null)
            midiNowInfo.Text = state switch
            {
                TimelineView.InstrumentState.Failed => Loc.T("sampler.missing"),
                TimelineView.InstrumentState.Loading => Loc.T("sampler.reading"),
                _ => SamplerMedia.Of(store.Project, t.Track)?.Name ?? Loc.T("sampler.none"),
            };
        if (t.Track.Granular != null && store != null)
            midiNowInfo.Text = state switch
            {
                TimelineView.InstrumentState.Failed => Loc.T("sampler.missing"),
                TimelineView.InstrumentState.Loading => Loc.T("sampler.reading"),
                _ => GranularMedia.Of(store.Project, t.Track)?.Name ?? Loc.T("sampler.none"),
            };
        if (t.Track.Gm != null)
            midiNowInfo.Text = state switch
            {
                TimelineView.InstrumentState.Failed => Loc.T("gm.missing"),
                TimelineView.InstrumentState.Loading => Loc.T("gm.loading"),
                _ => Loc.T("gm.ready", XtremeDaw.Audio.Dsp.GmBank.NameOf(XtremeDaw.Audio.Dsp.GmBank.Available(t.Track.Gm.Bank) && !XtremeDaw.Audio.Dsp.GmBank.Failed(t.Track.Gm.Bank)
                    ? t.Track.Gm.Bank : XtremeDaw.Audio.Dsp.GmBank.FallbackKey)),
            };
        midiPick.IsEnabled = store is { ReadOnly: false };
        midiOpen.IsVisible = plugin != null || t.Track.Sampler != null || t.Track.Granular != null || t.Track.Gm != null;
        midiOpen.IsEnabled = state == TimelineView.InstrumentState.Ready;
    }

}
