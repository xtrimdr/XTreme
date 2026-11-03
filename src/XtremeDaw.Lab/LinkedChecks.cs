using System.Diagnostics;
using System.Security.Cryptography;
using NAudio.Wave;
using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// Copying a linked take into the project and finding a file that moved (<see cref="LinkedMedia"/>):
/// <c>xtlab proyecto verificar-vinculados</c>, the commands that do the same on a real project, and
/// <c>proyecto cortar-copia</c>, a process that really dies in the middle of a copy or right after it, without saving.
/// </summary>
static class LinkedChecks
{
    /// <summary>Several of the copy's 1 MiB chunks, so it can die with part of the file written.</summary>
    const int LongSec = 30;
    /// <summary>How far the copy gets before the process kills itself.</summary>
    const double DieAt = 0.4;

    /// <summary>proyecto vinculados carpeta: the takes and where their files are.</summary>
    public static int List(string dir)
    {
        using var store = ProjectStore.Open(dir);
        foreach (var m in store.Project.Media)
            Console.WriteLine($"{m.Id} · {m.Name} · {(LinkedMedia.IsMissing(dir, m) ? "sin conexión" : m.Linked ? "vinculado" : "en el proyecto")} · {m.Audio}{(m.Video == null ? "" : " · " + m.Video)}");
        return 0;
    }

    /// <summary>proyecto copiar-vinculado carpeta medio.</summary>
    public static int Copy(string dir, Args a)
    {
        using var store = Writable(dir);
        var m = Take(store, a);
        var place = LinkedMedia.CopyIn(dir, m);
        LinkedMedia.Apply(store.Project, place);
        store.Save();
        Console.WriteLine($"{m.Name}: {m.Audio}{(m.Video == null ? "" : " · " + m.Video)}");
        return 0;
    }

    /// <summary>proyecto reencontrar carpeta medio --archivo X, or carpeta [medio] --buscar-en Y for all the takes that lack something.</summary>
    public static int Find(string dir, Args a)
    {
        using var store = Writable(dir);
        if (a.Get("archivo") is { } file)
        {
            var m = Take(store, a);
            LinkedMedia.Apply(store.Project, LinkedMedia.Pick(dir, m, Path.GetFullPath(file)));
            store.Save();
            Console.WriteLine($"{m.Name}: {m.Audio}");
            return 0;
        }
        string folder = a.Get("buscar-en") ?? throw new ArgumentException("Falta --archivo o --buscar-en.");
        var media = a.Positional.Count > 2 ? [Take(store, a)] : store.Project.Media;
        var found = LinkedMedia.Search(dir, media, Path.GetFullPath(folder));
        foreach (var place in found.Places) LinkedMedia.Apply(store.Project, place);
        store.Save();
        Console.WriteLine($"Encontré {found.Places.Count}; faltan {found.NotFound.Count}{(found.NotFound.Count > 0 ? ": " + string.Join(", ", found.NotFound) : "")}.");
        return found.NotFound.Count == 0 ? 0 : 1;
    }

    /// <summary>Child process: dies in the middle of the copy, or with <c>--al-final</c> once it is done, and saves nothing.</summary>
    public static int Die(string dir, Args a)
    {
        var store = Writable(dir);
        bool atEnd = a.Flag("al-final");
        LinkedMedia.CopyIn(dir, Take(store, a), f => { if (!atEnd && f >= DieAt) Process.GetCurrentProcess().Kill(); });
        Process.GetCurrentProcess().Kill();
        return 3;
    }

    static ProjectStore Writable(string dir)
    {
        var store = ProjectStore.Open(dir);
        if (store.ReadOnly) throw new IOException(store.ReadOnlyReason);
        return store;
    }

    static Media Take(ProjectStore store, Args a)
    {
        string id = a.Positional.ElementAtOrDefault(2) ?? throw new ArgumentException("Falta el medio.");
        return store.Project.Media.Find(m => m.Id == id || m.Name == id) ?? throw new ArgumentException($"No hay un medio {id}.");
    }

    static void Tone(string path, double seconds, int seed = 1)
    {
        var bytes = new byte[(int)(seconds * 48000) * 2];
        new Random(seed).NextBytes(bytes);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new WaveFileWriter(path, new WaveFormat(48000, 16, 1));
        w.Write(bytes, 0, bytes.Length);
    }

    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public static int Run()
    {
        int n = 0;
        void Check(bool ok, string text)
        {
            if (!ok) throw new InvalidOperationException($"Vinculados: {text}");
            n++;
        }
        bool Throws<T>(Action act) where T : Exception
        {
            try { act(); return false; }
            catch (T) { return true; }
        }
        string tmp = Path.Combine(Path.GetTempPath(), "xt-vinculados-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            string proj = Path.Combine(tmp, "proyecto");
            Directory.CreateDirectory(proj);
            string Inside(params string[] parts) => Path.Combine([proj, .. parts]);
            string[] MediaFiles() => Directory.GetFiles(Inside("media"));

            // copying
            string src = Path.Combine(tmp, "samples", "Base (v2).wav");
            Tone(src, 3);
            string srcHash = Hash(src);
            var p = new Project { Name = "Tema" };
            var track = AudioImport.AddClip(p, AudioImport.LinkIn(proj, src), 0);
            var m = p.FindMedia(track.Clips[0].MediaId)!;
            using (var store = ProjectStore.Create(proj, p)) { }
            Check(m.AudioBytes == new FileInfo(src).Length && LinkedMedia.CanCopy(proj, m), "el vínculo recuerda el tamaño y se puede copiar");

            var copied = LinkedMedia.CopyIn(proj, m);
            Check(m.Linked && m.Audio == src, "copiar no toca el proyecto hasta aplicarlo");
            Check(copied.Audio == "media/Base (v2).wav" && Hash(Inside("media", "Base (v2).wav")) == srcHash, "la copia entra a media/ igual al original");
            Check(Hash(src) == srcHash, "el archivo de afuera no se toca");
            Check(!MediaFiles().Any(f => f.EndsWith(AudioImport.PartialCopy)), "no deja temporales");

            var undo = new UndoManager();
            undo.Checkpoint(p);
            Check(LinkedMedia.Apply(p, copied) && !m.Linked && m.Audio == copied.Audio, "aplicado, la toma queda adentro");
            Check(!LinkedMedia.Apply(p, copied), "un lugar viejo no pisa la toma que ya cambió");
            Check(undo.Undo(p) && m.Linked && m.Audio == src && m.AudioBytes > 0, "deshacer la deja vinculada otra vez");
            Check(undo.Redo(p) && !m.Linked && m.Audio == copied.Audio, "rehacer vuelve a la copia");
            var later = new Media { Name = "Después", Audio = "media/despues.wav" };
            undo.Checkpoint(p);
            p.Media.Add(later);
            Check(undo.Undo(p) && p.FindMedia(later.Id)?.Audio == later.Audio, "una toma nueva no se va con un deshacer");
            Check(File.Exists(src), "nada borra el archivo de afuera");

            var kept = ProjectLibrary.Clone(p);
            Check(kept.FindMedia(m.Id)?.AudioBytes == m.AudioBytes, "el tamaño sobrevive al guardado");

            // both files, and one file that is both
            string clip = Path.Combine(tmp, "videos", "Toma.wav"), picture = Path.Combine(tmp, "videos", "Toma.mp4");
            Tone(clip, 2, 2);
            File.WriteAllBytes(picture, new byte[5000]);
            var both = new Media { Name = "Toma", Audio = clip, Video = picture, DurationSec = 2, AudioBytes = new FileInfo(clip).Length, VideoBytes = 5000 };
            var place = LinkedMedia.CopyIn(proj, both);
            Check(place.Audio == "media/Toma.wav" && place.Video == "media/Toma.mp4" && File.Exists(picture), "audio e imagen entran juntos, con el mismo nombre");
            var half = new Media { Name = "Mitad", Audio = "media/Toma.wav", Video = picture };
            place = LinkedMedia.CopyIn(proj, half);
            Check(place.Audio == half.Audio && place.Video == "media/Toma (2).mp4", "con el sonido adentro, entra solo la imagen");
            var one = new Media { Name = "Uno", Audio = picture, Video = picture };
            int count = MediaFiles().Length;
            place = LinkedMedia.CopyIn(proj, one);
            Check(place.Audio == place.Video && MediaFiles().Length == count + 1, "un archivo que es audio e imagen se copia una vez");

            // moved
            var lost = new Project { Name = "Movido" };
            string moved = Path.Combine(tmp, "antes", "Riff.wav");
            Tone(moved, 3, 3);
            string movedHash = Hash(moved);
            var lostTrack = AudioImport.AddClip(lost, AudioImport.LinkIn(proj, moved), 0);
            var riff = lost.FindMedia(lostTrack.Clips[0].MediaId)!;
            string now = Path.Combine(tmp, "despues", "hondo", "Riff.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(now)!);
            File.Move(moved, now);
            Check(LinkedMedia.IsMissing(proj, riff) && !LinkedMedia.CanCopy(proj, riff) && LinkedMedia.Wanted(proj, riff) == "Riff.wav", "un archivo movido queda sin conexión y no se puede copiar");
            Check(Throws<FileNotFoundException>(() => LinkedMedia.CopyIn(proj, riff)), "copiarlo avisa");

            // by hand
            string renamed = Path.Combine(tmp, "elegido", "Otro nombre.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(renamed)!);
            File.Copy(now, renamed);
            var undoLost = new UndoManager();
            undoLost.Checkpoint(lost);
            var picked = LinkedMedia.Pick(proj, riff, renamed);
            Check(riff.Audio == moved, "elegirlo no toca el proyecto hasta aplicarlo");
            Check(LinkedMedia.Apply(lost, picked) && !LinkedMedia.IsMissing(proj, riff) && riff.Audio == renamed, "por el diálogo vale otro nombre si es el mismo sonido");
            Check(undoLost.Undo(lost) && riff.Audio == moved && LinkedMedia.IsMissing(proj, riff), "deshacer vuelve a sin conexión");
            Check(undoLost.Redo(lost) && riff.Audio == renamed, "rehacer lo encuentra de nuevo");
            undoLost.Undo(lost);
            string shorter = Path.Combine(tmp, "elegido", "Corto.wav");
            Tone(shorter, 1, 4);
            Check(Throws<InvalidDataException>(() => LinkedMedia.Pick(proj, riff, shorter)) && riff.Audio == moved, "otra duración no es el mismo archivo");
            string note = Path.Combine(tmp, "elegido", "nota.txt");
            File.WriteAllText(note, "x");
            Check(Throws<InvalidDataException>(() => LinkedMedia.Pick(proj, riff, note)), "lo que no es audio avisa");
            Check(LinkedMedia.Pick(proj, m, src).Audio == m.Audio, "una toma que no falta queda igual");
            string insideCopy = Inside("media", "movida", "Riff.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(insideCopy)!);
            File.Copy(now, insideCopy);
            Check(LinkedMedia.Pick(proj, riff, insideCopy).Audio == "media/movida/Riff.wav", "un archivo dentro del proyecto queda con ruta relativa");
            File.Delete(insideCopy);

            // by name and size
            string look = Path.Combine(tmp, "despues");
            string decoy = Path.Combine(look, "Riff.wav");
            Tone(decoy, 3.5, 5);
            var result = LinkedMedia.Search(proj, [riff], look);
            Check(result.Places.Count == 1 && result.Places[0].Audio == now && result.NotFound.Count == 0, "buscando, el tamaño separa dos archivos del mismo nombre");
            Check(result.Places[0].AudioBytes == riff.AudioBytes && riff.Audio == moved, "buscar no toca el proyecto hasta aplicarlo");
            Check(LinkedMedia.Search(proj, [riff], look, deep: false) is { Places.Count: 0, NotFound.Count: 1 }, "un nombre sin el tamaño no se toma");

            var old = new Media { Name = "Viejo", Audio = moved, DurationSec = 3 };
            result = LinkedMedia.Search(proj, [old], look);
            Check(result.Places.Count == 1 && result.Places[0].Audio == now, "un vínculo sin tamaño va por nombre y duración");
            string twin = Path.Combine(tmp, "despues", "otra", "Riff.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(twin)!);
            File.Copy(now, twin);
            result = LinkedMedia.Search(proj, [old], look);
            Check(result is { Places.Count: 0, Ambiguous.Count: 1 }, "sin tamaño, entre dos que coinciden no adivina");
            result = LinkedMedia.Search(proj, [riff], look);
            Check(result.Places.Count == 1 && result.Places[0].Audio == now, "con el mismo tamaño toma el más cerca de la carpeta");

            string lostPic = Path.Combine(tmp, "antes", "Cam.mp4"), lostSound = Path.Combine(tmp, "antes", "Cam.wav");
            var cam = new Media { Name = "Cam", Audio = lostSound, Video = lostPic, DurationSec = 2, VideoBytes = 7000 };
            string foundSound = Path.Combine(tmp, "nuevo", "Cam.wav"), foundPic = Path.Combine(tmp, "nuevo", "Cam.mp4");
            Tone(foundSound, 2, 6);
            File.WriteAllBytes(foundPic, new byte[7000]);
            var duo = LinkedMedia.Pick(proj, cam, foundSound);
            Check(duo.Audio == foundSound && duo.Video == foundPic && duo.OldAudio == lostSound, "elegido el sonido, la imagen se busca al lado");
            var folder = LinkedMedia.Search(proj, [riff, cam, m], Path.Combine(tmp, "nuevo"));
            Check(folder.Places.Count == 1 && folder.Places[0].MediaId == cam.Id && folder.NotFound.SequenceEqual(["Riff"]), "una carpeta resuelve lo que tiene y dice lo que falta");
            var gone = new Media { Name = "Adentro", Audio = "media/se-fue.wav", DurationSec = 3 };
            string outside = Path.Combine(tmp, "rescate", "se-fue.wav");
            Tone(outside, 3, 7);
            Check(LinkedMedia.Search(proj, [gone], Path.GetDirectoryName(outside)!).Places[0].Audio == outside, "también vuelve una toma que estaba en media/");
            Check(Hash(now) == movedHash && File.Exists(renamed), "nada mueve ni borra lo de afuera");

            // the program dies in the middle
            string dying = Path.Combine(tmp, "muere");
            string big = Path.Combine(tmp, "largos", "Largo.wav");
            Tone(big, LongSec, 8);
            string bigHash = Hash(big);
            var dp = new Project { Name = "Muere" };
            var dt = AudioImport.AddClip(dp, AudioImport.LinkIn(dying, big), 0);
            string id = dt.Clips[0].MediaId;
            Directory.CreateDirectory(dying);
            using (ProjectStore.Create(dying, dp)) { }
            string lockMedia = Path.Combine(dying, ProjectStore.MediaDir);
            Directory.CreateDirectory(lockMedia);

            Child(dying, id);
            var partial = Directory.GetFiles(lockMedia);
            Check(partial.Length == 1 && partial[0].EndsWith(AudioImport.PartialCopy) && new FileInfo(partial[0]).Length < new FileInfo(big).Length, "muere a mitad de copia: solo queda el temporal");
            using (var store = ProjectStore.Open(dying))
            {
                var dm = store.Project.FindMedia(id)!;
                Check(dm.Audio == big && store.Project.Media.Count == 1, "al abrir la toma sigue vinculada, sin nada adoptado");
                Check(Directory.GetFiles(lockMedia).Length == 0 && Hash(big) == bigHash, "el temporal se limpia y lo de afuera queda igual");
            }

            Child(dying, id, "--al-final");
            using (var store = ProjectStore.Open(dying))
            {
                var dm = store.Project.FindMedia(id)!;
                var extra = store.Project.Media.Where(x => x.Id != id).ToList();
                Check(dm.Audio == big && extra.Count == 1 && Hash(Path.Combine(dying, extra[0].Audio!)) == bigHash, "muere con la copia hecha y sin guardar: sigue vinculada y la copia entera vuelve como toma sin usar");
                var again = LinkedMedia.CopyIn(dying, dm);
                Check(LinkedMedia.Apply(store.Project, again) && Hash(Path.Combine(dying, dm.Audio!)) == bigHash, "después se puede copiar igual");
                store.Save();
            }
            using (var store = ProjectStore.Open(dying))
                Check(!store.Project.FindMedia(id)!.Linked && Hash(big) == bigHash, "guardado queda adentro, y el original sigue en su lugar");
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (IOException) { }
        }
        Console.WriteLine($"Vinculados: {n} comprobaciones bien.");
        return 0;
    }

    /// <summary>Runs <c>proyecto cortar-copia</c> as a real process, which has to die by its own hand.</summary>
    static void Child(string dir, string mediaId, params string[] extra)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "xtlab.exe");
        var start = new ProcessStartInfo(exe, ["proyecto", "cortar-copia", dir, mediaId, .. extra]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var child = Process.Start(start)!;
        string err = child.StandardError.ReadToEnd();
        child.WaitForExit();
        if (child.ExitCode is 0 or 2 or 3) throw new InvalidOperationException($"cortar-copia no murió como debía ({child.ExitCode}): {err}");
    }
}
