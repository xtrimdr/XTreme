using Avalonia.Controls;
using XtremeDaw.App.Audio;
using XtremeDaw.Audio.Vst;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// Plugins in a shared session (#291, the user's call): only what everyone in it has is used. A plugin someone lacks
/// can't be picked (its menu entry is off and says who lacks it), and one already in the project (an instrument or a
/// VST3 slot of a track, a folder or a bus) stays off on every copy until all have it, so everybody hears the same:
/// its process doesn't run, an effect lets the sound through and an instrument is silent. Its chip and slot say
/// "Falta en nombre · apagado". Who has what comes from <see cref="LiveSession.LackingIt"/>.
/// </summary>
public partial class MainWindow
{
    /// <summary>What a track's instrument chip says while its plugin is off (built when things change, read on every frame).</summary>
    readonly Dictionary<string, string> lackNotes = [];

    /// <summary>Who in the session lacks the plugin at <paramref name="path"/>; null outside a session or when everybody has it.</summary>
    List<string>? LackingIt(string path) => live is { } s && s.LackingIt(path) is { Count: > 0 } who ? who : null;

    /// <summary>The plugin is off in this session (someone lacks it).</summary>
    bool PluginOff(PluginRef p) => LackingIt(p.Path) != null;

    /// <summary>"Falta en Ana · apagado" for a plugin someone in the session lacks; null when it is on.</summary>
    string? LackNote(PluginRef p) => LackingIt(p.Path) is { } who ? Loc.T("live.lacks", string.Join(", ", who)) : null;

    /// <summary>A plugin's menu entry: off, saying who lacks it, when someone in the session doesn't have it.</summary>
    void MarkLacking(MenuItem item, VstPluginInfo p)
    {
        if (LackingIt(p.Path) is not { } who) return;
        item.IsEnabled = false;
        ToolTip.SetTip(item, Loc.T("live.lacks.pick", string.Join(", ", who)));
        ToolTip.SetShowOnDisabled(item, true);
    }

    /// <summary>The instrument chips' notes and the silent flags: rebuilt when the tracks or who has what change.</summary>
    void RefreshLackNotes()
    {
        lackNotes.Clear();
        foreach (var t in tracks)
        {
            string? note = t.Track.Instrument is { } plugin ? LackNote(plugin) : null;
            if (note != null) lackNotes[t.Track.Id] = note;
            t.InstrumentOff = note != null;
        }
        Timeline.InvalidateVisual();
    }

    /// <summary>Who has which plugin changed (or the project's plugins did): what is off goes off, what is back loads.</summary>
    void LivePluginsChanged()
    {
        SyncInstruments();
        SyncEffects();
        RefreshLackNotes();
        InstrumentsChanged();
        EffectsChanged();
    }

    /// <summary>This copy's plugins changed (a scan): the others learn it.</summary>
    void ShareLiveCatalog()
    {
        if (live is { } s && store != null && s.ShareCatalog(store.Project)) LivePluginsChanged();
    }

    /// <summary>The plugin files this copy found, for the others to know what it has.</summary>
    static IEnumerable<string> LiveCatalog() => PluginCatalog.Plugins(PluginScan.Entries).Select(p => p.Path);
}
