namespace XtremeDaw.Projects;

/// <summary>
/// "Enviar comentarios": turns what the user wrote into GitHub's new-issue page, already filled in. It only builds a
/// link: the app holds no token (one inside the exe could be pulled out and used on the repository) and nothing is
/// sent until the user confirms on that page with their own account. Issues are public, so the report carries only
/// what the dialog shows. The type goes in the title because GitHub ignores <c>labels</c> from whoever can't label.
/// </summary>
public static class IssueReport
{
    public enum Kind { Bug, Idea }

    public const string NewIssueUrl = $"https://github.com/{UpdateCheck.Repo}/issues/new";

    /// <summary>
    /// GitHub refuses links near 8 KB, and one that goes through its login is encoded a second time, up to 5/3 longer.
    /// </summary>
    public const int MaxUrl = 4000;

    /// <summary>The title as it reaches GitHub, with its type first.</summary>
    public static string Title(Kind kind, string title) => $"[{(kind == Kind.Bug ? "Bug" : "Idea")}] {title.Trim()}";

    /// <summary>The description, with one line per fact under a rule; <paramref name="facts"/> empty = just the description.</summary>
    public static string Body(string text, IReadOnlyList<string> facts)
    {
        string body = text.Trim().ReplaceLineEndings("\n");
        return facts.Count == 0 ? body : $"{body}\n\n---\n{string.Join('\n', facts.Select(f => $"- {f}"))}";
    }

    /// <summary>
    /// The link that opens the filled-in page. When <paramref name="body"/> doesn't fit, it goes without it and
    /// <paramref name="complete"/> is false: the caller has to hand the text over some other way.
    /// </summary>
    public static string Url(string title, string body, out bool complete)
    {
        string head = $"{NewIssueUrl}?title={Uri.EscapeDataString(title)}";
        string full = $"{head}&body={Uri.EscapeDataString(body)}";
        complete = full.Length <= MaxUrl;
        return complete ? full : head;
    }
}
