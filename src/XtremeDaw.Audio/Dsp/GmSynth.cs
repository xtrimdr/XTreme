namespace XtremeDaw.Audio.Dsp;

/// <summary>What the export needs to play a track with the GM instrument: the shared bank and the preset it picked.</summary>
public sealed record GmRig(SoundFont Font, SfPreset Preset, GmTone Tone)
{
    /// <summary>How long it can ring after its last note: the longest release of its preset, at most a few seconds.</summary>
    public double TailSec => GmSynth.TailOf(Preset, Tone);
}

/// <summary>
/// What the user changed over a GM preset (asked by the user, #200: it had no controls), the same on every region of it.
/// Envelope times are a knob position, -1..1: under 0 they shrink the bank's time down to nothing, over 0 they add up to
/// <see cref="MaxAddSec"/>, because many presets start with no attack at all and a factor would leave them so. Sustain
/// moves the bank's level towards silence (under 0) or full (over 0). Immutable: a change is a new one, taken by each
/// note as it starts.
/// </summary>
public sealed record GmTone
{
    /// <summary>The most a time knob adds to the bank's attack, decay or release.</summary>
    public const double MaxAddSec = 10;
    /// <summary>The octaves Brillo and the velocity move the filter.</summary>
    public const double CutoffOctaves = 4;
    /// <summary>The most Resonancia adds to the bank's.</summary>
    public const double MaxResonanceDb = 20;
    public const double MaxGlideSec = 2;
    public const int MaxTranspose = 24;
    /// <summary>The instrument's volume range (asked by the user, #212): the bottom is silence.</summary>
    public const double MinVolumeDb = -60, MaxVolumeDb = 12;

    public static readonly GmTone Neutral = new();

    public double Attack { get; init; }
    public double Decay { get; init; }
    public double Sustain { get; init; }
    public double Release { get; init; }
    /// <summary>-1..1, <see cref="CutoffOctaves"/> down or up from the bank's cutoff.</summary>
    public double Bright { get; init; }
    /// <summary>0..1, of <see cref="MaxResonanceDb"/>.</summary>
    public double Resonance { get; init; }
    /// <summary>0..1: how much a soft note closes the filter, up to <see cref="CutoffOctaves"/> at the softest.</summary>
    public double VelBright { get; init; }
    /// <summary>0..1: 1 is the GM curve (velocity squared), 0 plays every note at full level.</summary>
    public double Velocity { get; init; } = 1;
    /// <summary>Semitones, which pick the regions too, as a keyboard's transpose does.</summary>
    public int Transpose { get; init; }
    /// <summary>Cents, only the pitch.</summary>
    public double Fine { get; init; }
    /// <summary>One note at a time: a new one cuts the one before.</summary>
    public bool Mono { get; init; }
    /// <summary>In Mono, how long a note tied to the one held slides from its pitch.</summary>
    public double GlideSec { get; init; }
    /// <summary>The instrument's level in dB over the bank's, on what already sounds too (glided per block, no clicks).</summary>
    public double VolumeDb { get; init; }

    /// <summary><see cref="VolumeDb"/> as a factor; 0 at <see cref="MinVolumeDb"/>.</summary>
    public float Gain => VolumeDb <= MinVolumeDb ? 0 : (float)Math.Pow(10, Math.Min(VolumeDb, MaxVolumeDb) / 20);

    /// <summary>A bank's time with a time knob's position on it.</summary>
    public static double Time(double bank, double knob) => knob < 0 ? bank * (1 + knob) : bank + MaxAddSec * knob * knob;

    /// <summary>A bank's sustain (centibels under the peak) with the Sostén knob on it.</summary>
    public static double SustainCb(double bank, double knob, double silent) =>
        knob < 0 ? bank + -knob * (silent - bank) : bank * (1 - knob);
}

/// <summary>
/// The native General MIDI instrument of a MIDI track (asked by the user, #200: an imported song plays with the
/// instruments it was written for): a SoundFont player over <see cref="SoundFont"/>, one preset per track (a GM program
/// or a drum kit). Each region a note hits is a voice: a 4-point Hermite read of its sample with its loop, the SF2
/// volume envelope (linear attack, decay and release in dB), the modulation envelope and the two LFOs on pitch and on a
/// resonant low pass, the velocity's default curve, what the bank's modulators add for the note's velocity and key
/// (<see cref="SfMod"/>), pan and the exclusive classes that cut a hi-hat. Over that, the
/// track's <see cref="GmTone"/> (envelope, filter, velocity, tuning, Mono with glide), which each note takes as it starts.
/// <para>
/// What it does with the notes (jumps, loops, the keyboards) is <see cref="NoteInstrument"/>'s, shared with the sampler.
/// Envelopes, LFOs and the filter move every <see cref="ControlFrames"/> frames of the voice's own time, so a voice
/// depends only on the frames since its note: any block size gives the same floats, live and in the export. Audio thread
/// only, except <see cref="Set"/>: every voice exists from the start and nothing allocates.
/// </para>
/// </summary>
public sealed class GmSynth : NoteInstrument
{
    /// <summary>Voices sounding at once before the oldest is taken (a GM note often plays two regions, one per side).</summary>
    public const int MaxVoices = 96;
    /// <summary>Room for the voices fading out behind those.</summary>
    const int Pool = MaxVoices + 32;
    /// <summary>Pitch, filter, envelopes and LFOs move every this many frames (0.33 ms at 48 kHz); gains glide in between.</summary>
    const int ControlFrames = 16;
    /// <summary>Under this much attenuation (centibels: -96 dB) a voice is over.</summary>
    const double SilentCb = 960;
    /// <summary>The whole instrument's level: a chord of a GM bank's full-scale samples needs room before the chain.</summary>
    public const float Headroom = 0.7f;
    /// <summary>The longest tail the export waits for after the last note.</summary>
    const double MaxTailSec = 6;
    /// <summary>A note picked up after this long jumps to where its envelope would be, without walking every step.</summary>
    const double ChaseWalkSec = 20;

    sealed class Voice
    {
        public bool Active, Fading, Released;
        public byte Source;
        public int Key;
        public long Order;
        public long OffIn;
        public SfRegion R = null!;
        public float[] Data = null!;
        public double Pos, Step, BaseSemis, RateRatio;
        public NoteBend? Bend;
        public float Pan, VelGain, FadeGain = 1;
        public long Time;
        public int Ctrl;
        // volume envelope: frames of each stage, its stage and how far in; level in centibels down from the peak
        public int Stage;
        public double StageT, AttackAmp, Cb, HoldSec, DecaySec, AttackSec, SustainCb, ReleaseSec;
        public double ReleaseFromCb;
        // what the tone adds: filter (cents, centibels) and the glide from the note before (semitones over frames)
        public double CutoffShift, ResonanceAdd, GlideSemis;
        // the region's filter and modulation envelope with what its modulators added for this note (SfMod)
        public double Cutoff, EnvToPitch, EnvToCutoff, ModAttackSec, ModHoldSec, ModDecaySec, ModReleaseSec;
        public long GlideFrames;
        // modulation envelope, 0..1
        public int ModStage;
        public double ModT, ModLevel, ModReleaseFrom;
        public double VibPhase, LfoPhase;
        public float GainL, GainR, StepL, StepR;
        public bool Filtering;
        public float B0, B1, B2, A1, A2, X1, X2, Y1, Y2;
    }

    const int SDelay = 0, SAttack = 1, SHold = 2, SDecay = 3, SSustain = 4, SRelease = 5, SDone = 6;

    readonly Voice[] voices = new Voice[Pool];
    volatile SoundFont? font;
    volatile SfPreset? preset;
    volatile GmTone tone = GmTone.Neutral;
    SoundFont? blockFont;
    SfPreset? blockPreset;
    GmTone blockTone = GmTone.Neutral;
    /// <summary>The volume reached by the last block, which the next one glides from (audio thread).</summary>
    float level = 1;
    long order;
    /// <summary>The last note each source began (song, keyboards, window): where a Mono glide starts from.</summary>
    readonly double[] lastNote = [double.NaN, double.NaN, double.NaN];

    public GmSynth(int sampleRate) : base(sampleRate)
    {
        for (int i = 0; i < Pool; i++) voices[i] = new Voice();
    }

    /// <summary>The bank and the preset new notes play; the ones sounding finish with theirs. UI thread.</summary>
    public void Set(SoundFont? bank, SfPreset? patch, GmTone? sound = null)
    {
        font = bank;
        preset = patch;
        tone = sound ?? GmTone.Neutral;
    }

    public SoundFont? Font => font;
    public SfPreset? Preset => preset;
    public GmTone Tone => tone;

    /// <summary>The longest release of a preset with its tone, so the export doesn't cut a pad's tail.</summary>
    public static double TailOf(SfPreset p, GmTone? sound = null)
    {
        double most = 0.5, knob = (sound ?? GmTone.Neutral).Release;
        // a modulator may lengthen it (a low key's release): the most it can add
        foreach (var r in p.Regions)
            most = Math.Max(most, GmTone.Time(Timed(r.Release, r.Mods.Where(m => m.Dest == SoundFont.DestRelease).Sum(m => Math.Abs(m.Amount))), knob));
        return Math.Min(MaxTailSec + GmTone.MaxAddSec, most);
    }

    protected override void Prepare()
    {
        blockFont = font;
        blockPreset = preset;
        blockTone = tone;
        // nothing sounds: no glide (the export starts at its level, whatever its blocks)
        if (!Sounding) level = blockTone.Gain;
    }

    protected override bool CanPlay => blockFont != null && blockPreset != null;

    protected override bool Lasts => false;

    protected override bool Sounding
    {
        get
        {
            for (int i = 0; i < Pool; i++)
                if (voices[i].Active) return true;
            return false;
        }
    }

    protected override void DropSong()
    {
        for (int i = 0; i < Pool; i++)
        {
            var v = voices[i];
            if (v.Active && v.Source == FromSong) v.Active = v.Fading = false;
        }
    }

    protected override void End(byte source, int key)
    {
        for (int i = 0; i < Pool; i++)
        {
            var v = voices[i];
            if (v.Active && v.Source == source && v.Key == key && v.OffIn < 0) Release(v);
        }
    }

    static void Release(Voice v)
    {
        v.OffIn = -1;
        if (v.Released) return;
        v.Released = true;
        if (v.Stage == SAttack) v.Cb = v.AttackAmp <= 1e-5 ? SilentCb : -200 * Math.Log10(v.AttackAmp);
        else if (v.Stage < SAttack) v.Cb = SilentCb;
        v.ReleaseFromCb = v.Cb;
        v.Stage = SRelease;
        v.StageT = 0;
        v.ModReleaseFrom = v.ModLevel;
        v.ModStage = SRelease;
        v.ModT = 0;
    }

    protected override void FadeAll(bool everyone)
    {
        for (int i = 0; i < Pool; i++)
        {
            var v = voices[i];
            if (v.Active && (everyone || v.Source == FromSong)) v.Fading = true;
        }
    }

    protected override void Begin(byte source, int key, double note, int velocity, long off, long t, NoteBend? bend = null, float pan = 0)
    {
        if (blockFont is not { } f || blockPreset is not { } p || velocity <= 0) return;
        var tn = blockTone;
        note += tn.Transpose;
        int k = Math.Clamp((int)Math.Round(note), 0, 127);
        double glide = double.NaN;
        if (tn.Mono && source < lastNote.Length)
        {
            bool held = false;
            for (int i = 0; i < Pool; i++)
            {
                var v = voices[i];
                if (!v.Active || v.Fading || v.Source != source) continue;
                if (!v.Released) held = true;
                v.Fading = true;
            }
            // only a note tied to the one held glides, and only one that starts on time: a chased note has none before
            if (held && t == 0 && tn.GlideSec > 0 && !double.IsNaN(lastNote[source])) glide = lastNote[source] - note;
            lastNote[source] = note;
        }
        foreach (var r in p.Regions)
        {
            if (!r.Covers(k, velocity)) continue;
            if (r.Exclusive != 0) CutClass(r.Exclusive, p);
            Start(f, r, tn, source, key, note, k, velocity, off, t, bend, pan, glide);
        }
    }

    /// <summary>A new note of an exclusive class cuts the voices of the same class of this preset.</summary>
    void CutClass(int cls, SfPreset p)
    {
        for (int i = 0; i < Pool; i++)
        {
            var v = voices[i];
            if (v.Active && !v.Fading && v.R.Exclusive == cls && v.Time > 0 && Array.IndexOf(p.Regions, v.R) >= 0) v.Fading = true;
        }
    }

    Voice Take()
    {
        Voice? free = null, oldest = null, oldestFading = null;
        int sounding = 0;
        for (int i = 0; i < Pool; i++)
        {
            var v = voices[i];
            if (!v.Active) { free ??= v; continue; }
            if (v.Fading) { if (oldestFading == null || v.Order < oldestFading.Order) oldestFading = v; continue; }
            sounding++;
            if (oldest == null || v.Order < oldest.Order) oldest = v;
        }
        if (sounding >= MaxVoices && oldest != null) oldest.Fading = true;
        return free ?? oldestFading ?? oldest!;
    }

    void Start(SoundFont f, SfRegion r, GmTone tn, byte source, int key, double note, int k, int velocity, long off, long t, NoteBend? bend, float pan, double glide)
    {
        var v = Take();
        v.Active = true;
        v.Fading = v.Released = false;
        v.FadeGain = 1;
        v.Source = source;
        v.Key = key;
        v.Order = ++order;
        v.OffIn = off;
        v.R = r;
        v.Data = f.Data;
        v.Pos = r.Start;
        double pitchKey = r.FixedKey >= 0 ? r.FixedKey : note;
        v.BaseSemis = ((pitchKey - r.Root) * r.ScaleTuning + r.TuneCents + tn.Fine) / 100;
        v.RateRatio = r.SampleRate / (double)rate;
        v.Bend = bend;
        // what the bank's modulators add for this velocity and key, in each generator's units
        double mPitch = 0, mCutoff = 0, mRes = 0, mEnvCut = 0, mPan = 0, mMA = 0, mMH = 0, mMD = 0, mMR = 0, mA = 0, mH = 0, mD = 0, mS = 0, mR = 0, mAtt = 0, mTune = 0;
        var mods = r.Mods;
        for (int i = 0; i < mods.Length; i++)
        {
            double d = mods[i].At(k, velocity);
            switch (mods[i].Dest)
            {
                case SoundFont.DestEnvToPitch: mPitch += d; break;
                case SoundFont.DestCutoff: mCutoff += d; break;
                case SoundFont.DestResonance: mRes += d; break;
                case SoundFont.DestEnvToCutoff: mEnvCut += d; break;
                case SoundFont.DestPan: mPan += d; break;
                case SoundFont.DestModAttack: mMA += d; break;
                case SoundFont.DestModHold: mMH += d; break;
                case SoundFont.DestModDecay: mMD += d; break;
                case SoundFont.DestModRelease: mMR += d; break;
                case SoundFont.DestAttack: mA += d; break;
                case SoundFont.DestHold: mH += d; break;
                case SoundFont.DestDecay: mD += d; break;
                case SoundFont.DestSustain: mS += d; break;
                case SoundFont.DestRelease: mR += d; break;
                case SoundFont.DestAttenuation: mAtt += d; break;
                case SoundFont.DestCoarse: mTune += 100 * d; break;
                case SoundFont.DestFine: mTune += d; break;
            }
        }
        v.BaseSemis += mTune / 100;
        v.Pan = Math.Clamp(r.Pan + (float)(mPan / 500) + pan, -1, 1);
        float vel = velocity / 127f;
        float modGain = mAtt == 0 ? 1 : (float)Math.Pow(10, -Math.Clamp(mAtt * SoundFont.AttenuationPart, -960, 960) / 200);
        v.VelGain = (float)(1 - tn.Velocity * (1 - vel * vel)) * r.Gain * modGain * Headroom;
        v.CutoffShift = 1200 * GmTone.CutoffOctaves * (tn.Bright - tn.VelBright * (1 - vel));
        v.ResonanceAdd = 10 * GmTone.MaxResonanceDb * tn.Resonance + mRes;
        v.Cutoff = mCutoff == 0 ? r.Cutoff : Math.Clamp(r.Cutoff + mCutoff, 1500, SoundFont.OpenCutoff);
        v.EnvToPitch = r.ModEnvToPitch + mPitch;
        v.EnvToCutoff = r.ModEnvToCutoff + mEnvCut;
        v.ModAttackSec = Timed(r.ModAttack, mMA);
        v.ModHoldSec = Timed(r.ModHold, mMH);
        v.ModDecaySec = Timed(r.ModDecay, mMD);
        v.ModReleaseSec = Timed(r.ModRelease, mMR);
        v.GlideSemis = double.IsNaN(glide) ? 0 : glide;
        v.GlideFrames = double.IsNaN(glide) ? 0 : Math.Max(1, (long)(tn.GlideSec * rate));
        v.Time = 0;
        v.Ctrl = 0;
        v.Stage = SDelay;
        v.StageT = 0;
        v.AttackAmp = 0;
        v.Cb = SilentCb;
        v.HoldSec = Timed(r.Hold, mH) * Math.Pow(2, r.HoldPerKey * (60 - k) / 1200);
        v.DecaySec = GmTone.Time(Timed(r.Decay, mD) * Math.Pow(2, r.DecayPerKey * (60 - k) / 1200), tn.Decay);
        v.AttackSec = GmTone.Time(Timed(r.Attack, mA), tn.Attack);
        v.ReleaseSec = GmTone.Time(Timed(r.Release, mR), tn.Release);
        v.SustainCb = GmTone.SustainCb(Math.Clamp(r.SustainCb + mS, 0, 1440), tn.Sustain, SilentCb);
        v.ModStage = SDelay;
        v.ModT = 0;
        v.ModLevel = 0;
        v.VibPhase = v.LfoPhase = 0;
        v.X1 = v.X2 = v.Y1 = v.Y2 = 0;
        v.GainL = v.GainR = 0;
        if (t > 0 && !Forward(v, t))
        {
            v.Active = false;
            return;
        }
        Control(v);
        // the first step starts at its level, not from silence
        v.GainL += v.StepL * ControlFrames;
        v.GainR += v.StepR * ControlFrames;
        v.StepL = v.StepR = 0;
    }

    /// <summary>A time in seconds with what a modulator adds in timecents (a stage of 0 s stays 0: the SF2's -12000).</summary>
    static double Timed(double sec, double timecents) => timecents == 0 || sec <= 0 ? sec : sec * Math.Pow(2, timecents / 1200);

    /// <summary>
    /// A note picked up <paramref name="t"/> frames after it began: its envelopes walk there in control steps (or jump,
    /// past <see cref="ChaseWalkSec"/>) and the sample is read from where it would be. False when it is already over.
    /// </summary>
    bool Forward(Voice v, long t)
    {
        var r = v.R;
        double step = Math.Pow(2, v.BaseSemis / 12) * v.RateRatio;
        double pos = r.Start + step * t;
        if (r.Loop != SfLoop.None && pos >= r.LoopEnd)
        {
            double len = r.LoopEnd - r.LoopStart;
            pos = r.LoopStart + (pos - r.LoopStart) % len;
        }
        else if (pos >= r.End) return false;
        v.Pos = pos;
        long walk = Math.Min(t, (long)(ChaseWalkSec * rate));
        int steps = (int)(walk / ControlFrames);
        for (int i = 0; i < steps; i++) Envelopes(v, ControlFrames);
        if (t > walk && v.Stage < SSustain)
        {
            v.Stage = SSustain;
            v.Cb = v.SustainCb;
        }
        v.Time = t;
        if (v.OffIn >= 0) v.OffIn = Math.Max(0, v.OffIn);
        return v.Cb < SilentCb || v.Stage <= SAttack;
    }

    /// <summary>Moves both envelopes <paramref name="n"/> frames on.</summary>
    void Envelopes(Voice v, int n)
    {
        var r = v.R;
        double dt = n / (double)rate;
        v.StageT += dt;
        switch (v.Stage)
        {
            case SDelay:
                if (v.StageT >= r.Delay) { v.Stage = SAttack; v.StageT = 0; }
                break;
            case SAttack:
                v.AttackAmp = v.AttackSec <= 0 ? 1 : Math.Min(1, v.StageT / v.AttackSec);
                v.Cb = v.AttackAmp <= 1e-5 ? SilentCb : -200 * Math.Log10(v.AttackAmp);
                if (v.AttackAmp >= 1) { v.Stage = SHold; v.StageT = 0; v.Cb = 0; }
                break;
            case SHold:
                v.Cb = 0;
                if (v.StageT >= v.HoldSec) { v.Stage = SDecay; v.StageT = 0; }
                break;
            case SDecay:
                // the decay time is for the whole 100 dB; it stops at the sustain
                v.Cb = v.DecaySec <= 0 ? v.SustainCb : Math.Min(v.SustainCb, 1000 * v.StageT / v.DecaySec);
                if (v.Cb >= v.SustainCb) v.Stage = SSustain;
                break;
            case SSustain:
                v.Cb = v.SustainCb;
                break;
            case SRelease:
                v.Cb = v.ReleaseSec <= 0 ? SilentCb : v.ReleaseFromCb + 1000 * v.StageT / v.ReleaseSec;
                if (v.Cb >= SilentCb) v.Stage = SDone;
                break;
        }
        if (v.EnvToPitch == 0 && v.EnvToCutoff == 0) return;
        v.ModT += dt;
        switch (v.ModStage)
        {
            case SDelay:
                if (v.ModT >= r.ModDelay) { v.ModStage = SAttack; v.ModT = 0; }
                break;
            case SAttack:
                v.ModLevel = v.ModAttackSec <= 0 ? 1 : Math.Min(1, v.ModT / v.ModAttackSec);
                if (v.ModLevel >= 1) { v.ModStage = SHold; v.ModT = 0; }
                break;
            case SHold:
                if (v.ModT >= v.ModHoldSec) { v.ModStage = SDecay; v.ModT = 0; }
                break;
            case SDecay:
                v.ModLevel = v.ModDecaySec <= 0 ? r.ModSustain : Math.Max(r.ModSustain, 1 - v.ModT / v.ModDecaySec);
                if (v.ModLevel <= r.ModSustain) v.ModStage = SSustain;
                break;
            case SSustain:
                v.ModLevel = r.ModSustain;
                break;
            case SRelease:
                v.ModLevel = v.ModReleaseSec <= 0 ? 0 : Math.Max(0, v.ModReleaseFrom * (1 - v.ModT / v.ModReleaseSec));
                break;
        }
    }

    static double Triangle(double phase) => 4 * Math.Abs(phase - Math.Floor(phase + 0.5)) - 1;

    /// <summary>One control step: envelopes, LFOs, pitch, filter and the gains the next frames glide to.</summary>
    void Control(Voice v)
    {
        var r = v.R;
        Envelopes(v, ControlFrames);
        double sec = v.Time / (double)rate, dt = ControlFrames / (double)rate;
        double vib = 0, lfo = 0;
        if (r.VibToPitch != 0 && sec >= r.VibDelay)
        {
            vib = Triangle(v.VibPhase);
            v.VibPhase += r.VibHz * dt;
            if (v.VibPhase >= 1) v.VibPhase -= 1;
        }
        if ((r.ModLfoToPitch != 0 || r.ModLfoToCutoff != 0 || r.ModLfoToVolume != 0) && sec >= r.ModLfoDelay)
        {
            lfo = Triangle(v.LfoPhase);
            v.LfoPhase += r.ModLfoHz * dt;
            if (v.LfoPhase >= 1) v.LfoPhase -= 1;
        }
        double cents = vib * r.VibToPitch + lfo * r.ModLfoToPitch + v.ModLevel * v.EnvToPitch;
        double semis = v.BaseSemis + cents / 100 + (v.Bend?.At(v.Time) ?? 0);
        if (v.Time < v.GlideFrames) semis += v.GlideSemis * (1 - v.Time / (double)v.GlideFrames);
        v.Step = Math.Pow(2, semis / 12) * v.RateRatio;

        double cutoff = v.Cutoff + v.ModLevel * v.EnvToCutoff + lfo * r.ModLfoToCutoff + v.CutoffShift;
        v.Filtering = cutoff < SoundFont.OpenCutoff - 1;
        if (v.Filtering) Lowpass(v, Math.Min(8.176 * Math.Pow(2, cutoff / 1200), 0.45 * rate), r.Resonance + v.ResonanceAdd);

        double cb = v.Cb + lfo * r.ModLfoToVolume;
        float level = v.Stage == SDone ? 0 : (float)Math.Pow(10, -Math.Max(0, cb) / 200) * v.VelGain;
        float tl = level * Math.Min(1, 1 - v.Pan), tr = level * Math.Min(1, 1 + v.Pan);
        v.StepL = (tl - v.GainL) / ControlFrames;
        v.StepR = (tr - v.GainR) / ControlFrames;
        v.Ctrl = ControlFrames;
    }

    /// <summary>A resonant 2-pole low pass (RBJ), its resonance in centibels over flat as the SF2 has it.</summary>
    void Lowpass(Voice v, double hz, double resonanceCb)
    {
        double q = Math.Pow(10, (resonanceCb / 10 - 3.01) / 20);
        double w = 2 * Math.PI * hz / rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * Math.Max(0.5, q));
        double a0 = 1 + alpha;
        v.B1 = (float)((1 - cos) / a0);
        v.B0 = v.B2 = v.B1 / 2;
        v.A1 = (float)(-2 * cos / a0);
        v.A2 = (float)((1 - alpha) / a0);
    }

    protected override void RenderVoices(int at, int n, Span<float> l, Span<float> r)
    {
        float to = blockTone.Gain, from = level, step = n > 0 ? (to - from) / n : 0;
        level = to;
        for (int i = 0; i < Pool; i++)
        {
            var v = voices[i];
            if (v.Active) Render(v, l.Slice(at, n), r.Slice(at, n), from, step);
        }
    }

    void Render(Voice v, Span<float> l, Span<float> r, float vol, float volStep)
    {
        var reg = v.R;
        var d = v.Data;
        int last = d.Length - 3;
        for (int i = 0; i < l.Length; i++)
        {
            if (v.Ctrl == 0)
            {
                if (v.Stage == SDone)
                {
                    v.Active = false;
                    return;
                }
                Control(v);
            }
            v.Ctrl--;
            if (v.OffIn >= 0 && v.OffIn-- == 0) Release(v);

            // the loop turns while it holds (or always); past the end, the voice is over
            bool looping = reg.Loop == SfLoop.Always || (reg.Loop == SfLoop.UntilRelease && !v.Released);
            if (looping && v.Pos >= reg.LoopEnd) v.Pos -= reg.LoopEnd - reg.LoopStart;
            else if (v.Pos >= reg.End)
            {
                v.Active = false;
                return;
            }
            int p = (int)v.Pos;
            float f = (float)(v.Pos - p);
            int span = reg.LoopEnd - reg.LoopStart;
            float xm = d[Math.Max(0, p - 1)], x0 = d[p];
            float x1 = d[looping && p + 1 >= reg.LoopEnd ? p + 1 - span : Math.Min(last, p + 1)];
            float x2 = d[looping && p + 2 >= reg.LoopEnd ? p + 2 - span : Math.Min(last, p + 2)];
            float c1 = 0.5f * (x1 - xm), c2 = xm - 2.5f * x0 + 2 * x1 - 0.5f * x2, c3 = 0.5f * (x2 - xm) + 1.5f * (x0 - x1);
            float s = ((c3 * f + c2) * f + c1) * f + x0;
            if (v.Filtering)
            {
                float y = v.B0 * s + v.B1 * v.X1 + v.B2 * v.X2 - v.A1 * v.Y1 - v.A2 * v.Y2;
                v.X2 = v.X1;
                v.X1 = s;
                v.Y2 = v.Y1;
                v.Y1 = y;
                s = y;
            }
            v.GainL += v.StepL;
            v.GainR += v.StepR;
            float gl = v.GainL, gr = v.GainR;
            if (v.Fading)
            {
                v.FadeGain -= 1f / FadeFrames;
                if (v.FadeGain <= 0)
                {
                    v.Active = false;
                    return;
                }
                gl *= v.FadeGain;
                gr *= v.FadeGain;
            }
            float g = vol + volStep * (i + 1);
            l[i] += s * gl * g;
            r[i] += s * gr * g;
            v.Pos += v.Step;
            v.Time++;
        }
    }
}
