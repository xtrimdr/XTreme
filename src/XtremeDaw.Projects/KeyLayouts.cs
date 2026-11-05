namespace XtremeDaw.Projects;

/// <summary>A DAW's key assignments: gestures as text ("Ctrl+Shift+Z", two keys with "|", none with "") by action id,
/// plus the modifiers of the wheel gestures.</summary>
public sealed record KeyLayout(string Id, string Name, IReadOnlyDictionary<string, string> Keys, IReadOnlyDictionary<string, string> Wheel);

/// <summary>
/// The key layouts of other DAWs, as text so xtlab can check them without the window. Only what differs from
/// XTreme's table is listed: an action the other DAW has no default for keeps XTreme's key, unless the layout gave that
/// key to something else (the window resolves that). Logic's keys are the Mac's with Command as Ctrl and Option as Alt;
/// its Control combinations, which would clash, use Ctrl+Alt. Keys that only exist on a number pad keep a second key,
/// so a laptop can still record and loop.
/// </summary>
public static class KeyLayouts
{
    /// <summary>The program's own table, with no overrides.</summary>
    public const string Own = "xtreme";

    public static readonly KeyLayout[] Daws =
    [
        new("cubase", "Cubase", new Dictionary<string, string>
        {
            ["toStart"] = "Home|Decimal", ["record"] = "Multiply|Shift+R",
            ["loop"] = "Divide|L", ["loopIn"] = "Ctrl+NumPad1", ["loopOut"] = "Ctrl+NumPad2",
            ["marker"] = "Insert", ["prevMarker"] = "Shift+B", ["nextMarker"] = "Shift+N",
            ["virtualKeys"] = "Alt+K", ["split"] = "Alt+X", ["duplicate"] = "Ctrl+D", ["deselect"] = "Ctrl+Shift+A",
            ["redo"] = "Ctrl+Shift+Z", ["fit"] = "Shift+F", ["mixer"] = "F3",
            ["leftPanel"] = "Ctrl+Alt+Left", ["rightPanel"] = "Ctrl+Alt+Right",
            ["toolSelect"] = "D1", ["toolRange"] = "D2", ["toolSplit"] = "D3", ["toolErase"] = "D5", ["toolZoom"] = "D6", ["toolHand"] = "D7", ["toolDraw"] = "D8", ["toolListen"] = "D9",
        }, new Dictionary<string, string> { ["wheelZoom"] = "Control", ["wheelSideways"] = "Shift", ["wheelHeight"] = "Alt" }),

        new("logic", "Logic Pro", new Dictionary<string, string>
        {
            ["toStart"] = "Enter|Home", ["record"] = "R|Multiply", ["click"] = "K", ["countIn"] = "Shift+K",
            ["loop"] = "C", ["loopIn"] = "Ctrl+Alt+I", ["loopOut"] = "Ctrl+Alt+O",
            ["marker"] = "Alt+OemQuotes", ["prevMarker"] = "Alt+OemComma", ["nextMarker"] = "Alt+OemPeriod",
            ["arm"] = "Ctrl+Alt+R", ["newTrack"] = "Ctrl+Alt+N", ["delete"] = "Delete|Back", ["split"] = "Ctrl+T", ["duplicate"] = "Ctrl+R",
            ["deselect"] = "Ctrl+Shift+A", ["redo"] = "Ctrl+Shift+Z", ["fit"] = "Z",
            ["leftPanel"] = "I", ["rightPanel"] = "Y", ["fullScreen"] = "Ctrl+Alt+F|F11",
        }, new Dictionary<string, string> { ["wheelZoom"] = "Alt", ["wheelSideways"] = "Shift", ["wheelHeight"] = "Control" }),

        new("fl", "FL Studio", new Dictionary<string, string>
        {
            ["record"] = "R", ["click"] = "Ctrl+M", ["countIn"] = "Ctrl+P",
            ["marker"] = "Alt+T", ["prevMarker"] = "Alt+Multiply", ["nextMarker"] = "Alt+Divide",
            ["mute"] = "Alt+M", ["arm"] = "Shift+R", ["virtualKeys"] = "Ctrl+T",
            ["split"] = "Insert", ["duplicate"] = "Ctrl+B", ["quantize"] = "Alt+Q",
            ["fit"] = "Shift+D4", ["zoom1"] = "Shift+D1", ["zoom2"] = "Shift+D2", ["zoom3"] = "Shift+D3",
            ["mixer"] = "F9", ["rightPanel"] = "Alt+F8",
            ["toolSelect"] = "E", ["toolDraw"] = "P", ["toolSplit"] = "C", ["toolErase"] = "D", ["toolListen"] = "Y", ["toolZoom"] = "Z",
        }, new Dictionary<string, string>()),

        new("reaper", "Reaper", new Dictionary<string, string>
        {
            ["toStart"] = "W", ["record"] = "Ctrl+R", ["click"] = "Ctrl+Shift+M",
            ["loop"] = "R", ["loopIn"] = "Shift+OemOpenBrackets|I", ["loopOut"] = "Shift+OemCloseBrackets|O",
            ["marker"] = "M", ["prevMarker"] = "OemOpenBrackets", ["nextMarker"] = "OemCloseBrackets",
            ["mute"] = "Alt+M", ["solo"] = "Alt+Shift+S", ["arm"] = "Ctrl+Shift+R", ["newTrack"] = "Ctrl+T", ["virtualKeys"] = "Alt+B",
            ["split"] = "S", ["duplicate"] = "Ctrl+D", ["deselect"] = "Ctrl+Shift+A", ["redo"] = "Ctrl+Shift+Z",
            ["fit"] = "Ctrl+PageDown", ["mixer"] = "Ctrl+M",
        }, new Dictionary<string, string> { ["wheelZoom"] = "Shift", ["wheelSideways"] = "Alt", ["wheelHeight"] = "Control" }),

        new("studiopro", "Studio Pro", new Dictionary<string, string>
        {
            ["toStart"] = "OemComma|Home", ["record"] = "Multiply|Shift+R",
            ["loop"] = "Divide|L", ["loopIn"] = "Shift+NumPad1", ["loopOut"] = "Shift+NumPad2",
            ["marker"] = "Y", ["prevMarker"] = "Shift+B", ["nextMarker"] = "Shift+N",
            ["newTrack"] = "T", ["deleteClose"] = "Ctrl+Alt+D", ["split"] = "Alt+X", ["duplicate"] = "D",
            ["redo"] = "Ctrl+Shift+Z|Ctrl+Y", ["fit"] = "Ctrl+Shift+F", ["mixer"] = "F3", ["leftPanel"] = "F4", ["rightPanel"] = "F5",
            ["toolListen"] = "D7",
        }, new Dictionary<string, string>()),
    ];

    public static KeyLayout? Find(string? id) => Daws.FirstOrDefault(l => l.Id == id);

    /// <summary>What a layout's text would break at runtime, one line per problem: a key on two actions, or two wheel
    /// gestures with the same modifiers.</summary>
    public static List<string> Problems(KeyLayout l)
    {
        var problems = new List<string>();
        foreach (var dup in l.Keys
                     .SelectMany(p => p.Value.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(k => (Key: k, Action: p.Key)))
                     .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"{dup.Key}: {string.Join(", ", dup.Select(x => x.Action))}");
        foreach (var dup in l.Wheel.GroupBy(p => p.Value).Where(g => g.Count() > 1))
            problems.Add($"{dup.Key}: {string.Join(", ", dup.Select(p => p.Key))}");
        return problems;
    }
}
