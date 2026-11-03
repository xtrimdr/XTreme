using System.Text.Json;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// The native sampler, rows of comprobar-dsp and the command <c>sampler</c>: a note at its root is the sample bit for bit,
/// other keys are in tune and clean, envelopes, filters and loops do what they say, the block size changes nothing,
/// the audio thread allocates nothing, and the export plays what the live path plays.
/// </summary>
static class SamplerChecks
{
    const int Sr = 48000;

    /// <summary>Renders a file on a key to a WAV, to listen to it: <c>sampler archivo.wav [--nota 60] [--raiz 60] [--segundos 2] [--salida x.wav]</c>.</summary>
    public static int Run(Args a)
    {
        if (a.Positional.Count == 0) throw new ArgumentException("sampler archivo.wav [--nota 60] [--raiz n] [--segundos 2] [--filtro lp24] [--corte 2000] [--salida x.wav]");
        var audio = XtremeDaw.Audio.MediaDecoder.Load(a.Positional[0], Sr);
        var s = new SamplerSettings { Filter = a.Get("filtro", SamplerSettings.FilterOff), Cutoff = a.Double("corte", 2000) };
        s.Adopt(Path.GetFileNameWithoutExtension(a.Positional[0]), audio.Left, audio.Right, Sr);
        if (a.Get("raiz") is { } root) s.Root = int.Parse(root);
        Console.WriteLine($"Raíz: {s.Root} ({s.Fine:+0;-0;0} cents) · {s.Mode}");
        int note = (int)a.Double("nota", s.Root);
        var sound = s.Sound(audio.Left, audio.Right);
        var (l, r) = Render(s.Patch(120, sound), sound, [On(0, (long)(a.Double("segundos", 2) * Sr), note)], (int)((a.Double("segundos", 2) + s.Release + 1) * Sr));
        Console.WriteLine($"Pico: {l.Concat(r).Max(Math.Abs):0.000}");
        if (a.Get("salida") is { } path) Wav.Write(path, l, r, Sr);
        return 0;
    }

    static NoteEvent On(long start, long length, int pitch, int velocity = 127) => new(start, length, NoteSynth.Hz(pitch), 1, pitch, velocity);

    static (float[] L, float[] R) Render(SamplerPatch patch, SamplerSound? sound, NoteEvent[] notes, int frames, int block = 256, Action<Sampler, long>? during = null, SamplerCurves? curves = null) =>
        Sequence(patch, sound, new NoteSequence(notes, Sr), frames, block, during, curves);

    static (float[] L, float[] R) Sequence(SamplerPatch patch, SamplerSound? sound, NoteSequence notes, int frames, int block = 256, Action<Sampler, long>? during = null, SamplerCurves? curves = null)
    {
        var s = new Sampler(Sr) { Sound = sound, Notes = notes, Curves = curves };
        s.Set(patch);
        var l = new float[frames];
        var r = new float[frames];
        for (int at = 0; at < frames; at += block)
        {
            during?.Invoke(s, at);
            int n = Math.Min(block, frames - at);
            s.Play(at, n, l.AsSpan(at, n), r.AsSpan(at, n));
        }
        return (l, r);
    }

    static SamplerSound Sound(float[] x, SamplerLoop loop = SamplerLoop.Off, double from = 0, double to = 1, double fade = 0, bool reverse = false) =>
        new(x, x, reverse, loop, from, to, fade);

    static float[] Sine(double hz, double sec, double level = 0.5)
    {
        var x = new float[(int)(sec * Sr)];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(level * Math.Sin(2 * Math.PI * hz * i / Sr));
        return x;
    }

    static float[] Noise(double sec, double level = 0.5)
    {
        var rnd = new Random(7);
        var x = new float[(int)(sec * Sr)];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(level * (2 * rnd.NextDouble() - 1));
        return x;
    }

    static double Rms(float[] x, int from, int to)
    {
        double sum = 0;
        for (int i = from; i < to; i++) sum += x[i] * x[i];
        return Math.Sqrt(sum / Math.Max(1, to - from));
    }

    /// <summary>The frequency from the zero crossings going up, between two frames.</summary>
    static double Hz(float[] x, int from, int to)
    {
        int first = -1, last = -1, count = 0;
        for (int i = from + 1; i < to; i++)
            if (x[i - 1] < 0 && x[i] >= 0)
            {
                if (first < 0) first = i;
                last = i;
                count++;
            }
        return count < 2 ? 0 : (count - 1) * (double)Sr / (last - first);
    }

    static double MaxDiff(float[] a, float[] b, int from = 0, int to = int.MaxValue)
    {
        double d = 0;
        for (int i = from; i < Math.Min(to, Math.Min(a.Length, b.Length)); i++) d = Math.Max(d, Math.Abs(a[i] - b[i]));
        return d;
    }

    static double Db(double ratio) => 20 * Math.Log10(Math.Max(ratio, 1e-9));

    /// <summary>Prints its rows; true when all of them pass.</summary>
    public static bool Check()
    {
        bool allOk = true;
        void Row(string name, string got, string want, bool ok)
        {
            allOk &= ok;
            Console.WriteLine($"{name,-44} {got,12} {want,10}  {(ok ? "igual" : "DISTINTO")}");
        }
        static string Diff(double d) => d == 0 ? "exacto" : $"{d:0.0e0}";

        var plain = new SamplerPatch { VelAmp = 0, Amp = new(0, 0.1, 1, 0.005) };
        var tone = Sine(440, 2);
        var noise = Noise(1);

        // 1. at its root it is the file, bit for bit (but for the edge at its very end)
        var (root, rootR) = Render(plain, Sound(noise), [On(0, Sr * 2, 60)], noise.Length);
        Row("Sampler: en la raíz, el archivo exacto", Diff(MaxDiff(root, noise, 0, noise.Length - 80)), "exacto", MaxDiff(root, noise, 0, noise.Length - 80) == 0);
        Row("Sampler: los dos canales del mono", Diff(MaxDiff(root, rootR)), "exacto", MaxDiff(root, rootR) == 0);

        // 2. in tune, and as long as it should be
        var (up, _) = Render(plain with { Root = 69 }, Sound(tone), [On(0, Sr * 2, 81)], Sr * 2);
        double hz = Hz(up, Sr / 10, Sr * 9 / 10);
        Row("Sampler: una octava arriba afina", $"{hz:0.00} Hz", "880", Math.Abs(hz - 880) < 0.05);
        int upEnd = Array.FindLastIndex(up, v => Math.Abs(v) > 1e-3f);
        Row("Sampler: el doble de rápido dura la mitad", $"{upEnd / (double)Sr:0.000} s", "1", Math.Abs(upEnd / (double)Sr - 1) < 0.002);
        var (fifth, _) = Render(plain with { Root = 69, Tune = 0.5 }, Sound(tone), [On(0, Sr, 69)], Sr);
        double quarter = Hz(fifth, Sr / 10, Sr * 9 / 10), wantQuarter = 440 * Math.Pow(2, 0.5 / 12);
        Row("Sampler: los cents llegan", $"{quarter:0.00} Hz", $"{wantQuarter:0.00}", Math.Abs(quarter - wantQuarter) < 0.05);

        // 3. a tone above Nyquist once played up isn't folded back: four octaves over a 6 kHz tone leave nothing
        var high = Sine(6000, 1);
        var (fold, _) = Render(plain with { Root = 48 }, Sound(high), [On(0, Sr, 96)], Sr / 16 - 200);
        double folded = Db(Rms(fold, 2000, fold.Length) / 0.354);
        Row("Sampler: sin aliasing cuatro octavas arriba", $"{folded:0.0} dB", "< -50", folded < -50);

        // 4. the amp envelope
        var flat = new float[Sr * 3];
        Array.Fill(flat, 0.5f);
        var (att, _) = Render(plain with { Amp = new(0.1, 0.1, 1, 0.1) }, Sound(flat), [On(0, Sr, 60)], Sr);
        Row("Amp: a mitad del ataque, la mitad", $"{att[Sr / 20]:0.000}", "0.25", Math.Abs(att[Sr / 20] - 0.25) < 0.002);
        var (sus, _) = Render(plain with { Amp = new(0, 0.1, 0.5, 0.1) }, Sound(flat), [On(0, Sr, 60)], Sr + Sr / 2);
        Row("Amp: sostiene en su nivel", $"{sus[Sr * 3 / 4]:0.000}", "0.25", Math.Abs(sus[Sr * 3 / 4] - 0.25) < 0.001);
        double fallen = Db(sus[Sr + Sr / 10] / 0.25);
        Row("Amp: la release baja 60 dB en su tiempo", $"{fallen:0.0} dB", "-60", Math.Abs(fallen + 60) < 2);
        var (edge, _) = Render(plain, Sound(flat), [On(0, Sr / 2, 60)], Sr);
        Row("Amp: la nota termina con la release", $"{edge[Sr * 3 / 4]:0.0000}", "0", edge[Sr * 3 / 4] == 0);

        // 5. velocity
        var (soft, _) = Render(plain with { VelAmp = 1 }, Sound(flat), [On(0, Sr / 2, 60, 64)], Sr / 2);
        double wantSoft = 0.5 * Math.Pow(64 / 127.0, 2);
        Row("Velocidad: al cuadrado", $"{soft[Sr / 4]:0.000}", $"{wantSoft:0.000}", Math.Abs(soft[Sr / 4] - wantSoft) < 1e-3);

        // 6. filters: where they cut, their slope and that they hold together at their limits
        double Through(SamplerFilter type, double toneHz, double cutoff, double res = 0)
        {
            var x = Sine(toneHz, 1);
            var (y, _) = Render(plain with { Filter = type, Cutoff = cutoff, Resonance = res }, Sound(x), [On(0, Sr, 60)], Sr);
            return Db(Rms(y, Sr / 2, Sr - 200) / Rms(x, Sr / 2, Sr - 200));
        }
        double lp = Through(SamplerFilter.Low12, 8000, 1000), lp24 = Through(SamplerFilter.Low24, 8000, 1000), hp = Through(SamplerFilter.High24, 125, 1000);
        Row("Filtro LP 12: tres octavas arriba", $"{lp:0.0} dB", "≈ -36", Math.Abs(lp + 36) < 3);
        Row("Filtro LP 24: el doble", $"{lp24:0.0} dB", "≈ -72", Math.Abs(lp24 + 72) < 5);
        Row("Filtro HP 24: tres octavas abajo", $"{hp:0.0} dB", "≈ -72", Math.Abs(hp + 72) < 5);
        double pass = Through(SamplerFilter.Low24, 100, 4000);
        Row("Filtro LP: deja pasar lo de abajo", $"{pass:0.0} dB", "≈ 0", Math.Abs(pass) < 1);
        double notch = Through(SamplerFilter.Notch, 1000, 1000);
        Row("Filtro Notch: se come su frecuencia", $"{notch:0.0} dB", "< -40", notch < -40);
        double peak = Through(SamplerFilter.Low12, 1000, 1000, 0.95);
        Row("Filtro: la resonancia levanta el corte", $"{peak:0.0} dB", "> 12", peak > 12);
        var sweep = plain with
        {
            Filter = SamplerFilter.Low24, Cutoff = 300, Resonance = 1, Drive = 1, FilterEnv = 1, FilterShape = new(0.01, 0.2, 0, 0.1), LfoShape = SamplerLfoShape.Square, LfoHz = 30, LfoFilter = 4,
        };
        var (wild, _) = Render(sweep, Sound(noise), [On(0, Sr, 60)], Sr);
        double wildPeak = wild.Max(Math.Abs);
        Row("Filtro: estable al máximo, barriendo", $"{wildPeak:0.00}", "< 20", wild.All(float.IsFinite) && wildPeak < 20);

        // 7. loops: a held note outlasts the sample, in tune, without a jump at the seam
        var bars = Sine(480, 0.5); // a whole number of periods: a clean seam
        var (looped, _) = Render(plain, Sound(bars, SamplerLoop.Forward, 0.2, 0.8), [On(0, Sr * 3, 60)], Sr * 3 + Sr / 2);
        double loopHz = Hz(looped, Sr, Sr * 2), jump = 0;
        for (int i = Sr; i < Sr * 2; i++) jump = Math.Max(jump, Math.Abs(looped[i] - looped[i - 1]));
        double smooth = 2 * Math.PI * 480 / Sr * 0.5 * 1.05;
        Row("Loop: sigue sonando afinado", $"{loopHz:0.00} Hz", "480", Math.Abs(loopHz - 480) < 0.05);
        Row("Loop: sin salto en la costura", $"{jump:0.000}", $"< {smooth:0.000}", jump < smooth);
        var (pingPong, _) = Render(plain, Sound(flat, SamplerLoop.PingPong, 0.1, 0.2), [On(0, Sr * 2, 60)], Sr * 2);
        Row("Loop: ida y vuelta sigue sonando", $"{pingPong[Sr * 3 / 2]:0.000}", "0.5", Math.Abs(pingPong[Sr * 3 / 2] - 0.5) < 1e-3);
        var ramp = new float[Sr];
        for (int i = 0; i < ramp.Length; i++) ramp[i] = i / (float)Sr;
        var seam = Sound(ramp, SamplerLoop.Forward, 0.5, 0.75, 0.2);
        var mix = seam.L[0];
        double seamJump = Math.Abs(mix[SamplerSound.Pad + seam.LoopEnd - 1] - mix[SamplerSound.Pad + seam.LoopStart - 1]);
        Row("Loop: el crossfade cierra la costura", $"{seamJump:0.0000}", "< 0.001", seamJump < 1e-3);

        // 8. modes
        var (oneShot, _) = Render(plain with { OneShot = true }, Sound(flat), [On(0, Sr / 10, 60)], Sr);
        Row("One shot: sigue después de soltar", $"{oneShot[Sr / 2]:0.000}", "0.5", Math.Abs(oneShot[Sr / 2] - 0.5) < 1e-3);
        var (fixedKey, _) = Render(plain with { Root = 69, KeyTrack = false }, Sound(tone), [On(0, Sr, 40)], Sr);
        double fixedHz = Hz(fixedKey, Sr / 10, Sr * 9 / 10);
        Row("Sin seguir la tecla, misma altura", $"{fixedHz:0.00} Hz", "440", Math.Abs(fixedHz - 440) < 0.05);
        var (reversed, _) = Render(plain, Sound(ramp, reverse: true), [On(0, Sr * 2, 60)], Sr / 2);
        Row("Reverse: suena al revés", $"{reversed[Sr / 4]:0.000}", "0.750", Math.Abs(reversed[Sr / 4] - 0.75) < 1e-3);
        var (cut, _) = Render(plain with { Start = 0.25, End = 0.5 }, Sound(ramp), [On(0, Sr * 2, 60)], Sr);
        Row("Inicio y fin: toca ese tramo", $"{cut[Sr / 8]:0.000}", "0.375", Math.Abs(cut[Sr / 8] - 0.375) < 1e-3);
        int cutEnd = Array.FindLastIndex(cut, v => v != 0);
        Row("Inicio y fin: termina en su lugar", $"{cutEnd / (double)Sr:0.000} s", "0.25", Math.Abs(cutEnd / (double)Sr - 0.25) < 1e-3);

        // 9. voices: a limit of one, and notes that overlap add up
        var (poly, _) = Render(plain with { Voices = 8 }, Sound(flat), [On(0, Sr, 60), On(0, Sr, 60)], Sr / 2);
        var (mono, _) = Render(plain with { Voices = 1 }, Sound(flat), [On(0, Sr, 60), On(100, Sr, 60)], Sr / 2);
        Row("Voces: dos notas suman", $"{poly[Sr / 4]:0.000}", "1", Math.Abs(poly[Sr / 4] - 1) < 1e-3);
        Row("Voces: mono deja una", $"{mono[Sr / 4]:0.000}", "0.5", Math.Abs(mono[Sr / 4] - 0.5) < 1e-3);
        var (bend, _) = Render(plain with { Root = 69, Voices = 1, Legato = true, GlideSec = 0.1 }, Sound(tone), [On(0, Sr / 2, 69), On(Sr / 2, Sr, 81)], Sr * 3 / 2);
        double bendHz = Hz(bend, Sr, Sr * 5 / 4);
        Row("Legato: dobla sin arrancar de nuevo", $"{bendHz:0.0} Hz", "880", Math.Abs(bendHz - 880) < 1);

        // 10. the same notes in any block size, with every moving part on
        var full = new SamplerPatch
        {
            Amp = new(0.01, 0.2, 0.6, 0.1), Filter = SamplerFilter.Low24, Cutoff = 1500, Resonance = 0.4, Drive = 0.3, FilterEnv = 0.4,
            LfoHz = 6, LfoPitch = 0.5, LfoFilter = 1, LfoAmp = 0.3, LfoDelay = 0.1, PitchEnv = 12, PitchDecay = 0.05, Pan = 0.3, Root = 57,
        };
        NoteEvent[] tune = [On(0, Sr / 2, 60), On(1234, Sr / 3, 64), On(20011, Sr / 2, 67, 90), On(Sr * 3 / 4, Sr / 2, 72, 50)];
        var (b64, _) = Render(full, Sound(noise), tune, Sr * 2, 64);
        var (b1000, _) = Render(full, Sound(noise), tune, Sr * 2, 1000);
        var (b37, _) = Render(full, Sound(noise), tune, Sr * 2, 37);
        Row("Bloques: cualquier tamaño, lo mismo", Diff(Math.Max(MaxDiff(b64, b1000), MaxDiff(b64, b37))), "exacto", MaxDiff(b64, b1000) == 0 && MaxDiff(b64, b37) == 0);

        // 11. nothing allocates on the audio thread, once the JIT has run it
        {
            var player = new Sampler(Sr) { Sound = Sound(noise), Notes = new NoteSequence(tune, Sr) };
            player.Set(full);
            var l = new float[256];
            var r = new float[256];
            void Lap() { for (int at = 0; at < Sr; at += 256) player.Play(at, 256, l, r); player.Play(100, 256, l, r); }
            Lap();
            long before = GC.GetAllocatedBytesForCurrentThread();
            Lap();
            long grew = GC.GetAllocatedBytesForCurrentThread() - before;
            Row("Hilo de audio: no asigna memoria", $"{grew} bytes", "0", grew == 0);
        }

        // 12. landing in the middle of a note picks it up from where it would be, and it comes in soft
        {
            var player = new Sampler(Sr) { Sound = Sound(flat), Notes = new NoteSequence([On(0, Sr * 2, 60)], Sr) };
            player.Set(plain);
            var l = new float[1024];
            var r = new float[1024];
            player.Play(Sr, 1024, l, r);
            Row("Salto: toma la nota por la mitad", $"{l[1000]:0.000}", "0.5", Math.Abs(l[1000] - 0.5) < 1e-3);
            Row("Salto: entra sin click", $"{l[0]:0.000}", "< 0.01", Math.Abs(l[0]) < 0.01);
        }

        // 13. a loop that turns inside a block starts the note at its start on the exact frame, whole
        {
            var player = new Sampler(Sr) { Sound = Sound(noise), Notes = new NoteSequence([On(1000, Sr, 60)], Sr) };
            player.Set(plain);
            var l = new float[512];
            var r = new float[512];
            player.Play(5000, 512, l, r);
            l.AsSpan().Clear();
            r.AsSpan().Clear();
            player.Play(5512, 512, l, r, null, 100, 1000);
            var fresh = new float[512];
            var freshR = new float[512];
            var clean = new Sampler(Sr) { Sound = Sound(noise), Notes = new NoteSequence([On(0, Sr, 60)], Sr) };
            clean.Set(plain);
            clean.Play(0, 412, fresh, freshR);
            double err = 0;
            // the old voice fades for 240 frames over it: past that, the new one alone
            for (int i = 340; i < 512; i++) err = Math.Max(err, Math.Abs(l[i] - fresh[i - 100]));
            Row("Bucle: la nota entra en su cuadro exacto", Diff(err), "exacto", err == 0);
        }

        // 14. the root from its name and from its sound
        Row("Raíz: del nombre", SamplerRoot.FromName("Piano_C#3 soft")?.ToString() ?? "-", "49", SamplerRoot.FromName("Piano_C#3 soft") == 49);
        Row("Raíz: no inventa con una palabra", SamplerRoot.FromName("Add9 Bass2 Dry")?.ToString() ?? "-", "-", SamplerRoot.FromName("Add9 Bass2 Dry") == null);
        var heard = SamplerRoot.FromSound(tone, tone, Sr);
        Row("Raíz: del sonido", heard?.Key.ToString() ?? "-", "69", heard?.Key == 69 && Math.Abs(heard.Value.Cents) < 3);
        var sharp = Sine(440 * Math.Pow(2, 20 / 1200.0), 1);
        var off = SamplerRoot.FromSound(sharp, sharp, Sr);
        Row("Afinación: mide los cents", off is { } o ? $"{o.Key} {o.Cents:+0.0;-0.0}" : "-", "69 +20", off?.Key == 69 && Math.Abs(off.Value.Cents - 20) < 1.5);
        var tuned = new SamplerSettings();
        tuned.Adopt("toma", sharp, sharp, Sr);
        var (fixedTone, _) = Render(tuned.Patch(120, null) with { VelAmp = 0 }, Sound(sharp), [On(0, Sr, 69)], Sr);
        double fixedPitch = Hz(fixedTone, Sr / 10, Sr * 9 / 10);
        Row("Afinación: un sample nuevo queda afinado", $"{fixedPitch:0.00} Hz", "440", Math.Abs(fixedPitch - 440) < 0.5);
        float[] two = [.. Sine(220, 0.5), .. Sine(440, 0.5)];
        var part = SamplerRoot.FromSound(two, two, Sr, Sr / 2, Sr);
        Row("Afinación: mira solo el tramo elegido", part?.Key.ToString() ?? "-", "69", part?.Key == 69);
        var brief = Sine(440, 0.06);
        Row("Afinación: un tramo muy corto no alcanza", SamplerRoot.FromSound(brief, brief, Sr, 0, 1000)?.Key.ToString() ?? "-", "-", SamplerRoot.FromSound(brief, brief, Sr, 0, 1000) == null);
        Row("Raíz: un ruido no tiene", SamplerRoot.FromSound(noise, noise, Sr)?.Key.ToString() ?? "-", "-", SamplerRoot.FromSound(noise, noise, Sr) == null);

        // 15. the project: broken values come back in range, unknown fields stay, and the sample counts as in use
        {
            var p = new Project();
            var t = new Track { Kind = TrackKinds.Midi, Sampler = new SamplerSettings { MediaId = "med_x", Voices = 900, Cutoff = double.NaN, Start = 0.9, End = 0.1, Filter = "nada" } };
            p.Tracks.Add(t);
            p.Normalize();
            Row("Proyecto: valores rotos vuelven al rango", $"{t.Sampler!.Voices} · {t.Sampler.Cutoff}", "32 · 2000", t.Sampler.Voices == 32 && t.Sampler.Cutoff == 2000 && t.Sampler.End == 1 && t.Sampler.Filter == "off");
            Row("Proyecto: el sample cuenta como usado", ProjectHistory.UsedMediaIds(p).Contains("med_x") ? "sí" : "no", "sí", ProjectHistory.UsedMediaIds(p).Contains("med_x"));
            string json = JsonSerializer.Serialize(t.Sampler, ProjectStore.Json);
            var back = JsonSerializer.Deserialize<SamplerSettings>(json, ProjectStore.Json)!;
            Row("Proyecto: va y vuelve", back.MediaId ?? "-", "med_x", back.MediaId == "med_x" && JsonSerializer.Serialize(back, ProjectStore.Json) == json);
            var audio = new Track { Sampler = new SamplerSettings() };
            p.Tracks.Add(audio);
            p.Normalize();
            Row("Proyecto: una pista de audio no lleva", audio.Sampler == null ? "null" : "sampler", "null", audio.Sampler == null);
        }

        // 16. the export plays exactly what the voices play, and waits for a one shot to end
        {
            var rig = new SamplerRig(full, Sound(noise));
            var seq = new NoteSequence(tune, Sr);
            var l = new float[Sr * 2];
            MixRenderer.RenderTo([new MixTrackSpec { Notes = seq, Sampler = rig, Inserts = [] }], new MixBusSpec(), 2, (at, a, _) => { if (at < l.Length) a[..Math.Min(a.Length, l.Length - at)].CopyTo(l.AsSpan(at)); }, Sr, limit: false, master: false);
            var (live, _) = Render(full, rig.Sound, tune, Sr * 2);
            Row("Exportación: lo mismo que en vivo", Diff(MaxDiff(l, live)), "< 1e-6", MaxDiff(l, live) < 1e-6);
            var longHit = new SamplerRig(plain with { OneShot = true, Root = 72 }, Sound(flat));
            double sec = MixRenderer.MixDurationSec([new MixTrackSpec { Notes = new NoteSequence([On(0, 100, 60)], Sr), Sampler = longHit }], Sr);
            Row("Exportación: espera al one shot, más grave", $"{sec:0.0} s", "6", Math.Abs(sec - 6) < 0.2);
        }

        // 17. what a note expresses: a slide bends its own voice with no new attack, a pan is its own, and neither depends on the block
        {
            const int beat = 24000; // at 120 bpm and 48 kHz
            var sine = Sound(tone, SamplerLoop.Forward, 0.25, 0.75);
            var held = plain with { Root = 69 };
            NoteSequence Written(params MidiNote[] written)
            {
                var p = new Project();
                p.Settings.Bpm = 120;
                var c = MidiEditing.AddClip(p, MidiEditing.AddTrack(p), 0, 32);
                MidiEditing.SetNotes(p, c.Id, [.. written]);
                return ChannelDsp.Notes(p.Tracks[0], 120, Sr)!;
            }
            MidiNote Note(int pitch, double at, double beats, bool slide = false, bool porta = false, int pan = 0) => new() { Pitch = pitch, Beat = at, Beats = beats, Velocity = 127, Slide = slide, Porta = porta, Pan = pan };

            var slid = Written(Note(69, 0, 4), Note(81, 1, 1, slide: true));
            var (bent, _) = Sequence(held, sine, slid, 4 * beat);
            double before = Hz(bent, beat / 4, beat), after = Hz(bent, 2 * beat + 1000, 3 * beat + 12000);
            Row("Slide: arranca en su nota", $"{before:0.0} Hz", "440", Math.Abs(before - 440) < 0.5);
            Row("Slide: llega a la altura del slide", $"{after:0.0} Hz", "880", Math.Abs(after - 880) < 0.5);
            // a tone that rises through the middle: half way there is half an octave, in pitch
            double half = Hz(bent, beat + beat / 2 - 500, beat + beat / 2 + 500), wantHalf = 440 * Math.Sqrt(2);
            Row("Slide: recto en altura", $"{half:0} Hz", $"{wantHalf:0}", Math.Abs(half - wantHalf) < 8);
            double step = 0, edge880 = 2 * Math.PI * 880 / Sr * 0.5 * 1.05;
            for (int i = 1; i < 3 * beat; i++) step = Math.Max(step, Math.Abs(bent[i] - bent[i - 1]));
            Row("Slide: sin ataque nuevo ni salto", $"{step:0.000}", $"< {edge880:0.000}", step < edge880);
            var chained = Written(Note(69, 0, 5), Note(81, 1, 1, slide: true), Note(76, 2, 1, slide: true));
            var (linked, _) = Sequence(held, sine, chained, 5 * beat);
            double second = Hz(linked, 3 * beat + 1000, 4 * beat);
            Row("Slide: encadena sobre la misma voz", $"{second:0.0} Hz", $"{NoteSynth.Hz(76):0.0}", Math.Abs(second - NoteSynth.Hz(76)) < 0.5 && chained.Notes.Length == 1);
            var rising = Written(Note(69, 0, 1), Note(81, 1, 2, porta: true));
            var (fell, _) = Sequence(held, sine, rising, 3 * beat);
            double landed = Hz(fell, 2 * beat, 3 * beat - 200);
            Row("Porta: llega a su nota", $"{landed:0.0} Hz", "880", Math.Abs(landed - 880) < 0.5);
            double flying = Hz(fell, beat + 300, beat + 1500);
            Row("Porta: sale de la nota de antes", $"{flying:0} Hz", "< 700", flying > 430 && flying < 700);
            var (fixedKey2, _) = Sequence(held with { KeyTrack = false }, sine, slid, 4 * beat);
            double still = Hz(fixedKey2, 2 * beat + 1000, 3 * beat);
            Row("Slide: sin seguir la tecla, no dobla", $"{still:0.0} Hz", "440", Math.Abs(still - 440) < 0.5);

            var panned = Written(Note(69, 0, 2, pan: -100), Note(69, 2, 2, pan: 50), Note(69, 4, 2));
            var (pl, pr) = Sequence(plain with { Root = 69 }, Sound(flat), panned, 6 * beat);
            Row("Paneo por nota: todo a un lado", $"L {pl[beat]:0.00} R {pr[beat]:0.00}", "0.5 / 0", Math.Abs(pl[beat] - 0.5) < 1e-3 && pr[beat] == 0);
            Row("Paneo por nota: la ley del sampler", $"L {pl[3 * beat]:0.00} R {pr[3 * beat]:0.00}", "0.25 / 0.5", Math.Abs(pl[3 * beat] - 0.25) < 1e-3 && Math.Abs(pr[3 * beat] - 0.5) < 1e-3);
            Row("Paneo por nota: la que no tiene, al centro", $"L {pl[5 * beat]:0.00} R {pr[5 * beat]:0.00}", "0.5 / 0.5", Math.Abs(pl[5 * beat] - 0.5) < 1e-3 && pl[5 * beat] == pr[5 * beat]);
            var (sum, _) = Sequence(plain with { Root = 69, Pan = 0.5 }, Sound(flat), Written(Note(69, 0, 2, pan: 100)), beat);
            Row("Paneo por nota: se suma al del sampler", $"{sum[beat / 2]:0.00}", "0", sum[beat / 2] == 0);

            NoteSequence all = Written(Note(60, 0, 2, pan: 30), Note(67, 1, 1, slide: true), Note(64, 2.25, 1, porta: true, pan: -40));
            var (a64, a64R) = Sequence(full, Sound(noise), all, Sr * 2, 64);
            var (a997, a997R) = Sequence(full, Sound(noise), all, Sr * 2, 997);
            Row("Slide y paneo: cualquier bloque, lo mismo", Diff(Math.Max(MaxDiff(a64, a997), MaxDiff(a64R, a997R))), "exacto", MaxDiff(a64, a997) == 0 && MaxDiff(a64R, a997R) == 0);

            // the export reads the same held note
            var rig = new SamplerRig(full, Sound(noise));
            var xl = new float[Sr * 2];
            MixRenderer.RenderTo([new MixTrackSpec { Notes = all, Sampler = rig, Inserts = [] }], new MixBusSpec(), 2, (at, a, _) => { if (at < xl.Length) a[..Math.Min(a.Length, xl.Length - at)].CopyTo(xl.AsSpan(at)); }, Sr, limit: false, master: false);
            var (al, _) = Sequence(full, rig.Sound, all, Sr * 2);
            Row("Slide y paneo: la exportación igual", Diff(MaxDiff(xl, al)), "< 1e-6", MaxDiff(xl, al) < 1e-6);

            // landing in the middle of a slide: it reads the sample where it would have got to, at the pitch it would have
            var ramped = new float[Sr * 2];
            for (int i = 0; i < ramped.Length; i++) ramped[i] = i / (float)ramped.Length;
            var whole = Written(Note(69, 0, 3), Note(81, 1, 2, slide: true));
            var late = new Sampler(Sr) { Sound = Sound(ramped), Notes = whole };
            late.Set(held);
            var ll = new float[2048];
            var lr = new float[2048];
            late.Play(2 * beat, 2048, ll, lr);
            var (straight, _) = Sequence(held, Sound(ramped), whole, 2 * beat + 2048);
            double err = Math.Abs(ll[2000] - straight[2 * beat + 2000]);
            Row("Slide: un salto cae donde debe", $"{err:0.0e0}", "< 1e-4", err < 1e-4);
        }

        // 18. the lanes on its knobs: read at each voice's own steps, at the frame of the song, so nothing but the song decides
        {
            static AutomationLane Lane(string target, params (double Sec, double Value)[] points) => new()
            {
                Target = target,
                Blocks = [new() { LengthSec = points[^1].Sec + 1, Points = [.. points.Select(x => new AutomationPoint { Sec = x.Sec, Value = x.Value })] }],
            };
            static SamplerCurves? Lanes(params AutomationLane[] lanes) => Automations.SamplerCurves(new Track { Kind = TrackKinds.Midi, Sampler = new SamplerSettings(), Automation = [.. lanes] });

            NoteEvent[] song = [On(0, Sr * 2, 60)];
            var steady = plain with { Filter = SamplerFilter.Low24, Cutoff = 300 };
            var gain = Lanes(Lane(AutoTarget.Sampler(SamplerKnob.Gain), (0, 0.75), (1, 0.75), (2, 0.5)));
            var (loud, _) = Render(plain, Sound(flat), song, Sr * 2, curves: gain);
                        Row("Carril: el volumen sigue su curva", $"{loud[Sr / 2]:0.000}", "0.5", Math.Abs(loud[Sr / 2] - 0.5) < 1e-3); // 0.75 of -36..12 is 0 dB
            double down = 0.5 * Math.Pow(10, -12.0 / 20);
            Row("Carril: llega al valor del final", $"{loud[Sr * 2 - 100]:0.000}", $"{down:0.000}", Math.Abs(loud[Sr * 2 - 100] - down) < 2e-3);
            double slope = 0;
            for (int i = 1; i < loud.Length; i++) slope = Math.Max(slope, Math.Abs(loud[i] - loud[i - 1]));
            Row("Carril: sin escalones", $"{slope:0.0e0}", "< 1e-4", slope < 1e-4);

            var opening = Lanes(Lane(AutoTarget.Sampler(SamplerKnob.Cutoff), (0, 0), (2, 1)));
            var (shut, _) = Render(steady, Sound(noise), [On(0, Sr, 60), On(Sr, Sr, 60)], Sr * 2 - 200, curves: opening);
            double dark = Rms(shut, Sr / 10, Sr / 5), bright = Rms(shut, Sr * 15 / 10, Sr * 19 / 10);
            Row("Carril: el corte abre mientras suena", $"{Db(bright / dark):0.0} dB", "> 12", bright > 4 * dark);
            var (plainCut, _) = Render(steady with { Cutoff = 1000 }, Sound(noise), song, Sr);
            var flatCut = Lanes(Lane(AutoTarget.Sampler(SamplerKnob.Cutoff), (0, SamplerKnobs.Position(SamplerKnob.Cutoff, 1000))));
            var (laneCut, _) = Render(steady, Sound(noise), song, Sr, curves: flatCut);
            Row("Carril: plano, como la perilla", Diff(MaxDiff(plainCut, laneCut)), "< 1e-3", MaxDiff(plainCut, laneCut) < 1e-3);
            var (res, _) = Render(plain with { Filter = SamplerFilter.Low12, Cutoff = 1000 }, Sound(Sine(1000, 1)), [On(0, Sr, 60)], Sr, curves: Lanes(Lane(AutoTarget.Sampler(SamplerKnob.Resonance), (0, 0.95))));
            double boost = Db(Rms(res, Sr / 2, Sr - 200) / Rms(Sine(1000, 1), Sr / 2, Sr - 200));
            Row("Carril: la resonancia llega", $"{boost:0.0} dB", "> 12", boost > 12);
            var (driven, _) = Render(steady with { Cutoff = 16000 }, Sound(Sine(100, 1)), [On(0, Sr, 60)], Sr, curves: Lanes(Lane(AutoTarget.Sampler(SamplerKnob.Drive), (0, 1))));
            Row("Carril: el drive llega", $"{driven.Max(Math.Abs):0.00}", "0.40", Math.Abs(driven.Max(Math.Abs) - 0.4) < 0.01); // 0.5 x 8 hits the ceiling, times the make-up of 1 / 2.5
            var unlit = Lane(AutoTarget.Sampler(SamplerKnob.Cutoff), (0, 0.1));
            unlit.On = false;
            Row("Carril: apagado, manda la perilla", Lanes(unlit) == null ? "sin curvas" : "curvas", "sin curvas", Lanes(unlit) == null);

            var moving = Lanes(
                Lane(AutoTarget.Sampler(SamplerKnob.Cutoff), (0, 0.3), (0.5, 0.9), (1, 0.4), (2, 0.8)),
                Lane(AutoTarget.Sampler(SamplerKnob.Resonance), (0, 0.1), (2, 0.8)),
                Lane(AutoTarget.Sampler(SamplerKnob.Drive), (0, 0), (2, 0.6)),
                Lane(AutoTarget.Sampler(SamplerKnob.FilterEnv), (0, 0.5), (2, 0.8)),
                Lane(AutoTarget.Sampler(SamplerKnob.Gain), (0, 0.75), (2, 0.6)),
                Lane(AutoTarget.Sampler(SamplerKnob.Pan), (0, 0.2), (2, 0.8)),
                Lane(AutoTarget.Sampler(SamplerKnob.LfoPitch), (0, 0), (2, 0.5)),
                Lane(AutoTarget.Sampler(SamplerKnob.LfoFilter), (0, 0), (2, 0.5)),
                Lane(AutoTarget.Sampler(SamplerKnob.LfoAmp), (0, 0), (2, 0.5)));
            var (m64, m64R) = Render(full, Sound(noise), tune, Sr * 2, 64, curves: moving);
            var (m1000, m1000R) = Render(full, Sound(noise), tune, Sr * 2, 1000, curves: moving);
            var (m37, m37R) = Render(full, Sound(noise), tune, Sr * 2, 37, curves: moving);
            double blocks = new[] { MaxDiff(m64, m1000), MaxDiff(m64, m37), MaxDiff(m64R, m1000R), MaxDiff(m64R, m37R) }.Max();
            Row("Carriles: cualquier bloque, lo mismo", Diff(blocks), "exacto", blocks == 0);

            var rig = new SamplerRig(full, Sound(noise)) { Curves = moving };
            var xl = new float[Sr * 2];
            var xr = new float[Sr * 2];
            MixRenderer.RenderTo([new MixTrackSpec { Notes = new NoteSequence(tune, Sr), Sampler = rig, Inserts = [] }], new MixBusSpec(), 2, (at, a, b) =>
            {
                if (at >= xl.Length) return;
                a[..Math.Min(a.Length, xl.Length - at)].CopyTo(xl.AsSpan(at));
                b[..Math.Min(b.Length, xr.Length - at)].CopyTo(xr.AsSpan(at));
            }, Sr, limit: false, master: false);
            Row("Carriles: la exportación igual que en vivo", Diff(Math.Max(MaxDiff(xl, m64), MaxDiff(xr, m64R))), "< 1e-6", MaxDiff(xl, m64) < 1e-6 && MaxDiff(xr, m64R) < 1e-6);

            // one stepped through a song: stopped, the keyboard hears the lanes where the playhead rests
            var keys = new Sampler(Sr) { Sound = Sound(flat), Curves = gain };
            keys.Set(plain);
            var kl = new float[1024];
            var kr = new float[1024];
            keys.Press(60, 127);
            keys.Idle(1024, kl, kr, null, Sr * 2);
            Row("Carril: parado, lee donde está el cursor", $"{kl[800]:0.000}", $"{down:0.000}", Math.Abs(kl[800] - down) < 2e-3);

            var t = new Track { Kind = TrackKinds.Midi, Sampler = new SamplerSettings { Cutoff = 2000 }, Automation = [unlit] };
            var target = AutoTarget.Sampler(SamplerKnob.Cutoff);
            double back = SamplerKnobs.Value(SamplerKnob.Cutoff, Automations.Position(t, target));
            Row("Carril nuevo: arranca donde está la perilla", $"{back:0} Hz", "2000", Math.Abs(back - 2000) < 1e-6);
            bool laws = true;
            foreach (var k in Enum.GetValues<SamplerKnob>())
                foreach (double x in (double[])[0, 0.25, 0.5, 1]) laws &= Math.Abs(SamplerKnobs.Position(k, SamplerKnobs.Value(k, x)) - x) < 1e-9;
            Row("Carril: la ley va y vuelve", laws ? "sí" : "no", "sí", laws);
            bool alive = Automations.Exists(t, target);
            t.Sampler = null;
            Row("Carril: sin sampler queda sin destino", alive && !Automations.Exists(t, target) ? "sí" : "no", "sí", alive && !Automations.Exists(t, target));
            Row("Carril: el destino se lee de vuelta", AutoTarget.SamplerOf(target)?.ToString() ?? "-", "Cutoff", AutoTarget.SamplerOf(target) == SamplerKnob.Cutoff && AutoTarget.SamplerOf("sampler:nada") == null);

            // nothing allocates with a slide, a pan and every lane moving
            var player = new Sampler(Sr) { Sound = Sound(noise), Notes = new NoteSequence([On(0, Sr, 60) with { Pan = 0.5f, Bend = new NoteBend([0, 1000, 5000], [0, 2, -3]) }, .. tune], Sr), Curves = moving };
            player.Set(full);
            var l = new float[256];
            var r = new float[256];
            void Lap() { for (int at = 0; at < Sr; at += 256) player.Play(at, 256, l, r); player.Play(100, 256, l, r); }
            Lap();
            long grew = GC.GetAllocatedBytesForCurrentThread();
            Lap();
            grew = GC.GetAllocatedBytesForCurrentThread() - grew;
            Row("Carriles y slide: no asignan memoria", $"{grew} bytes", "0", grew == 0);
        }
        return allOk;
    }
}
