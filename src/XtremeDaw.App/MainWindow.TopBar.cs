using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XtremeDaw.App.Controls;
using XtremeDaw.App.Audio;
using XtremeDaw.App.Model;
using XtremeDaw.Audio;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The top bar from App.tsx's header: the save state next to the project name, the system meter, camera
/// resolution, sample rate and FFmpeg check, and the theme picker. Native: the audio engine's DSP load sits here
/// too, between thin lines, instead of in the transport bar (asked by the user); the sample rate went down under the
/// input meter with the buffer size. Same as the web except the save state
/// follows xtreme_project.json (the web's follows the take uploads: here takes are written while they are recorded),
/// so "Guardando" also shows during the 1.5 s before an edit is saved, and a read-only project says
/// "Solo lectura". Native: when <see cref="SaveMode"/> saves every few minutes or by hand, pending edits say "Sin
/// guardar" and the disk next to undo saves them (it is off while there is nothing to save). The bar also holds the status text here, so the system meter hides below
/// <see cref="SystemMeterMinWidth"/> (the web hides it below its lg breakpoint). Native: the camera resolution is not
/// here (the "En vivo" monitor shows it) and FFmpeg only appears when it is missing (asked by the user); its version
/// is in "Acerca de".
/// </summary>
public partial class MainWindow
{
    /// <summary>Narrower than this, CPU / GPU / RAM would leave no room for the status text.</summary>
    const double SystemMeterMinWidth = 1600;

    string? saveStateShown;

    /// <summary>The window's title (taskbar, Alt+Tab): the project's name, and the build when it is a test copy.</summary>
    static string WindowTitle(string? project)
    {
        var t = project == null ? "XTreme" : $"{project} — XTreme";
        return TestBuild.Label == null ? t : $"{t} [{Loc.T("title.test")} {TestBuild.Label}]";
    }

    void InitTopBar()
    {
        if (TestBuild.Label != null)
        {
            TestBuildChip.IsVisible = true;
            TestBuildText.Text = TestBuild.Label;
            ToolTip.SetTip(TestBuildChip, TestBuild.Detail);
            Title = WindowTitle(null);
            InitTestList();
        }
        UiTheme.Changed += OnThemeChanged;
        InputMeter.OpenRequested += () =>
        {
            if (Projects.IsVisible || Export.IsVisible || RecordingBusy) return;
            Settings.Show("audio");
        };
        SizeChanged += (_, e) => SysMeter.IsVisible = SysMeterSep.IsVisible = e.NewSize.Width >= SystemMeterMinWidth;
        MonitorToggle.IsCheckedChanged += (_, _) => engine.SoftwareMonitor = MonitorToggle.IsChecked == true;
        ToolTip.AddToolTipOpeningHandler(DspBox, (_, _) => ToolTip.SetTip(DspBox, DspTip()));
        InitComponents();
    }

    /// <summary>The top bar's FFmpeg chip (only while it is missing) and the about box; null = not on the PATH.</summary>
    void ShowFfmpeg(string? version)
    {
        bool ready = version != null;
        About.SetFfmpeg(version);
        FfmpegDot.Fill = ready ? Palette.Brush("#10b981") : Palette.Rec;
        FfmpegText.Text = Loc.T("title.ffmpeg.missing");
        ToolTip.SetTip(FfmpegState, Loc.T("title.ffmpeg.missing.tip"));
        FfmpegState.IsVisible = FfmpegSep.IsVisible = !ready;
    }

    /// <summary>Above this the callback is close to its deadline and the audio may drop out: the DSP bar goes red.</summary>
    const double DspHotPercent = 80;
    int dspShown = -1;

    /// <summary>How often each track's share of the audio thread is measured for the DSP tooltip.</summary>
    const double TrackLoadSec = 0.5;
    long trackLoadAt;

    /// <summary>From the tick: each track's share of real time on the audio thread, every <see cref="TrackLoadSec"/>. Allocates nothing.</summary>
    void MeasureTrackLoad()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long span = now - trackLoadAt;
        if (span < TrackLoadSec * System.Diagnostics.Stopwatch.Frequency) return;
        trackLoadAt = now;
        foreach (var t in tracks)
        {
            var (busy, inst) = t.TakeLoad();
            t.LoadPercent = 100.0 * busy / span;
            t.InstrumentPercent = 100.0 * inst / span;
        }
    }

    /// <summary>The DSP tooltip, when it opens: what the load means, and the track that takes the most of it.</summary>
    string DspTip()
    {
        var tip = Loc.T("title.dsp.tip");
        LiveTrack? top = null;
        foreach (var t in tracks)
            if (top == null || t.LoadPercent > top.LoadPercent) top = t;
        if (top == null || top.LoadPercent < 0.5) return tip;
        return tip + "\n" + Loc.T("title.dsp.heaviest", top.Track.Name, $"{top.LoadPercent:0}", $"{top.InstrumentPercent:0}");
    }

    /// <summary>The DSP load as a bar like CPU / GPU / RAM; from the tick, so it only touches the controls when the whole percent changes.</summary>
    void ShowDsp(double pct)
    {
        int p = (int)Math.Round(Math.Clamp(pct, 0, 100));
        if (p == dspShown) return;
        dspShown = p;
        bool hot = p >= DspHotPercent;
        DspText.Text = $"{p}%";
        DspText.Foreground = hot ? Palette.Rec : Palette.Dim;
        DspFill.Width = 32 * p / 100.0;
        DspFill.Background = hot ? Palette.Rec : Palette.Accent;
    }

    /// <summary>Called from <see cref="Tick"/>: cheap string checks, the controls only change when the state does.</summary>
    void TickTopBar()
    {
        // dirty waits for a save only after each edit; in the other modes it is "Sin guardar" until it goes
        string state = store == null ? "none" : store.ReadOnly ? "readonly" : saveFailure != null ? "error"
            : !saving.IsCompleted || dirty && SaveMode.Mode == SaveMode.OnEdit ? "saving"
            // without a place it isn't saved where the user can find it until "Guardar" asks where
            : dirty || Untitled && UntitledProjects.HasContent(store.Project) ? "unsaved" : "saved";
        if (state != saveStateShown)
        {
            saveStateShown = state;
            SaveState.IsVisible = state != "none";
            SaveSpin.IsVisible = state == "saving";
            SaveIcon.IsVisible = state == "saved";
            SaveText.Text = state switch { "error" => Loc.T("title.saveError"), "saving" => Loc.T("title.saving"), "unsaved" => Loc.T("title.unsaved"), "readonly" => Loc.T("title.readOnly"), _ => Loc.T("title.saved") };
            SaveText.Foreground = state switch { "error" => Palette.Rec, "saving" or "unsaved" => Palette.Dim, _ => Palette.Faint };
            SaveButton.IsEnabled = state is "unsaved" or "error";
            SaveButton.Classes.Set("pending", SaveButton.IsEnabled);
            SaveButton.Classes.Set("failed", state == "error");
            ToolTip.SetTip(SessionChip, store == null ? Loc.T("title.projects") : Loc.T("title.project.tip", store.Dir));
        }
        // Configuración can switch monitoring too
        if (MonitorToggle.IsChecked != engine.SoftwareMonitor) MonitorToggle.IsChecked = engine.SoftwareMonitor;
    }

    /// <summary>Brushes follow the theme on their own; what was drawn or built from a color is redrawn here.</summary>
    void OnThemeChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var v in this.GetVisualDescendants()) v.InvalidateVisual();
            RefreshColorPanel();
            RefreshMixer();
            ShowTrack(sel);
        });
    }
}
