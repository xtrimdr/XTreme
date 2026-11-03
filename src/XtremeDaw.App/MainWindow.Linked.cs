using Avalonia.Platform.Storage;
using Avalonia.Threading;
using XtremeDaw.App.Model;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// "Copiar al proyecto" on a linked take and finding a file that isn't there anymore, from the Biblioteca and from an
/// offline clip's menu: the slow part runs in the background (<see cref="LinkedMedia"/>) and what it returns is one
/// undo step. Whether a file is there is asked in the background too, on every refresh of the list: it can be on a
/// disk that takes its time, and a file can leave while the project is open (its clips keep playing from memory).
/// </summary>
public partial class MainWindow
{
    /// <summary>Takes with a file that isn't there, as last checked.</summary>
    readonly HashSet<string> historyMissing = [];
    bool historyChecking, historyCheckAgain;

    /// <summary>The clip was loaded from another place than the one its media says now.</summary>
    static bool FilesMoved(ProjectStore st, LiveClip c) =>
        c.Media != null && (c.AudioPath != st.Resolve(c.Media.Audio) || c.VideoPath != st.Resolve(c.Media.Video));

    /// <summary>What the list remembers of a take's files, measured again on its next refresh.</summary>
    void ForgetFileFacts(IEnumerable<string> mediaIds)
    {
        foreach (string id in mediaIds)
        {
            historyBytes.Remove(id);
            takePeaks.Remove(id);
        }
    }

    void CheckMissingFiles(ProjectStore st)
    {
        if (historyChecking) { historyCheckAgain = true; return; } // the answer on its way may be from before an edit
        historyChecking = true;
        var media = st.Project.Media.ToList();
        string dir = st.Dir;
        Task.Run(() => media.Where(m => LinkedMedia.IsMissing(dir, m)).Select(m => m.Id).ToHashSet()).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                historyChecking = false;
                bool again = historyCheckAgain;
                historyCheckAgain = false;
                if (t.IsFaulted || store != st) return;
                if (!historyMissing.SetEquals(t.Result))
                {
                    historyMissing.Clear();
                    historyMissing.UnionWith(t.Result);
                    RefreshHistory();
                }
                else if (again) CheckMissingFiles(st);
            });
        }, TaskScheduler.Default);
    }

    /// <summary>Puts the takes in their new places, one undo step, and the clips read them from there.</summary>
    /// <param name="sameSound">The audio is the same bytes elsewhere (a copy): what is loaded stays, so it keeps playing.</param>
    async Task ApplyPlaces(ProjectStore st, List<LinkedMedia.Place> places, EditSteps.Label step, string activity, bool sameSound = false)
    {
        if (st != store) return;
        await EditTimeline(p =>
        {
            foreach (var place in places)
            {
                if (!LinkedMedia.Apply(p, place) || !sameSound) continue;
                foreach (var c in tracks.SelectMany(t => t.Clips).Where(c => c.Media?.Id == place.MediaId)) c.AudioPath = st.Resolve(place.Audio);
            }
            return sel?.Track.Id;
        }, activity, Timeline.SelectedClipId, step);
        ForgetFileFacts(places.Select(x => x.MediaId));
        RefreshHistory();
    }

    async Task CopyLinked(ProjectStore st, Media m)
    {
        if (st != store || !CanImport() || historyBusyId != null) return;
        historyBusyId = m.Id;
        RefreshHistory();
        var toast = Toasts.ShowProgress(Loc.T("hist.copying", m.Name));
        try
        {
            var place = await Task.Run(() => LinkedMedia.CopyIn(st.Dir, m, toast.Report));
            await ApplyPlaces(st, [place], EditSteps.CopiedIn(m.Name), "linked-copied", sameSound: true);
            Toast(Loc.T("hist.copied", m.Name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Toast(Loc.T("hist.err.copy", m.Name, e.Message.TrimEnd('.')));
        }
        finally
        {
            toast.Close();
            historyBusyId = null;
            RefreshHistory();
        }
    }

    /// <summary>The file chosen by hand: read in the background, since it has to be the same take.</summary>
    async Task FindFile(ProjectStore? st, Media m)
    {
        if (st == null || st != store || !CanImport() || historyBusyId != null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("hist.find.title", LinkedMedia.Wanted(st.Dir, m) ?? m.Name),
            AllowMultiple = false,
            FileTypeFilter = [AudioFiles, FilePickerFileTypes.All],
        });
        if (files.Select(f => f.TryGetLocalPath()).OfType<string>().FirstOrDefault() is not { } path || st != store || !CanImport()) return;
        historyBusyId = m.Id;
        RefreshHistory();
        var toast = Toasts.ShowProgress(Loc.T("hist.reading", m.Name));
        try
        {
            var place = await Task.Run(() => LinkedMedia.Pick(st.Dir, m, path));
            await ApplyPlaces(st, [place], EditSteps.FileFound(m.Name), "linked-found");
            Toast(Loc.T(LinkedMedia.IsMissing(st.Dir, m) ? "hist.foundPart" : "hist.found", m.Name));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Toast(Loc.T("hist.err.find", e.Message.TrimEnd('.')));
        }
        finally
        {
            toast.Close();
            historyBusyId = null;
            RefreshHistory();
        }
    }

    /// <summary>Everything the project lacks, looked for by name and size in a folder and under it, so a folder that moved comes back in one go.</summary>
    async Task FindInFolder(ProjectStore? st)
    {
        if (st == null || st != store || !CanImport() || historyBusyId != null) return;
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("hist.findFolder.title") });
        if (picked.Select(f => f.TryGetLocalPath()).OfType<string>().FirstOrDefault() is not { } folder || st != store || !CanImport()) return;
        historyBusyId = "";
        RefreshHistory();
        var toast = Toasts.ShowProgress(Loc.T("hist.searching"));
        try
        {
            var media = st.Project.Media.ToList();
            var found = await Task.Run(() => LinkedMedia.Search(st.Dir, media, folder));
            int n = found.Places.Count;
            if (n > 0)
            {
                var step = n == 1 ? EditSteps.FileFound(media.Find(m => m.Id == found.Places[0].MediaId)!.Name) : EditSteps.FilesFound(n);
                await ApplyPlaces(st, found.Places, step, "linked-found");
            }
            string left = string.Join(", ", found.NotFound);
            if (found.Ambiguous.Count > 0) Toast(Loc.T("hist.scan.many", string.Join(", ", found.Ambiguous)));
            else if (found.NotFound.Count > 0) Toast(Loc.T(n > 0 ? "hist.scan.some" : "hist.scan.none", n, left));
            else Toast(Loc.T("hist.scan.found", n));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Toast(Loc.T("hist.err.search", e.Message.TrimEnd('.')));
        }
        finally
        {
            toast.Close();
            historyBusyId = null;
            RefreshHistory();
        }
    }
}
