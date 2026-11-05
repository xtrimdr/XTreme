using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// Note names as the user reads them, in the one system of the UI language (Do Re Mi… in Spanish, C D E… in
/// English, from the <c>note.0</c>..<c>note.11</c> keys of <see cref="Loc"/>), always with sharps. With octave they
/// number like <see cref="XtremeDaw.Projects.MidiNote.Name"/> (MIDI 60 = "Do4" / "C4"), which stays the engine's
/// name for xtlab and saved files. Every place in the window that shows a note goes through here so the systems
/// can't drift. The names are built once, on first use (after <c>Loc.Load</c>), into arrays: drawing code only reads.
/// The user can pick the other system from the piano roll's keys (asked by the user): <see cref="Notation"/>, kept in
/// config.json ("noteNames"); none picked = the UI language's.
/// </summary>
public static class NoteNames
{
    /// <summary>MIDI pitches with a cached name (the whole MIDI range).</summary>
    const int Pitches = 128;

    /// <summary>The two systems the user can pick.</summary>
    public const string Letters = "letters", Solfege = "solfege";
    static readonly string[] LetterNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    static readonly string[] SolfegeNames = ["Do", "Do#", "Re", "Re#", "Mi", "Fa", "Fa#", "Sol", "Sol#", "La", "La#", "Si"];
    const string ConfigKey = "noteNames";

    static string? picked;
    static string[]? classes, pitches, octaves;

    static string[] Classes => classes ??= picked switch
    {
        Letters => LetterNames,
        Solfege => SolfegeNames,
        _ => [.. Enumerable.Range(0, 12).Select(i => Loc.T($"note.{i}"))],
    };

    /// <summary>The system in use, <see cref="Letters"/> or <see cref="Solfege"/> (the language's when none was picked).</summary>
    public static string Notation => picked ?? (Class(0) == LetterNames[0] ? Letters : Solfege);

    /// <summary>The notation changed: whoever keeps a name drawn rebuilds it.</summary>
    public static event Action? Changed;

    public static void Load()
    {
        try { if (AppConfig.Get(ConfigKey) is Letters or Solfege) picked = AppConfig.Get(ConfigKey); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the language's */ }
    }

    public static void Set(string notation)
    {
        if (notation is not (Letters or Solfege) || notation == Notation) return;
        picked = notation;
        classes = pitches = octaves = null;
        spelled.Clear();
        chords.Clear();
        Changed?.Invoke();
        _ = Task.Run(() =>
        {
            try { AppConfig.Set(ConfigKey, notation); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* keep this session's choice */ }
        });
    }

    /// <summary>The name of a pitch class ("Sol#", "G#"); any integer wraps to 0..11, 0 = Do / C.</summary>
    public static string Class(int pitchClass) => Classes[((pitchClass % 12) + 12) % 12];

    /// <summary>The name with octave of a MIDI pitch ("Sol3", "G3"; 60 = "Do4").</summary>
    public static string Of(int pitch)
    {
        pitches ??= [.. Enumerable.Range(0, Pitches).Select(Build)];
        return pitch is >= 0 and < Pitches ? pitches[pitch] : Build(pitch);
    }

    /// <summary>The name of the Do / C of an octave, by octave number from -1 ("Do-1") to 9 ("Do9").</summary>
    public static string Octave(int octave)
    {
        octaves ??= [.. Enumerable.Range(-1, 11).Select(o => Classes[0] + o)];
        return octave is >= -1 and <= 9 ? octaves[octave + 1] : Classes[0] + octave;
    }

    /// <summary>The letters' pitch classes, for <see cref="Spelled"/>.</summary>
    static readonly int[] LetterClass = [9, 11, 0, 2, 4, 5, 7];
    static readonly Dictionary<string, string> spelled = [];

    /// <summary>A name the engine spells in letters ("Bb", "F#m", "C5": the scale and chord detector, which keeps
    /// flats where the key has them) with its note in the UI language ("Sib", "Fa#m", "Do5"); what follows the note
    /// stays as is. Anything not starting with A..G comes back untouched. Cached, so it can be asked while drawing.</summary>
    public static string Spelled(string name)
    {
        if (name.Length == 0 || name[0] is < 'A' or > 'G') return name;
        if (spelled.TryGetValue(name, out var s)) return s;
        return spelled[name] = Classes[LetterClass[name[0] - 'A']] + name[1..];
    }

    static string Build(int pitch) => Class(pitch) + (pitch / 12 - 1);

    /// <summary>What a chord name looks like: a root, its accidental, a quality made of chord marks only and an
    /// optional bass after a slash ("Bbm7", "F#m7b5", "Csus4", "G/B"). Words that start with A..G don't match.</summary>
    static readonly System.Text.RegularExpressions.Regex ChordShape = new(
        @"^[A-G][#b]?(?:m|M|maj|min|dim|aug|sus|add|no|[0-9]|[#b+°ø()-])*(?:/[A-G][#b]?)?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    static readonly Dictionary<string, string> chords = [];

    /// <summary>
    /// A chord marker's name in the UI language when it is a chord in letters ("Bbm7" → "Sibm7", "G/B" → "Sol/Si");
    /// anything else the user typed ("Coro", "Puente") comes back as it is. Cached: the chord strip draws it.
    /// </summary>
    public static string Chord(string name)
    {
        if (chords.TryGetValue(name, out var s)) return s;
        if (!ChordShape.IsMatch(name)) return chords[name] = name;
        int slash = name.IndexOf('/');
        return chords[name] = slash < 0 ? Spelled(name) : Spelled(name[..slash]) + "/" + Spelled(name[(slash + 1)..]);
    }
}
