using System.Text.Json;
using Avalonia.Platform;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// What changed in each version, newest first, one JSON per language in Assets/News (embedded). es.json is the
/// reference: a version missing from another language falls back to Spanish, like <see cref="HelpGuide"/>, whose marks
/// the texts use. The window opens by itself when the newest version in the file is not the one last seen
/// (config.json "newsSeen"), so it follows the notes and not the build: a version with nothing to tell asks nothing.
/// </summary>
public static class NewsGuide
{
    public sealed class Item
    {
        /// <summary>Segoe Fluent Icons code point, in hex.</summary>
        public string Icon { get; init; } = "";
        public string Title { get; init; } = "";
        public string Text { get; init; } = "";
        /// <summary>The help topic that explains it.</summary>
        public string? Topic { get; init; }
    }

    public sealed class Release
    {
        public string Version { get; init; } = "";
        public Item[] Items { get; init; } = [];
    }

    sealed class Book
    {
        public Release[] Releases { get; init; } = [];
    }

    const string SeenKey = "newsSeen";

    static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    static Release[]? releases;

    public static IReadOnlyList<Release> Releases => releases ??= Load();

    public static Release? Latest => Releases.Count > 0 ? Releases[0] : null;

    public static bool Unseen => Latest is { } r && AppConfig.Get(SeenKey) != r.Version;

    public static void MarkSeen()
    {
        if (Latest is not { } r) return;
        try { AppConfig.Set(SeenKey, r.Version); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* It would only show again. */ }
    }

    static Release[] Load()
    {
        var reference = Read("es");
        if (Loc.Language == "es") return reference;
        var own = Read(Loc.Language);
        return reference.Select(r => own.FirstOrDefault(o => o.Version == r.Version) ?? r).ToArray();
    }

    static Release[] Read(string code)
    {
        try
        {
            using var s = AssetLoader.Open(new Uri($"avares://XTreme/Assets/News/{code}.json"));
            return JsonSerializer.Deserialize<Book>(s, Options)?.Releases ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or FileNotFoundException)
        {
            return [];
        }
    }
}
