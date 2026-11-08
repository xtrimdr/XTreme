using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Avalonia.Platform;

namespace XtremeDaw.App;

/// <summary>
/// The contents of the help window: categories of task-named topics, one JSON per language in Assets/Help (embedded).
/// They live apart from Assets/Lang because they are pages, not labels. es.json is the reference: a category or topic
/// missing from another language falls back to Spanish, like <see cref="Loc"/>. Texts use **bold** for what the user
/// sees on screen, {key:id} / {wheel:id} for shortcuts, so a key the user changed reads right here too, and {icon:E74E}
/// for the glyph of a button on screen.
/// </summary>
public static partial class HelpGuide
{
    public sealed class Topic
    {
        public string Id { get; init; } = "";
        /// <summary>Segoe Fluent Icons code point, in hex; without one, the category's.</summary>
        public string Icon { get; init; } = "";
        public string Title { get; init; } = "";
        /// <summary>Words people search with that the text itself doesn't use ("no suena", "pisar una toma").</summary>
        public string Keywords { get; init; } = "";
        public string Intro { get; init; } = "";
        public string[] Steps { get; init; } = [];
        public string[] Notes { get; init; } = [];
        /// <summary>A tab of Configuración that the topic offers to open.</summary>
        public string? Settings { get; init; }
        public string[] See { get; init; } = [];
        [JsonIgnore] public Category Category { get; set; } = null!;
        [JsonIgnore] public string Haystack { get; set; } = "";
    }

    public sealed class Category
    {
        public string Id { get; init; } = "";
        /// <summary>Segoe Fluent Icons code point, in hex.</summary>
        public string Icon { get; init; } = "";
        public string Title { get; init; } = "";
        public string Hint { get; init; } = "";
        public Topic[] Topics { get; set; } = [];
    }

    sealed class Book
    {
        public Category[] Categories { get; init; } = [];
    }

    static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    static Category[]? categories;

    public static IReadOnlyList<Category> Categories => categories ??= Load();

    public static Topic? Find(string id)
    {
        foreach (var c in Categories)
            foreach (var t in c.Topics)
                if (t.Id == id) return t;
        return null;
    }

    public static string Glyph(string hex) => char.ConvertFromUtf32(int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    public static string Glyph(Category c) => Glyph(c.Icon);

    public static string Glyph(Topic t) => Glyph(t.Icon.Length > 0 ? t.Icon : t.Category.Icon);

    static Category[] Load()
    {
        var reference = Read("es");
        var own = Loc.Language == "es" ? reference : Read(Loc.Language);
        var cats = reference.Select(c => own.FirstOrDefault(o => o.Id == c.Id) ?? c).ToArray();
        for (int i = 0; i < cats.Length; i++)
        {
            var translated = cats[i].Topics;
            cats[i].Topics = reference[i].Topics.Select(t => translated.FirstOrDefault(o => o.Id == t.Id) ?? t).ToArray();
            foreach (var t in cats[i].Topics) t.Category = cats[i];
        }
        return cats;
    }

    static Category[] Read(string code)
    {
        try
        {
            using var s = AssetLoader.Open(new Uri($"avares://XTreme/Assets/Help/{code}.json"));
            return JsonSerializer.Deserialize<Book>(s, Options)?.Categories ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or FileNotFoundException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"\*\*(?<bold>.+?)\*\*|\{key:(?<key>\w+)\}|\{wheel:(?<wheel>\w+)\}|\{icon:(?<icon>[0-9A-Fa-f]+)\}")]
    private static partial Regex Marks();

    public enum Mark { Plain, Bold, Key, Icon }

    /// <summary>Text with its marks found: each piece in order, with what it is.</summary>
    public static IEnumerable<(string Text, Mark Mark)> Pieces(string text)
    {
        int at = 0;
        foreach (Match m in Marks().Matches(text))
        {
            if (m.Index > at) yield return (text[at..m.Index], Mark.Plain);
            if (m.Groups["bold"].Success) yield return (m.Groups["bold"].Value, Mark.Bold);
            else if (m.Groups["key"].Success) yield return (KeyLabel(m.Groups["key"].Value), Mark.Key);
            else if (m.Groups["wheel"].Success) yield return (WheelLabel(m.Groups["wheel"].Value), Mark.Key);
            else yield return (Glyph(m.Groups["icon"].Value), Mark.Icon);
            at = m.Index + m.Length;
        }
        if (at < text.Length) yield return (text[at..], Mark.Plain);
    }

    static string KeyLabel(string id) =>
        Shortcuts.All.Any(a => a.Id == id) && Shortcuts.Label(id) is { Length: > 0 } k ? k : Loc.T("set.sc.none");

    static string WheelLabel(string id) =>
        Shortcuts.Wheel.Any(a => a.Id == id) && Shortcuts.WheelLabel(id) is { Length: > 0 } k ? k : Loc.T("set.sc.none");

    static string Plain(string text) => string.Concat(Pieces(text).Where(p => p.Mark != Mark.Icon).Select(p => p.Text));

    /// <summary>Lowercase and without accents: "cómo" finds "Como" and the other way around.</summary>
    internal static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Texts are indexed each time the window opens, because shortcuts may have changed since the last time.</summary>
    public static void Index()
    {
        foreach (var c in Categories)
            foreach (var t in c.Topics)
                t.Haystack = Fold(string.Join(' ', [t.Title, t.Keywords, Plain(t.Intro), .. t.Steps.Select(Plain), .. t.Notes.Select(Plain)]));
    }

    /// <summary>The topics with every word of the query, those with it in the title first.</summary>
    public static List<Topic> Search(string query)
    {
        var words = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Categories.SelectMany(c => c.Topics)
            .Where(t => words.All(t.Haystack.Contains))
            .OrderByDescending(t => words.Count(w => Fold(t.Title).Contains(w) || Fold(t.Keywords).Contains(w)))
            .ToList();
    }
}
