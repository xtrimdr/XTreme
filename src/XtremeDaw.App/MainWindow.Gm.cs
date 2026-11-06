using Avalonia.Controls;
using XtremeDaw.App.Audio;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Dialogs;
using XtremeDaw.App.Model;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The native General MIDI instrument on MIDI tracks (asked by the user, #200): each track with a <see cref="Track.Gm"/>
/// has its <see cref="GmSynth"/> here, kept across the rebuilds of the live tracks so nothing cuts off on an edit, and
/// handed to the track by <see cref="SyncGms"/>. The bank (<see cref="GmBank"/>) is read once in the background, the
/// first time a track wants it; until then the tracks say they are loading. Picking a program is a project edit with its
/// undo, from the instrument menu's "General MIDI" submenu (families of 8, then the drum kits).
/// </summary>
public partial class MainWindow
{
    readonly Dictionary<string, GmSynth> gms = [];
    /// <summary>The bank is being read; <see cref="gmMissing"/> once it turned out not to be there.</summary>
    bool gmLoading, gmMissing;

    /// <summary>Hands each track its GM instrument with its preset and drops the ones no track wants. After every <see cref="ApplyTracks"/>.</summary>
    void SyncGms()
    {
        foreach (var id in gms.Keys.ToList())
            if (tracks.FirstOrDefault(t => t.Track.Id == id)?.Track.Gm == null) gms.Remove(id);
        bool any = false;
        foreach (var t in tracks)
        {
            if (t.Track.Gm is not { } g || t.Track.Instrument != null || t.Track.Sampler != null || t.Track.Granular != null)
            {
                t.Gm = null;
                continue;
            }
            any = true;
            if (!gms.TryGetValue(t.Track.Id, out var synth)) gms[t.Track.Id] = synth = new GmSynth(PlaybackEngine.SampleRate);
            if (t.Gm != synth) t.Gm = synth;
            if (GmBank.Loaded && GmBank.Load() is { } font)
            {
                var preset = font.Find(g.Program, g.Drums);
                var tone = g.Tone();
                if (synth.Preset != preset || synth.Tone != tone) synth.Set(font, preset, tone);
            }
        }
        if (any && !GmBank.Loaded && !gmLoading && !gmMissing) _ = LoadGmBank();
        foreach (var (id, window) in gmWindows.ToList())
        {
            if (tracks.FirstOrDefault(t => t.Track.Id == id) is { Track.Gm: not null } live && gms.ContainsKey(id)) ShowGmState(live, window);
            else window.Close();
        }
    }

    readonly Dictionary<string, GmWindow> gmWindows = [];

    /// <summary>The GM instrument's window of a track: its families, programs and kits, and a keyboard to try it.</summary>
    void OpenGmWindow(string trackId)
    {
        if (!gms.TryGetValue(trackId, out var synth) || tracks.FirstOrDefault(t => t.Track.Id == trackId) is not { } live) return;
        if (gmWindows.TryGetValue(trackId, out var open))
        {
            open.Activate();
            return;
        }
        var window = new GmWindow(synth)
        {
            Current = () => tracks.FirstOrDefault(t => t.Track.Id == trackId)?.Track.Gm,
            Pick = (n, drums) =>
            {
                if (tracks.FirstOrDefault(t => t.Track.Id == trackId) is { } t) _ = SetGm(t, n, drums);
            },
            Edit = (change, key) => EditGm(trackId, change, key),
            Shortcut = e => OnKey(this, e),
            Front = on => SamplerInFront(trackId, on),
            IsEnabledForEdits = store is { ReadOnly: false },
        };
        window.Closed += (_, _) =>
        {
            if (gmWindows.GetValueOrDefault(trackId) == window) gmWindows.Remove(trackId);
            SamplerInFront(trackId, false);
        };
        gmWindows[trackId] = window;
        ShowGmState(live, window);
        window.Show(this);
    }

    void ShowGmState(LiveTrack t, GmWindow window)
    {
        window.ShowTrack(t.Track.Name, t.Track.Color);
        window.Refresh(gmMissing ? Loc.T("gm.missing") : GmBank.Loaded ? null : Loc.T("gm.loading"));
    }

    async Task LoadGmBank()
    {
        gmLoading = true;
        InstrumentsChanged();
        var font = await Task.Run(GmBank.Load);
        gmLoading = false;
        gmMissing = font == null;
        if (gmMissing) Toast(Loc.T("gm.missing"));
        SyncGms();
        InstrumentsChanged();
    }

    void DropAllGms()
    {
        foreach (var window in gmWindows.Values.ToList()) window.Close();
        gms.Clear();
    }

    TimelineView.InstrumentState GmState => gmMissing ? TimelineView.InstrumentState.Failed
        : GmBank.Loaded ? TimelineView.InstrumentState.Ready : TimelineView.InstrumentState.Loading;

    /// <summary>Sets a MIDI track's GM program (or kit): one undo step, in place of whatever it played with.</summary>
    Task SetGm(LiveTrack track, int program, bool drums)
    {
        if (store == null || store.ReadOnly || !track.Track.IsMidi) return Task.CompletedTask;
        string id = track.Track.Id;
        return EditTimeline(p =>
        {
            if (p.Tracks.Find(t => t.Id == id) is not { } t) return null;
            t.Instrument = null;
            t.Sampler = null;
            t.Granular = null;
            // the tone stays: what the user set is how they want this track to play, whatever program it has
            t.Gm ??= new GmSettings();
            t.Gm.Program = program;
            t.Gm.Drums = drums;
            return id;
        }, "instrument-set");
    }

    /// <summary>One edit of a track's GM tone, undone with the ones of the same key that follow it (a knob's drag).</summary>
    void EditGm(string trackId, Action<GmSettings> change, string key)
    {
        if (restoring || store == null || store.ReadOnly) return;
        if (tracks.FirstOrDefault(t => t.Track.Id == trackId) is not { Track.Gm: { } g }) return;
        undo.Checkpoint(store.Project, $"gm:{trackId}:{key}");
        change(g);
        g.Normalize();
        SyncGms();
        MarkDirty();
        UpdateUndoButtons();
    }

    /// <summary>
    /// "General MIDI" in an instrument menu: a submenu per family of 8 programs and one with the drum kits, each with a
    /// check on what the track plays. <paramref name="pick"/> is what choosing one does (set it on the track, or make a
    /// new track with it).
    /// </summary>
    MenuItem GmMenu(Func<GmSettings?> current, Action<int, bool> pick, bool enabled, List<Action>? refresh)
    {
        var families = new List<object>();
        for (int f = 0; f < 16; f++)
        {
            int first = f * 8;
            var programs = Enumerable.Range(first, 8).Select(n => (object)GmItem(GmNames.Program(n), () => current() is { Drums: false } g && g.Program == n,
                () => pick(n, false), enabled, refresh)).ToList();
            families.Add(GmGroup(GmNames.Family(f), programs, () => current() is { Drums: false } g && g.Program / 8 == f, refresh));
        }
        var kits = GmNames.Kits.Select(k => (object)GmItem(GmNames.KitOnly(k.Program), () => current() is { Drums: true } g && g.Program == k.Program,
            () => pick(k.Program, true), enabled, refresh)).ToList();
        families.Add(new Separator());
        families.Add(GmGroup(Loc.T("gm.drums"), kits, () => current() is { Drums: true }, refresh));
        var menu = GmGroup(Loc.T("gm.name"), families, () => current() != null, refresh);
        ToolTip.SetTip(menu, Loc.T("gm.tip"));
        ToolTip.SetShowDelay(menu, (int)MenuTipDelay.TotalMilliseconds);
        return menu;
    }

    MenuItem GmItem(string label, Func<bool> on, Action pick, bool enabled, List<Action>? refresh)
    {
        if (refresh == null) return MenuAction(label, () => { pick(); return Task.CompletedTask; }, enabled: enabled);
        return MenuCheck(label, on, pick, refresh, enabled: enabled);
    }

    /// <summary>A submenu whose title carries the check when what the track plays is inside it, so it is found without opening each one.</summary>
    static MenuItem GmGroup(string title, List<object> items, Func<bool> holds, List<Action>? refresh)
    {
        if (refresh == null) return new MenuItem { Header = new TextBlock { Text = title, FontSize = 12 }, ItemsSource = items };
        string Check() => holds() ? "" : " ";
        var lead = new TextBlock { Classes = { "icon" }, Text = Check(), FontSize = 12, Width = 12, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        refresh.Add(() => lead.Text = Check());
        return new MenuItem { Header = MenuHeader(title, lead), ItemsSource = items };
    }
}
