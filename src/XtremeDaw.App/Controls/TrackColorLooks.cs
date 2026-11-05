using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// How a track's color shows on the timeline (asked by the user, #162, picked in Configuración → Apariencia): how its
/// clips are painted (<see cref="Clips"/>, "trackColor.clips") and what else takes the color: a stripe on the header,
/// a tint on the header or on the lane, and the selection mark in the track's color instead of the accent
/// ("trackColor.marks", the ones turned on). The defaults are the timeline as it was. Only tracks with a color change;
/// an uncolored one stays in the accent. Nothing here allocates while drawing: brushes and pens come from
/// <see cref="Palette"/>, which caches them.
/// </summary>
public static class TrackColorLooks
{
    public const string Soft = "soft", Solid = "solid", WaveOnly = "wave", TitleBar = "bar";

    /// <summary>In the picker's order; the first is the default.</summary>
    public static readonly string[] AllClips = [Soft, Solid, WaveOnly, TitleBar];

    public const string HeaderStripe = "stripe", HeaderTint = "header", LaneTint = "lane", Selection = "selection";

    /// <summary>The marks that can be turned on, in the picker's order; all off by default.</summary>
    public static readonly string[] AllMarks = [HeaderStripe, HeaderTint, LaneTint, Selection];

    const string ClipsKey = "trackColor.clips", MarksKey = "trackColor.marks";

    public const string Default = Soft;

    public static string Clips { get; private set; } = Default;

    static readonly HashSet<string> marks = [];

    public static bool Has(string mark) => marks.Contains(mark);

    public static bool IsDefault => Clips == Default && marks.Count == 0;

    // --- inspector (#162: "lo mismo pero para el inspector") ---

    /// <summary>How the inspector's header takes the color: the rule under it and a tint on the name's box (as it was),
    /// the rule alone, the box filled, or not at all.</summary>
    public const string HeadRule = "rule", HeadLine = "line", HeadFill = "fill", HeadNone = "none";

    /// <summary>In the picker's order; the first is the default.</summary>
    public static readonly string[] AllHeads = [HeadRule, HeadLine, HeadFill, HeadNone];

    /// <summary>A stripe along the inspector's edge, its section headers tinted, their icons in the color.</summary>
    public const string InspEdge = "edge", InspSections = "sections", InspIcons = "icons";

    /// <summary>The inspector's marks, in the picker's order; all off by default.</summary>
    public static readonly string[] AllInspMarks = [InspEdge, InspSections, InspIcons];

    const string HeadKey = "trackColor.inspector", InspMarksKey = "trackColor.inspectorMarks", PointerKey = "trackColor.pointer";

    /// <summary>The line from the picked track's header to the inspector's, in its color, while both are in the window
    /// (#180: "este track se ve acá"); on by default (<see cref="InspectorPointer"/> draws it).</summary>
    public static bool Pointer { get; private set; } = true;

    public static string Head { get; private set; } = HeadRule;

    static readonly HashSet<string> inspMarks = [];

    public static bool HasInsp(string mark) => inspMarks.Contains(mark);

    public static bool InspectorIsDefault => Head == HeadRule && inspMarks.Count == 0 && Pointer;

    /// <summary>Something changed: the timeline redraws and the inspector repaints.</summary>
    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            if (AppConfig.Get(ClipsKey) is { } id && AllClips.Contains(id)) Clips = id;
            foreach (string m in (AppConfig.Get(MarksKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (AllMarks.Contains(m)) marks.Add(m);
            if (AppConfig.Get(HeadKey) is { } head && AllHeads.Contains(head)) Head = head;
            foreach (string m in (AppConfig.Get(InspMarksKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (AllInspMarks.Contains(m)) inspMarks.Add(m);
            Pointer = AppConfig.Get(PointerKey) != "0";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the defaults */ }
    }

    public static void SetHead(string look)
    {
        if (!AllHeads.Contains(look) || look == Head) return;
        Head = look;
        Saved();
    }

    public static void SetInspMark(string mark, bool on)
    {
        if (!AllInspMarks.Contains(mark) || on == HasInsp(mark)) return;
        if (on) inspMarks.Add(mark); else inspMarks.Remove(mark);
        Saved();
    }

    public static void SetPointer(bool on)
    {
        if (on == Pointer) return;
        Pointer = on;
        Saved();
    }

    public static void ResetInspector()
    {
        if (InspectorIsDefault) return;
        Head = HeadRule;
        inspMarks.Clear();
        Pointer = true;
        Saved();
    }

    /// <summary>Thickness of the rule under the inspector's header in the color; the tint of the name's box
    /// (<see cref="HeadRule"/>, <see cref="HeadFill"/>) and of a section's header (<see cref="InspSections"/>).</summary>
    public const double HeadRuleW = 3, HeadTintA = 0.10, HeadFillA = 0.30, SectionTintA = 0.08;

    /// <summary>Width of <see cref="InspEdge"/>'s stripe.</summary>
    public const double EdgeW = 3;

    public static void SetClips(string look)
    {
        if (!AllClips.Contains(look) || look == Clips) return;
        Clips = look;
        Saved();
    }

    public static void SetMark(string mark, bool on)
    {
        if (!AllMarks.Contains(mark) || on == Has(mark)) return;
        if (on) marks.Add(mark); else marks.Remove(mark);
        Saved();
    }

    public static void Reset()
    {
        if (IsDefault) return;
        Clips = Default;
        marks.Clear();
        Saved();
    }

    static void Saved()
    {
        Changed?.Invoke();
        string clips = Clips, list = string.Join(",", AllMarks.Where(marks.Contains)),
            head = Head, inspList = string.Join(",", AllInspMarks.Where(inspMarks.Contains)), pointer = Pointer ? "1" : "0";
        _ = Task.Run(() =>
        {
            try
            {
                AppConfig.Set(ClipsKey, clips);
                AppConfig.Set(MarksKey, list);
                AppConfig.Set(HeadKey, head);
                AppConfig.Set(InspMarksKey, inspList);
                AppConfig.Set(PointerKey, pointer);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* keep this session's choice */ }
        });
    }

    // --- drawing --------------------------------------------------------------------------------

    /// <summary>Height of <see cref="TitleBar"/>'s strip along a clip's top.</summary>
    public const double BarH = 16;
    /// <summary>Tint of a header or a lane under <see cref="HeaderTint"/> / <see cref="LaneTint"/>.</summary>
    const double HeaderTintA = 0.14, LaneTintA = 0.07;
    /// <summary>Width of <see cref="HeaderStripe"/>'s bar.</summary>
    const double StripeW = 3;

    /// <summary>How a clip of a track colored <paramref name="c"/> is painted in the chosen look: its fill, its border and
    /// what is drawn inside it (the wave, the notes).</summary>
    public static (IBrush Fill, IPen Border, IBrush Content) Clip(Color c, bool sel) => Clip(Clips, c, sel);

    static (IBrush Fill, IPen Border, IBrush Content) Clip(string look, Color c, bool sel) => look switch
    {
        Solid => (Palette.Alpha(c, sel ? 0.95 : 0.78), sel ? Palette.Pen(Palette.Fg, 1.5) : Palette.Pen(Palette.Alpha(c, 1)),
            Palette.Alpha(Colors.Black, 0.72)),
        WaveOnly => (Palette.Alpha(Palette.HoverColor, sel ? 0.75 : 0.45), sel ? Palette.Pen(Palette.Alpha(c, 0.95), 1.5) : Palette.Pen(Palette.Line),
            Palette.Alpha(c, sel ? 1 : 0.9)),
        _ => (Palette.Alpha(c, sel ? 0.24 : 0.14), sel ? Palette.Pen(Palette.Alpha(c, 0.95), 1.5) : Palette.Pen(Palette.Alpha(c, 0.45)),
            Palette.Alpha(c, sel ? 1 : 0.8)),
    };

    /// <summary>Room the look takes at a clip's top (<see cref="TitleBar"/>'s strip), so the wave starts below it.
    /// At most <see cref="BarH"/>, and a share of a low clip (tracks go down to 24 px).</summary>
    public static double Top(double clipH) => Clips == TitleBar ? Math.Min(BarH, Math.Round(clipH * BarShare)) : 0;

    /// <summary>Most of a clip's height <see cref="TitleBar"/>'s strip takes.</summary>
    const double BarShare = 0.3;

    /// <summary><see cref="TitleBar"/>'s strip along a clip's top; call it clipped to the clip's rounded box.</summary>
    public static void DrawBar(DrawingContext ctx, Rect clip, Color c, bool sel)
    {
        if (Clips == TitleBar) ctx.FillRectangle(Palette.Alpha(c, sel ? 1 : 0.85), new Rect(clip.X, clip.Y, clip.Width, Top(clip.Height)));
    }

    /// <summary>A header's marks in its track's color: the tint over it and the stripe at its left. The stripe is a bar of
    /// its own with rounded ends at <paramref name="stripeX"/>, with the track's content (it follows a folder's indent,
    /// clear of the folder's frame), set apart from the selected frame and the grip dots, and ending before the header's
    /// rounded box (<paramref name="box"/>) curves, with a gap, like the Bench's cards (#162: a bar clipped to the curve
    /// drew a crescent; one against the frame and the dots looked cramped). Over the header's background, under the rest.</summary>
    public static void DrawHeader(DrawingContext ctx, Color? c, double x, double y, double w, double h, Rect box, double stripeX, double stripeW)
    {
        if (c is not { } color) return;
        if (Has(HeaderTint)) ctx.FillRectangle(Palette.Alpha(color, HeaderTintA), new Rect(x, y, w, h));
        if (!Has(HeaderStripe) || box.Width <= stripeW) return;
        // low tracks (24 px) keep half their height as the bar
        double inset = Math.Min(StripeInsetY, box.Height / 4), bh = box.Height - 2 * inset;
        if (bh > 0)
            ctx.DrawRectangle(Palette.Alpha(color, 1), null, new Rect(stripeX, box.Y + inset, stripeW, bh), stripeW / 2, stripeW / 2);
    }

    /// <summary>Whether a track colored <paramref name="c"/> shows <see cref="HeaderStripe"/>'s bar (its grip dots make room).</summary>
    public static bool HasStripe(Color? c) => c != null && Has(HeaderStripe);

    /// <summary>Where <see cref="HeaderStripe"/>'s bar goes in a header, from the track's indent: 2 px past the selected
    /// frame; the grip dots move <see cref="StripeGripShift"/> right to leave 3 px after it. The compact header has no
    /// room to move them: a thinner bar just before them.</summary>
    public const double StripeX = 6, StripeGripShift = 4, CompactStripeX = 4.5, CompactStripeW = 2;

    /// <summary>Width of <see cref="HeaderStripe"/>'s bar in the full header.</summary>
    public const double HeaderStripeW = StripeW;

    /// <summary>How far <see cref="HeaderStripe"/>'s bar stays from the box's top and bottom: past the curve of its
    /// corners, with a gap.</summary>
    const double StripeInsetY = 6;

    /// <summary>A lane's tint in its track's color (<see cref="LaneTint"/>).</summary>
    public static void DrawLane(DrawingContext ctx, Color? c, Rect lane)
    {
        if (c is { } color && Has(LaneTint)) ctx.FillRectangle(Palette.Alpha(color, LaneTintA), lane);
    }

    /// <summary>The color that marks a selected track: its own under <see cref="Selection"/>, else the accent.</summary>
    public static Color SelectionColor(Color? c) => c is { } color && Has(Selection) ? color : Palette.AccentColor;

    // --- picker ---------------------------------------------------------------------------------

    /// <summary>One chip per clip look with a sample (a made-up clip in the accent, drawn the same way), and a check box
    /// per mark. Follows <see cref="Changed"/> while shown, so "Restablecer" updates it.</summary>
    public static Control Picker()
    {
        var grid = new UniformGrid { Columns = AllClips.Length };
        var buttons = new List<ToggleButton>();
        foreach (string id in AllClips)
        {
            string name = Loc.T("set.look.trackColor.clips." + id);
            var b = new ToggleButton
            {
                Classes = { "chip" }, Tag = id, Margin = new Thickness(3), Padding = new Thickness(6, 12, 6, 8), IsChecked = id == Clips,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children = { new Sample(id) { Width = 104, Height = 40, HorizontalAlignment = HorizontalAlignment.Center }, new TextBlock { Text = name, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center } },
                },
            };
            AutomationProperties.SetName(b, name);
            b.Click += (_, _) => SetClips(id);
            buttons.Add(b);
            grid.Children.Add(b);
        }
        var list = new StackPanel { Spacing = 6, Margin = new Thickness(3, 10, 3, 0) };
        var boxes = new List<(string Key, CheckBox Box)>();
        foreach (string m in AllMarks)
        {
            var box = new CheckBox { IsChecked = Has(m), FontSize = 12, Content = Loc.T("set.look.trackColor.mark." + m) };
            box.IsCheckedChanged += (_, _) => SetMark(m, box.IsChecked == true);
            boxes.Add((m, box));
            list.Children.Add(box);
        }
        var panel = new StackPanel { Children = { grid, list } };
        void Sync()
        {
            foreach (var o in buttons) o.IsChecked = (string)o.Tag! == Clips;
            foreach (var (m, box) in boxes) box.IsChecked = Has(m);
        }
        panel.AttachedToVisualTree += (_, _) => { Changed += Sync; Sync(); };
        panel.DetachedFromVisualTree += (_, _) => Changed -= Sync;
        return panel;
    }

    /// <summary>The inspector's picker: one chip per header look with a sample (a made-up inspector in the accent), and a
    /// check box per mark. Follows <see cref="Changed"/> while shown.</summary>
    public static Control InspectorPicker()
    {
        var grid = new UniformGrid { Columns = AllHeads.Length };
        var buttons = new List<ToggleButton>();
        foreach (string id in AllHeads)
        {
            string name = Loc.T("set.look.trackColorInspector.head." + id);
            var b = new ToggleButton
            {
                Classes = { "chip" }, Tag = id, Margin = new Thickness(3), Padding = new Thickness(6, 12, 6, 8), IsChecked = id == Head,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children = { new InspectorSample(id) { Width = 104, Height = 40, HorizontalAlignment = HorizontalAlignment.Center }, new TextBlock { Text = name, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center } },
                },
            };
            AutomationProperties.SetName(b, name);
            b.Click += (_, _) => SetHead(id);
            buttons.Add(b);
            grid.Children.Add(b);
        }
        var list = new StackPanel { Spacing = 6, Margin = new Thickness(3, 10, 3, 0) };
        var boxes = new List<(string Key, CheckBox Box)>();
        foreach (string m in AllInspMarks)
        {
            var box = new CheckBox { IsChecked = HasInsp(m), FontSize = 12, Content = Loc.T("set.look.trackColorInspector.mark." + m) };
            box.IsCheckedChanged += (_, _) => SetInspMark(m, box.IsChecked == true);
            boxes.Add((m, box));
            list.Children.Add(box);
        }
        var pointer = new CheckBox { IsChecked = Pointer, FontSize = 12, Content = Loc.T("set.look.trackColorInspector.pointer") };
        pointer.IsCheckedChanged += (_, _) => SetPointer(pointer.IsChecked == true);
        list.Children.Add(pointer);
        var panel = new StackPanel { Children = { grid, list } };
        void Sync()
        {
            foreach (var o in buttons) o.IsChecked = (string)o.Tag! == Head;
            foreach (var (m, box) in boxes) box.IsChecked = HasInsp(m);
            pointer.IsChecked = Pointer;
        }
        panel.AttachedToVisualTree += (_, _) => { Changed += Sync; Sync(); };
        panel.DetachedFromVisualTree += (_, _) => Changed -= Sync;
        return panel;
    }

    /// <summary>A made-up inspector in one header look, in the accent: the name's box, the rule under it and two rows.</summary>
    sealed class InspectorSample(string look) : Control
    {
        public override void Render(DrawingContext ctx)
        {
            var size = Bounds.Size;
            if (size.Width < 8 || size.Height < 8) return;
            var box = new Rect(size).Deflate(0.5);
            var c = Palette.AccentColor;
            using (ctx.PushClip(new RoundedRect(box, 5)))
            {
                ctx.FillRectangle(Palette.Panel, box);
                double headH = Math.Round(box.Height * 0.42), y = box.Y + headH;
                if (look is HeadRule or HeadFill) ctx.FillRectangle(Palette.Alpha(c, look == HeadFill ? HeadFillA : HeadTintA), new Rect(box.X, box.Y, box.Width, headH));
                ctx.FillRectangle(Palette.Alpha(Palette.FgColor, 0.55), new Rect(box.X + 8, box.Y + headH / 2 - 2, box.Width * 0.45, 4));
                bool colored = look != HeadNone;
                double rule = colored ? HeadRuleW : 1;
                ctx.FillRectangle(colored ? Palette.Alpha(c, 1) : Palette.Line, new Rect(box.X, y, box.Width, rule));
                for (int i = 0; i < 2; i++)
                    ctx.FillRectangle(Palette.Alpha(Palette.FgColor, 0.18), new Rect(box.X + 8, y + rule + 6 + i * 8, box.Width * (0.7 - i * 0.2), 3));
            }
            ctx.DrawRectangle(null, Palette.Pen(Palette.Line), box, 5, 5);
        }
    }

    /// <summary>A made-up clip in one look, in the accent: its box, a strip of wave and, in <see cref="TitleBar"/>, the strip.</summary>
    sealed class Sample(string look) : Control
    {
        public override void Render(DrawingContext ctx)
        {
            var size = Bounds.Size;
            if (size.Width < 8 || size.Height < 8) return;
            var box = new Rect(size).Deflate(0.5);
            var c = Palette.AccentColor;
            var (fill, border, content) = Clip(look, c, false);
            ctx.DrawRectangle(fill, border, box, 5, 5);
            double top = box.Top + 4;
            using (ctx.PushClip(new RoundedRect(box, 5)))
            {
                if (look == TitleBar)
                {
                    ctx.FillRectangle(Palette.Alpha(c, 0.85), new Rect(box.X, box.Y, box.Width, 10));
                    top = box.Top + 13;
                }
                double mid = (top + box.Bottom - 4) / 2, half = (box.Bottom - 4 - top) / 2;
                for (double x = box.X + 4; x < box.Right - 4; x += 3)
                {
                    double t = (x - box.X) / box.Width, a = half * (0.25 + 0.7 * Math.Abs(Math.Sin(t * 9.1) * Math.Exp(-((t * 3) % 1) * 1.6)));
                    ctx.FillRectangle(content, new Rect(x, mid - a, 2, a * 2));
                }
            }
        }
    }
}
