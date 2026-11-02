using XtremeDaw.Audio.Dsp;

namespace XtremeDaw.Audio;

/// <summary>
/// Finds the scale and the chords of a piece of audio (a take, a loop, a song): the peaks of each frame's spectrum, with
/// the recording's own tuning taken off so a guitar 30 cents flat lands on its notes and the odd partials handed back to
/// their string (<see cref="FoldHarmonics"/>), summed per pitch class (each frame evens out, so a loud chord doesn't
/// outvote the rest of the song), and that profile correlated with the Krumhansl–Kessler major and minor profiles on the
/// 12 tonics. The same frames give the chords: their pitch classes, smoothed in time so a drum hit or a passing note
/// doesn't count, against major, minor and power-chord templates, plus what the bass plays (it tells A from F#m when the
/// upper notes can't), and a Viterbi path that only changes chord when the music does (<see cref="Chords"/>), with no
/// chord shorter than about a beat of the piece's tempo. A scale and its relative share their
/// notes, so the chords settle that one: home is the chord that opens, closes and lasts. <see cref="Result.Confidence"/>
/// looks at the distance to the best scale that isn't the relative, and the window always names the relative too.
/// When that reading isn't very sure, of the scale or of the chords, <see cref="DetectBest"/> hears the audio a second
/// way and keeps the surer one: the bass and the harmony alone (<see cref="StemSeparator"/>, without voice or drums,
/// the best reading of the chords but the slowest) or, without its models, the notes <see cref="AudioToMidi"/> hears.
/// Offline only (allocates, runs on a worker thread).
/// </summary>
public static class ScaleDetector
{
    /// <summary>Analysis frame, in seconds at least: 0.34 s at 48 kHz, short enough for a chord that lasts a beat.</summary>
    const double FrameSec = 0.3;
    /// <summary>Frames per frame length.</summary>
    const int Overlap = 4;
    /// <summary>The pitches that count: from the bass's lower strings to where a partial no longer says the note.</summary>
    const double LowHz = 55, HighHz = 2100;
    /// <summary>A peak under this share of the frame's highest one is noise or a far partial.</summary>
    const double PeakFloor = 0.03;
    /// <summary>The partials that go back to their note, how close they have to be, how loud the note has to be against
    /// the partial, and how much of the partial it takes.</summary>
    const int MaxPartial = 7;
    const double PartialToleranceSemis = 0.35, OwnerShare = 0.25, FoldShare = 0.8;
    /// <summary>The stretch at each end that weighs more, and by how much.</summary>
    const double EdgeSec = 5, EdgeBoost = 2;
    /// <summary>Shorter than this there are too few notes to trust.</summary>
    public const double MinSec = 3;
    /// <summary>Ten minutes are read: every chord of a song counts, and past that it would take too long.</summary>
    const double MaxSec = 600;
    /// <summary>Below this level (≈ −70 dBFS RMS) a frame is silence.</summary>
    const double SilenceRms = 3e-4;
    /// <summary>The correlation lead over the best scale that isn't the relative that counts as all clear.</summary>
    const double ClearLead = 0.25;
    /// <summary>Under this <see cref="Result.Tonal"/> no pitch class stands out: noise. A lone voice or a mix with
    /// effects can fit a scale poorly and still say its notes, so this looks at how uneven they are, not at the fit.</summary>
    public const double MinTonal = 0.17;
    /// <summary>Two relatives this close in correlation are decided by their chords.</summary>
    const double RelativeGap = 0.2;
    /// <summary>What opening or closing the piece counts for, as a share of its whole length.</summary>
    const double HomeShare = 0.15;
    /// <summary>What a change of chord costs the path, in frames' worth of match: higher, fewer chords.</summary>
    const double ChangePenalty = 1.2;
    /// <summary>The match of "no chord", only where nothing sounds: music always has a chord, even when it fits badly.</summary>
    const double NoChordMatch = 0.45;
    /// <summary>What a chord built on the scale's notes gains: on a tie, the one that belongs.</summary>
    const double DiatonicBonus = 0.2;
    /// <summary>What a power chord pays: with a third that sounds at all, the chord is major or minor.</summary>
    const double PowerHandicap = 0.07;
    /// <summary>A chord shorter than this is a passing note and joins what comes before, or that many beats when the
    /// tempo is known.</summary>
    const double MinChordSec = 0.3, MinChordBeats = 0.9;
    /// <summary>Under this tempo confidence the beat isn't trusted for the shortest chord.</summary>
    const double MinTempoConfidence = 0.3;
    /// <summary>What counts as the bass and what stands in for the chord, in semitones from A4: up to 185 Hz, and up to
    /// 1.1 kHz, where the voice and the solos start to outweigh the harmony.</summary>
    const double BassTopSemis = -15, ChordTopSemis = 16;
    /// <summary>What the bass adds to a chord's match, when it plays the root.</summary>
    const double BassWeight = 1;
    /// <summary>Frames to each side in the median of every pitch class: about ±0.35 s.</summary>
    const int SmoothFrames = 4;
    /// <summary>The mean cosine with the chords that reads as 0 % and as 100 % in <see cref="Result.ChordFit"/>.</summary>
    const double LooseFit = 0.6, TightFit = 0.95;
    /// <summary>The progress is told this many times at most.</summary>
    const int ProgressSteps = 100;

    /// <summary>Krumhansl &amp; Kessler (1982): how well each degree fits a major and a minor key, from the tonic up.</summary>
    static readonly double[] MajorProfile = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88],
        MinorProfile = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];

    static readonly int[] MajorSteps = [0, 2, 4, 5, 7, 9, 11], MinorSteps = [0, 2, 3, 5, 7, 8, 10];
    /// <summary>Tonics as they are written: the flat or sharp side that key signatures use.</summary>
    static readonly string[] MajorNames = ["C", "Db", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"],
        MinorNames = ["C", "C#", "D", "Eb", "E", "F", "F#", "G", "G#", "A", "Bb", "B"];
    /// <summary>A chord root outside the scale, on the side the scale leans to.</summary>
    static readonly string[] SharpNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"],
        FlatNames = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"];
    const string Letters = "CDEFGAB";
    static readonly int[] LetterPitch = [0, 2, 4, 5, 7, 9, 11];

    /// <param name="Tonic">Its pitch class (0 = C).</param>
    public readonly record struct Scale(int Tonic, bool Minor)
    {
        /// <summary>The tonic as written ("Bb", "F#").</summary>
        public string Root => (Minor ? MinorNames : MajorNames)[Tonic];

        /// <summary>The scale with the same notes.</summary>
        public Scale Relative => new((Tonic + (Minor ? 3 : 9)) % 12, !Minor);

        /// <summary>Its seven notes from the tonic, one per letter ("A B C D E F G").</summary>
        public IReadOnlyList<string> Notes
        {
            get
            {
                var steps = Minor ? MinorSteps : MajorSteps;
                int first = Letters.IndexOf(Root[0]);
                var notes = new string[steps.Length];
                for (int i = 0; i < steps.Length; i++)
                {
                    int letter = (first + i) % Letters.Length;
                    int sharps = ((Tonic + steps[i] - LetterPitch[letter] + 18) % 12) - 6;
                    notes[i] = Letters[letter] + new string(sharps > 0 ? '#' : 'b', Math.Abs(sharps));
                }
                return notes;
            }
        }
    }

    public enum ChordKind { Major, Minor, Power }

    /// <param name="StartSec">From the start of the stretch that was given.</param>
    /// <param name="Root">Its pitch class (0 = C).</param>
    /// <param name="Name">As a marker shows it ("Am", "Bb", "E5").</param>
    public readonly record struct Chord(double StartSec, double EndSec, int Root, ChordKind Kind, string Name);

    /// <param name="Scale">The one that fits best.</param>
    /// <param name="Confidence">0..1: how far it stands from the other scales (its relative aside).</param>
    /// <param name="TuningCents">How far from A = 440 the recording sits (−50..50).</param>
    /// <param name="Chords">In order, each one until the next (a gap is silence or no chord).</param>
    /// <param name="Tonal">How uneven the pitch classes are (their deviation over their mean): flat noise is near 0.</param>
    /// <param name="Method">What gave this reading.</param>
    /// <param name="Retried">The first reading wasn't sure enough, so a second one was tried.</param>
    /// <param name="BeatSec">One beat of the tempo it heard; 0 when it didn't hear a clear one.</param>
    public sealed record Result(Scale Scale, double Confidence, double TuningCents, IReadOnlyList<Chord> Chords, double Tonal,
        Method Method = Method.Spectrum, bool Retried = false, double BeatSec = 0, double ChordFit = 0)
    {
        /// <summary>The weaker of the two: what decides whether to try another method, and which one stays.</summary>
        public double Score => Math.Min(Confidence, ChordFit);
    }

    public enum Method { Spectrum, Stems, Notes }

    /// <summary>What can get in the way of the second reading: a take in progress holds the separation, and the user
    /// can skip it.</summary>
    public sealed record Options(bool AllowStems = true, Func<bool>? Hold = null, CancellationToken Cancel = default);

    /// <summary>Under this <see cref="Result.Score"/>, <see cref="DetectBest"/> tries another method: only a reading
    /// that is very sure stands alone (asked by the user).</summary>
    public const double RetryBelow = 0.85;
    /// <summary>The time between the frames that notes are laid on, as the spectrum's hop.</summary>
    const double NoteHopSec = 0.085;

    /// <summary>
    /// <see cref="Detect"/>, and when it isn't very sure, a second reading (see the class); the surer of the two wins.
    /// Without the models, or if it fails or is skipped, the first reading stands.
    /// </summary>
    public static Result? DetectBest(float[] left, float[] right, int sampleRate, int start = 0, int count = -1, Action<double>? progress = null, Options? options = null) =>
        Run(left, right, sampleRate, start, count, options ?? new(), progress);

    /// <summary>The scale and chords of notes already transcribed: each one weighs on the frames it sounds in, and each
    /// frame adds up the same, as in the spectrum. Null without notes.</summary>
    public static Result? FromNotes(IReadOnlyList<AudioToMidi.Note> notes, double length, double beatSec = 0)
    {
        if (notes.Count == 0) return null;
        const int A4 = 69;
        int total = (int)Math.Ceiling(length / NoteHopSec);
        var frames = new List<Frame>(total);
        for (int f = 0; f < total; f++)
        {
            double sec = (f + 0.5) * NoteHopSec;
            frames.Add(new Frame(sec, Math.Min(sec, length - sec) < EdgeSec, []));
        }
        foreach (var n in notes)
            for (int f = Math.Max(0, (int)(n.StartSec / NoteHopSec)); f < total && f * NoteHopSec < n.EndSec; f++)
                frames[f].Peaks.Add((n.Pitch - A4, n.Velocity / 127.0));
        foreach (var f in frames)
        {
            double sum = f.Peaks.Sum(x => x.Weight);
            for (int i = 0; i < f.Peaks.Count; i++) f.Peaks[i] = (f.Peaks[i].Semis, f.Peaks[i].Weight / sum);
        }
        return Analyse(frames, 0, length, beatSec) with { Method = Method.Notes };
    }

    /// <summary>The scale and chords of <paramref name="count"/> samples from <paramref name="start"/> (all of it by
    /// default), from the spectrum alone; null when it is too short or silent. <paramref name="progress"/> hears 0..1 as
    /// it goes, from its thread.</summary>
    public static Result? Detect(float[] left, float[] right, int sampleRate, int start = 0, int count = -1, Action<double>? progress = null) =>
        Run(left, right, sampleRate, start, count, null, progress);

    /// <summary>The bar has to reach the end either way, so what's left is shared out once the peaks tell whether the
    /// notes will be needed.</summary>
    static Result? Run(float[] left, float[] right, int sampleRate, int start, int count, Options? options, Action<double>? progress)
    {
        start = Math.Clamp(start, 0, left.Length);
        if (count < 0 || count > left.Length - start) count = left.Length - start;
        count = Math.Min(count, (int)(MaxSec * sampleRate));
        if (count < MinSec * sampleRate) return null;

        var frames = Peaks(left, right, sampleRate, start, count, p => progress?.Invoke(p * PeaksShare));
        if (frames.All(f => f.Peaks.Count == 0)) return null;
        double tuning = Tuning(frames), length = count / (double)sampleRate;
        bool stems = options is { AllowStems: true } && StemSeparator.Available(StemSeparator.Layout.Four, StemSeparator.Quality.Fast);
        bool notes = options != null && !stems && AudioToMidi.Available;
        double tempoEnd = stems || notes ? PeaksShare + TempoShare : 1;
        var tempo = TempoDetector.Detect(left, right, sampleRate, start, count, p => progress?.Invoke(PeaksShare + (tempoEnd - PeaksShare) * p));
        double beat = tempo is { Confidence: >= MinTempoConfidence } ? 60 / tempo.Bpm : 0;
        var first = Analyse(frames, tuning, length, beat);
        if (first.Score >= RetryBelow) stems = notes = false;
        try
        {
            Result? second = null;
            if (stems)
            {
                const double SeparationEnd = 0.92;
                var parts = StemSeparator.Separate(left, right, sampleRate, start, count, new(StemSeparator.Layout.Four, StemSeparator.Quality.Fast, 0),
                    p => progress?.Invoke(tempoEnd + (SeparationEnd - tempoEnd) * p), options!.Hold, options.Cancel);
                float[] l = new float[count], r = new float[count];
                foreach (var part in parts.Where(x => x.Id is not ("vocals" or "drums")))
                    for (int i = 0; i < count; i++)
                    {
                        l[i] += part.L[i];
                        r[i] += part.R[i];
                    }
                var heard = Peaks(l, r, sampleRate, 0, count, p => progress?.Invoke(SeparationEnd + (1 - SeparationEnd) * p));
                if (heard.Any(f => f.Peaks.Count > 0)) second = Analyse(heard, Tuning(heard), length, beat) with { Method = Method.Stems };
            }
            else if (notes)
                second = FromNotes(AudioToMidi.Extract(left, right, sampleRate, start, count, new(), p => progress?.Invoke(tempoEnd + (1 - tempoEnd) * p)), length, beat);
            if (stems || notes)
                first = second != null && second.Score > first.Score ? second with { TuningCents = first.TuningCents, Retried = true } : first with { Retried = true };
        }
        catch (Exception e) when (e is InvalidOperationException or OperationCanceledException) { /* the first reading stands */ }
        progress?.Invoke(1);
        return first;
    }

    /// <summary>0..1 from the lead over the scales that aren't the relative.</summary>
    static double Clarity(double lead) => Math.Clamp(lead / ClearLead, 0, 1);

    /// <summary>How much of the progress the peaks take, and the tempo after them when the notes follow.</summary>
    const double PeaksShare = 0.3, TempoShare = 0.15;

    /// <summary>What both readings share: from frames of pitches to the scale and its chords.</summary>
    static Result Analyse(List<Frame> frames, double tuning, double length, double beatSec)
    {
        var chroma = Sum(frames, tuning);
        var (best, lead, closeRelative) = Best(chroma);
        var (chords, fit) = Chords(frames, tuning, best, Math.Max(MinChordSec, MinChordBeats * beatSec));
        if (closeRelative && HomeScore(chords, best.Relative, length) > HomeScore(chords, best, length))
        {
            best = best.Relative;
            var scale = best;
            chords = [.. chords.Select(c => c with { Name = ChordName(c.Root, c.Kind, scale) })];
        }
        return new Result(best, Clarity(lead), tuning * 100, chords, Unevenness(chroma), BeatSec: beatSec, ChordFit: fit);
    }

    /// <summary>How much <paramref name="scale"/>'s tonic chord sounds, plus the piece opening and closing on it.</summary>
    static double HomeScore(List<Chord> chords, Scale scale, double length)
    {
        if (chords.Count == 0) return 0;
        bool Home(Chord c) => c.Root == scale.Tonic && (c.Kind == ChordKind.Power || (c.Kind == ChordKind.Minor) == scale.Minor);
        double score = chords.Where(Home).Sum(c => c.EndSec - c.StartSec);
        if (Home(chords[0])) score += HomeShare * length;
        if (Home(chords[^1])) score += HomeShare * length;
        return score;
    }

    /// <summary>One frame's spectral peaks: how far each is from A4, in semitones, and how much it weighs.</summary>
    /// <param name="Sec">Its middle.</param>
    /// <param name="Edge">It falls in the stretch at either end that weighs more.</param>
    sealed record Frame(double Sec, bool Edge, List<(double Semis, double Weight)> Peaks);

    static List<Frame> Peaks(float[] left, float[] right, int sampleRate, int start, int count, Action<double>? progress)
    {
        int size = 1 << (int)Math.Ceiling(Math.Log2(FrameSec * sampleRate));
        int hop = size / Overlap;
        int total = count < size ? 0 : (count - size) / hop + 1;
        int every = Math.Max(1, total / ProgressSteps);
        var fft = new Fft(size);
        double[] re = new double[size], im = new double[size], window = new double[size], mag = new double[size / 2];
        for (int i = 0; i < size; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size);
        int low = Math.Max(2, (int)(LowHz * size / sampleRate));
        int high = Math.Min(size / 2 - 2, (int)(HighHz * size / sampleRate));
        double length = count / (double)sampleRate;
        var frames = new List<Frame>(total);
        for (int f = 0; f < total; f++)
        {
            if (f % every == 0) progress?.Invoke((double)f / total);
            int at = start + f * hop;
            double sq = 0;
            for (int i = 0; i < size; i++)
            {
                double v = 0.5 * (left[at + i] + right[at + i]);
                sq += v * v;
                re[i] = v * window[i];
                im[i] = 0;
            }
            double rms = Math.Sqrt(sq / size), sec = (f * hop + size / 2.0) / sampleRate;
            var peaks = new List<(double Semis, double Weight)>();
            frames.Add(new Frame(sec, Math.Min(sec, length - sec) < EdgeSec, peaks));
            if (rms < SilenceRms) continue;
            fft.Transform(re, im, inverse: false);
            double top = 0;
            for (int k = low - 1; k <= high + 1; k++)
            {
                mag[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                top = Math.Max(top, mag[k]);
            }
            double sum = 0;
            for (int k = low; k <= high; k++)
            {
                double m = mag[k];
                if (m < PeakFloor * top || m <= mag[k - 1] || m < mag[k + 1]) continue;
                double hz = (k + Parabola(mag[k - 1], m, mag[k + 1])) * sampleRate / size;
                // compressed, so a soft high note counts next to the low string that rings over it
                double weight = Math.Sqrt(m / top);
                peaks.Add((12 * Math.Log2(hz / 440), weight));
                sum += weight;
            }
            if (sum <= 0) continue;
            FoldHarmonics(peaks);
            // every frame adds up the same, but for how loud it is: a soft passage counts, a fade-out's tail less
            double gain = Math.Sqrt(rms) / sum;
            for (int i = 0; i < peaks.Count; i++) peaks[i] = (peaks[i].Semis, peaks[i].Weight * gain);
        }
        return frames;
    }

    /// <summary>A string's third and fifth partials read as its fifth and its third, so an E riff would pull towards B:
    /// a peak that is an odd partial of a lower one (loud enough to own it) gives most of its weight to that note.</summary>
    static void FoldHarmonics(List<(double Semis, double Weight)> peaks)
    {
        for (int i = peaks.Count - 1; i > 0; i--)
            for (int j = 0; j < i; j++)
            {
                double ratio = Math.Pow(2, (peaks[i].Semis - peaks[j].Semis) / 12);
                int h = (int)Math.Round(ratio);
                // an octave stays in its pitch class: nothing to fix
                if (h < 2 || h > MaxPartial || (h & (h - 1)) == 0) continue;
                if (Math.Abs(12 * Math.Log2(ratio / h)) > PartialToleranceSemis || peaks[j].Weight < OwnerShare * peaks[i].Weight) continue;
                peaks[j] = (peaks[j].Semis, peaks[j].Weight + FoldShare * peaks[i].Weight);
                peaks[i] = (peaks[i].Semis, (1 - FoldShare) * peaks[i].Weight);
                break;
            }
    }

    /// <summary>Where the peak between bins sits, from −0.5 to 0.5 around the middle one.</summary>
    static double Parabola(double a, double b, double c)
    {
        double d = a - 2 * b + c;
        return Math.Abs(d) < 1e-12 ? 0 : Math.Clamp(0.5 * (a - c) / d, -0.5, 0.5);
    }

    /// <summary>The recording's tuning, in semitones (−0.5..0.5): the circular mean of how far the peaks sit from the
    /// equal-tempered notes, each one a turn around a circle of one semitone.</summary>
    static double Tuning(List<Frame> frames)
    {
        double x = 0, y = 0;
        foreach (var f in frames)
            foreach (var (semis, weight) in f.Peaks)
            {
                x += weight * Math.Cos(2 * Math.PI * semis);
                y += weight * Math.Sin(2 * Math.PI * semis);
            }
        return Math.Atan2(y, x) / (2 * Math.PI);
    }

    /// <summary>Adds how much of each pitch class sounds in a frame (0 = C); a peak halfway between two notes counts for neither.</summary>
    static void AddChroma(Frame f, double tuning, double[] chroma, double gain)
    {
        const int A = 9;
        foreach (var (semis, weight) in f.Peaks)
        {
            double p = semis - tuning;
            double near = Math.Round(p);
            double fit = Math.Cos(Math.PI * (p - near));
            chroma[(((int)near + A) % 12 + 12) % 12] += gain * weight * fit * fit;
        }
    }

    /// <summary>The pitch classes of the whole piece; the first and last seconds count more, since that's where a song
    /// says what its home is.</summary>
    /// <summary>Per frame, the pitch classes of the chord and of the bass, each frame as a unit vector so the loud ones
    /// don't weigh more, and each pitch class the median of the frames around, which takes out what lasts only a moment.</summary>
    static (double[][] Upper, double[][] Bass) Smoothed(List<Frame> frames, double tuning)
    {
        int n = frames.Count;
        double[][] upper = new double[n][], bass = new double[n][];
        for (int f = 0; f < n; f++)
        {
            upper[f] = new double[12];
            bass[f] = new double[12];
            var low = new Frame(0, false, []);
            var mid = new Frame(0, false, []);
            foreach (var peak in frames[f].Peaks)
            {
                if (peak.Semis < BassTopSemis) low.Peaks.Add(peak);
                if (peak.Semis < ChordTopSemis) mid.Peaks.Add(peak);
            }
            AddChroma(low, tuning, bass[f], 1);
            AddChroma(mid, tuning, upper[f], 1);
            Unit(bass[f]);
            Unit(upper[f]);
        }
        return (Median(upper), Median(bass));
    }

    static void Unit(double[] v)
    {
        double norm = Math.Sqrt(v.Sum(x => x * x));
        if (norm <= 0) return;
        for (int i = 0; i < v.Length; i++) v[i] /= norm;
    }

    static double[][] Median(double[][] rows)
    {
        int n = rows.Length;
        var window = new double[2 * SmoothFrames + 1];
        var smooth = new double[n][];
        for (int f = 0; f < n; f++)
        {
            smooth[f] = new double[12];
            int a = Math.Max(0, f - SmoothFrames), b = Math.Min(n - 1, f + SmoothFrames), count = b - a + 1;
            for (int c = 0; c < 12; c++)
            {
                for (int k = 0; k < count; k++) window[k] = rows[a + k][c];
                Array.Sort(window, 0, count);
                smooth[f][c] = window[count / 2];
            }
        }
        return smooth;
    }

    static double[] Sum(List<Frame> frames, double tuning)
    {
        var chroma = new double[12];
        foreach (var f in frames) AddChroma(f, tuning, chroma, f.Edge ? EdgeBoost : 1);
        return chroma;
    }

    /// <summary>The scale whose profile correlates best with <paramref name="chroma"/>, its lead over the best one that
    /// isn't its relative (that one has the same notes: it is told apart, not ruled out), and whether the relative is
    /// close enough to ask the chords.</summary>
    static (Scale Best, double Lead, bool CloseRelative) Best(double[] chroma)
    {
        var scores = new List<(Scale Scale, double R)>(24);
        for (int tonic = 0; tonic < 12; tonic++)
        {
            scores.Add((new(tonic, false), Correlation(chroma, MajorProfile, tonic)));
            scores.Add((new(tonic, true), Correlation(chroma, MinorProfile, tonic)));
        }
        scores.Sort((a, b) => b.R.CompareTo(a.R));
        var best = scores[0];
        double other = scores.First(s => s.Scale != best.Scale && s.Scale != best.Scale.Relative).R;
        double relative = scores.First(s => s.Scale == best.Scale.Relative).R;
        return (best.Scale, best.R - other, best.R - relative <= RelativeGap);
    }

    static double Unevenness(double[] chroma)
    {
        double mean = chroma.Average();
        return mean <= 0 ? 0 : Math.Sqrt(chroma.Average(x => (x - mean) * (x - mean))) / mean;
    }

    /// <summary>Pearson's correlation of the chroma with the profile laid from <paramref name="tonic"/>.</summary>
    static double Correlation(double[] chroma, double[] profile, int tonic)
    {
        double ma = chroma.Average(), mb = profile.Average(), ab = 0, aa = 0, bb = 0;
        for (int i = 0; i < 12; i++)
        {
            double a = chroma[(tonic + i) % 12] - ma, b = profile[i] - mb;
            ab += a * b;
            aa += a * a;
            bb += b * b;
        }
        return aa <= 0 ? 0 : ab / Math.Sqrt(aa * bb);
    }

    /// <summary>
    /// The chords over time: each frame's match with every chord (the cosine with its template), and the Viterbi path
    /// that adds up the most match paying <see cref="ChangePenalty"/> per change, so one odd frame doesn't make a chord.
    /// Then what lasts less than <paramref name="shortest"/> joins the chord before.
    /// </summary>
    static (List<Chord> Chords, double Fit) Chords(List<Frame> frames, double tuning, Scale scale, double shortest)
    {
        const int Kinds = 3, Count = 12 * Kinds, None = Count;
        var inScale = new bool[12];
        foreach (int s in scale.Minor ? MinorSteps : MajorSteps) inScale[(scale.Tonic + s) % 12] = true;
        var templates = new double[Count][];
        var bonus = new double[Count + 1];
        for (int root = 0; root < 12; root++)
            for (int k = 0; k < Kinds; k++)
            {
                var kind = (ChordKind)k;
                int third = (root + (kind == ChordKind.Minor ? 3 : 4)) % 12, fifth = (root + 7) % 12;
                var t = new double[12];
                t[root] = 1;
                t[fifth] = 0.8;
                if (kind != ChordKind.Power) t[third] = 0.8;
                double norm = Math.Sqrt(t.Sum(x => x * x));
                templates[root * Kinds + k] = [.. t.Select(x => x / norm)];
                bonus[root * Kinds + k] = (inScale[root] && inScale[fifth] && (kind == ChordKind.Power || inScale[third]) ? DiatonicBonus : 0)
                    - (kind == ChordKind.Power ? PowerHandicap : 0);
            }

        int n = frames.Count;
        var back = new byte[n][];
        double[] score = new double[Count + 1], next = new double[Count + 1];
        var (upper, bass) = Smoothed(frames, tuning);
        for (int f = 0; f < n; f++)
        {
            var chroma = upper[f];
            double norm = Math.Sqrt(chroma.Sum(x => x * x)), bassSum = bass[f].Sum();
            int top = 0;
            for (int s = 1; s <= Count; s++) if (score[s] > score[top]) top = s;
            back[f] = new byte[Count + 1];
            for (int s = 0; s <= Count; s++)
            {
                double match = norm > 0 ? double.NegativeInfinity : NoChordMatch;
                if (s != None && norm > 0)
                {
                    match = bonus[s];
                    for (int i = 0; i < 12; i++) match += templates[s][i] * chroma[i] / norm;
                    if (bassSum > 0) match += BassWeight * bass[f][s / Kinds] / bassSum;
                }
                else if (s != None) match = 0;
                bool stay = score[s] >= score[top] - ChangePenalty;
                next[s] = (stay ? score[s] : score[top] - ChangePenalty) + match;
                back[f][s] = (byte)(stay ? s : top);
            }
            (score, next) = (next, score);
        }

        var path = new int[n];
        int at = 0;
        for (int s = 1; s <= Count; s++) if (score[s] > score[at]) at = s;
        for (int f = n - 1; f >= 0; f--)
        {
            path[f] = at;
            at = back[f][at];
        }

        // how much of what sounds the chords explain: the cosine with the chosen template, frame by frame
        double fitSum = 0;
        int fitted = 0;
        for (int f = 0; f < n; f++)
        {
            double norm = Math.Sqrt(upper[f].Sum(x => x * x));
            if (path[f] == None || norm <= 0) continue;
            double cos = 0;
            for (int i = 0; i < 12; i++) cos += templates[path[f]][i] * upper[f][i] / norm;
            fitSum += cos;
            fitted++;
        }
        double fit = fitted == 0 ? 0 : Math.Clamp((fitSum / fitted - LooseFit) / (TightFit - LooseFit), 0, 1);

        double half = n > 1 ? (frames[1].Sec - frames[0].Sec) / 2 : 0;
        var chords = new List<Chord>();
        for (int f = 0; f < n;)
        {
            int e = f;
            while (e + 1 < n && path[e + 1] == path[f]) e++;
            double from = Math.Max(0, frames[f].Sec - half), to = frames[e].Sec + half;
            if (path[f] != None)
            {
                int root = path[f] / Kinds;
                var kind = (ChordKind)(path[f] % Kinds);
                bool same = chords.Count > 0 && chords[^1].Root == root && chords[^1].Kind == kind;
                if (chords.Count > 0 && (same || to - from < shortest)) chords[^1] = chords[^1] with { EndSec = to };
                else if (to - from >= shortest) chords.Add(new(from, to, root, kind, ChordName(root, kind, scale)));
            }
            f = e + 1;
        }
        return (chords, fit);
    }

    /// <summary>The chord's name, its root spelled as <paramref name="scale"/> spells it.</summary>
    static string ChordName(int root, ChordKind kind, Scale scale)
    {
        var notes = scale.Notes;
        int degree = Array.IndexOf(scale.Minor ? MinorSteps : MajorSteps, (root - scale.Tonic + 12) % 12);
        string note = degree >= 0 ? notes[degree] : (notes.Any(x => x.EndsWith('b')) ? FlatNames : SharpNames)[root];
        return note + kind switch { ChordKind.Minor => "m", ChordKind.Power => "5", _ => "" };
    }
}
