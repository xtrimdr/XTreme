using XtremeDaw.Projects;

namespace XtremeDaw.Lab;

/// <summary>
/// xtlab proyecto verificar-sin-guardar: the project without a place (<see cref="UntitledProjects"/>), in a temporary
/// data folder. A new one lives under sin-guardar\ and stays off the recent list; at the next start an empty one is
/// deleted and one with a take is found again (the newest); Guardar como takes it to its place with its take.
/// </summary>
static class UntitledChecks
{
    public static int Run()
    {
        int failed = 0;
        void Check(string what, bool ok)
        {
            Console.WriteLine($"{(ok ? "ok   " : "FALLA")} {what}");
            if (!ok) failed++;
        }
        string tmp = Path.Combine(Path.GetTempPath(), $"xtlab-sin-guardar-{Guid.NewGuid():N}");
        string? savedData = AppConfig.DirectoryOverride, savedRoot = ProjectLibrary.RootOverride;
        AppConfig.DirectoryOverride = Path.Combine(tmp, "datos");
        ProjectLibrary.RootOverride = Path.Combine(tmp, "proyectos");
        try
        {
            string empty = UntitledProjects.Create("audio");
            Check("se crea en sin-guardar y es un proyecto", UntitledProjects.Is(empty) && ProjectStore.IsProject(empty));
            Check("no entra en la lista de recientes", !RecentProjects.Load().Any(e => ProjectLibrary.SamePath(e.Dir, empty)));
            Thread.Sleep(5); // folders are named by the time
            string full = UntitledProjects.Create("audio");
            using (var st = ProjectStore.Open(full))
            {
                Directory.CreateDirectory(Path.Combine(full, "media"));
                File.WriteAllBytes(Path.Combine(full, "media", "toma1.wav"), new byte[4000]);
                var m = new Media { Name = "toma1", Audio = "media/toma1.wav" };
                st.Project.Media.Add(m);
                st.Project.Tracks.Add(new Track { Name = "Guitarra", Clips = [new Clip { MediaId = m.Id }] });
                st.Save();
            }
            using (var st = ProjectStore.Open(full))
                Check("uno con contenido lo tiene", UntitledProjects.HasContent(st.Project));
            string? left = UntitledProjects.Leftover();
            Check("al arrancar se encuentra el que tiene una toma", left != null && ProjectLibrary.SamePath(left, full));
            Check("y el vacío se borra", !Directory.Exists(empty));
            Check("una carpeta de afuera no cuenta como sin guardar", !UntitledProjects.Is(Path.Combine(tmp, "proyectos", "Tema")));

            string target = Path.Combine(tmp, "afuera", "Mi tema.xtp");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string saved = ProjectLibrary.SaveAs(full, target);
            using (var st = ProjectStore.Open(saved))
                Check("Guardar como lo lleva a su lugar con su toma",
                    !UntitledProjects.Is(saved) && st.Project.Tracks.Count == 1 && File.Exists(Path.Combine(saved, "media", "toma1.wav")));
            UntitledProjects.Remove(full);
            Check("y la carpeta sin guardar se va", !Directory.Exists(full) && UntitledProjects.Leftover() == null);

            // "Guardar" goes on in the copy with the same project in memory (#297): nothing reopens
            string live = UntitledProjects.Create("audio");
            using (var st = ProjectStore.Open(live))
            {
                var project = st.Project;
                project.Tracks.Add(new Track { Name = "Bajo" });
                st.Save();
                string to = ProjectLibrary.SaveAs(live, Path.Combine(tmp, "afuera", "Sigue.xtp"));
                project.Tracks.Add(new Track { Name = "Teclado" }); // an edit after the copy was read
                st.MoveTo(to);
                Check("Guardar sigue en la copia con el mismo proyecto en memoria",
                    st.Project == project && ProjectLibrary.SamePath(st.Dir, to) && project.Name == "Sigue");
                Check("y escribe ahí lo que hay en memoria, con su origen en el registro",
                    ProjectStore.Parse(File.ReadAllText(st.FilePath)).Tracks.Count == 2 && project.Log.Any(l => l.Type == "session-saved-as"));
                Check("toma el bloqueo de la copia y suelta el de antes",
                    File.Exists(Path.Combine(to, ProjectStore.LockName)) && !File.Exists(Path.Combine(live, ProjectStore.LockName)));
            }
            UntitledProjects.Remove(live);
        }
        finally
        {
            AppConfig.DirectoryOverride = savedData;
            ProjectLibrary.RootOverride = savedRoot;
            try { Directory.Delete(tmp, recursive: true); } catch (IOException) { }
        }
        Console.WriteLine(failed == 0 ? "todo bien" : $"{failed} fallaron");
        return failed == 0 ? 0 : 1;
    }
}
