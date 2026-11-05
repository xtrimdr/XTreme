using System.Text.Json;
using Avalonia.Input;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The keyboard shortcuts (native only, the web's are fixed in App.tsx onKey): one table of actions with their
/// default keys, the user's changes on top, remembered in the program's config ("shortcuts", only what differs
/// from the default). Tooltips read their key from here (<see cref="Tip"/>), so a change shows up everywhere.
/// Esc is not in the table: it always cancels (count-in, gesture, dialogs). The timeline's wheel gestures (zoom,
/// sideways, track height) are a second, smaller table (<see cref="Wheel"/>): their "key" is only the modifiers held
/// while turning the wheel, remembered apart ("wheel") because a modifier alone is not a <see cref="KeyGesture"/>.
/// A layout (<see cref="KeyLayouts"/>, config "shortcutLayout") swaps the base table for another DAW's; the user's
/// changes go on top of the active layout and each layout keeps its own ("shortcuts.cubase"…; XTreme's table keeps
/// the plain keys). The wheel's direction is the user's hand, not a layout's, so it stays the same in all of them.
/// </summary>
static class Shortcuts
{
    public sealed record Action(string Id, string Group, string Name, KeyGesture[] Defaults);

    static KeyGesture G(Key k, KeyModifiers m = KeyModifiers.None) => new(k, m);

    public static readonly Action[] All =
    [
        new("play", Loc.T("sc.group.transport"), Loc.T("sc.play"), [G(Key.Space)]),
        new("toStart", Loc.T("sc.group.transport"), Loc.T("sc.toStart"), [G(Key.Home)]),
        new("cutAudio", Loc.T("sc.group.transport"), Loc.T("sc.cutAudio"), [G(Key.OemPeriod, KeyModifiers.Control)]),
        new("record", Loc.T("sc.group.transport"), Loc.T("sc.record"), [G(Key.R, KeyModifiers.Shift), G(Key.Multiply)]),
        new("click", Loc.T("sc.group.transport"), Loc.T("sc.click"), [G(Key.C)]),
        new("countIn", Loc.T("sc.group.transport"), Loc.T("sc.countIn"), [G(Key.C, KeyModifiers.Shift)]),
        new("tap", Loc.T("sc.group.transport"), Loc.T("sc.tap"), [G(Key.T)]),
        new("tuner", Loc.T("sc.group.transport"), Loc.T("sc.tuner"), [G(Key.A)]),
        new("loop", Loc.T("sc.group.transport"), Loc.T("sc.loop"), [G(Key.L)]),
        new("loopIn", Loc.T("sc.group.transport"), Loc.T("sc.loopIn"), [G(Key.I)]),
        new("loopOut", Loc.T("sc.group.transport"), Loc.T("sc.loopOut"), [G(Key.O)]),
        new("marker", Loc.T("sc.group.transport"), Loc.T("sc.marker"), [G(Key.K)]),
        new("prevMarker", Loc.T("sc.group.transport"), Loc.T("sc.prevMarker"), [G(Key.OemComma)]),
        new("nextMarker", Loc.T("sc.group.transport"), Loc.T("sc.nextMarker"), [G(Key.OemPeriod)]),
        new("mute", Loc.T("sc.group.tracks"), Loc.T("sc.mute"), [G(Key.M)]),
        new("solo", Loc.T("sc.group.tracks"), Loc.T("sc.solo"), [G(Key.S)]),
        new("arm", Loc.T("sc.group.tracks"), Loc.T("sc.arm"), [G(Key.R)]),
        new("newTrack", Loc.T("sc.group.tracks"), Loc.T("sc.newTrack"), [G(Key.N, KeyModifiers.Control | KeyModifiers.Shift)]),
        new("duplicateTrack", Loc.T("sc.group.tracks"), Loc.T("sc.duplicateTrack"), [G(Key.D, KeyModifiers.Control | KeyModifiers.Shift)]),
        new("virtualKeys", Loc.T("sc.group.tracks"), Loc.T("sc.virtualKeys"), [G(Key.K, KeyModifiers.Control)]),
        new("delete", Loc.T("sc.group.editing"), Loc.T("sc.delete"), [G(Key.Delete)]),
        new("split", Loc.T("sc.group.editing"), Loc.T("sc.split"), [G(Key.X, KeyModifiers.Alt), G(Key.Insert)]),
        new("duplicate", Loc.T("sc.group.editing"), Loc.T("sc.duplicate"), [G(Key.D), G(Key.B, KeyModifiers.Control)]),
        new("alignGrid", Loc.T("sc.group.editing"), Loc.T("sc.alignGrid"), [G(Key.Q, KeyModifiers.Shift)]),
        new("selectAll", Loc.T("sc.group.editing"), Loc.T("sc.selectAll"), [G(Key.A, KeyModifiers.Control)]),
        new("deselect", Loc.T("sc.group.editing"), Loc.T("sc.deselect"), [G(Key.D, KeyModifiers.Control)]),
        new("quantize", Loc.T("sc.group.editing"), Loc.T("sc.quantize"), [G(Key.Q)]),
        new("chordTool", Loc.T("sc.group.editing"), Loc.T("sc.chordTool"), [G(Key.H)]),
        new("copy", Loc.T("sc.group.editing"), Loc.T("sc.copy"), [G(Key.C, KeyModifiers.Control)]),
        new("cut", Loc.T("sc.group.editing"), Loc.T("sc.cut"), [G(Key.X, KeyModifiers.Control)]),
        new("paste", Loc.T("sc.group.editing"), Loc.T("sc.paste"), [G(Key.V, KeyModifiers.Control)]),
        new("deleteClose", Loc.T("sc.group.editing"), Loc.T("sc.deleteClose"), [G(Key.Delete, KeyModifiers.Shift)]),
        new("undo", Loc.T("sc.group.editing"), Loc.T("sc.undo"), [G(Key.Z, KeyModifiers.Control)]),
        new("redo", Loc.T("sc.group.editing"), Loc.T("sc.redo"), [G(Key.Y, KeyModifiers.Control), G(Key.Z, KeyModifiers.Control | KeyModifiers.Shift)]),
        new("save", Loc.T("sc.group.editing"), Loc.T("sc.save"), [G(Key.S, KeyModifiers.Control)]),
        new("saveAs", Loc.T("sc.group.editing"), Loc.T("sc.saveAs"), [G(Key.S, KeyModifiers.Control | KeyModifiers.Shift)]),
        new("fit", Loc.T("sc.group.view"), Loc.T("sc.fit"), [G(Key.F)]),
        new("zoom1", Loc.T("sc.group.view"), Loc.T("sc.zoom1"), []),
        new("zoom2", Loc.T("sc.group.view"), Loc.T("sc.zoom2"), []),
        new("zoom3", Loc.T("sc.group.view"), Loc.T("sc.zoom3"), []),
        new("zoom4", Loc.T("sc.group.view"), Loc.T("sc.zoom4"), []),
        new("zoom5", Loc.T("sc.group.view"), Loc.T("sc.zoom5"), []),
        new("mixer", Loc.T("sc.group.view"), Loc.T("sc.mixer"), [G(Key.X)]),
        new("leftPanel", Loc.T("sc.group.view"), Loc.T("sc.leftPanel"), [G(Key.B)]),
        new("rightPanel", Loc.T("sc.group.view"), Loc.T("sc.rightPanel"), [G(Key.P)]),
        new("maximize", Loc.T("sc.group.view"), Loc.T("sc.maximize"), [G(Key.Enter)]),
        new("fullScreen", Loc.T("sc.group.view"), Loc.T("sc.fullScreen"), [G(Key.F11)]),
        new("uiLarger", Loc.T("sc.group.view"), Loc.T("sc.uiLarger"), [G(Key.OemPlus, KeyModifiers.Control), G(Key.Add, KeyModifiers.Control)]),
        new("uiSmaller", Loc.T("sc.group.view"), Loc.T("sc.uiSmaller"), [G(Key.OemMinus, KeyModifiers.Control), G(Key.Subtract, KeyModifiers.Control)]),
        new("uiReset", Loc.T("sc.group.view"), Loc.T("sc.uiReset"), [G(Key.D0, KeyModifiers.Control), G(Key.NumPad0, KeyModifiers.Control)]),
        new("help", Loc.T("sc.group.view"), Loc.T("sc.help"), [G(Key.F1)]),
        new("toolSelect", Loc.T("sc.group.tools"), Loc.T("sc.toolSelect"), [G(Key.D1), G(Key.NumPad1)]),
        new("toolDraw", Loc.T("sc.group.tools"), Loc.T("sc.toolDraw"), [G(Key.D2), G(Key.NumPad2)]),
        new("toolPaint", Loc.T("sc.group.tools"), Loc.T("sc.toolPaint"), [G(Key.D3), G(Key.NumPad3)]),
        new("toolRange", Loc.T("sc.group.tools"), Loc.T("sc.toolRange"), [G(Key.D4), G(Key.NumPad4)]),
        new("toolSplit", Loc.T("sc.group.tools"), Loc.T("sc.toolSplit"), [G(Key.D5), G(Key.NumPad5)]),
        new("toolErase", Loc.T("sc.group.tools"), Loc.T("sc.toolErase"), [G(Key.D6), G(Key.NumPad6)]),
        new("toolListen", Loc.T("sc.group.tools"), Loc.T("sc.toolListen"), [G(Key.D7), G(Key.NumPad7)]),
        new("toolZoom", Loc.T("sc.group.tools"), Loc.T("sc.toolZoom"), [G(Key.D8), G(Key.NumPad8)]),
        new("toolHand", Loc.T("sc.group.tools"), Loc.T("sc.toolHand"), [G(Key.D9), G(Key.NumPad9)]),
        .. PianoKeys(),
    ];

    /// <summary>The start of the id of what only works with the focus on the piano roll: tool ids, and the note commands of <see cref="MainWindow"/>.</summary>
    public const string PianoPrefix = "piano.";

    static IEnumerable<Action> PianoKeys()
    {
        const KeyModifiers Alt = KeyModifiers.Alt, Ctrl = KeyModifiers.Control, Shift = KeyModifiers.Shift;
        (string Id, KeyGesture[] Keys)[] table =
        [
            ("draw", [G(Key.D1, Alt)]), ("paint", [G(Key.D2, Alt)]), ("slice", [G(Key.D3, Alt)]),
            ("arpeggiate", [G(Key.A, Alt)]), ("strum", [G(Key.S, Alt)]), ("legato", [G(Key.L, Ctrl)]), ("articulate", [G(Key.L, Alt)]),
            ("chop-quick", [G(Key.U, Ctrl)]), ("chop", [G(Key.U, Alt)]), ("glue", [G(Key.G, Ctrl)]), ("flam", [G(Key.F, Alt)]),
            ("claw", [G(Key.W, Alt)]), ("limit", [G(Key.J, Alt)]), ("flip", [G(Key.Y, Alt)]), ("random", [G(Key.R, Alt)]),
            ("riff", [G(Key.E, Alt)]), ("progression", [G(Key.P, Alt)]), ("quantize", [G(Key.Q, Alt)]), ("levels", [G(Key.V, Alt)]), ("lfo", [G(Key.O, Alt)]),
            ("mute", [G(Key.M, Shift)]), ("group", [G(Key.G, Shift)]), ("ungroup", [G(Key.G, Alt)]), ("invert", [G(Key.I, Shift)]),
        ];
        foreach (var (id, keys) in table)
            yield return new(PianoPrefix + id, Loc.T("sc.group.piano"), Loc.Or($"tool.{id}", Loc.T($"sc.piano.{id}")), keys);
    }

    /// <summary>The transport's actions.</summary>
    public static IEnumerable<Action> Transport => All.Where(a => a.Group == Loc.T("sc.group.transport"));

    /// <summary>The Windows virtual key, for what a plugin's window has to recognize; 0 if it has none here.</summary>
    public static int VirtualKey(Key k) => k switch
    {
        >= Key.A and <= Key.Z => 0x41 + (k - Key.A),
        >= Key.D0 and <= Key.D9 => 0x30 + (k - Key.D0),
        >= Key.NumPad0 and <= Key.NumPad9 => 0x60 + (k - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => 0x70 + (k - Key.F1),
        Key.Back => 0x08, Key.Tab => 0x09, Key.Enter => 0x0D, Key.Space => 0x20,
        Key.PageUp => 0x21, Key.PageDown => 0x22, Key.End => 0x23, Key.Home => 0x24,
        Key.Left => 0x25, Key.Up => 0x26, Key.Right => 0x27, Key.Down => 0x28, Key.Insert => 0x2D, Key.Delete => 0x2E,
        Key.Multiply => 0x6A, Key.Add => 0x6B, Key.Subtract => 0x6D, Key.Decimal => 0x6E, Key.Divide => 0x6F,
        Key.OemSemicolon => 0xBA, Key.OemPlus => 0xBB, Key.OemComma => 0xBC, Key.OemMinus => 0xBD, Key.OemPeriod => 0xBE,
        Key.OemQuestion => 0xBF, Key.OemTilde => 0xC0, Key.OemOpenBrackets => 0xDB, Key.OemPipe => 0xDC, Key.OemCloseBrackets => 0xDD, Key.OemQuotes => 0xDE,
        _ => 0,
    };

    public sealed record WheelAction(string Id, string Name, KeyModifiers Default);

    /// <summary>The wheel's modifiers in the timeline; the plain wheel always scrolls up and down.</summary>
    public static readonly WheelAction[] Wheel =
    [
        new("wheelZoom", Loc.T("sc.wheelZoom"), KeyModifiers.Shift),
        new("wheelSideways", Loc.T("sc.wheelSideways"), KeyModifiers.Control),
        new("wheelHeight", Loc.T("sc.wheelHeight"), KeyModifiers.Alt),
    ];

    const KeyModifiers Mods = KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt;

    /// <summary>The keys the user changed; an empty array means "sin atajo".</summary>
    static readonly Dictionary<string, KeyGesture[]> custom = [];

    /// <summary>The wheel modifiers the user changed; None means "sin atajo".</summary>
    static readonly Dictionary<string, KeyModifiers> customWheel = [];

    /// <summary>The plain wheel (up and down) has no modifiers to change, but it can be reversed like the others.</summary>
    public const string WheelScroll = "wheelScroll";

    /// <summary>The wheel gestures (and <see cref="WheelScroll"/>) the user turned around, each on its own
    /// ("wheelReverse").</summary>
    static readonly HashSet<string> reversed = [];

    /// <summary>A shortcut changed: tooltips that show keys refresh.</summary>
    public static event System.Action? Changed;

    /// <summary>The active layout's id, <see cref="KeyLayouts.Own"/> for XTreme's own table.</summary>
    public static string Layout { get; private set; } = KeyLayouts.Own;

    /// <summary>The active layout's keys and wheel modifiers; empty for XTreme's own table.</summary>
    static Dictionary<string, KeyGesture[]> layoutKeys = [];
    static Dictionary<string, KeyModifiers> layoutWheel = [];

    /// <summary>What the config keys of the active layout's changes end with.</summary>
    static string LayoutSuffix => Layout == KeyLayouts.Own ? "" : "." + Layout;

    /// <summary>Applies a layout's table, keeping the changes the user made to it. Picking one loses nothing.</summary>
    public static void SetLayout(string id)
    {
        if (id == Layout) return;
        ApplyLayout(id);
        LoadChanges();
        string saved = id;
        _ = Task.Run(() =>
        {
            try { AppConfig.Set("shortcutLayout", saved); }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException) { /* keep this session's layout */ }
        });
        Changed?.Invoke();
    }

    /// <summary>An action the layout doesn't list keeps XTreme's keys, minus those the layout gave to another one.</summary>
    static void ApplyLayout(string id)
    {
        var layout = KeyLayouts.Find(id);
        Layout = layout?.Id ?? KeyLayouts.Own;
        layoutKeys = [];
        layoutWheel = [];
        if (layout == null) return;
        foreach (var a in All)
            if (layout.Keys.TryGetValue(a.Id, out var keys))
                layoutKeys[a.Id] = keys.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(Parse).OfType<KeyGesture>().ToArray();
        var taken = layoutKeys.Values.SelectMany(k => k).ToArray();
        foreach (var a in All)
            if (!layoutKeys.ContainsKey(a.Id) && a.Defaults.Any(g => taken.Any(t => Same(t, g))))
                layoutKeys[a.Id] = a.Defaults.Where(g => !taken.Any(t => Same(t, g))).ToArray();
        foreach (var a in Wheel)
            if (layout.Wheel.TryGetValue(a.Id, out var mods) && Enum.TryParse<KeyModifiers>(mods, out var m))
                layoutWheel[a.Id] = m & Mods;
    }

    /// <summary>The key or keys an action has in the active layout, before the user's changes.</summary>
    static KeyGesture[] BaseKeys(string id) =>
        layoutKeys.TryGetValue(id, out var k) ? k : All.First(a => a.Id == id).Defaults;

    static KeyModifiers BaseWheel(string id) =>
        layoutWheel.TryGetValue(id, out var m) ? m : Wheel.First(a => a.Id == id).Default;

    public static void Load()
    {
        ApplyLayout(AppConfig.Get("shortcutLayout") ?? KeyLayouts.Own);
        LoadChanges();
        reversed.Clear();
        try
        {
            foreach (var id in JsonSerializer.Deserialize<string[]>(AppConfig.Get("wheelReverse") ?? "[]") ?? [])
                if (id == WheelScroll || Wheel.Any(a => a.Id == id)) reversed.Add(id);
        }
        catch (JsonException) { }
    }

    /// <summary>The user's changes to the active layout.</summary>
    static void LoadChanges()
    {
        custom.Clear();
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, string[]>>(AppConfig.Get("shortcuts" + LayoutSuffix) ?? "{}") ?? [];
            foreach (var (id, keys) in saved)
                if (All.Any(a => a.Id == id))
                    custom[id] = keys.Select(Parse).OfType<KeyGesture>().ToArray();
        }
        catch (JsonException) { }
        customWheel.Clear();
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(AppConfig.Get("wheel" + LayoutSuffix) ?? "{}") ?? [];
            foreach (var (id, mods) in saved)
                if (Wheel.Any(a => a.Id == id) && Enum.TryParse<KeyModifiers>(mods, out var m))
                    customWheel[id] = m & Mods;
        }
        catch (JsonException) { }
    }

    public static KeyGesture[] Keys(string id) =>
        custom.TryGetValue(id, out var k) ? k : BaseKeys(id);

    public static bool IsDefault(string id) => !custom.ContainsKey(id) && !customWheel.ContainsKey(id);

    public static bool AllDefault => custom.Count == 0 && customWheel.Count == 0 && reversed.Count == 0;

    /// <summary>-1 if the user reversed this wheel gesture, else 1. Runs on every wheel step, so it doesn't allocate.</summary>
    public static int WheelSign(string id) => reversed.Contains(id) ? -1 : 1;

    public static bool WheelReversed(string id) => reversed.Contains(id);

    public static void SetWheelReversed(string id, bool on)
    {
        if (on) reversed.Add(id);
        else reversed.Remove(id);
        Save();
    }

    public static KeyModifiers WheelMods(string id) =>
        customWheel.TryGetValue(id, out var m) ? m : BaseWheel(id);

    /// <summary>The wheel action for the held modifiers (exact match), or null for the plain scroll. Runs on every
    /// wheel step, so it doesn't allocate.</summary>
    public static string? FindWheel(KeyModifiers mods)
    {
        mods &= Mods;
        if (mods == KeyModifiers.None) return null;
        foreach (var a in Wheel)
            if (WheelMods(a.Id) == mods) return a.Id;
        return null;
    }

    /// <summary>Like <see cref="Assign"/> for a wheel gesture: another wheel action with the same modifiers loses them.</summary>
    public static string? AssignWheel(string id, KeyModifiers mods)
    {
        mods &= Mods;
        string? taken = null;
        if (mods != KeyModifiers.None)
            foreach (var a in Wheel)
                if (a.Id != id && WheelMods(a.Id) == mods)
                {
                    SetWheel(a.Id, KeyModifiers.None);
                    taken = a.Name;
                }
        SetWheel(id, mods);
        Save();
        return taken;
    }

    public static void ResetWheel(string id)
    {
        var def = BaseWheel(id);
        if (FindWheel(def) is { } other && other != id) SetWheel(other, KeyModifiers.None);
        customWheel.Remove(id);
        Save();
    }

    static void SetWheel(string id, KeyModifiers mods)
    {
        if (mods == BaseWheel(id)) customWheel.Remove(id);
        else customWheel[id] = mods;
    }

    /// <summary>The action for a key press, or null. Modifiers have to match exactly (C is click, Shift+C the count-in).</summary>
    public static string? Find(KeyEventArgs e) => Find(e.Key, e.KeyModifiers);

    static string? Find(Key key, KeyModifiers mods)
    {
        mods &= Mods;
        foreach (var a in All)
            if (Keys(a.Id).Any(g => g.Key == key && g.KeyModifiers == mods)) return a.Id;
        return null;
    }

    /// <summary>
    /// Gives an action one key; another action that had it loses it (its name comes back so the dialog can say so).
    /// Null leaves the action without a shortcut.
    /// </summary>
    public static string? Assign(string id, KeyGesture? gesture)
    {
        string? taken = null;
        if (gesture != null)
            foreach (var a in All)
            {
                if (a.Id == id) continue;
                var keys = Keys(a.Id);
                var left = keys.Where(g => !Same(g, gesture)).ToArray();
                if (left.Length == keys.Length) continue;
                SetKeys(a.Id, left);
                taken = a.Name;
            }
        SetKeys(id, gesture == null ? [] : [gesture]);
        Save();
        return taken;
    }

    public static void Reset(string id)
    {
        foreach (var g in BaseKeys(id))
            if (Find(g.Key, g.KeyModifiers) is { } other && other != id)
                SetKeys(other, Keys(other).Where(k => !Same(k, g)).ToArray());
        custom.Remove(id);
        Save();
    }

    public static void ResetAll()
    {
        custom.Clear();
        customWheel.Clear();
        reversed.Clear();
        Save();
    }

    static void SetKeys(string id, KeyGesture[] keys)
    {
        var defaults = BaseKeys(id);
        if (keys.Length == defaults.Length && keys.Zip(defaults).All(p => Same(p.First, p.Second))) custom.Remove(id);
        else custom[id] = keys;
    }

    static bool Same(KeyGesture a, KeyGesture b) => a.Key == b.Key && a.KeyModifiers == b.KeyModifiers;

    static void Save()
    {
        string json = JsonSerializer.Serialize(custom.ToDictionary(p => p.Key, p => p.Value.Select(g => g.ToString()).ToArray()));
        string wheel = JsonSerializer.Serialize(customWheel.ToDictionary(p => p.Key, p => p.Value.ToString()));
        string reverse = JsonSerializer.Serialize(reversed.Order().ToArray());
        string suffix = LayoutSuffix;
        _ = Task.Run(() =>
        {
            try { AppConfig.Set("shortcuts" + suffix, json); AppConfig.Set("wheel" + suffix, wheel); AppConfig.Set("wheelReverse", reverse); }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException) { /* keep this session's keys */ }
        });
        Changed?.Invoke();
    }

    static KeyGesture? Parse(string s)
    {
        try { return KeyGesture.Parse(s); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>"Espacio", "Ctrl+Y o Ctrl+Shift+Z"; empty when the action has no key.</summary>
    public static string Label(string id) => string.Join(Loc.T("sc.or"), Keys(id).Select(Label));

    public static string Label(KeyGesture g) => Label(g.KeyModifiers, KeyName(g.Key));

    /// <summary>The first key alone, for the right side of a menu entry ("Alt+X"); empty when the action has none.</summary>
    public static string First(string id) => Keys(id).FirstOrDefault() is { } g ? Label(g) : "";

    /// <summary>"Shift+rueda"; empty when the wheel action has no modifiers.</summary>
    public static string WheelLabel(string id) => WheelMods(id) is var m && m != KeyModifiers.None ? Label(m, Loc.T("sc.key.wheel")) : "";

    /// <summary>"Zoom horizontal (Shift+rueda)", or just the text if the wheel action has no modifiers.</summary>
    public static string WheelTip(string text, string id) => WheelLabel(id) is { Length: > 0 } k ? $"{text} ({k})" : text;

    static string Label(KeyModifiers mods, string key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(key);
        return string.Join("+", parts);
    }

    static string KeyName(Key k) => k switch
    {
        Key.Space => Loc.T("sc.key.space"), Key.Home => Loc.T("sc.key.home"), Key.End => Loc.T("sc.key.end"), Key.Delete => Loc.T("sc.key.del"), Key.Insert => "Insert",
        Key.Back => Loc.T("sc.key.back"), Key.Enter => "Enter", Key.Tab => "Tab", Key.PageUp => Loc.T("sc.key.pgup"), Key.PageDown => Loc.T("sc.key.pgdn"),
        Key.Left => "←", Key.Right => "→", Key.Up => "↑", Key.Down => "↓",
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => Loc.T("sc.key.num", (int)(k - Key.NumPad0)),
        Key.Multiply => "Num *", Key.Divide => "Num /", Key.Add => "Num +", Key.Subtract => "Num -",
        Key.OemComma => ",", Key.OemPeriod => ".", Key.OemMinus => "-", Key.OemPlus => "+",
        _ => k.ToString(),
    };

    /// <summary>"Reproducir (Espacio)": the tooltip text with the action's key, or just the text if it has none.</summary>
    public static string Tip(string text, string id) => text + Paren(id);

    /// <summary>" (R)" to put inside a sentence, or nothing if the action has no key.</summary>
    public static string Paren(string id) => Label(id) is { Length: > 0 } k ? $" ({k})" : "";

    /// <summary>" · Alt+X: dividir" for a list of gestures in a tooltip, or nothing if the action has no key.</summary>
    public static string Note(string id, string what) => Label(id) is { Length: > 0 } k ? $" · {k}: {what}" : "";

    /// <summary>Keys that can't be a shortcut: modifiers alone, Esc (always cancels) and system keys.</summary>
    public static bool Assignable(Key k) => k is not (Key.None or Key.Escape or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.Apps or Key.System or Key.CapsLock or Key.NumLock or Key.Scroll
        or Key.PrintScreen or Key.ImeProcessed);
}
