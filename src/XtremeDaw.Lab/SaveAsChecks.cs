using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// xtlab proyecto verificar-guardar-como: the .xtp file and "Guardar como", in a temporary folder. An old
/// xtreme_project.json or a WildTrack guitarcam.json becomes Name.xtp on open, a rename renames the file, a picked .xtp opens its folder, a damaged
/// .xtp comes back from its .bak, and Save As lands in a new folder (or the empty one picked) with its takes, leaves
/// linked takes linked, refuses a taken folder or one inside the project, and never leaves a half copy.
/// </summary>
static class SaveAsChecks
{
    public static int Run()
    {
        int failed = 0;
        void Check(string what, bool ok)
        {
            Console.WriteLine($"{(ok ? "ok   " : "FALLA")} {what}");
            if (!ok) failed++;
        }
        static bool Throws(Action a)
        {
            try { a(); return false; } catch (IOException) { return true; }
        }
        string tmp = Path.Combine(Path.GetTempPath(), $"xtlab-guardar-{Guid.NewGuid():N}");
        string root = Path.Combine(tmp, "proyectos"), src = Path.Combine(root, "Tema"), elsewhere = Path.Combine(tmp, "afuera");
        string? savedData = AppConfig.DirectoryOverride, savedRoot = ProjectLibrary.RootOverride;
        AppConfig.DirectoryOverride = Path.Combine(tmp, "datos");
        ProjectLibrary.RootOverride = root;
        try
        {
            Directory.CreateDirectory(Path.Combine(src, ProjectStore.MediaDir));
            Directory.CreateDirectory(elsewhere);
            File.WriteAllBytes(Path.Combine(src, "media", "toma1.wav"), new byte[5000]);
            string outsideFile = Path.Combine(elsewhere, "base.wav");
            File.WriteAllBytes(outsideFile, new byte[4000]);
            var inside = new Media { Name = "toma1", Audio = "media/toma1.wav" };
            var outside = new Media { Name = "base", Kind = "import", Audio = outsideFile };
            var p = new Project { Name = "Tema", Media = [inside, outside] };
            p.Tracks.Add(new Track { Name = "Guitarra", Clips = [new Clip { MediaId = inside.Id }, new Clip { MediaId = outside.Id, AtSec = 10 }] });
            p.Exports.Add(new ExportRecord { File = "exports/video.mp4" });
            using (var s = ProjectStore.Create(src, p)) s.Save(); // a second save leaves a .bak
            string xtp = Path.Combine(src, "Tema.xtp");
            Check("un proyecto nuevo es Tema.xtp", File.Exists(xtp) && ProjectStore.FindFile(src) == xtp);

            // the format before .xtp
            File.Move(xtp, Path.Combine(src, ProjectStore.OldFileName));
            File.Move(xtp + ".bak", Path.Combine(src, ProjectStore.OldFileName + ".bak"));
            Check("un xtreme_project.json se reconoce", ProjectStore.IsProject(src));
            using (ProjectStore.Open(src)) { }
            Check("al abrirlo pasa a Tema.xtp, con su .bak",
                File.Exists(xtp) && File.Exists(xtp + ".bak") && !File.Exists(Path.Combine(src, ProjectStore.OldFileName)));

            // a WildTrack project: guitarcam.json, its own format id, and the lock it left if it crashed
            string wild = Path.Combine(tmp, "WildTrack Proyectos", "Viejo");
            Directory.CreateDirectory(Path.Combine(wild, ProjectStore.MediaDir));
            File.WriteAllBytes(Path.Combine(wild, "media", "toma1.wav"), new byte[5000]);
            var wp = new Project { Name = "Viejo", Media = [new Media { Name = "toma1", Audio = "media/toma1.wav" }] };
            wp.Tracks.Add(new Track { Name = "Guitarra", Clips = [new Clip { MediaId = wp.Media[0].Id }] });
            string wildJson = System.Text.Json.JsonSerializer.Serialize(wp, ProjectStore.Json).Replace($"\"{Project.FormatId}\"", $"\"{Project.OldFormatId}\"");
            File.WriteAllText(Path.Combine(wild, ProjectStore.WildTrackFileName), wildJson);
            File.WriteAllText(Path.Combine(wild, "guitarcam.lock"), "");
            Check("un proyecto de WildTrack se reconoce y se elige por su guitarcam.json",
                wildJson.Contains(Project.OldFormatId) && ProjectStore.IsProject(wild)
                && ProjectLibrary.Inspect(Path.Combine(wild, ProjectStore.WildTrackFileName)).Kind == ProjectLibrary.PickedKind.Project);
            using (var s = ProjectStore.Open(wild))
                Check("abre editable, con su toma, y pasa a Viejo.xtp",
                    !s.ReadOnly && s.Project.Tracks[0].Clips.Count == 1 && s.FilePath == Path.Combine(wild, "Viejo.xtp")
                    && !File.Exists(Path.Combine(wild, ProjectStore.WildTrackFileName)));
            Check("guardado como proyecto de XTreme", File.ReadAllText(Path.Combine(wild, "Viejo.xtp")).Contains($"\"{Project.FormatId}\""));

            Check("elegir el .xtp abre su carpeta",
                ProjectLibrary.Inspect(xtp) is { Kind: ProjectLibrary.PickedKind.Project } k && ProjectLibrary.SamePath(k.Dir, src));

            ProjectLibrary.Rename(src, "Tema nuevo", renameFolder: false);
            string renamed = Path.Combine(src, "Tema nuevo.xtp");
            Check("renombrar renombra el archivo", File.Exists(renamed) && !File.Exists(xtp) && ProjectStore.FindFile(src) == renamed);

            File.WriteAllText(renamed, "{ roto");
            using (var s = ProjectStore.Open(src))
                Check("un .xtp dañado vuelve desde su .bak con el mismo nombre",
                    s.Project.Name == "Tema nuevo" && s.FilePath == renamed && s.Notices.Count > 0 && !s.ReadOnly);
            Check("el dañado queda aparte y no se toma por el proyecto",
                Directory.GetFiles(src, "*.json").Length == 1 && ProjectStore.FindFile(src) == renamed);

            // Save As next to other files: a new folder named like the file
            string dest = Path.Combine(tmp, "destino");
            Directory.CreateDirectory(dest);
            File.WriteAllText(Path.Combine(dest, "otra cosa.txt"), "x");
            string a = ProjectLibrary.SaveAs(src, Path.Combine(dest, "Copia.xtp"));
            Check("guardar como en una carpeta con cosas crea Copia\\Copia.xtp: " + a,
                ProjectLibrary.SamePath(a, Path.Combine(dest, "Copia")) && File.Exists(Path.Combine(a, "Copia.xtp")));
            using (var s = ProjectStore.Open(a))
            {
                var m = s.Project.Media;
                Check("la copia trae su toma adentro y deja vinculada la de afuera",
                    File.Exists(s.Resolve(inside.Audio)) && m.Single(x => x.Id == outside.Id).Audio == outsideFile);
                Check("la copia se llama como el archivo, sin exportaciones y con otro id",
                    s.Project.Name == "Copia" && s.Project.Exports.Count == 0 && s.Project.Id != p.Id
                    && s.Project.Log.Any(l => l.Type == "session-saved-as"));
            }
            Check("el original no se tocó", File.ReadAllText(renamed).Contains("Tema nuevo") && Directory.GetFiles(src, "*.xtp").Length == 1);

            // Save As into an empty folder: that folder is the project
            string empty = Path.Combine(tmp, "vacia");
            Directory.CreateDirectory(empty);
            string b = ProjectLibrary.SaveAs(src, Path.Combine(empty, "Otra.xtp"));
            Check("guardar como en una carpeta vacía la usa a ella", ProjectLibrary.SamePath(b, empty) && File.Exists(Path.Combine(empty, "Otra.xtp")));

            Check("una carpeta que ya existe se rechaza", Throws(() => ProjectLibrary.SaveAs(src, Path.Combine(dest, "Copia.xtp"))));
            Check("dentro del mismo proyecto se rechaza", Throws(() => ProjectLibrary.SaveAs(src, Path.Combine(src, "exports", "Dentro.xtp"))));
            Check("sin carpetas a medio guardar",
                !Directory.EnumerateDirectories(dest, ".guardando-*").Any() && !Directory.EnumerateDirectories(tmp, ".guardando-*").Any());
        }
        finally
        {
            AppConfig.DirectoryOverride = savedData;
            ProjectLibrary.RootOverride = savedRoot;
            try { Directory.Delete(tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine(failed == 0 ? "Todo bien." : $"{failed} comprobaciones fallaron.");
        return failed == 0 ? 0 : 3;
    }
}
