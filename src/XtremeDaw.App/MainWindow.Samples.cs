using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using XtremeDaw.App.Audio;
using XtremeDaw.App.Controls;
using XtremeDaw.Audio;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The Biblioteca's Samples view (asked by the user, 2026-11-01): folders of the PC as a tree, a search over all of them,
/// listening in place and bringing a file to the timeline. The Proyecto view is the take list of MainWindow.History.cs.
/// The list is <see cref="SampleList"/>, drawn by hand; what is on disk is <see cref="SampleLibrary"/>, which opens the
/// first time this view shows. Listening goes through the engine (<see cref="SampleVoice"/>), so it is heard on the audio
/// interface and with no driver falls back to Windows' output. Dragging or double clicking copies the file into the
/// project through the import of MainWindow.Import.cs: a project never depends on the sample folder.
/// </summary>
public partial class MainWindow
{
    /// <summary>The wave of the player: enough bins for a wide sidebar.</summary>
    const int SamplePeakBins = 300;
    /// <summary>What listening decodes of a file: a whole film isn't kept in memory to audition it.</summary>
    const double MaxListenSec = 600;
    /// <summary>The search waits for this pause in the typing.</summary>
    const int SampleSearchMs = 120;
    const double DefaultSampleVolume = 0.8;
    /// <summary>How far the pointer travels before a press on a take becomes a drag.</summary>
    const double LibraryDragStart = 6;

    readonly SampleLibrary samples = new();
    readonly Grid libraryRoot = new() { RowDefinitions = new RowDefinitions("Auto,*") };
    readonly ScrollViewer libraryScroll = new();
    readonly DockPanel sampleView = new();
    readonly ScrollViewer sampleScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly SampleList sampleList = new();
    readonly TextBox sampleSearch = new() { Classes = { "field" }, Watermark = Loc.T("smp.search"), Height = 32, MinHeight = 32, FontSize = 12, Padding = new Thickness(30, 0, 28, 0) };
    readonly TextBlock sampleStatus = new() { Classes = { "mono" }, FontSize = 10, Foreground = Palette.Faint, Margin = new Thickness(12, 0, 12, 6), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly StackPanel sampleEmpty = new() { Spacing = 10, Margin = new Thickness(16), IsVisible = false };
    readonly TextBlock sampleEmptyText = new() { FontSize = 12, Foreground = Palette.Faint, TextWrapping = TextWrapping.Wrap };
    readonly Button sampleEmptyAdd = new() { Classes = { "chip" }, HorizontalAlignment = HorizontalAlignment.Left };
    readonly List<(string Key, ToggleButton Button)> sampleFilterButtons = [];
    readonly List<(string Key, ToggleButton Button)> libraryViewButtons = [];
    readonly TakeScrubber sampleScrubber = new();
    readonly Button samplePlay = new() { Classes = { "act" } };
    readonly TextBlock sampleName = new() { FontSize = 12, Foreground = Palette.Fg, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock sampleMeta = new() { Classes = { "mono" }, FontSize = 10, Foreground = Palette.Faint, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock samplePos = new() { Classes = { "mono" }, FontSize = 10, Foreground = Palette.Faint, VerticalAlignment = VerticalAlignment.Center };
    readonly ToggleButton sampleLoop = new() { Classes = { "chip" } };
    readonly ToggleButton sampleAuto = new() { Classes = { "chip" } };
    readonly ToggleButton samplePicture = new() { Classes = { "chip" }, IsVisible = false };
    readonly Slider sampleVolume = new() { Minimum = 0, Maximum = 1, Width = 80, VerticalAlignment = VerticalAlignment.Center };
    readonly DispatcherTimer sampleClock = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly DispatcherTimer sampleSearchDelay = new() { Interval = TimeSpan.FromMilliseconds(SampleSearchMs) };
    readonly HashSet<string> sampleOpen = new(StringComparer.OrdinalIgnoreCase);
    string libraryView = "project", sampleFilter = "all";
    List<SampleNode> sampleTree = [];
    IReadOnlyList<SampleFile>? sampleTreeFiles;
    IReadOnlyList<SampleFolder>? sampleTreeFolders;
    SampleFile? samplePicked, sampleLoaded;
    double sampleLoadedSec;
    CancellationTokenSource? sampleLoad;
    int sampleRefresh, sampleRepaint, sampleTimeShown = -1;
    bool samplesOpened;

    SampleVoice SampleOut => engine.Sample;

    void BuildSamplesPanel()
    {
        var seg = new UniformGrid { Rows = 1 };
        foreach (var (key, text, glyph) in new[] { ("project", Loc.T("smp.view.project"), "\uE7C3"), ("samples", Loc.T("smp.view.samples"), "\uE8D6") })
        {
            var b = new ToggleButton { Classes = { "seg" }, Content = IconText(glyph, text), Padding = new Thickness(4, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) => SetLibraryView(key);
            libraryViewButtons.Add((key, b));
            seg.Children.Add(b);
        }
        libraryRoot.Children.Add(new Border { Classes = { "seg" }, Child = seg, Margin = new Thickness(12, 10, 12, 8), HorizontalAlignment = HorizontalAlignment.Stretch });
        libraryScroll.Content = libraryPanel;
        Grid.SetRow(libraryScroll, 1);
        Grid.SetRow(sampleView, 1);
        libraryRoot.Children.Add(libraryScroll);
        libraryRoot.Children.Add(sampleView);

        var top = new StackPanel { Spacing = 8, Margin = new Thickness(12, 0, 12, 8) };
        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 4 };
        var searchBox = new Panel();
        searchBox.Children.Add(sampleSearch);
        searchBox.Children.Add(new TextBlock { Classes = { "icon" }, Text = "", FontSize = 12, Foreground = Palette.Faint, Margin = new Thickness(10, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        var clear = new Button { Classes = { "ghost" }, Width = 20, Height = 20, MinHeight = 20, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center, Foreground = Palette.Faint,
            Content = new TextBlock { Classes = { "icon" }, Text = "", FontSize = 10 }, IsVisible = false };
        ToolTip.SetTip(clear, Loc.T("hist.search.clear"));
        clear.Click += (_, _) => sampleSearch.Text = "";
        searchBox.Children.Add(clear);
        searchRow.Children.Add(searchBox);
        var add = RowButton("", Loc.T("smp.add"), () => _ = AddSampleFolder());
        Grid.SetColumn(add, 1); searchRow.Children.Add(add);
        var rescan = RowButton("", Loc.T("smp.rescan"), () => samples.Scan());
        Grid.SetColumn(rescan, 2); searchRow.Children.Add(rescan);
        top.Children.Add(searchRow);
        sampleSearch.TextChanged += (_, _) =>
        {
            clear.IsVisible = !string.IsNullOrEmpty(sampleSearch.Text);
            sampleSearchDelay.Stop();
            sampleSearchDelay.Start();
        };
        sampleSearch.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(sampleSearch.Text)) { sampleSearch.Text = ""; e.Handled = true; }
            else if (e.Key == Key.Down) { sampleList.Focus(); sampleList.HandleKey(e); e.Handled = true; }
        };
        sampleSearchDelay.Tick += (_, _) => { sampleSearchDelay.Stop(); RefreshSamples(); };

        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,Auto") }; // the last one has the longest word
        foreach (var (key, text, glyph) in new[] { ("all", Loc.T("smp.filter.all"), "\uE8FD"), ("audio", Loc.T("smp.filter.audio"), "\uE8D6"), ("video", Loc.T("smp.filter.video"), "\uE714"), ("favorites", Loc.T("smp.filter.favorites"), "\uE734") })
        {
            var b = new ToggleButton { Classes = { "seg" }, Content = IconText(glyph, text), Padding = new Thickness(4, 0), IsChecked = key == sampleFilter,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) => { SetSampleFilter(key); };
            Grid.SetColumn(b, sampleFilterButtons.Count);
            sampleFilterButtons.Add((key, b));
            filters.Children.Add(b);
        }
        top.Children.Add(new Border { Classes = { "seg" }, Child = filters, HorizontalAlignment = HorizontalAlignment.Stretch });
        DockPanel.SetDock(top, Dock.Top);
        sampleView.Children.Add(top);
        DockPanel.SetDock(sampleStatus, Dock.Top);
        sampleView.Children.Add(sampleStatus);

        var footer = BuildSamplePlayer();
        DockPanel.SetDock(footer, Dock.Bottom);
        sampleView.Children.Add(footer);

        sampleEmptyAdd.Content = IconText("\uE8F4", Loc.T("smp.add"));
        sampleEmptyAdd.Click += async (_, _) => await AddSampleFolder();
        sampleEmpty.Children.Add(sampleEmptyText);
        sampleEmpty.Children.Add(sampleEmptyAdd);
        sampleList.Picked += PickSample;
        sampleList.Toggled += ToggleSampleFolder;
        sampleList.Activated += f => _ = InsertSample(f);
        sampleList.ListenPressed += ToggleSampleListen;
        sampleList.FavoritePressed += ToggleSampleFavorite;
        sampleList.MenuRequested += OpenSampleMenu;
        sampleList.DragRequested += DragSample;
        sampleList.NeedsInfo = samples.Want;
        sampleList.IsFavorite = samples.IsFavorite;
        sampleList.IsOpen = n => sampleOpen.Contains(n.Path);
        sampleList.OfflineText = Loc.T("smp.offline");
        sampleList.Attach(sampleScroll);
        sampleView.Children.Add(new Panel { Children = { sampleScroll, sampleEmpty } });

        samples.Changed += () => Dispatcher.UIThread.Post(RefreshSamples);
        samples.Analyzed += () =>
        {
            if (Interlocked.Exchange(ref sampleRepaint, 1) == 0)
                Dispatcher.UIThread.Post(() => { sampleRepaint = 0; sampleList.InvalidateVisual(); ShowPickedSample(); }, DispatcherPriority.Background);
        };
        Closed += (_, _) =>
        {
            StopSample();
            SampleOut.Dispose();
            samples.Dispose();
        };
        foreach (string path in ReadSampleOpen()) sampleOpen.Add(path);
        SetLibraryView(AppConfig.Get("libraryView") == "samples" ? "samples" : "project", save: false);
    }

    Control BuildSamplePlayer()
    {
        var box = new StackPanel { Spacing = 6, Margin = new Thickness(12, 8) };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        samplePlay.Click += (_, _) => ToggleSampleListen();
        head.Children.Add(samplePlay);
        Grid.SetColumn(sampleName, 1); head.Children.Add(sampleName);
        Grid.SetColumn(samplePos, 2); head.Children.Add(samplePos);
        box.Children.Add(head);
        box.Children.Add(sampleScrubber);
        box.Children.Add(sampleMeta);
        sampleScrubber.SeekRequested += f =>
        {
            if (sampleLoaded != null) SampleOut.Seek(f * sampleLoadedSec);
        };

        sampleLoop.Content = IconText("\uE8EE", Loc.T("smp.loop"));
        sampleAuto.Content = IconText("\uE945", Loc.T("smp.auto"));
        samplePicture.Content = IconText("\uE714", Loc.T("smp.picture"));
        ToolTip.SetTip(sampleLoop, Loc.T("smp.loop.tip"));
        ToolTip.SetTip(sampleAuto, Loc.T("smp.auto.tip"));
        ToolTip.SetTip(samplePicture, Loc.T("smp.picture.tip"));
        ToolTip.SetTip(sampleVolume, Loc.T("smp.volume"));
        Avalonia.Automation.AutomationProperties.SetName(sampleVolume, Loc.T("smp.volume"));
        sampleLoop.IsChecked = AppConfig.Get("sampleLoop") == "1";
        sampleAuto.IsChecked = AppConfig.Get("sampleAuto") != "0";
        samplePicture.IsChecked = AppConfig.Get("samplePicture") != "0";
        SampleOut.Loop = sampleLoop.IsChecked == true;
        sampleLoop.Click += (_, _) =>
        {
            SampleOut.Loop = sampleLoop.IsChecked == true;
            SaveViewPref("sampleLoop", SampleOut.Loop ? "1" : "0");
        };
        sampleAuto.Click += (_, _) => SaveViewPref("sampleAuto", sampleAuto.IsChecked == true ? "1" : "0");
        samplePicture.Click += (_, _) => SaveViewPref("samplePicture", samplePicture.IsChecked == true ? "1" : "0");
        sampleVolume.Value = double.TryParse(AppConfig.Get("sampleVolume"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? Math.Clamp(v, 0, 1) : DefaultSampleVolume;
        SampleOut.Gain = sampleVolume.Value * sampleVolume.Value;
        sampleVolume.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            SampleOut.Gain = sampleVolume.Value * sampleVolume.Value;
            SaveViewPref("sampleVolume", sampleVolume.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        };
        var tools = new ToolbarPanel();
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { sampleLoop, sampleAuto, samplePicture } };
        tools.Children.Add(chips);
        tools.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { HistoryIcon("\uE767", 12, Palette.Faint), sampleVolume } });
        box.Children.Add(tools);

        sampleClock.Tick += (_, _) => TickSamplePlayer();
        ShowPickedSample();
        return new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = box };
    }

    void SetLibraryView(string key, bool save = true)
    {
        libraryView = key;
        foreach (var (k, b) in libraryViewButtons) b.IsChecked = k == key;
        sampleView.IsVisible = key == "samples";
        libraryScroll.IsVisible = key != "samples";
        if (save) SaveViewPref("libraryView", key);
        if (key != "samples") return;
        if (!samplesOpened)
        {
            samplesOpened = true;
            sampleStatus.Text = Loc.T("smp.reading");
            Task.Run(samples.Open);
        }
        else samples.Scan(); // a disk may have come back
    }

    void SetSampleFilter(string key)
    {
        sampleFilter = key;
        foreach (var (k, b) in sampleFilterButtons) b.IsChecked = k == key;
        RefreshSamples();
    }

    /// <summary>Works out the rows off the UI thread, as a search goes through every file. Only the latest request lands.</summary>
    void RefreshSamples()
    {
        int gen = ++sampleRefresh;
        var folders = samples.Folders;
        var files = samples.Files;
        string query = sampleSearch.Text?.Trim() ?? "", filter = sampleFilter;
        var open = new HashSet<string>(sampleOpen, StringComparer.OrdinalIgnoreCase);
        var tree = sampleTreeFiles == files && sampleTreeFolders == folders ? sampleTree : null;
        Task.Run(() =>
        {
            tree ??= SampleLibrary.Tree(folders, files);
            bool flat = query.Length > 0 || filter != "all";
            var rows = !flat ? SampleLibrary.Rows(tree, open)
                : SampleLibrary.Search(files.Where(f => filter switch { "audio" => !f.IsVideo, "video" => f.IsVideo, "favorites" => samples.IsFavorite(f), _ => true }), query)
                    .Select(f => new SampleRow(null, f, 0)).ToList();
            Dispatcher.UIThread.Post(() =>
            {
                (sampleTree, sampleTreeFiles, sampleTreeFolders) = (tree, files, folders);
                if (gen != sampleRefresh) return;
                samples.ForgetWanted();
                sampleList.SetRows(rows);
                sampleStatus.Text = samples.Scanning ? Loc.T("smp.scanning", files.Count)
                    : flat ? Loc.T("smp.found", rows.Count) : Loc.T("smp.count", files.Count);
                bool none = folders.Count == 0;
                sampleEmpty.IsVisible = none || rows.Count == 0;
                sampleEmptyAdd.IsVisible = none;
                sampleEmptyText.Text = Loc.T(none ? "smp.empty" : "smp.noMatch");
            });
        });
    }

    async Task AddSampleFolder()
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("smp.add"), AllowMultiple = true });
        foreach (var f in picked)
        {
            if (f.TryGetLocalPath() is not { } path) continue;
            samples.AddFolder(path);
            sampleOpen.Add(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar));
        }
        SaveSampleOpen();
    }

    void ToggleSampleFolder(SampleNode folder)
    {
        if (!sampleOpen.Remove(folder.Path)) sampleOpen.Add(folder.Path);
        SaveSampleOpen();
        RefreshSamples();
    }

    static string[] ReadSampleOpen()
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<string[]>(AppConfig.Get("sampleOpen") ?? "[]") ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    void SaveSampleOpen() => SaveViewPref("sampleOpen", System.Text.Json.JsonSerializer.Serialize(sampleOpen.ToArray()));

    void PickSample(SampleRow row)
    {
        if (row.File is not { } f) return;
        samplePicked = f;
        ShowPickedSample();
        if (sampleAuto.IsChecked == true && !f.Broken) PlaySample(f);
    }

    /// <summary>The player's texts: what is selected, what it is and where it lives.</summary>
    void ShowPickedSample()
    {
        var f = samplePicked;
        sampleName.Text = f?.Name ?? Loc.T("smp.none");
        samplePlay.IsEnabled = f != null && !f.Broken;
        samplePicture.IsVisible = f?.IsVideo == true && !AudioOnly;
        if (f == null) sampleMeta.Text = "";
        else
        {
            string kind = Path.GetExtension(f.Name).TrimStart('.').ToUpperInvariant();
            string length = f.Broken ? Loc.T("smp.unreadable") : f.DurationSec > 0 ? ProjectHistory.FormatDuration(f.DurationSec) : "…";
            string from = Path.GetDirectoryName(Path.GetRelativePath(f.Root, f.Path)) ?? "";
            sampleMeta.Text = string.Join(" · ", new[] { kind, length, ProjectHistory.FormatBytes(f.Size), from }.Where(t => t.Length > 0));
        }
        UpdateSamplePlayIcon();
    }

    void UpdateSamplePlayIcon()
    {
        bool current = sampleLoaded != null && sampleLoaded == samplePicked;
        bool playing = current && SampleOut.Active && !SampleOut.Paused;
        samplePlay.Content = HistoryIcon(playing ? "" : "");
        samplePlay.Foreground = current ? Palette.Accent : Palette.Faint;
        string tip = Loc.T(playing ? "hist.pause" : "hist.listen");
        ToolTip.SetTip(samplePlay, tip);
        Avalonia.Automation.AutomationProperties.SetName(samplePlay, tip);
    }

    void ToggleSampleListen()
    {
        if (samplePicked is not { Broken: false } f) return;
        if (sampleLoaded == f && SampleOut.Active)
        {
            SampleOut.Paused = !SampleOut.Paused;
            UpdateSamplePlayIcon();
        }
        else PlaySample(f);
    }

    /// <summary>Decodes in the background and starts it; moving on to another file meanwhile cancels this one.</summary>
    void PlaySample(SampleFile f)
    {
        if (RecordingBusy) return;
        takePreview.Stop();
        sampleLoad?.Cancel();
        var load = sampleLoad = new CancellationTokenSource();
        Task.Run(() =>
        {
            var audio = MediaDecoder.Load(f.Path, PlaybackEngine.SampleRate, null, MaxListenSec, load.Token);
            return (audio, peaks: MediaDecoder.Peaks(audio.Left, audio.Right, SamplePeakBins));
        }, load.Token).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (load != sampleLoad) return; // something else was asked for meanwhile
                if (t.IsCanceled) return;
                if (t.IsFaulted)
                {
                    if (f.DurationSecValue == 0) f.DurationSecValue = -1;
                    sampleList.InvalidateVisual();
                    ShowPickedSample();
                    Status(Loc.T("smp.fail", f.Name, t.Exception?.GetBaseException().Message.TrimEnd('.') ?? ""));
                    return;
                }
                if (RecordingBusy) return;
                var (audio, peaks) = t.Result;
                sampleLoaded = f;
                sampleLoadedSec = audio.DurationSec;
                sampleTimeShown = -1;
                sampleScrubber.SetPeaks([.. peaks.Select(p => p / 255f)]);
                sampleScrubber.SetProgress(0);
                SampleOut.Play(audio.Left, audio.Right);
                if (!engine.IsOpen) SampleOut.UseWindowsOutput();
                sampleList.SetPlaying(f.Path);
                sampleClock.Start();
                UpdateSamplePlayIcon();
            });
        }, TaskScheduler.Default);
    }

    void StopSample()
    {
        sampleLoad?.Cancel();
        sampleLoad = null;
        SampleOut.Stop();
        EndSample();
    }

    void EndSample()
    {
        sampleLoaded = null;
        sampleList.SetPlaying(null);
        sampleScrubber.SetProgress(0);
        samplePos.Text = "";
        UpdateSamplePlayIcon();
    }

    /// <summary>Moves the wave's played edge every frame; the time text changes (and allocates) once a second.</summary>
    void TickSamplePlayer()
    {
        var o = SampleOut;
        if (engine.IsOpen && o.OnWindows) o.DropWindowsOutput();
        if (RecordingBusy && sampleLoaded != null) StopSample();
        if (!o.Active)
        {
            if (sampleLoaded != null) EndSample();
            sampleClock.Stop();
            o.DropWindowsOutput();
            return;
        }
        if (sampleLoaded == null) return;
        double pos = o.PositionSec;
        sampleScrubber.SetProgress(sampleLoadedSec > 0 ? pos / sampleLoadedSec : 0);
        int whole = (int)pos;
        if (whole == sampleTimeShown) return;
        sampleTimeShown = whole;
        samplePos.Text = $"{ProjectHistory.FormatDuration(whole)} / {ProjectHistory.FormatDuration(sampleLoadedSec)}";
    }

    void ToggleSampleFavorite(SampleFile f)
    {
        samples.ToggleFavorite(f);
        sampleList.InvalidateVisual();
        if (sampleFilter == "favorites") RefreshSamples();
    }

    /// <summary>At the playhead, on the selected track when that stretch is free, like a take.</summary>
    Task InsertSample(SampleFile f) => ImportAudio([f.Path], engine.PositionSec, sel?.Track.Id, BringsPicture(f.Path));

    /// <summary>A video brings its picture unless the user turned that off or the project is audio only.</summary>
    bool BringsPicture(string path) => MediaDecoder.IsVideo(path) && samplePicture.IsChecked == true && !AudioOnly;

    void DragSample(SampleFile f, PointerEventArgs e) => CarryToTimeline(SampleDrag, f.Path, e);

    /// <summary>Starts a drag; MainWindow.Import.cs receives it.</summary>
    static async void CarryToTimeline(DataFormat<string> format, string value, PointerEventArgs e)
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(format, value));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
    }

    /// <summary>A glyph and a word for a button; both take the button's color.</summary>
    static StackPanel IconText(string glyph, string text) => new()
    {
        Orientation = Orientation.Horizontal, Spacing = 5,
        Children = { HistoryIcon(glyph, 11), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
    };

    void OpenSampleMenu(SampleRow row, Point at)
    {
        var items = new List<Control>();
        if (row.File is { } f)
        {
            items.Add(MenuAction(Loc.T("smp.menu.insert"), () => InsertSample(f), icon: ""));
            if (f.IsVideo)
            {
                items.Add(MenuAction(Loc.T("smp.menu.insertAudio"), () => ImportAudio([f.Path], engine.PositionSec, sel?.Track.Id, false)));
            }
            items.Add(MenuAction(Loc.T("smp.menu.sampler"), () => SampleToSampler(f.Path)));
            items.Add(MenuAction(Loc.T(samples.IsFavorite(f) ? "smp.menu.unfavorite" : "smp.menu.favorite"), () => { ToggleSampleFavorite(f); return Task.CompletedTask; }, icon: ""));
            items.Add(new Separator());
            items.Add(MenuAction(Loc.T("hist.reveal"), () => { RevealInExplorer(null, f.Path); return Task.CompletedTask; }, icon: ""));
            items.Add(MenuAction(Loc.T("smp.menu.copyPath"), async () => { if (Clipboard is { } c) await c.SetTextAsync(f.Path); }, icon: ""));
        }
        else if (row.Folder is { } n)
        {
            items.Add(MenuAction(Loc.T("hist.reveal"), () => { RevealInExplorer(n.Path, null); return Task.CompletedTask; }, enabled: !n.Offline, icon: ""));
            if (n.Depth == 0)
            {
                items.Add(new Separator());
                items.Add(MenuAction(Loc.T("smp.menu.rename"), () =>
                {
                    AskName(sampleList, at, Loc.T("smp.menu.rename"), n.Name, Loc.T("smp.menu.renameOk"), name => samples.RenameFolder(n.Path, name));
                    return Task.CompletedTask;
                }));
                items.Add(MenuAction(Loc.T("smp.menu.remove"), () => { samples.RemoveFolder(n.Path); return Task.CompletedTask; }, danger: true, tip: Loc.T("smp.menu.remove.tip")));
            }
        }
        new ContextMenu { ItemsSource = items }.Open(sampleList);
    }
}
