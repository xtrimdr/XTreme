using Avalonia.Controls;
using Avalonia.Platform.Storage;
using XtremeDaw.App.Audio;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Dialogs;
using XtremeDaw.App.Model;
using XtremeDaw.Audio;
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
    /// <summary>The banks being read (<see cref="GmBank.Key"/>); one that failed is <see cref="GmBank.Failed"/>.</summary>
    readonly HashSet<string> gmLoading = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hands each track its GM instrument with its preset and drops the ones no track wants. After every <see cref="ApplyTracks"/>.</summary>
    void SyncGms()
    {
        foreach (var id in gms.Keys.ToList())
            if (tracks.FirstOrDefault(t => t.Track.Id == id)?.Track.Gm == null) gms.Remove(id);
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks)
        {
            if (t.Track.Gm is not { } g || t.Track.Instrument != null || t.Track.Sampler != null || t.Track.Granular != null)
            {
                t.Gm = null;
                continue;
            }
            wanted.Add(GmBank.Key(g.Bank));
            if (!gms.TryGetValue(t.Track.Id, out var synth)) gms[t.Track.Id] = synth = new GmSynth(PlaybackEngine.SampleRate);
            if (t.Gm != synth) t.Gm = synth;
            if (GmFont(g, synth) is { } font)
            {
                var preset = font.Find(g.Program, g.Drums);
                var tone = g.Tone();
                if (synth.Font != font || synth.Preset != preset || synth.Tone != tone) synth.Set(font, preset, tone);
            }
        }
        // the shipped bank always: it is what plays while a track's own is read or missing
        if (wanted.Count > 0) wanted.Add(GmBank.Default);
        foreach (var key in wanted)
            if (!GmBank.IsLoaded(key) && !GmBank.Failed(key) && !gmLoading.Contains(key) && (GmBank.IsDefault(key) || GmBank.Available(key))) _ = LoadGmBank(key);
        GmBank.Keep(wanted);
        foreach (var (id, window) in gmWindows.ToList())
        {
            if (tracks.FirstOrDefault(t => t.Track.Id == id) is { Track.Gm: not null } live && gms.ContainsKey(id)) ShowGmState(live, window);
            else window.Close();
        }
    }

    /// <summary>
    /// What a track plays: its bank once read; while it is read, what it already had (or the shipped bank); and the shipped
    /// one when its own isn't on this PC. Null until the shipped one is read.
    /// </summary>
    static SoundFont? GmFont(GmSettings g, GmSynth synth)
    {
        if (GmBank.IsLoaded(g.Bank)) return GmBank.Load(g.Bank);
        bool waiting = !GmBank.IsDefault(g.Bank) && GmBank.Available(g.Bank) && !GmBank.Failed(g.Bank);
        if (waiting && synth.Font != null) return synth.Font;
        return GmBank.Loaded ? GmBank.Load() : null;
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
            PickBank = bank => _ = SetGmBank(trackId, bank),
            DownloadBank = id => _ = DownloadGmBank(trackId, id),
            LoadBankFile = () => _ = PickGmFile(trackId, gmWindows.GetValueOrDefault(trackId) ?? (Window)this),
            Shortcut = e => OnKey(this, e),
            Front = on => SamplerInFront(trackId, on),
            IsEnabledForEdits = store is { ReadOnly: false },
        };
        window.Closed += (_, _) =>
        {
            if (gmWindows.GetValueOrDefault(trackId) == window) gmWindows.Remove(trackId);
            SamplerInFront(trackId, false);
        };
        ForwardKeyUps(window);
        gmWindows[trackId] = window;
        ShowGmState(live, window);
        window.Show(this);
    }

    void ShowGmState(LiveTrack t, GmWindow window)
    {
        window.ShowTrack(t.Track.Name, t.Track.Color);
        window.Refresh(t.Track.Gm is { } g ? GmNotice(g) : null);
    }

    /// <summary>What the GM window says over the program: the bank loading or missing; null when it plays as chosen.</summary>
    string? GmNotice(GmSettings g)
    {
        if (GmBank.Failed(null)) return Loc.T("gm.missing");
        if (!GmBank.IsDefault(g.Bank))
        {
            if (!GmBank.Available(g.Bank)) return Loc.T("gm.bank.missing", GmBank.NameOf(g.Bank), GmBank.DefaultName);
            if (GmBank.Failed(g.Bank)) return Loc.T("gm.bank.broken", GmBank.NameOf(g.Bank), GmBank.DefaultName);
            if (!GmBank.IsLoaded(g.Bank)) return Loc.T("gm.bank.loading", GmBank.NameOf(g.Bank));
        }
        return GmBank.Loaded ? null : Loc.T("gm.loading");
    }

    async Task LoadGmBank(string key)
    {
        gmLoading.Add(key);
        InstrumentsChanged();
        RefreshGmWindows();
        var font = await Task.Run(() => GmBank.Load(key));
        gmLoading.Remove(key);
        if (font == null) Toast(GmBank.IsDefault(key) ? Loc.T("gm.missing") : Loc.T("gm.bank.broken", GmBank.NameOf(key), GmBank.DefaultName));
        SyncGms();
        InstrumentsChanged();
    }

    void RefreshGmWindows()
    {
        foreach (var (id, window) in gmWindows)
            if (tracks.FirstOrDefault(t => t.Track.Id == id) is { Track.Gm: not null } live) ShowGmState(live, window);
    }

    void DropAllGms()
    {
        foreach (var window in gmWindows.Values.ToList()) window.Close();
        gms.Clear();
    }

    /// <summary>Loading only while nothing plays yet: a track whose own bank is still being read already sounds with another.</summary>
    TimelineView.InstrumentState GmState(GmSettings g) => GmBank.Failed(null) ? TimelineView.InstrumentState.Failed
        : GmBank.Loaded ? TimelineView.InstrumentState.Ready : TimelineView.InstrumentState.Loading;

    /// <summary>Sets the bank a MIDI track's GM instrument plays (null = the shipped one): one undo step.</summary>
    Task SetGmBank(string trackId, string? bank)
    {
        if (store == null || store.ReadOnly) return Task.CompletedTask;
        if (RefuseFrozen(trackId))
        {
            RefreshGmWindows();
            return Task.CompletedTask;
        }
        return EditTimeline(p =>
        {
            if (p.Tracks.Find(t => t.Id == trackId) is not { Gm: { } g }) return null;
            g.Bank = bank;
            g.Normalize();
            return trackId;
        }, "instrument-set");
    }

    /// <summary>A bank offered for download, from the GM window: Complementos installs it and the track takes it once it is here.</summary>
    async Task DownloadGmBank(string trackId, string id)
    {
        await ComponentInstall.Run(Components.GmBanks, GmBanksComponent.For(id));
        if (GmBank.Available(id)) await SetGmBank(trackId, id);
        else RefreshGmWindows();
    }

    /// <summary>"Cargar .sf2…" in the GM window: a bank of the user, by its whole path.</summary>
    async Task PickGmFile(string trackId, Window owner)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = Loc.T("gm.bank.file.title"),
            AllowMultiple = false,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType(Loc.T("gm.bank.file.type")) { Patterns = ["*.sf2"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        // a file that doesn't read says so once and isn't kept on the track
        var font = await Task.Run(() => { GmBank.Retry(); return GmBank.Load(path); });
        if (font == null)
        {
            Toast(Loc.T("gm.bank.unreadable", Path.GetFileName(path)));
            return;
        }
        await SetGmBank(trackId, path);
    }

    /// <summary>Sets a MIDI track's GM program (or kit): one undo step, in place of whatever it played with.</summary>
    Task SetGm(LiveTrack track, int program, bool drums)
    {
        if (store == null || store.ReadOnly || !track.Track.IsMidi) return Task.CompletedTask;
        if (RefuseFrozen(track.Track.Id))
        {
            RefreshGmWindows();
            return Task.CompletedTask;
        }
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
        if (tracks.FirstOrDefault(t => t.Track.Id == trackId) is not { Track.Gm: { } g } live) return;
        if (RefuseFrozen(trackId))
        {
            if (gmWindows.TryGetValue(trackId, out var window)) ShowGmState(live, window);
            return;
        }
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
