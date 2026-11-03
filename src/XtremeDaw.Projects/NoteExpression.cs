using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.Projects;

/// <summary>
/// Turns the notes a track hears into what the engine plays, with what each one expresses: Pan, ModX and ModY as CCs
/// where it starts, Release in its note-off, and Slide and Porta as glides.
/// <para>
/// For a plugin a slide never triggers: the note sounding where it starts (the one that began last, also when it ends
/// right there) is held until the slide ends and the pitch bend carries it to the slide's pitch, in steps of
/// <see cref="BendStepSec"/>. Slides in a row chain on the same held note. With nothing sounding, it is a normal note. A
/// porta note is a real attack whose bend starts at the note before and falls to the centre. Pitch bend belongs to the
/// channel, so every note sounding on it bends with the held one: a chord bends together, and past
/// <see cref="NoteControl.BendRange"/> a bend stops there.
/// </para>
/// <para>
/// A <see cref="MidiNote.Tie"/> joins the note of its pitch that ends where it starts before anything else is worked out,
/// whichever clips they are in, so a divided note is the one note it was: same length for a glide, one attack, one release.
/// </para>
/// <para>
/// The test tone has neither limit: there each note is its own, so a slide is a chain of events tied at the exact frame
/// (<see cref="NoteSynth.Glide"/>), kept apart in <see cref="NoteSequence.Tone"/>. The sampler hasn't either: it plays
/// the same held note, which carries the way its pitch goes (<see cref="NoteEvent.Bend"/>).
/// </para>
/// </summary>
sealed class NoteExpression(double beatSec, int sampleRate)
{
    /// <summary>How long one value of a ramp lasts.</summary>
    const double BendStepSec = 0.005;
    /// <summary>
    /// How long the bend stays after its held note ends, unless another note starts first: back at the centre right
    /// away, the release of that note would jump in pitch.
    /// </summary>
    const double BendTailSec = 0.25;

    /// <summary>A note as heard: its place in frames, the plugin's pitch and the test tone's (a Transponer's cents included).</summary>
    public readonly record struct Heard(long From, long Length, int Pitch, double TonePitch, MidiNote Note);

    readonly long step = Math.Max(1, (long)Math.Round(BendStepSec * sampleRate));
    readonly List<NoteEvent> notes = [];
    /// <summary>The test tone's events, and which is the last one of each of <see cref="notes"/>.</summary>
    readonly List<NoteEvent> tone = [];
    readonly List<int> lastTone = [];
    /// <summary>The pitch path of each of <see cref="notes"/> that has one, by its index.</summary>
    readonly Dictionary<int, List<(long Frame, double Semis)>> paths = [];
    readonly List<NoteControl> controls = [];
    /// <summary>The bend's events: only ever cut at the end and added to, so later notes can overrule what is still to come.</summary>
    readonly List<NoteControl> bends = [];
    bool slid;
    /// <summary>A slide left the bend off centre; it holds until this frame, and the next note after that needs it back.</summary>
    bool bentBy;
    long bentUntil;

    public NoteSequence? Build(List<Heard> heard)
    {
        // slides after what starts with them: what they hold has to be there. Without either, the order stays as it came
        if (heard.Exists(h => h.Note.Slide || h.Note.Porta || h.Note.Tie))
            heard.Sort((a, b) => a.From != b.From ? a.From.CompareTo(b.From)
                : a.Note.Slide != b.Note.Slide ? a.Note.Slide.CompareTo(b.Note.Slide) : a.Pitch.CompareTo(b.Pitch));
        JoinTies(heard);

        // what was playing before this frame, for a porta; notes of one chord aren't each other's "before"
        (int Pitch, double Tone)? before = null, latest = null;
        long latestAt = -1;
        foreach (var h in heard)
        {
            var n = h.Note;
            if (h.From > latestAt)
            {
                before = latest;
                latestAt = h.From;
            }
            latest = (h.Pitch, h.TonePitch);
            if (n.Pan != 0) controls.Add(new(h.From, NoteControl.Pan, (n.Pan + 100) / 200f));
            if (n.ModX != 0) controls.Add(new(h.From, NoteControl.ModX, n.ModX / 127f));
            if (n.ModY != 0) controls.Add(new(h.From, NoteControl.ModY, n.ModY / 127f));
            if (n.Slide && Sounding(h.From) is { } held) Slide(held, h);
            else Attack(h, n.Porta ? before : null);
        }
        if (notes.Count == 0) return null;
        foreach (var (i, path) in paths) notes[i] = notes[i] with { Bend = Path(path) };
        return new NoteSequence([.. notes], sampleRate, slid ? [.. tone] : null, [.. controls, .. bends]);
    }

    /// <summary>The note that began last and still sounds at <paramref name="frame"/>; ending right there (or a rounding before) counts.</summary>
    int? Sounding(long frame)
    {
        for (int i = notes.Count - 1; i >= 0; i--)
            if (notes[i].Start + notes[i].Length >= frame - 1) return i;
        return null;
    }

    /// <summary>Each tie lengthens the note of its pitch that ends right there (or a rounding off) and goes; with none, it stays as a note. Sorted by start.</summary>
    static void JoinTies(List<Heard> heard)
    {
        int kept = 0;
        for (int i = 0; i < heard.Count; i++)
        {
            var h = heard[i];
            int head = -1;
            if (h.Note.Tie)
                for (int k = kept - 1; k >= 0 && head < 0; k--)
                    if (heard[k].Pitch == h.Pitch && Math.Abs(heard[k].From + heard[k].Length - h.From) <= 1) head = k;
            if (head >= 0) heard[head] = heard[head] with { Length = h.From + h.Length - heard[head].From };
            else heard[kept++] = h;
        }
        heard.RemoveRange(kept, heard.Count - kept);
    }

    void Attack(in Heard h, (int Pitch, double Tone)? from)
    {
        var n = h.Note;
        var e = new NoteEvent(h.From, h.Length, NoteSynth.Hz(h.TonePitch), NoteSynth.Gain(n.Velocity), h.Pitch, n.Velocity, n.Release) { Pan = n.Pan / 100f };
        if (bentBy && h.From >= bentUntil)
        {
            Bend(h.From, NoteControl.Centre);
            bentBy = false;
        }
        if (from is { } was && was.Pitch != h.Pitch)
        {
            long glide = Math.Min((long)Math.Round(MidiNote.PortaBeats * beatSec * sampleRate), h.Length);
            e = e with { BendSemis = was.Tone - h.TonePitch, BendFrames = glide };
            Bend(h.From, Value(was.Pitch - h.Pitch));
            Ramp(h.From, glide, NoteControl.Centre);
            bentBy = false;
            paths[notes.Count] = [(0, e.BendSemis), (Math.Max(1, glide), 0)];
        }
        notes.Add(e);
        tone.Add(e);
        lastTone.Add(tone.Count - 1);
    }

    void Slide(int held, in Heard h)
    {
        slid = true;
        var note = notes[held];
        long end = Math.Max(note.Start + note.Length, h.From + h.Length);
        notes[held] = note with { Length = end - note.Start, Release = h.Note.Release != 0 ? h.Note.Release : note.Release };

        SlidePath(held, note, h);
        Cut(h.From);
        Ramp(h.From, h.Length, Value(h.Pitch - note.Pitch));
        bentBy = true;
        bentUntil = end;
        if (Now != NoteControl.Centre) Add(end + (long)Math.Round(BendTailSec * sampleRate), NoteControl.Centre);

        int last = lastTone[held];
        var seg = tone[last];
        long t = h.From - seg.Start;
        tone[last] = seg with { Length = t, Tied = true };
        tone.Add(NoteSynth.Glide(seg, t, end - h.From, h.TonePitch, h.Length, sampleRate));
        lastTone[held] = tone.Count - 1;
    }

    static NoteBend Path(List<(long Frame, double Semis)> path) => new([.. path.Select(p => p.Frame)], [.. path.Select(p => p.Semis)]);

    /// <summary>The held note's pitch goes from where it is to the slide's, over the slide's length; what it had planned from there on goes.</summary>
    void SlidePath(int held, in NoteEvent note, in Heard h)
    {
        if (!paths.TryGetValue(held, out var path)) paths[held] = path = [(0, 0.0)];
        long t = h.From - note.Start;
        double was = Path(path).At(t);
        path.RemoveAll(p => p.Frame >= t);
        path.Add((t, was));
        path.Add((t + Math.Max(1, h.Length), h.TonePitch - (69 + 12 * Math.Log2(note.Hz / 440))));
    }

    /// <summary>0..1 for a distance in semitones, stopped at the range.</summary>
    static float Value(int semis) => NoteControl.Centre * (1 + Math.Clamp(semis / (float)NoteControl.BendRange, -1, 1));

    float Now => bends.Count > 0 ? bends[^1].Value : NoteControl.Centre;

    /// <summary>What was still to come from <paramref name="frame"/> on is overruled.</summary>
    void Cut(long frame)
    {
        while (bends.Count > 0 && bends[^1].Frame >= frame) bends.RemoveAt(bends.Count - 1);
    }

    void Add(long frame, float value) => bends.Add(new(frame, NoteControl.Bend, value));

    /// <summary>The bend jumps to <paramref name="value"/> here, and nothing planned after that stands.</summary>
    void Bend(long frame, float value)
    {
        Cut(frame);
        if (Now != value) Add(frame, value);
    }

    /// <summary>From where the bend is now to <paramref name="to"/>, arriving on the last of <paramref name="length"/> frames.</summary>
    void Ramp(long from, long length, float to)
    {
        float at = Now;
        if (at == to) return;
        long end = from + Math.Max(1, length) - 1;
        for (long f = from + step; f < end; f += step) Add(f, at + (to - at) * (f - from) / (end - from));
        if (bends.Count > 0 && bends[^1].Frame == end) bends.RemoveAt(bends.Count - 1); // a ramp of one frame lands on its jump
        Add(end, to);
    }
}
