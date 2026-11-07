using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using XtremeDaw.App.Controls;
using XtremeDaw.Audio.Dsp;
using XtremeDaw.Projects;

namespace XtremeDaw.App;

/// <summary>
/// The tonal balance's panel (#210), inside the effect panel every native effect has (<see cref="FxBody"/>): its choices
/// (target, what it listens to, starting the reading over, a target from references) over its picture
/// (<see cref="TonalView"/>) and its Respuesta knob. The target from references is analysed off the window's thread and
/// kept in the slot (<see cref="InsertSlot.Tonal"/>), so the project doesn't depend on those files.
/// </summary>
public partial class MainWindow
{
    /// <summary>The picture's height in the full view (the compact one is only the meters).</summary>
    const double TonalFullH = 300;

    static readonly string[] TonalModes = ["0", "1", "2"];

    static TonalInsert? LiveTonal(SlotOwner o, string id) => o.Insert(id) as TonalInsert;

    /// <summary>The choices over the picture: presets, start over and references; the target; Estéreo, Mid or Side.</summary>
    void TonalChoices(StackPanel body, SlotOwner o, string id, List<Action> refresh)
    {
        var reset = InspectorIcon("", Loc.T("tonal.reset.tip"), () => LiveTonal(o, id)?.Meter.Reset());
        var reference = InspectorIcon("", Loc.T("tonal.ref.tip"), () => _ = PickTonalReference(o, id));
        body.Children.Add(PresetBar(o, id, TrackInserts.Tonal, refresh, [], [reset, reference]));

        // the menu lists the genres in their order and the slot's own last; the slot keeps the saved value (FxDefs.TonalValue)
        var target = StripSelect([]);
        ToolTip.SetTip(target, Loc.T("fx.tonal.target.tip"));
        int ownAt = TonalTargets.Factory.Length;
        int MenuIndex(InsertSlot? s)
        {
            string tid = s == null ? TonalTargets.Factory[0].Id : FxDefs.TonalIdOf(s);
            return tid == FxDefs.TonalOwnId ? ownAt : Math.Max(0, Array.FindIndex(TonalTargets.Factory, t => t.Id == tid));
        }
        target.Selected += i =>
        {
            if (i < 0) return;
            if (i == ownAt && o.Slot(id)?.Tonal == null)
            {
                // the menu shows the target it had until the reference is in
                target.SelectedIndex = MenuIndex(o.Slot(id));
                _ = PickTonalReference(o, id);
                return;
            }
            int value = FxDefs.TonalValue(i == ownAt ? FxDefs.TonalOwnId : TonalTargets.Factory[i].Id);
            SlotEdit(o, id, s => s.Stage!.Values["target"] = value, "tonal.target");
        };
        refresh.Add(() =>
        {
            if (o.Slot(id) is not { } s) return;
            var names = new string[ownAt + 1];
            var hints = new string?[names.Length];
            for (int i = 0; i < ownAt; i++)
            {
                names[i] = Loc.T("tonal.target." + TonalTargets.Factory[i].Id);
                string tip = "tonal.target." + TonalTargets.Factory[i].Id + ".tip";
                hints[i] = Loc.Has(tip) ? Loc.T(tip) : null;
            }
            names[^1] = s.Tonal is { } own ? Loc.T("tonal.target.custom", own.Name) : Loc.T("tonal.target.custom.none");
            hints[^1] = s.Tonal is { Files.Count: > 0 } f ? string.Join("\n", f.Files) : Loc.T("tonal.ref.tip");
            target.SetItems(names, hints);
            target.SelectedIndex = MenuIndex(s);
        });
        body.Children.Add(target);

        body.Children.Add(FxSegments(
            [(TonalModes[0], Loc.T("tonal.mode.stereo"), Loc.T("tonal.mode.stereo.tip")), (TonalModes[1], "Mid", Loc.T("tonal.mode.mid.tip")),
             (TonalModes[2], "Side", Loc.T("tonal.mode.side.tip"))],
            () => TonalModes[(int)(o.Slot(id) is { } s ? FxDefs.TonalModeOf(s) : TonalMode.Stereo)],
            m => SlotEdit(o, id, s => s.Stage!.Values["mode"] = int.Parse(m), "tonal.mode"), refresh));
    }

    /// <summary>The picture, following the slot's target, mode and Respuesta on every edit.</summary>
    TonalView TonalOf(SlotOwner o, string id, bool fine, Color tint, List<Action> refresh)
    {
        var view = new TonalView(fine, TonalFullH) { Live = () => LiveTonal(o, id), Tint = tint, Margin = new Thickness(0, 2, 0, 2) };
        refresh.Add(() =>
        {
            if (o.Slot(id) is not { } s) return;
            view.TimeSec = FxDefs.TonalSpeedOf(s);
            view.Show(FxDefs.TonalTargetOf(s), FxDefs.TonalModeOf(s));
        });
        return view;
    }

    /// <summary>
    /// "Referencia…": the user picks one or more songs and their average balance becomes the slot's own target (one undo
    /// step), chosen at once. Decoding and analysis run in the background with a progress toast.
    /// </summary>
    async Task PickTonalReference(SlotOwner o, string id)
    {
        if (store is not { ReadOnly: false }) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("tonal.ref.pick"),
            AllowMultiple = true,
            FileTypeFilter = [AudioFiles, FilePickerFileTypes.All],
        });
        string[] paths = [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
        if (paths.Length == 0) return;
        var toast = Toasts.ShowProgress(Loc.T("tonal.ref.working"));
        TonalTarget? made;
        try
        {
            made = await Task.Run(() => TonalReference.AnalyzeFiles(paths, Audio.PlaybackEngine.SampleRate, toast.Report));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Toast(Loc.T("tonal.ref.fail", e.Message.TrimEnd('.')));
            return;
        }
        finally
        {
            toast.Close();
        }
        if (made == null)
        {
            Toast(Loc.T("tonal.ref.silent"));
            return;
        }
        string name = paths.Length == 1 ? Path.GetFileNameWithoutExtension(paths[0]) : Loc.T("tonal.ref.many", paths.Length);
        var curve = TonalCurveSettings.From(made, name, paths);
        SlotEdit(o, id, s =>
        {
            s.Tonal = curve;
            s.Stage!.Values["target"] = FxDefs.TonalCustom;
        }, "tonal.ref");
    }
}
