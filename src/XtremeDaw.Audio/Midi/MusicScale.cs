namespace XtremeDaw.Audio.Midi;

/// <summary>A scale's name and its notes in semitones above the root; <see cref="Group"/> is its place in the list.</summary>
public sealed record ScaleKind(string Id, string Group, int[] Steps);

/// <summary>
/// A root and a scale: what the piano roll and the virtual keyboard follow (asked by the user, FL Studio's list without
/// its pad layouts). Notes are counted in degrees, an absolute count along the scale that goes on past the octave, so
/// moving something "two notes up" works the same on every scale, including those without seven notes. A pitch that
/// isn't in the scale counts as its nearest note (the lower one on a tie). Immutable: it crosses threads by reference.
/// </summary>
public sealed class MusicScale
{
    public const string Major = "major", Minor = "minor", Other = "other";

    public static readonly ScaleKind[] Kinds =
    [
        new("major", Major, [0, 2, 4, 5, 7, 9, 11]),
        new("major-bebop", Major, [0, 2, 4, 5, 7, 8, 9, 11]),
        new("major-pentatonic", Major, [0, 2, 4, 7, 9]),
        new("minor", Minor, [0, 2, 3, 5, 7, 8, 10]),
        new("minor-harmonic", Minor, [0, 2, 3, 5, 7, 8, 11]),
        new("minor-melodic", Minor, [0, 2, 3, 5, 7, 9, 11]),
        new("minor-pentatonic", Minor, [0, 3, 5, 7, 10]),
        new("minor-hungarian", Minor, [0, 2, 3, 6, 7, 8, 11]),
        new("minor-neapolitan", Minor, [0, 1, 3, 5, 7, 8, 11]),
        new("dorian", Other, [0, 2, 3, 5, 7, 9, 10]),
        new("phrygian", Other, [0, 1, 3, 5, 7, 8, 10]),
        new("lydian", Other, [0, 2, 4, 6, 7, 9, 11]),
        new("mixolydian", Other, [0, 2, 4, 5, 7, 9, 10]),
        new("locrian", Other, [0, 1, 3, 5, 6, 8, 10]),
        new("blues", Other, [0, 3, 5, 6, 7, 10]),
        new("arabic", Other, [0, 1, 4, 5, 7, 8, 11]),
        new("diminished", Other, [0, 2, 3, 5, 6, 8, 9, 11]),
        new("dominant-bebop", Other, [0, 2, 4, 5, 7, 9, 10, 11]),
        new("enigmatic", Other, [0, 1, 4, 6, 8, 10, 11]),
        new("insen", Other, [0, 1, 5, 7, 10]),
        new("neapolitan", Other, [0, 1, 3, 5, 7, 9, 11]),
        new("whole-tone", Other, [0, 2, 4, 6, 8, 10]),
    ];

    public int Root { get; }
    public ScaleKind Kind { get; }
    /// <summary>The nearest degree of each semitone above the root; the last ones may be the octave's root (<see cref="Count"/>).</summary>
    readonly int[] nearest = new int[12];
    readonly bool[] inside = new bool[12];

    public MusicScale(int root, ScaleKind kind)
    {
        Root = Mod(root, 12);
        Kind = kind;
        foreach (int s in kind.Steps) inside[s] = true;
        for (int s = 0; s < 12; s++)
        {
            int best = 0;
            for (int d = 1; d <= kind.Steps.Length; d++)
                if (Math.Abs(StepAt(d) - s) < Math.Abs(StepAt(best) - s)) best = d;
            nearest[s] = best;
        }
    }

    /// <summary>The scale with this id, or null: none, or one this version doesn't know.</summary>
    public static MusicScale? Find(int root, string? id) => Array.Find(Kinds, k => k.Id == id) is { } kind ? new MusicScale(root, kind) : null;

    /// <summary>Notes per octave.</summary>
    public int Count => Kind.Steps.Length;

    int StepAt(int degree) => degree == Count ? 12 : Kind.Steps[degree];

    static int Mod(int v, int m) => (v % m + m) % m;

    public bool Contains(int pitch) => inside[Mod(pitch - Root, 12)];

    public bool IsRoot(int pitch) => Mod(pitch - Root, 12) == 0;

    /// <summary>The pitch's absolute degree (0 = the root of MIDI's octave -1): its own or, out of the scale, its nearest's.</summary>
    public int Degree(int pitch)
    {
        int above = pitch - Root, octave = (int)Math.Floor(above / 12.0);
        return octave * Count + nearest[above - octave * 12];
    }

    /// <summary>The pitch of an absolute degree.</summary>
    public int PitchOf(int degree)
    {
        int octave = (int)Math.Floor((double)degree / Count);
        return Root + octave * 12 + Kind.Steps[degree - octave * Count];
    }

    /// <summary>The nearest note of the scale, the same pitch if it already is one.</summary>
    public int Nearest(int pitch) => PitchOf(Degree(pitch));

    /// <summary>The note this many degrees away along the scale.</summary>
    public int Move(int pitch, int degrees) => PitchOf(Degree(pitch) + degrees);
}

/// <summary>A chord by its intervals above the root; <see cref="Suffix"/> is how it is written after the note.</summary>
public sealed record ChordKind(string Id, string Suffix, int[] Steps);

/// <summary>
/// Chords as lists of pitches, lowest first. The two "scale" kinds stack every other note of the scale, so each degree
/// gets the chord that belongs to it (asked by the user: one click, nothing to know); without a scale they fall back to
/// major and major seventh. An inversion takes that many of the lowest notes up an octave, and <see cref="Read"/> undoes
/// it, so it also recognizes a chord that was already written.
/// </summary>
public static class Chords
{
    public const string InScale = "scale", InScale7 = "scale7";

    /// <summary>What can be drawn: the scale's own first, then fixed shapes.</summary>
    public static readonly ChordKind[] Kinds =
    [
        new(InScale, "", []),
        new(InScale7, "", []),
        new("maj", "", [0, 4, 7]),
        new("min", "m", [0, 3, 7]),
        new("7", "7", [0, 4, 7, 10]),
        new("maj7", "maj7", [0, 4, 7, 11]),
        new("m7", "m7", [0, 3, 7, 10]),
        new("sus2", "sus2", [0, 2, 7]),
        new("sus4", "sus4", [0, 5, 7]),
        new("dim", "°", [0, 3, 6]),
        new("aug", "+", [0, 4, 8]),
        new("5", "5", [0, 7]),
    ];

    /// <summary>The shapes <see cref="Read"/> knows: what can be drawn and what stacking a scale's notes gives.</summary>
    static readonly ChordKind[] Known =
    [
        .. Kinds.Where(k => k.Steps.Length > 0),
        new("m7b5", "ø", [0, 3, 6, 10]),
        new("dim7", "°7", [0, 3, 6, 9]),
        new("mMaj7", "mMaj7", [0, 3, 7, 11]),
        new("augMaj7", "+maj7", [0, 4, 8, 11]),
    ];

    /// <summary>The chord on <paramref name="root"/> in root position. A "scale" kind's root goes to the scale first.</summary>
    public static int[] Build(string kindId, int root, MusicScale? scale)
    {
        if (kindId is InScale or InScale7)
        {
            int size = kindId == InScale ? 3 : 4;
            if (scale != null) return Stack(scale, root, size);
            kindId = size == 3 ? "maj" : "maj7";
        }
        var kind = Array.Find(Kinds, k => k.Id == kindId) ?? Kinds[2];
        return [.. kind.Steps.Select(s => root + s)];
    }

    /// <summary>Every other note of the scale from <paramref name="root"/>: <paramref name="size"/> notes.</summary>
    public static int[] Stack(MusicScale scale, int root, int size)
    {
        int degree = scale.Degree(root);
        return [.. Enumerable.Range(0, size).Select(i => scale.PitchOf(degree + i * 2))];
    }

    /// <summary>The voicing with <paramref name="inversion"/> of its lowest notes an octave up, lowest first.</summary>
    public static int[] Invert(int[] rootPosition, int inversion) =>
        [.. rootPosition.Select((p, i) => i < inversion ? p + 12 : p).Order()];

    /// <summary>The pitches a chord can take up to: a voicing is only offered if all of it is in MIDI's range.</summary>
    public static bool InRange(int[] pitches) => pitches.All(p => p is >= 0 and <= 127);

    /// <summary>
    /// A close voicing (lowest first) as a known chord: its root position, which inversion it is, and the shape. Null
    /// when it isn't one, or spans an octave or more.
    /// </summary>
    public static (int[] Root, int Inversion, ChordKind Kind)? Read(IReadOnlyList<int> voicing)
    {
        int n = voicing.Count;
        if (n < 2 || voicing[n - 1] - voicing[0] >= 12) return null;
        for (int inversion = 0; inversion < n; inversion++)
        {
            int[] root = [.. voicing.Select((p, i) => i >= n - inversion ? p - 12 : p).Order()];
            if (Array.Find(Known, k => k.Steps.Length == n && k.Steps.Select((s, i) => root[0] + s == root[i]).All(x => x)) is { } kind)
                return (root, inversion, kind);
        }
        return null;
    }
}
