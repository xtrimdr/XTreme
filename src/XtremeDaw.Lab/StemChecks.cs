using XtremeDaw.Audio;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// xtlab separar (the stems of a file, like "Separar en stems" on a clip) and the rows of comprobar-dsp for
/// <see cref="StemSeparator"/>: that its transforms give the audio back, and that the stems of a short sound have its
/// length and add up to it. Without the models only the transforms are checked.
/// </summary>
static class StemChecks
{
    const int Sr = 48000;

    /// <summary>The stems by the names of <c>--solo</c>.</summary>
    static readonly Dictionary<string, string> Names = new()
    {
        ["voz"] = "vocals", ["bateria"] = "drums", ["bajo"] = "bass", ["guitarra"] = "guitar", ["piano"] = "piano",
    };

    /// <summary>xtlab separar --comprobar (only its rows of comprobar-dsp), or separar archivo [--stems 2|4|6] [--calidad rapida|alta|maxima] [--modelo general|porstem|6] [--solo voz,bateria,bajo,guitarra,piano] [--pasadas n] [--sin-desplazar] [--desde s] [--hasta s] [--salida carpeta] [--cancelar s] [--cpu].</summary>
    public static int Run(Args a)
    {
        if (a.Flag("comprobar")) return Check() ? 0 : 3;
        string file = a.Positional.ElementAtOrDefault(0) ?? throw new ArgumentException("separar: falta el archivo.");
        var layout = a.Get("stems") switch
        {
            "2" => StemSeparator.Layout.Two,
            "6" => StemSeparator.Layout.Six,
            null or "4" => StemSeparator.Layout.Four,
            var x => throw new ArgumentException($"separar: --stems {x} no existe (2, 4 o 6)."),
        };
        var quality = a.Get("calidad") switch
        {
            "rapida" => StemSeparator.Quality.Fast,
            "maxima" => StemSeparator.Quality.Max,
            null or "alta" => StemSeparator.Quality.High,
            var x => throw new ArgumentException($"separar: --calidad {x} no existe (rapida, alta o maxima)."),
        };
        var model = a.Get("modelo") switch
        {
            null => StemSeparator.Model.Auto,
            "general" => StemSeparator.Model.General,
            "porstem" => StemSeparator.Model.Tuned,
            "6" => StemSeparator.Model.Six,
            var x => throw new ArgumentException($"separar: --modelo {x} no existe (general, porstem o 6)."),
        };
        var only = a.Get("solo")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Names.TryGetValue(s, out var id) ? id : throw new ArgumentException($"separar: --solo {s} no existe ({string.Join(", ", Names.Keys)}).")).ToList();
        int? shifts = a.Flag("sin-desplazar") ? 0 : a.Get("pasadas") != null ? Math.Clamp((int)a.Double("pasadas", 1), 1, StemSeparator.MaxShifts) : null;
        var audio = AudioLoader.Load(file, 0);
        int from = (int)(a.Double("desde", 0) * audio.SampleRate);
        int to = a.Get("hasta") != null ? (int)(a.Double("hasta", 0) * audio.SampleRate) : audio.Left.Length;
        string dir = a.Get("salida") ?? Path.GetFileNameWithoutExtension(file) + " - stems";
        Directory.CreateDirectory(dir);
        using var cancel = new CancellationTokenSource();
        double cancelAt = a.Double("cancelar", 0);
        if (cancelAt > 0) cancel.CancelAfter(TimeSpan.FromSeconds(cancelAt));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<StemSeparator.Stem> stems;
        try
        {
            stems = StemSeparator.Separate(audio.Left, audio.Right, audio.SampleRate, from, to - from,
                new(layout, quality, shifts, a.Flag("cpu"), model, only), ct: cancel.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"Cancelado a los {cancelAt:0.0} s: paró {sw.Elapsed.TotalSeconds - cancelAt:0.00} s después.");
            return 0;
        }
        sw.Stop();
        foreach (var s in stems) Wav.Write(Path.Combine(dir, s.Id + ".wav"), s.L, s.R, audio.SampleRate);
        double seconds = (to - from) / (double)audio.SampleRate;
        Console.WriteLine($"{stems.Count} stems ({string.Join(", ", stems.Select(s => s.Id))}) de {seconds:0.0} s de audio en {sw.Elapsed.TotalSeconds:0.0} s, en {(StemSeparator.LastOnGpu ? "la GPU" : "la CPU")}: {dir}");
        return 0;
    }

    /// <summary>The rows of comprobar-dsp.</summary>
    public static bool Check()
    {
        bool ok = true;
        void Row(string what, string got, string want, bool pass)
        {
            Console.WriteLine($"  {(pass ? "OK " : "MAL")} {what}: {got} (esperado {want})");
            ok &= pass;
        }

        double back = StemSeparator.TransformRoundTripDb();
        Row("Stems: la STFT devuelve el audio", $"{back:0.0} dB", "< -30 dB", back < -30);

        var p = new Project();
        var source = new Track { Name = "Mezcla", Video = false };
        source.Clips.Add(new Clip { MediaId = "m", GainDb = -3 });
        var next = new Track { Name = "Después" };
        p.Tracks.AddRange([source, next]);
        var made = new[] { "Voz", "Resto" }.Select(n => new AudioImport.Imported(n, $"media/{n}.wav", 1, Sr, 2)).ToList();
        string folder = AudioImport.AddStems(p, source.Id, source.Clips[0].Id, 2, made, "Mezcla · stems");
        var order = string.Join(",", p.Tracks.Select(t => t.Name));
        Row("Stems: pistas bajo el clip", order, "Mezcla,Voz,Resto,Después", order == "Mezcla,Voz,Resto,Después");
        Row("Stems: van en su carpeta", $"{p.Tracks.Count(t => t.FolderId == folder)}", "2", p.Tracks.Count(t => t.FolderId == folder) == 2);
        Row("Stems: la original queda en silencio", $"{source.Channel.Muted}", "True", source.Channel.Muted);
        Row("Stems: mismo lugar y ganancia", $"{p.Tracks[1].Clips[0].AtSec} s, {p.Tracks[1].Clips[0].GainDb} dB", "2 s, -3 dB",
            p.Tracks[1].Clips[0].AtSec == 2 && p.Tracks[1].Clips[0].GainDb == -3);

        // #146: what happens with the original, and what the advanced mode runs
        Project Fresh(out Track t)
        {
            var q = new Project();
            t = new Track { Name = "Mezcla", Video = false };
            t.Clips.Add(new Clip { MediaId = "m" });
            q.Tracks.Add(t);
            return q;
        }
        var keep = Fresh(out var kept);
        AudioImport.AddStems(keep, kept.Id, kept.Clips[0].Id, 0, made, "x", StemOriginal.Keep);
        Row("Stems: Dejar la original", $"mute {kept.Channel.Muted}, {kept.Clips.Count} clip", "mute False, 1 clip", !kept.Channel.Muted && kept.Clips.Count == 1);
        var remove = Fresh(out var removed);
        AudioImport.AddStems(remove, removed.Id, removed.Clips[0].Id, 0, made, "x", StemOriginal.Remove);
        Row("Stems: Quitar el clip", $"mute {removed.Channel.Muted}, {removed.Clips.Count} clips", "mute False, 0 clips", !removed.Channel.Muted && removed.Clips.Count == 0);
        var locked = Fresh(out var lockedTrack);
        lockedTrack.Clips[0].Locked = true;
        AudioImport.AddStems(locked, lockedTrack.Id, lockedTrack.Clips[0].Id, 0, made, "x", StemOriginal.Remove);
        Row("Stems: un clip bloqueado no se quita", $"mute {lockedTrack.Channel.Muted}, {lockedTrack.Clips.Count} clip", "mute True, 1 clip", lockedTrack.Channel.Muted && lockedTrack.Clips.Count == 1);
        string Files(StemSeparator.Settings s) => string.Join(",", StemSeparator.Files(s));
        var voice = Files(new(Model: StemSeparator.Model.Tuned, Only: ["vocals"]));
        Row("Stems: por stem, solo voz", voice, "htdemucs_ft_vocals.onnx", voice == "htdemucs_ft_vocals.onnx");
        var drumsBass = Files(new(Model: StemSeparator.Model.Tuned, Only: ["drums", "bass"]));
        Row("Stems: por stem, batería y bajo", drumsBass, "htdemucs_ft_drums.onnx,htdemucs_ft_bass.onnx", drumsBass == "htdemucs_ft_drums.onnx,htdemucs_ft_bass.onnx");
        var six = Files(new(Layout: StemSeparator.Layout.Two, Model: StemSeparator.Model.Six, Only: ["guitar"]));
        Row("Stems: el modelo manda sobre la cantidad", six, "htdemucs_6s.onnx", six == "htdemucs_6s.onnx");
        double one = StemSeparator.EstimateSec(new(Model: StemSeparator.Model.General, Shifts: 1), 60);
        double four = StemSeparator.EstimateSec(new(Model: StemSeparator.Model.General, Shifts: 4), 60);
        Row("Stems: el tiempo sigue a las pasadas", $"x{four / one:0.0}", "x4.0", Math.Abs(four / one - 4) < 1e-9);
        if (!StemSeparator.Available(StemSeparator.Layout.Four, StemSeparator.Quality.Fast))
        {
            Console.WriteLine("  Falta lib\\demucs: corré tools\\get-demucs.ps1 y compilá de nuevo (no se comprueba la separación).");
            return ok;
        }

        int n = Sr * 2;
        var l = new float[n];
        var r = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Sr;
            l[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 110 * t) * Math.Exp(-3 * (t % 0.5)));
            r[i] = l[i] + (float)(0.1 * Math.Sin(2 * Math.PI * 660 * t));
        }
        var stems = StemSeparator.Separate(l, r, Sr, 0, n, new(StemSeparator.Layout.Four, StemSeparator.Quality.Fast, 0, CpuOnly: true));
        Row("Stems: cuántos", $"{stems.Count}", "4", stems.Count == 4);
        Row("Stems: largo", $"{stems.Min(s => s.L.Length)}", $"{n}", stems.All(s => s.L.Length == n && s.R.Length == n));
        double worst = 0;
        for (int i = 0; i < n; i++)
        {
            worst = Math.Max(worst, Math.Abs(l[i] - stems.Sum(s => s.L[i])));
            worst = Math.Max(worst, Math.Abs(r[i] - stems.Sum(s => s.R[i])));
        }
        Row("Stems: suman la mezcla (48 kHz)", $"{worst:0.0000}", "< 0.02", worst < 0.02);
        return ok;
    }
}
