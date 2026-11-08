using System.Collections.Concurrent;

namespace XtremeDaw.Projects;

/// <summary>
/// The plugins of a shared session (#291, the user's call): only what everyone in it has is used. A plugin is told by
/// its file (<see cref="PluginRef.Path"/>, the same on every computer where its installer put it): it is there or the
/// copy can't load it. Each copy tells the others which ones it has (<see cref="LiveSession.LackingIt"/>); a plugin
/// someone lacks can't be picked, and one already in the project stays off on every copy until all have it, so
/// everybody hears the same.
/// </summary>
public static class LivePlugins
{
    /// <summary>How long whether a plugin file is there is believed before looking again.</summary>
    const long InstalledHoldMs = 10_000;
    static readonly ConcurrentDictionary<string, (bool There, long At)> installed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The plugin's file (or bundle folder) is on this computer.</summary>
    public static bool Installed(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        long now = Environment.TickCount64;
        if (installed.TryGetValue(path, out var known) && now - known.At < InstalledHoldMs) return known.There;
        bool there = File.Exists(path) || Directory.Exists(path);
        installed[path] = (there, now);
        return there;
    }

    /// <summary>How a plugin is named between copies: its file.</summary>
    public static string Key(string path) => path.Replace('/', '\\').Trim().ToLowerInvariant();

    /// <summary>Every plugin the project plays through: instruments and the VST3 slots of tracks, folders and buses.</summary>
    public static IEnumerable<PluginRef> All(Project p)
    {
        foreach (var t in p.Tracks)
        {
            if (t.Instrument is { Path.Length: > 0 } i) yield return i;
            foreach (var s in t.Channel.Slots) if (Vst(s) is { } v) yield return v;
        }
        foreach (var f in p.Folders)
            foreach (var s in f.Inserts) if (Vst(s) is { } v) yield return v;
        foreach (var b in p.Settings.Buses)
            foreach (var s in b.Inserts) if (Vst(s) is { } v) yield return v;
    }

    static PluginRef? Vst(InsertSlot s) => s.Type == InsertSlot.VstKind && s.Known && s.Vst!.Plugin.Path.Length > 0 ? s.Vst.Plugin : null;
}
