using System.Text.Json.Nodes;

namespace XtremeDaw.Projects;

/// <summary>
/// The difference between two JSON states, small enough to send on every edit of a shared session (#291), and its
/// application to another copy. Objects go key by key; an array whose items all carry an <c>id</c> (tracks, clips,
/// buses, slots…) goes item by item, so two people editing different tracks don't overwrite each other; any other
/// array, and any value, is replaced whole. A patch also applies to a state that is not the one it came from (an
/// older undo step, a copy that already has someone else's change): a changed item that is not there is skipped.
/// <para>Ops, one key each: <c>{"=": value}</c> sets, <c>{"-": 1}</c> removes, <c>{"o": patch}</c> goes into an
/// object, <c>{"a": {"del": [ids], "put": [items], "mod": {id: patch}, "order": [ids]}}</c> goes into an array by id.</para>
/// </summary>
public static class LiveDiff
{
    /// <summary>The patch that turns <paramref name="from"/> into <paramref name="to"/>; empty when they are equal.</summary>
    public static JsonObject Diff(JsonObject from, JsonObject to)
    {
        var patch = new JsonObject();
        foreach (var (key, a) in from)
            if (!to.ContainsKey(key)) patch[key] = new JsonObject { ["-"] = 1 };
        foreach (var (key, b) in to)
        {
            from.TryGetPropertyValue(key, out var a);
            if (!from.ContainsKey(key)) { patch[key] = Set(b); continue; }
            if (Op(a, b) is { } op) patch[key] = op;
        }
        return patch;
    }

    static JsonObject? Op(JsonNode? a, JsonNode? b)
    {
        if (JsonNode.DeepEquals(a, b)) return null;
        if (a is JsonObject oa && b is JsonObject ob) return new JsonObject { ["o"] = Diff(oa, ob) };
        if (a is JsonArray xa && b is JsonArray xb && Ids(xa) is { } ia && Ids(xb) is { } ib)
            return new JsonObject { ["a"] = DiffArray(xa, ia, xb, ib) };
        return Set(b);
    }

    static JsonObject Set(JsonNode? value) => new() { ["="] = value?.DeepClone() };

    /// <summary>The items' ids in order, or null when some item has none (or two share one): then it's replaced whole.</summary>
    static List<string>? Ids(JsonArray a)
    {
        var ids = new List<string>(a.Count);
        var seen = new HashSet<string>();
        foreach (var item in a)
        {
            if (item is not JsonObject o || !o.TryGetPropertyValue("id", out var v) || v is not JsonValue jv
                || !jv.TryGetValue<string>(out var id) || id.Length == 0 || !seen.Add(id)) return null;
            ids.Add(id);
        }
        return ids;
    }

    static JsonObject DiffArray(JsonArray a, List<string> ia, JsonArray b, List<string> ib)
    {
        var inB = ib.ToHashSet();
        var byA = new Dictionary<string, JsonObject>();
        for (int i = 0; i < a.Count; i++) byA[ia[i]] = (JsonObject)a[i]!;
        var del = new JsonArray();
        foreach (var id in ia) if (!inB.Contains(id)) del.Add(id);
        var put = new JsonArray();
        var mod = new JsonObject();
        for (int i = 0; i < b.Count; i++)
        {
            var item = (JsonObject)b[i]!;
            if (!byA.TryGetValue(ib[i], out var old)) put.Add(item.DeepClone());
            else if (!JsonNode.DeepEquals(old, item)) mod[ib[i]] = Diff(old, item);
        }
        var op = new JsonObject();
        if (del.Count > 0) op["del"] = del;
        if (put.Count > 0) op["put"] = put;
        if (mod.Count > 0) op["mod"] = mod;
        // the order only goes when applying the rest wouldn't give it: removals keep it, new items go at the end
        var expected = ia.Where(inB.Contains).Concat(ib.Where(id => !byA.ContainsKey(id)));
        if (!expected.SequenceEqual(ib)) op["order"] = new JsonArray([.. ib.Select(id => (JsonNode?)JsonValue.Create(id))]);
        return op;
    }

    /// <summary>Changes <paramref name="target"/> in place by <paramref name="patch"/>.</summary>
    public static void Apply(JsonObject target, JsonObject patch)
    {
        foreach (var (key, node) in patch)
        {
            if (node is not JsonObject op) continue;
            if (op.ContainsKey("-")) target.Remove(key);
            else if (op.TryGetPropertyValue("=", out var value)) target[key] = value?.DeepClone();
            else if (op["o"] is JsonObject inner)
            {
                // an object that someone else replaced or removed: rebuilt from what this change says
                if (target[key] is not JsonObject o) target[key] = o = [];
                Apply(o, inner);
            }
            else if (op["a"] is JsonObject arr)
            {
                if (target[key] is not JsonArray list) target[key] = list = [];
                ApplyArray(list, arr);
            }
        }
    }

    static string? IdOf(JsonNode? n) =>
        n is JsonObject o && o["id"] is JsonValue v && v.TryGetValue<string>(out var id) ? id : null;

    static void ApplyArray(JsonArray list, JsonObject op)
    {
        var items = list.Select(n => n?.DeepClone()).ToList();
        if (op["del"] is JsonArray del)
        {
            var gone = del.Select(d => d?.GetValue<string>()).ToHashSet();
            items.RemoveAll(n => IdOf(n) is { } id && gone.Contains(id));
        }
        if (op["mod"] is JsonObject mod)
            foreach (var (id, p) in mod)
                if (p is JsonObject patch && items.Find(n => IdOf(n) == id) is JsonObject item) Apply(item, patch);
        if (op["put"] is JsonArray put)
            foreach (var n in put)
            {
                string? id = IdOf(n);
                int at = items.FindIndex(x => id != null && IdOf(x) == id);
                if (at >= 0) items[at] = n?.DeepClone();
                else items.Add(n?.DeepClone());
            }
        if (op["order"] is JsonArray order)
        {
            var rank = new Dictionary<string, int>();
            for (int i = 0; i < order.Count; i++) if (order[i]?.GetValue<string>() is { } id) rank.TryAdd(id, i);
            // what the order doesn't name (someone else's new item) keeps its place after the ones it follows
            var named = items.Where(n => IdOf(n) is { } id && rank.ContainsKey(id)).OrderBy(n => rank[IdOf(n)!]).ToList();
            int k = 0;
            for (int i = 0; i < items.Count; i++)
                if (IdOf(items[i]) is { } id && rank.ContainsKey(id)) items[i] = named[k++];
        }
        list.Clear();
        foreach (var n in items) list.Add(n);
    }

    /// <summary>The patch without the keys <paramref name="drop"/> at its top level.</summary>
    public static JsonObject Without(JsonObject patch, params string[] drop)
    {
        var copy = (JsonObject)patch.DeepClone();
        foreach (var k in drop) copy.Remove(k);
        return copy;
    }
}
