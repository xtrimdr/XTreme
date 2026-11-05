using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XtremeDaw.Projects;

/// <summary>
/// One ticket of the bench (asked by the user, 2026-11-04: a ticket system, so nothing said is ever lost): a change to
/// try by hand, or a <see cref="Request"/> the user asks for, with its conversation in <see cref="Thread"/> — the request
/// or what to try, each fix an agent sends, each verdict with its note, and comments from either side, in order.
/// </summary>
public sealed class TestItem
{
    public const string Pending = "pending", Works = "ok", Fails = "fail", WorksBadly = "bad", Improve = "improve", Request = "request";

    /// <summary>
    /// What kind of request it is, because each one asks the agent for something else: a bug is fixed, an improvement
    /// adjusted, a feature built (asking about its design) and an idea discussed or kept in PENDIENTES.
    /// </summary>
    public const string Bug = "bug", Better = "improve", Feature = "feature", Idea = "idea";
    public static readonly string[] Kinds = [Bug, Better, Feature, Idea];

    /// <summary>
    /// The kind of a request (<see cref="Kinds"/>); null on what an agent added, and on a request until the agent that
    /// takes it gives it one (asked by the user, 2026-11-04: the bench does not ask for it).
    /// </summary>
    public string? Kind { get; set; }

    /// <summary>
    /// The part of the app a ticket is about (asked by the user, 2026-11-04: a fixed list, thought for the long run, so the
    /// same thing never ends up under two names). Each part is where a musician would look for it, wide enough to last:
    /// a new feature finds its place here instead of adding a topic. The bench filters by it; agents group work by it.
    /// </summary>
    public const string TopicTimeline = "timeline", TopicMixer = "mixer", TopicInspector = "inspector", TopicPianoRoll = "pianoroll",
        TopicInstruments = "instruments", TopicEffects = "effects", TopicMastering = "mastering", TopicAutomation = "automation",
        TopicPlugins = "plugins", TopicAudio = "audio", TopicVideo = "video", TopicExport = "export", TopicAnalysis = "analysis",
        TopicProject = "project", TopicInterface = "interface", TopicBench = "bench";
    /// <summary>In the order the bench shows them: from the arrangement to the tools around the app.</summary>
    public static readonly string[] Topics =
    [
        TopicTimeline, TopicMixer, TopicInspector, TopicPianoRoll, TopicInstruments, TopicEffects, TopicMastering, TopicAutomation,
        TopicPlugins, TopicAudio, TopicVideo, TopicExport, TopicAnalysis, TopicProject, TopicInterface, TopicBench,
    ];

    /// <summary>The ticket's topic (<see cref="Topics"/>); null while nobody gave it one.</summary>
    public string? Topic { get; set; }

    public int Id { get; set; }
    /// <summary>The ticket's subject: the request, or what to try as it was first written. A fix says its own in the thread.</summary>
    public string Text { get; set; } = "";
    /// <summary>
    /// A short name the agent gives the ticket (asked by the user, 2026-11-04): the bench shows it on top and the subject
    /// becomes the first entry of the timeline. Null while no agent named it; the subject stands in.
    /// </summary>
    public string? Title { get; set; }
    /// <summary>Folder name of the test copy (date first, so names sort by age).</summary>
    public string Build { get; set; } = "";
    public DateTime At { get; set; }
    public string State { get; set; } = Pending;
    /// <summary>The note of the current verdict (also in the thread); a fix that comes back clears it.</summary>
    public string? Note { get; set; }
    /// <summary>What the user is writing in the ticket's box: on disk in case the copy being tried dies.</summary>
    public string? Reply { get; set; }
    /// <summary>The verdict picked while the reply is being written: the ticket stays where it is until it is saved.</summary>
    public string? Draft { get; set; }
    /// <summary>How many times it went out to be tried: a fix after "no anda" adds one.</summary>
    public int Tries { get; set; } = 1;
    /// <summary>When an agent took a request to work on it; null while nobody has.</summary>
    public DateTime? Taken { get; set; }
    /// <summary>Who took it (<see cref="TestList.Who"/>): the same session taking it again is not "busy".</summary>
    public string? TakenBy { get; set; }
    /// <summary>
    /// Goes up with every change of what the ticket says (text, state, copy, thread). A click carries the one it saw, so a
    /// ticket left on the screen cannot undo what an agent did meanwhile (a fix sent while the old verdict was shown).
    /// </summary>
    public int Rev { get; set; }
    /// <summary>Added, sent back or answered by an agent and not opened by the user yet: the bench marks it "Nuevo".</summary>
    public bool Unseen { get; set; }
    /// <summary>The conversation, oldest first.</summary>
    public List<TestMessage> Thread { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>One message of a ticket's conversation.</summary>
public sealed class TestMessage
{
    /// <summary>The ticket opened, a fix sent, a verdict given, or something said without changing the state.</summary>
    public const string Open = "open", Fix = "fix", Verdict = "verdict", Comment = "comment";

    public DateTime At { get; set; }
    /// <summary><see cref="TestList.User"/>, <c>agent:&lt;session&gt;</c>, <c>agent</c> (unknown session) or <c>xtlab</c>.</summary>
    public string Who { get; set; } = "";
    public string Kind { get; set; } = Comment;
    /// <summary>The verdict, on a <see cref="Verdict"/>; null on a note kept from before tickets.</summary>
    public string? State { get; set; }
    public string? Text { get; set; }
    /// <summary>The attempt it belongs to.</summary>
    public int Try { get; set; }
    /// <summary>
    /// Images that go with it (asked by the user, #176: to show a problem), as paths under <see cref="TestList.ImageFolder"/>
    /// ("176/20261105-131200-1.png"); null when it has none.
    /// </summary>
    public List<string>? Images { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>An image to attach to a message, not written yet: its bytes and its file extension (".png").</summary>
public sealed record TestImage(byte[] Data, string Extension);

/// <summary>What <c>pruebas\lista.json</c> holds.</summary>
public sealed class TestListData
{
    /// <summary>2: tickets, with their conversation. Older lists are moved on reading (<see cref="TestList"/>).</summary>
    public const int CurrentVersion = 2;
    public int Version { get; set; }
    /// <summary>Numbers are never reused, so "#12" keeps meaning the same thing in a conversation.</summary>
    public int Next { get; set; } = 1;
    public List<TestItem> Items { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public int Count(string state) => Items.Count(i => i.State == state);
}

/// <summary>
/// One line of the list's history (<c>lista.historial.jsonl</c>): who changed which item, how and when. Written only
/// after the change reached the list, so the history never tells of a change that did not happen. Unlike the thread,
/// it also keeps what left the list.
/// </summary>
public sealed class TestEvent
{
    public const string Added = "add", Asked = "request", Edited = "edit", Moved = "resend", Took = "take", Released = "release",
        Fixed = "fix", FixRefused = "fix-closed", Marked = "mark", Commented = "comment", Removed = "remove", Cleared = "clear", Topic = "topic", Titled = "title", Kinded = "kind";

    public DateTime At { get; set; }
    public int Id { get; set; }
    /// <summary><see cref="TestList.User"/>, <c>agent:&lt;session&gt;</c> or <c>xtlab</c>.</summary>
    public string Who { get; set; } = "";
    public string What { get; set; } = "";
    /// <summary>The item's state after the change.</summary>
    public string? State { get; set; }
    public string? Text { get; set; }
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>The click was made on a ticket that no longer says what the item says: nothing was written.</summary>
public sealed class TestListStaleException(int id) : Exception($"#{id} cambió mientras se veía")
{
    public int Id { get; } = id;
}

/// <summary>
/// The bench's tickets (asked by the user: with several changes it was impossible to tell what had been tried and what
/// worked). It lives outside the test copies, in <c>pruebas\lista.json</c>, so it survives them: <c>tools\probar.ps1</c>
/// adds to it through xtlab, the bench marks it, and an agent reads the verdicts back. Several processes write at once:
/// each change holds <c>lista.lock</c> for one read, change and atomic swap, and readers need no lock. Every change also
/// goes to the history, with who made it.
/// </summary>
public static class TestList
{
    /// <summary>The person, in the bench: what they do is never "new" to them.</summary>
    public const string User = "user";
    /// <summary>An agent of a session nobody wrote down (tickets from before the thread).</summary>
    public const string SomeAgent = "agent";
    /// <summary>An agent that took a request this long ago and never sent it to be tried probably died: another one may take it.</summary>
    public static readonly TimeSpan TakeStale = TimeSpan.FromHours(6);
    /// <summary>Long enough for the other writers, short enough for a click.</summary>
    static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);
    /// <summary>A reader can hold the file for a moment while it is being swapped.</summary>
    const int SwapTries = 40;
    const int RetryMs = 25;

    /// <summary>
    /// Who this process writes as: the bench sets <see cref="User"/>; an agent's xtlab says its Claude Code session, so
    /// two sessions never take each other's items for their own.
    /// </summary>
    public static string Who { get; set; } = Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID") is { Length: > 0 } s
        ? "agent:" + s[..Math.Min(8, s.Length)]
        : "xtlab";

    public static string HistoryPath(string path) => Path.ChangeExtension(path, null) + ".historial.jsonl";

    /// <summary>Where a list keeps its tickets' images, one folder per ticket: <c>pruebas\adjuntos</c>.</summary>
    public static string ImageFolder(string path) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "adjuntos");

    /// <summary>The file of one of a message's <see cref="TestMessage.Images"/>.</summary>
    public static string ImagePath(string path, string image) => Path.Combine(ImageFolder(path), image.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The image types the bench takes, by extension.</summary>
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];
    /// <summary>Past this an image is surely not a screenshot: it is refused, so the folder does not fill up.</summary>
    public const int MaxImageBytes = 25 * 1024 * 1024;

    /// <summary>The list, empty if it does not exist yet or cannot be understood.</summary>
    public static TestListData Read(string path) => TryRead(path) ?? new TestListData();

    /// <summary>The list; null if it cannot be understood, so a viewer keeps what it shows instead of showing it empty.</summary>
    public static TestListData? TryRead(string path)
    {
        for (int i = 1; ; i++)
        {
            try
            {
                // File.Replace can leave no file for a moment: while a swap is under way, wait instead of reading an empty list
                if (!File.Exists(path))
                {
                    if (i < SwapTries && File.Exists(path + ".tmp")) { Thread.Sleep(RetryMs); continue; }
                    return new TestListData { Version = TestListData.CurrentVersion };
                }
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return Migrate(JsonSerializer.Deserialize<TestListData>(file, ProjectStore.Json) ?? new TestListData());
            }
            catch (JsonException) { return null; }
            catch (IOException) when (i < SwapTries) { Thread.Sleep(RetryMs); }
            catch (UnauthorizedAccessException) when (i < SwapTries) { Thread.Sleep(RetryMs); }
        }
    }

    /// <summary>
    /// A list from before tickets gets its threads from what it has: the subject, the verdict and its note, and the fix
    /// that brought a ticket back. A note held as a draft is the reply being written; a pending ticket past its first try
    /// carries the note of the try before. In memory; it reaches the file with the next change.
    /// </summary>
    static TestListData Migrate(TestListData d)
    {
        if (d.Version >= TestListData.CurrentVersion) return d;
        foreach (var i in d.Items)
        {
            if (i.Thread.Count > 0) continue;
            string opener = i.State == TestItem.Request || i.Kind != null ? User : SomeAgent;
            i.Thread.Add(new TestMessage { At = i.At, Who = opener, Kind = TestMessage.Open, Text = i.Text, Try = 1 });
            if (i.Draft != null)
            {
                i.Reply = i.Note;
                i.Note = null;
            }
            else if (i.Note != null && i.State == TestItem.Pending)
            {
                if (i.Tries > 1)
                {
                    i.Thread.Add(new TestMessage { At = i.At, Who = User, Kind = TestMessage.Verdict, Text = i.Note, Try = i.Tries - 1 });
                    i.Note = null;
                }
                else
                {
                    i.Reply = i.Note;
                    i.Note = null;
                }
            }
            else if (i.Note != null || i.State is TestItem.Works or TestItem.Fails or TestItem.WorksBadly or TestItem.Improve)
                i.Thread.Add(new TestMessage { At = i.At, Who = User, Kind = TestMessage.Verdict, State = i.State, Text = i.Note, Try = Math.Max(1, i.Tries) });
            if (i.Tries > 1 && i.State == TestItem.Pending)
                i.Thread.Add(new TestMessage { At = i.At, Who = SomeAgent, Kind = TestMessage.Fix, Try = i.Tries });
        }
        d.Version = TestListData.CurrentVersion;
        return d;
    }

    /// <summary>A pending ticket that came back from a fix: it was tried before, and its thread says how it went.</summary>
    public static bool Back(TestItem i) => i.State == TestItem.Pending && i.Tries > 1;

    /// <summary>Whether a copy cannot have this change yet: it was made before the item's copy.</summary>
    public static bool Missing(TestItem item, string build) => string.CompareOrdinal(item.Build, build) > 0;

    /// <summary>Adds a change to try and returns its number; the same text still waiting is moved to the new copy.</summary>
    public static int Add(string path, string text, string build, string? topic = null)
    {
        topic = CheckTopic(topic);
        int id = 0;
        Update(path, (d, log) =>
        {
            text = text.Trim();
            var same = d.Items.Find(i => i.State == TestItem.Pending && i.Text == text);
            if (same != null)
            {
                same.Build = build;
                if (topic != null) same.Topic = topic;
                Touch(same);
                id = same.Id;
                log(same, TestEvent.Moved, null);
                return;
            }
            id = d.Next++;
            var item = new TestItem { Id = id, Text = text, Topic = topic, Build = build, At = DateTime.Now, Unseen = Who != User };
            Say(item, TestMessage.Open, null, text);
            d.Items.Add(item);
            log(item, TestEvent.Added, null);
        });
        return id;
    }

    /// <summary>
    /// A change the user asks for (asked by the user: every request goes here, and the agents take their work from
    /// here). It counts no tries until an agent sends it to be tried. The same request still waiting is not repeated.
    /// </summary>
    public static int AddRequest(string path, string text, string? kind, string build, string? topic = null, IReadOnlyList<TestImage>? images = null)
    {
        if (kind != null && !TestItem.Kinds.Contains(kind)) throw new ArgumentException($"Categoría desconocida: {kind}");
        topic = CheckTopic(topic);
        int id = 0;
        Update(path, (d, log) =>
        {
            text = text.Trim();
            if (d.Items.Find(i => i.State == TestItem.Request && i.Text == text) is { } same)
            {
                if (kind != null) same.Kind = kind;
                if (topic != null) same.Topic = topic;
                if (images is { Count: > 0 }) Say(same, TestMessage.Comment, null, null, images, path);
                Touch(same);
                id = same.Id;
                log(same, TestEvent.Edited, null);
                return;
            }
            id = d.Next++;
            var item = new TestItem
            {
                Id = id, Text = text, Kind = kind, Topic = topic, Build = build, At = DateTime.Now, State = TestItem.Request, Tries = 0, Unseen = Who != User,
            };
            Say(item, TestMessage.Open, null, text, images, path);
            d.Items.Add(item);
            log(item, TestEvent.Asked, null);
        });
        return id;
    }

    public enum TakeResult { Taken, NotFound, NotOpen, Busy }

    /// <summary>
    /// Whether an agent has work to do on the item: a request, or something the user marked as failing or to improve.
    /// Only those can be taken (asked by the user: two sessions reviewing the list must not work on the same item).
    /// </summary>
    public static bool Open(TestItem item) => item.State is TestItem.Request or TestItem.Fails or TestItem.WorksBadly or TestItem.Improve;

    /// <summary>
    /// Whether an agent may take the item: anything not marked as working. An item waiting to be tried is taken too when
    /// the user asks for more in its thread (#173: an agent worked on one and the bench did not show it in progress).
    /// </summary>
    public static bool Takeable(TestItem item) => item.State != TestItem.Works;

    /// <summary>
    /// Whether an agent is still on an item (asked by the user, #173): taken, not stale, and its session not ended.
    /// A session that was closed leaves it free at once, for the bench and for the next <see cref="Take"/>.
    /// </summary>
    public static bool Held(TestItem item, AgentSessions sessions) =>
        Takeable(item) && item.Taken is { } t && DateTime.Now - t < TakeStale && sessions.Alive(item.TakenBy) != false;

    /// <summary>
    /// An agent takes an item (<see cref="Takeable"/>), so no other one works on it at the same time (unless the first
    /// one went stale). The session that has it may take it again. <paramref name="holder"/> says who has it when busy.
    /// </summary>
    public static TakeResult Take(string path, int id) => Take(path, id, out _);

    public static TakeResult Take(string path, int id, out string? holder)
    {
        var result = TakeResult.NotFound;
        string? by = null;
        // read before the lock: it asks the processes, and the lock is only for the change
        var sessions = AgentSessions.Read(AgentSessions.RootOf(path));
        Update(path, (d, log) =>
        {
            if (d.Items.Find(i => i.Id == id) is not { } item) return;
            if (!Takeable(item)) result = TakeResult.NotOpen;
            else if (item.TakenBy != Who && Held(item, sessions))
            {
                result = TakeResult.Busy;
                by = item.TakenBy;
            }
            else
            {
                item.Taken = DateTime.Now;
                item.TakenBy = Who;
                result = TakeResult.Taken;
                log(item, TestEvent.Took, null);
            }
        });
        holder = by;
        return result;
    }

    /// <summary>
    /// The user changes a request's text, kind or topic; false if it is no longer a request. <paramref name="kind"/>: null
    /// leaves it. <paramref name="topic"/>: null leaves it, "" takes it off.
    /// </summary>
    public static bool EditRequest(string path, int id, string text, string? kind, int? rev = null, string? topic = null)
    {
        string? next = topic == "" ? null : CheckTopic(topic);
        bool found = false;
        Update(path, (d, log) =>
        {
            if (d.Items.Find(i => i.Id == id && i.State == TestItem.Request) is not { } item) return;
            Check(item, rev);
            found = true;
            if (!string.IsNullOrWhiteSpace(text)) item.Text = text.Trim();
            if (kind != null && TestItem.Kinds.Contains(kind)) item.Kind = kind;
            if (topic != null) item.Topic = next;
            if (item.Thread.Count > 0 && item.Thread[0].Kind == TestMessage.Open) item.Thread[0].Text = item.Text;
            Touch(item);
            log(item, TestEvent.Edited, null);
        });
        return found;
    }

    /// <summary>
    /// Gives a ticket its topic, or takes it off with null; false if it is not there. It does not change what the ticket
    /// says (no <see cref="TestItem.Rev"/>, not "Nuevo"): a verdict clicked meanwhile still counts.
    /// </summary>
    public static bool SetTopic(string path, int id, string? topic)
    {
        topic = CheckTopic(topic);
        bool found = false;
        Update(path, (d, log) =>
        {
            if (d.Items.Find(i => i.Id == id) is not { } item) return;
            found = true;
            if (item.Topic == topic) return;
            item.Topic = topic;
            log(item, TestEvent.Topic, topic);
        });
        return found;
    }

    /// <summary>
    /// Gives a request its kind, or takes it off with null; false if it is not a request. The agent that takes it does it
    /// (asked by the user, 2026-11-04). Like the topic, it does not change what the ticket says.
    /// </summary>
    public static bool SetKind(string path, int id, string? kind)
    {
        if (kind != null && !TestItem.Kinds.Contains(kind)) throw new ArgumentException($"Categoría desconocida: {kind}");
        bool found = false;
        Update(path, (d, log) =>
        {
            if (d.Items.Find(i => i.Id == id && i.State == TestItem.Request) is not { } item) return;
            found = true;
            if (item.Kind == kind) return;
            item.Kind = kind;
            log(item, TestEvent.Kinded, kind);
        });
        return found;
    }

    /// <summary>
    /// Gives a ticket its title, or takes it off with an empty one; false if it is not there. Like the topic, it does not
    /// change what the ticket says (no <see cref="TestItem.Rev"/>, not "Nuevo").
    /// </summary>
    public static bool SetTitle(string path, int id, string? title)
    {
        title = Clean(title);
        bool found = false;
        Update(path, (d, log) =>
        {
            if (d.Items.Find(i => i.Id == id) is not { } item) return;
            found = true;
            if (item.Title == title) return;
            item.Title = title;
            log(item, TestEvent.Titled, title);
        });
        return found;
    }

    /// <summary>A topic from the list, or null for none; anything else is refused, so the list stays the one in <see cref="TestItem.Topics"/>.</summary>
    static string? CheckTopic(string? topic) => topic switch
    {
        null or "" => null,
        _ when TestItem.Topics.Contains(topic) => topic,
        _ => throw new ArgumentException($"Tema desconocido: {topic}"),
    };

    /// <summary>Gives a taken item back, untouched, for another agent.</summary>
    public static bool Release(string path, int id)
    {
        bool found = false;
        Update(path, (d, log) =>
        {
            if (d.Items.Find(i => i.Id == id && Takeable(i)) is not { } item) return;
            found = true;
            item.Taken = null;
            item.TakenBy = null;
            log(item, TestEvent.Released, null);
        });
        return found;
    }

    public enum FixResult { Done, NotFound, Closed }

    /// <summary>
    /// A fix for an item: it goes back to be tried on the new copy, and what to try now is the fix's message in the
    /// thread. The note of the verdict before stays in the thread, not in the box of the new try (asked by the user: it
    /// looked as if the new copy had been seen already). An item the user already marked as working stays closed.
    /// </summary>
    public static FixResult Fixed(string path, int id, string build, string? text = null)
    {
        var result = FixResult.NotFound;
        Update(path, (d, log) =>
        {
            if (d.Items.Find(i => i.Id == id) is not { } item) return;
            if (item.State == TestItem.Works)
            {
                result = FixResult.Closed;
                log(item, TestEvent.FixRefused, null);
                return;
            }
            result = FixResult.Done;
            item.Note = null;
            item.Draft = null;
            item.State = TestItem.Pending;
            item.Build = build;
            item.At = DateTime.Now;
            item.Tries++;
            item.Taken = null;
            item.TakenBy = null;
            item.Unseen = Who != User;
            Say(item, TestMessage.Fix, null, string.IsNullOrWhiteSpace(text) ? null : text.Trim());
            Touch(item);
            log(item, TestEvent.Fixed, text);
        });
        return result;
    }

    /// <summary>The user's verdict and note on an item; <paramref name="rev"/> is the one the ticket showed.</summary>
    public static void Mark(string path, int id, string state, string? note, int? rev = null, IReadOnlyList<TestImage>? images = null) => Update(path, (d, log) =>
    {
        if (d.Items.Find(i => i.Id == id) is not { } item) return;
        Check(item, rev);
        item.State = state;
        item.Draft = null;
        item.Reply = null;
        item.Note = Clean(note);
        item.Unseen = false;
        Say(item, TestMessage.Verdict, state, item.Note, images, path);
        Touch(item);
        log(item, TestEvent.Marked, item.Note);
    });

    /// <summary>
    /// Something said in a ticket without changing its state: the user adding to it, or an agent asking or telling.
    /// What an agent says is "Nuevo" to the user. False if the ticket is not there, or it has neither text nor images.
    /// </summary>
    public static bool Comment(string path, int id, string? text, int? rev = null, IReadOnlyList<TestImage>? images = null)
    {
        text = Clean(text);
        if (text == null && images is not { Count: > 0 }) return false;
        bool found = false;
        Update(path, (d, log) =>
        {
            if (d.Items.Find(i => i.Id == id) is not { } item) return;
            Check(item, rev);
            found = true;
            Say(item, TestMessage.Comment, null, text, images, path);
            if (Who == User) item.Reply = null;
            else item.Unseen = true;
            Touch(item);
            log(item, TestEvent.Commented, text ?? $"({images!.Count} img.)");
        });
        return found;
    }

    /// <summary>
    /// The reply being written and its verdict: on disk in case the copy being tried dies, without moving the item. A
    /// draft equal to the saved verdict is none, so a reply saved late does not leave the verdict half open. Not in the
    /// history nor the thread: it is a draft.
    /// </summary>
    public static void Hold(string path, int id, string? draft, string? reply) => Update(path, (d, _) =>
    {
        if (d.Items.Find(i => i.Id == id) is not { } item) return;
        item.Draft = draft == item.State ? null : draft;
        item.Reply = Clean(reply);
    });

    /// <summary>The user opened these tickets: they stop being "Nuevo". Not in the history.</summary>
    public static void See(string path, IReadOnlyCollection<int> ids)
    {
        if (ids.Count == 0) return;
        Update(path, (d, _) =>
        {
            foreach (var i in d.Items)
                if (ids.Contains(i.Id)) i.Unseen = false;
        });
    }

    static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public static void Remove(string path, int id, int? rev = null) => Update(path, (d, log) =>
    {
        if (d.Items.Find(i => i.Id == id) is not { } item) return;
        Check(item, rev);
        d.Items.Remove(item);
        DropImages(path, item.Id);
        log(item, TestEvent.Removed, null);
    });

    /// <summary>Takes off everything that works.</summary>
    public static void ClearWorking(string path) => Update(path, (d, log) =>
    {
        foreach (var item in d.Items.Where(i => i.State == TestItem.Works).ToList())
        {
            d.Items.Remove(item);
            DropImages(path, item.Id);
            log(item, TestEvent.Cleared, null);
        }
    });

    /// <summary>The history, oldest first: of one item, or of all with <paramref name="id"/> 0. Lines it cannot read are skipped.</summary>
    public static List<TestEvent> History(string path, int id = 0)
    {
        var list = new List<TestEvent>();
        var file = HistoryPath(path);
        if (!File.Exists(file)) return list;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize<TestEvent>(line, ProjectStore.Json) is { } e && (id == 0 || e.Id == id)) list.Add(e);
                }
                catch (JsonException) { }
            }
        }
        catch (IOException) { }
        return list;
    }

    static void Say(TestItem item, string kind, string? state, string? text, IReadOnlyList<TestImage>? images = null, string? path = null) =>
        item.Thread.Add(new TestMessage
        {
            At = DateTime.Now, Who = Who, Kind = kind, State = state, Text = text, Try = Math.Max(1, item.Tries),
            Images = images is { Count: > 0 } && path != null && WriteImages(path, item.Id, images) is { Count: > 0 } names ? names : null,
        });

    /// <summary>
    /// Writes a message's images into the ticket's folder, each through a temporary file, while the list's lock is held,
    /// so two writers never pick the same name. Names start with the time, so they sort in the order they were said.
    /// </summary>
    static List<string> WriteImages(string path, int id, IReadOnlyList<TestImage> images)
    {
        var dir = Path.Combine(ImageFolder(path), id.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        var names = new List<string>();
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        int n = 1;
        foreach (var img in images)
        {
            if (img.Data.Length == 0 || img.Data.Length > MaxImageBytes) continue;
            string ext = img.Extension.ToLowerInvariant();
            if (!ImageExtensions.Contains(ext)) ext = ".png";
            string name;
            while (File.Exists(Path.Combine(dir, name = $"{stamp}-{n}{ext}"))) n++;
            string file = Path.Combine(dir, name), tmp = file + ".tmp";
            using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                f.Write(img.Data);
                f.Flush(true);
            }
            File.Move(tmp, file);
            names.Add($"{id}/{name}");
            n++;
        }
        return names;
    }

    /// <summary>A ticket that leaves the list takes its images with it; a folder that cannot be deleted now stays, harmless.</summary>
    static void DropImages(string path, int id)
    {
        try
        {
            var dir = Path.Combine(ImageFolder(path), id.ToString(CultureInfo.InvariantCulture));
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static void Touch(TestItem item) => item.Rev++;

    static void Check(TestItem item, int? rev)
    {
        if (rev is { } r && item.Rev != r) throw new TestListStaleException(item.Id);
    }

    delegate void Log(TestItem item, string what, string? note);

    static void Update(string path, Action<TestListData, Log> change)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var turn = TakeLock(path + ".lock");
        if (File.Exists(path) && !Readable(path)) File.Copy(path, path + ".roto", true);
        var data = Read(path);
        var events = new List<TestEvent>();
        change(data, (item, what, note) => events.Add(new TestEvent
        {
            At = DateTime.Now, Id = item.Id, Who = Who, What = what, State = item.State, Text = item.Text, Note = note,
        }));
        Write(path, JsonSerializer.Serialize(data, ProjectStore.Json));
        if (events.Count > 0) Append(HistoryPath(path), events);
    }

    /// <summary>
    /// Appends, still holding the list's lock, so lines never interleave. One line per event: a crash halfway leaves at
    /// most a broken last line, which <see cref="History"/> skips. A history that cannot be written never stops the change.
    /// </summary>
    static void Append(string file, List<TestEvent> events)
    {
        var sb = new StringBuilder();
        foreach (var e in events) sb.Append(JsonSerializer.Serialize(e, ProjectStore.Json).Replace("\n", "").Replace("\r", "")).Append('\n');
        try
        {
            using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(Encoding.UTF8.GetBytes(sb.ToString()));
            stream.Flush(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A file nobody can read would be replaced by an empty list: it is kept aside first.</summary>
    static bool Readable(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            return true;
        }
        catch (JsonException) { return false; }
    }

    static FileStream TakeLock(string path)
    {
        var until = DateTime.UtcNow + LockWait;
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(RetryMs); }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < until) { Thread.Sleep(RetryMs); }
        }
    }

    /// <summary>Temporary file, forced to disk, swapped in: a crash at any point leaves the old list whole.</summary>
    static void Write(string path, string content)
    {
        string tmp = path + ".tmp";
        using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            file.Write(Encoding.UTF8.GetBytes(content));
            file.Flush(true);
        }
        for (int i = 1; ; i++)
        {
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null, true);
                else File.Move(tmp, path);
                return;
            }
            catch (IOException) when (i < SwapTries) { Thread.Sleep(RetryMs); }
            catch (UnauthorizedAccessException) when (i < SwapTries) { Thread.Sleep(RetryMs); }
        }
    }
}
