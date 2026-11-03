using System.Security.Cryptography;
using System.Text;

namespace XtremeDaw.Projects;

/// <summary>
/// The pictures of the plugins, one file per plugin in %APPDATA%\XTreme\plugin-thumbs: each computer makes its own from
/// what is installed there, nothing ships with the app. A plugin has no way to give one away except the PNG a VST3 bundle
/// may carry (Contents\Resources\Snapshots), so the rest come from the plugin's own window the first time it opens, or
/// when the user asks (what other DAWs do by hand). Opening every plugin to get them was left out on purpose: some only
/// draw on screen and others show a login until someone signs in.
/// </summary>
public static class PluginThumbs
{
    static string Dir => Path.Combine(AppConfig.Dir, "plugin-thumbs");

    /// <summary>Where this computer's picture of a plugin goes. A hash: the plugin's path and class make a poor file name.</summary>
    public static string FileOf(string path, string classId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant() + "|" + classId));
        return Path.Combine(Dir, Convert.ToHexString(hash, 0, 12) + ".png");
    }

    /// <summary>The same, with its folder in place, for the host to write to.</summary>
    public static string Prepare(string path, string classId)
    {
        Directory.CreateDirectory(Dir);
        return FileOf(path, classId);
    }

    /// <summary>The picture to show: the one taken here, or the bundle's. Null: none yet.</summary>
    public static string? Find(string path, string classId)
    {
        string own = FileOf(path, classId);
        return File.Exists(own) ? own : Bundled(path, classId);
    }

    /// <summary>Its window should be pictured when it opens: there is none, or the plugin was updated after the last one.</summary>
    public static bool Due(string path, string classId)
    {
        string own = FileOf(path, classId);
        if (!File.Exists(own)) return Bundled(path, classId) == null;
        try { return Modified(path) > File.GetLastWriteTimeUtc(own); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The PNG a VST3 bundle carries for hosts, the sharper one first.</summary>
    static string? Bundled(string path, string classId)
    {
        string folder = Path.Combine(path, "Contents", "Resources", "Snapshots");
        if (!Directory.Exists(folder)) return null;
        foreach (string name in new[] { "_snapshot_2.0x.png", "_snapshot.png" })
            if (Path.Combine(folder, classId + name) is var file && File.Exists(file)) return file;
        return null;
    }

    static DateTime Modified(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : Directory.GetLastWriteTimeUtc(path);
}
