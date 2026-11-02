using XtremeDaw.Audio;
using XtremeDaw.Projects;
using NAudio.Wave;

namespace XtremeDaw.Lab;

/// <summary>
/// The Biblioteca's sample folders from the console: <c>wtlab samples listar carpeta [--buscar "kick 808"]</c>,
/// <c>wtlab samples leer archivo</c>, and <see cref="Check"/> (part of <c>comprobar-dsp</c>), which verifies the search,
/// the tree, the index and the preview voice's fades without an audio interface. What needs FFmpeg is skipped without it.
/// </summary>
static class SampleChecks
{
    public static int Run(Args a)
    {
        string sub = a.Positional.FirstOrDefault() ?? "";
        string target = a.Positional.Skip(1).FirstOrDefault() ?? throw new ArgumentException("Falta la carpeta o el archivo.");
        if (sub == "listar")
        {
            var files = SampleLibrary.Walk(Path.GetFullPath(target)).ToList();
            var hits = SampleLibrary.Search(files, a.Get("buscar") ?? "");
            foreach (var f in hits) Console.WriteLine($"{(f.IsVideo ? "video" : "audio")}  {f.Size,12}  {Path.GetRelativePath(target, f.Path)}");
            Console.WriteLine($"{hits.Count} de {files.Count} archivos");
            return 0;
        }
        if (sub == "leer")
        {
            var audio = MediaDecoder.Load(target, AudioLoader.DefaultSampleRate);
            Console.WriteLine($"{Path.GetFileName(target)}: {audio.DurationSec:0.000} s, {(audio.Converted ? "por FFmpeg" : "nativo")}, en el encabezado {MediaDecoder.Duration(target):0.000} s");
            return 0;
        }
        throw new ArgumentException("Usá: wtlab samples listar carpeta [--buscar texto] | leer archivo");
    }

    public static bool Check()
    {
        try
        {
            Console.WriteLine($"Samples: {Verify()} comprobaciones OK");
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"FALLA: {e.Message}");
            return false;
        }
    }

    static int Verify()
    {
        int n = 0;
        void Ok(bool ok, string text)
        {
            if (!ok) throw new InvalidOperationException($"Samples: {text}");
            n++;
        }
        string tmp = Path.Combine(Path.GetTempPath(), "gc-samples-" + Guid.NewGuid().ToString("N")[..8]);
        string root = Path.Combine(tmp, "Pack");
        string? before = AppConfig.DirectoryOverride;
        Directory.CreateDirectory(Path.Combine(root, "Baterías", "Kicks"));
        try
        {
            AppConfig.DirectoryOverride = Path.Combine(tmp, "config");
            string kick = Path.Combine(root, "Baterías", "Kicks", "Kick_Seco-10.wav");
            string kick2 = Path.Combine(root, "Baterías", "Kicks", "Kick_Seco-2.wav");
            Tone(kick, 0.5);
            Tone(kick2, 0.25);
            File.WriteAllText(Path.Combine(root, "léeme.txt"), "no es audio");
            File.WriteAllText(Path.Combine(root, "roto.wav"), "tampoco");

            var files = SampleLibrary.Walk(root).ToList();
            Ok(files.Count == 3, "solo toma archivos de audio o video");
            Ok(SampleLibrary.Search(files, "seco kick BATERIAS").Count == 2, "busca varias palabras, en cualquier orden, sin acentos ni mayúsculas");
            Ok(SampleLibrary.Search(files, "kick 2").Single().Path == kick2, "los números también se buscan");
            Ok(SampleLibrary.Search(files, "snare").Count == 0, "lo que no está no aparece");

            var tree = SampleLibrary.Tree([new SampleFolder(root, "Pack")], files);
            Ok(tree.Single().Count == 3 && tree[0].Folders.Single().Folders.Single().Files[0].Path == kick2, "el árbol cuenta y ordena los números por valor");
            var closed = SampleLibrary.Rows(tree, new HashSet<string>());
            var open = SampleLibrary.Rows(tree, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root, Path.Combine(root, "Baterías"), Path.Combine(root, "Baterías", "Kicks") });
            Ok(closed.Count == 1 && open.Count == 6 && open[^1].File != null, "solo se ve lo que está abierto");

            SampleLibrary.AnalyzeOne(files.First(f => f.Path == kick));
            var analyzed = files.First(f => f.Path == kick);
            Ok(analyzed.DurationSec is > 0.49 and < 0.51 && analyzed.Peaks is { Length: SampleLibrary.PeakBins }, "el análisis da largo y onda");
            var broken = files.First(f => f.Name == "roto.wav");
            SampleLibrary.AnalyzeOne(broken);
            Ok(broken.Broken, "un archivo roto queda marcado y no rompe nada");

            using (var lib = new SampleLibrary())
            {
                lib.Open();
                lib.AddFolder(root);
                for (int i = 0; i < 500 && (lib.Scanning || lib.Files.Count == 0); i++) Thread.Sleep(10);
                Ok(lib.Files.Count == 3, "agregar una carpeta la lee");
                var first = lib.Files.First(f => f.Path == kick);
                SampleLibrary.AnalyzeOne(first);
                lib.ToggleFavorite(first);
                lib.RenameFolder(root, "Baterías");
                lib.Scan();
                File.Delete(kick2);
                lib.Scan();
                for (int i = 0; i < 500 && (lib.Scanning || lib.Files.Count != 2); i++) Thread.Sleep(10);
                Ok(lib.Files.Count == 2, "volver a leer saca lo que se borró");
            }
            using (var lib = new SampleLibrary())
            {
                lib.Open();
                for (int i = 0; i < 500 && lib.Scanning; i++) Thread.Sleep(10);
                var kept = lib.Files.FirstOrDefault(f => f.Path == kick);
                Ok(kept is { DurationSec: > 0.4, Peaks: not null } && lib.IsFavorite(kept) && lib.Folders.Single().Name == "Baterías", "el índice guarda el análisis, los favoritos y el nombre");
            }
            // with the library closed: moving a folder someone is watching can be refused by Windows
            Directory.Move(root, root + "-fuera");
            using (var lib = new SampleLibrary())
            {
                lib.Open();
                for (int i = 0; i < 500 && lib.Scanning; i++) Thread.Sleep(10);
                Ok(lib.Files.Count == 2, "una carpeta que no está conserva sus archivos");
            }
            Directory.Move(root + "-fuera", root);

            Voice(Ok);
            if (FfmpegInfo.Probe() != null) Ffmpeg(tmp, kick, Ok);
        }
        finally
        {
            AppConfig.DirectoryOverride = before;
            try { Directory.Delete(tmp, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return n;
    }

    /// <summary>The preview voice run in blocks like the audio thread: no jump anywhere, and it ends by itself.</summary>
    static void Voice(Action<bool, string> ok)
    {
        const int Block = 64, Sr = AudioLoader.DefaultSampleRate;
        var dc = new float[Sr / 2];
        Array.Fill(dc, 0.8f);
        var voice = new SampleVoice { Gain = 1 };
        var left = new float[Block];
        var right = new float[Block];
        float last = 0, jump = 0;
        void Run(int blocks)
        {
            for (int b = 0; b < blocks; b++)
            {
                Array.Clear(left);
                Array.Clear(right);
                voice.Mix(left, right, Block);
                for (int i = 0; i < Block; i++)
                {
                    jump = Math.Max(jump, Math.Abs(left[i] - last));
                    last = left[i];
                }
            }
        }
        voice.Play(dc, dc);
        ok(voice.Active, "un sonido pedido cuenta como activo antes de empezar");
        Run(100);
        double pos = voice.PositionSec;
        ok(pos is > 0.13 and < 0.14 && last == 0.8f, "suena y avanza");
        voice.Seek(0.4);
        Run(10);
        ok(voice.PositionSec is > 0.4 and < 0.42, "salta");
        voice.Paused = true;
        Run(10);
        ok(last == 0, "en pausa se calla");
        double held = voice.PositionSec;
        Run(10);
        ok(voice.PositionSec == held, "en pausa no avanza");
        voice.Paused = false;
        Run(5);
        voice.Stop();
        Run(10);
        ok(!voice.Active && last == 0, "parar se termina solo");
        ok(jump < 0.8f / 200, $"sin clics al empezar, saltar, pausar y parar (salto de {jump:0.00000})");

        jump = 0;
        voice.Loop = true;
        voice.Play(dc, dc);
        Run(Sr / 2 / Block * 3);
        ok(voice.Active && jump < 0.8f / 200, "el loop no corta ni hace clic en la unión");
        voice.Loop = false;
        Run(Sr / Block);
        ok(!voice.Active && last == 0, "sin loop termina al final del sonido");
    }

    static void Ffmpeg(string tmp, string wav, Action<bool, string> ok)
    {
        string opus = Path.Combine(tmp, "tono.opus"), video = Path.Combine(tmp, "toma.mp4"), mute = Path.Combine(tmp, "muda.mp4");
        Tool("-i", wav, opus);
        Tool("-f", "lavfi", "-i", "sine=frequency=330:duration=1", "-f", "lavfi", "-i", "color=c=black:s=64x64:d=1", "-shortest", video);
        Tool("-f", "lavfi", "-i", "color=c=black:s=64x64:d=1", mute);
        var a = MediaDecoder.Load(opus, AudioLoader.DefaultSampleRate);
        ok(a.Converted && a.SampleRate == AudioLoader.DefaultSampleRate && a.DurationSec is > 0.45 and < 0.6, "FFmpeg lee lo que NAudio no");
        ok(MediaDecoder.Duration(opus) is > 0.45 and < 0.6, "el largo sale sin decodificar");
        var v = MediaDecoder.Load(video, AudioLoader.DefaultSampleRate);
        ok(v.Converted && v.DurationSec is > 0.9 and < 1.1, "el sonido de un video");
        ok(MediaDecoder.Duration(mute) == 0, "un video sin audio se reconoce");
        bool refused = false;
        try { MediaDecoder.Load(mute); } catch (InvalidDataException) { refused = true; }
        ok(refused, "un video sin audio se rechaza con un mensaje");

        string proj = Path.Combine(tmp, "proyecto");
        var read = AudioImport.Read(video, AudioLoader.DefaultSampleRate);
        var file = AudioImport.CopyVideoIn(proj, video, AudioImport.CopyIn(proj, video, read));
        ok(file.RelativePath.EndsWith(".wav") && file.Video!.EndsWith(".mp4") && File.Exists(Path.Combine(proj, file.RelativePath)) && File.Exists(Path.Combine(proj, file.Video)),
            "un video entra como WAV más su imagen, sin depender de FFmpeg al abrir");
        var p = new Project();
        var track = AudioImport.AddClip(p, file, 2);
        ok(track.Video && p.FindMedia(track.Clips[0].MediaId) is { Video: not null, VideoOffsetMs: 0 }, "con imagen la pista lleva panel, sin la latencia de una cámara");
        var over = AudioImport.AddClip(p, file with { Video = null }, 2.5, track.Id, fit: true);
        var after = AudioImport.AddClip(p, file with { Video = null }, 4, track.Id, fit: true);
        ok(over != track && after == track && track.Clips.Count == 2, "soltar sobre una pista usa el lugar libre y nunca tapa lo que hay");

        var media = p.FindMedia(track.Clips[0].MediaId)!;
        string first = ClipEditing.PlaceMedia(p, media.Id, 3, track.Id);
        ok(first != track.Id && p.Tracks.Find(t => t.Id == first) is { Video: true }, "una toma arrastrada sobre algo ocupado va a una pista nueva, con su panel");
        ok(ClipEditing.PlaceMedia(p, media.Id, 7, track.Id) == track.Id && ClipEditing.Window(track.Clips[^1], media).Start == 7, "cae donde se soltó, y la misma toma se puede usar varias veces");
        var synced = new Media { Name = "Toma", Audio = "media/t.wav", DurationSec = 2, SyncMs = 100 };
        p.Media.Add(synced);
        string placed = ClipEditing.PlaceMedia(p, synced.Id, 1, null);
        var taken = p.Tracks.Find(t => t.Id == placed)!;
        ok(Math.Abs(ClipEditing.Window(taken.Clips[0], synced).Start - 1) < 0.001, "la latencia de una grabación no corre dónde cae");
    }

    static void Tool(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in new[] { "-y", "-v", "error", "-nostdin" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        _ = p.StandardOutput.ReadToEndAsync();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"Samples: ffmpeg falló: {err}");
    }

    static void Tone(string path, double sec)
    {
        int frames = (int)(sec * 44100);
        using var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(44100, 1));
        var buf = new float[frames];
        for (int i = 0; i < frames; i++) buf[i] = 0.5f * MathF.Sin(i * 0.05f);
        w.WriteSamples(buf, 0, frames);
    }
}
