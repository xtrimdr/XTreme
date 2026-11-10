using System.Text.Json;
using System.Text.Json.Nodes;

namespace XtremeDaw.Projects;

/// <summary>
/// What a shared session (#291) keeps the same on every copy: the undo's editable state (<see cref="UndoManager"/>:
/// tracks, folders, buses, markers, tempo…) plus the media list, so a take recorded on one side shows up on the others.
/// Each person's own things stay out: where their files are, and the solo of tracks and folders (decided with the
/// user: each one listens on their own). A media entry carries its files as <c>media/&lt;name&gt;</c>, where the
/// others will keep them; an entry already there never changes, and none is removed from the others' lists.
/// </summary>
public static class LiveState
{
    public static JsonObject Capture(Project p)
    {
        var o = JsonNode.Parse(UndoManager.Capture(p))!.AsObject();
        o.Remove("places");
        foreach (var t in Items(o, "tracks")) if (t["channel"] is JsonObject ch) ch.Remove("solo");
        foreach (var f in Items(o, "folders")) f.Remove("solo");
        var media = new JsonArray();
        foreach (var m in p.Media)
        {
            var e = JsonSerializer.SerializeToNode(m, ProjectStore.Json)!.AsObject();
            e["audio"] = Shared(m.Audio, m.Id);
            e["video"] = Shared(m.Video, m.Id);
            e.Remove("audioBytes");
            e.Remove("videoBytes");
            media.Add(e);
        }
        o["media"] = media;
        return o;
    }

    /// <summary>Where the others keep a file of a media: always inside the project, by its name.</summary>
    /// <remarks>
    /// The name carries the media's id: two different files can have the same name in two copies (everyone's
    /// "loop.wav", the stems' "vocals.wav"), and by the name alone a copy would take its own file for the other's.
    /// A name that already starts with the id (a take, or one that came through here) stays as it is, so every copy
    /// works out the same name.
    /// </remarks>
    public static string? Shared(string? path, string? mediaId)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string name = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(mediaId) && !name.StartsWith(mediaId, StringComparison.Ordinal)) name = mediaId + "-" + name;
        return ProjectStore.MediaDir + "/" + name;
    }

    static IEnumerable<JsonObject> Items(JsonObject o, string key) =>
        o[key] is JsonArray a ? a.OfType<JsonObject>() : [];

    /// <summary>
    /// Puts <paramref name="state"/> into the project, keeping this copy's solos and file places. Returns the media
    /// that came new (their files still have to arrive).
    /// </summary>
    public static List<Media> Apply(Project p, JsonObject state)
    {
        var o = (JsonObject)state.DeepClone();
        var media = o["media"] as JsonArray;
        o.Remove("media");
        // where this copy keeps its files is its own: a peer that sends places could point a media at any file here
        o.Remove("places");
        var solos = p.Tracks.ToDictionary(t => t.Id, t => t.Channel.Solo);
        var folderSolos = p.Folders.ToDictionary(f => f.Id, f => f.Solo);
        foreach (var t in Items(o, "tracks"))
            if (t["channel"] is JsonObject ch && t["id"]?.GetValue<string>() is { } id && solos.TryGetValue(id, out bool s)) ch["solo"] = s;
        foreach (var f in Items(o, "folders"))
            if (f["id"]?.GetValue<string>() is { } id && folderSolos.TryGetValue(id, out bool s)) f["solo"] = s;
        UndoManager.ApplyState(p, o.ToJsonString(ProjectStore.Json));
        var added = new List<Media>();
        if (media == null) return added;
        var have = p.Media.Select(m => m.Id).ToHashSet();
        foreach (var n in media)
        {
            if (n is not JsonObject e) continue;
            var m = e.Deserialize<Media>(ProjectStore.Json);
            // a media without an id can't be found again, and it would break every save after
            if (m == null || string.IsNullOrEmpty(m.Id) || !have.Add(m.Id)) continue;
            m.Audio = Shared(m.Audio, m.Id);
            m.Video = Shared(m.Video, m.Id);
            p.Media.Add(m);
            added.Add(m);
        }
        return added;
    }
}
