using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XtremeDaw.Projects;

/// <summary>One change to try by hand: what to try, the test copy that first has it and what the user found.</summary>
public sealed class TestItem
{
    public const string Pending = "pending", Works = "ok", Fails = "fail";

    public int Id { get; set; }
    public string Text { get; set; } = "";
    /// <summary>Folder name of the test copy (date first, so names sort by age).</summary>
    public string Build { get; set; } = "";
    public DateTime At { get; set; }
    public string State { get; set; } = Pending;
    public string? Note { get; set; }
    /// <summary>How many times it went out to be tried: a fix after "no anda" adds one.</summary>
    public int Tries { get; set; } = 1;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>What <c>pruebas\lista.json</c> holds.</summary>
public sealed class TestListData
{
    /// <summary>Numbers are never reused, so "#12" keeps meaning the same thing in a conversation.</summary>
    public int Next { get; set; } = 1;
    public List<TestItem> Items { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public int Count(string state) => Items.Count(i => i.State == state);
}

/// <summary>
/// The list of changes waiting to be tried by hand (asked by the user: with several changes it was impossible to tell
/// what had been tried and what worked). It lives outside the test copies, in <c>pruebas\lista.json</c>, so it
/// survives them: <c>tools\probar.ps1</c> adds to it through xtlab, the panel of a test copy marks it, and an agent
/// reads the verdicts back. Several processes write at once: each change holds <c>lista.lock</c> for one read, change
/// and atomic swap, and readers need no lock.
/// </summary>
public static class TestList
{
    /// <summary>Long enough for the other writers, short enough for a click.</summary>
    static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);
    /// <summary>A reader can hold the file for a moment while it is being swapped.</summary>
    const int SwapTries = 40;
    const int RetryMs = 25;

    /// <summary>The list, empty if it does not exist yet or cannot be understood.</summary>
    public static TestListData Read(string path)
    {
        for (int i = 1; ; i++)
        {
            try
            {
                if (!File.Exists(path)) return new TestListData();
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<TestListData>(file, ProjectStore.Json) ?? new TestListData();
            }
            catch (JsonException) { return new TestListData(); }
            catch (IOException) when (i < SwapTries) { Thread.Sleep(RetryMs); }
            catch (UnauthorizedAccessException) when (i < SwapTries) { Thread.Sleep(RetryMs); }
        }
    }

    /// <summary>Whether a copy cannot have this change yet: it was made before the item's copy.</summary>
    public static bool Missing(TestItem item, string build) => string.CompareOrdinal(item.Build, build) > 0;

    /// <summary>Adds a change to try and returns its number; the same text still waiting is moved to the new copy.</summary>
    public static int Add(string path, string text, string build)
    {
        int id = 0;
        Update(path, d =>
        {
            text = text.Trim();
            var same = d.Items.Find(i => i.State == TestItem.Pending && i.Text == text);
            if (same != null)
            {
                same.Build = build;
                id = same.Id;
                return;
            }
            id = d.Next++;
            d.Items.Add(new TestItem { Id = id, Text = text, Build = build, At = DateTime.Now });
        });
        return id;
    }

    /// <summary>A fix for an item: it goes back to be tried on the new copy and keeps its note. False if it is not on the list.</summary>
    public static bool Fixed(string path, int id, string build, string? text = null)
    {
        bool found = false;
        Update(path, d =>
        {
            if (d.Items.Find(i => i.Id == id) is not { } item) return;
            found = true;
            item.State = TestItem.Pending;
            item.Build = build;
            item.At = DateTime.Now;
            item.Tries++;
            if (!string.IsNullOrWhiteSpace(text)) item.Text = text.Trim();
        });
        return found;
    }

    /// <summary>The user's verdict and note on an item.</summary>
    public static void Mark(string path, int id, string state, string? note) => Update(path, d =>
    {
        if (d.Items.Find(i => i.Id == id) is not { } item) return;
        item.State = state;
        item.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    });

    public static void Remove(string path, int id) => Update(path, d => d.Items.RemoveAll(i => i.Id == id));

    /// <summary>Takes off everything that works.</summary>
    public static void ClearWorking(string path) => Update(path, d => d.Items.RemoveAll(i => i.State == TestItem.Works));

    static void Update(string path, Action<TestListData> change)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var turn = TakeLock(path + ".lock");
        if (File.Exists(path) && !Readable(path)) File.Copy(path, path + ".roto", true);
        var data = Read(path);
        change(data);
        Write(path, JsonSerializer.Serialize(data, ProjectStore.Json));
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
