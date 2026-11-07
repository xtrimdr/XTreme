using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using XtremeDaw.Projects;

namespace XtremeDaw.App.Controls;

/// <summary>
/// How the mixer's strips sit together and take their channel's color (asked by the user, #250, picked in
/// Configuración → Apariencia, like the timeline's <see cref="TrackColorLooks"/>). <see cref="Strips"/>
/// ("mixer.strips"): each strip a card apart (as it was), or the strips joined into one console, the whole height of
/// the mixer, with no gap and a groove between them (<see cref="Joined"/>, the default since the user picked it).
/// <see cref="ColorLook"/> ("mixer.color"): a line along the strip's top or a wider band (the default; the user dropped
/// the tinted strip, the filled head and no color). Changing either rebuilds the strips once; nothing here
/// runs while drawing.
/// </summary>
public static class MixerLooks
{
    public const string Cards = "cards", JoinedId = "joined";

    /// <summary>In the picker's order.</summary>
    public static readonly string[] AllStrips = [Cards, JoinedId];

    /// <summary>The factory layout and color (asked by the user, #250: "hacé que estos sean los valores por defecto").</summary>
    public const string DefaultStrips = JoinedId, DefaultColor = Band;

    public const string Edge = "edge", Band = "band";

    /// <summary>In the picker's order.</summary>
    public static readonly string[] AllColors = [Edge, Band];

    const string StripsKey = "mixer.strips", ColorKey = "mixer.color";

    public static string Strips { get; private set; } = DefaultStrips;

    public static string ColorLook { get; private set; } = DefaultColor;

    public static bool Joined => Strips == JoinedId;

    public static bool IsDefault => Strips == DefaultStrips && ColorLook == DefaultColor;

    /// <summary>Something changed: the mixer rebuilds its strips.</summary>
    public static event Action? Changed;

    /// <summary>Height of the color's line (<see cref="Edge"/>) and of <see cref="Band"/>'s band along the strip's top.</summary>
    public const double EdgeH = 2, BandH = 6;

    /// <summary>With the strips as cards, the color sits this far (px) inside the card's border, straight, with
    /// <see cref="MarkRadius"/> corners (#267: run edge to edge it took the card's rounded corners and looked bent).</summary>
    public const double MarkInset = 3, MarkRadius = 2;

    /// <summary>The joined strips' groove: a dark line (the window's background) with a lit one at its left, so each
    /// strip reads apart without a gap.</summary>
    public const double GrooveLitA = 0.06;

    public static void Load()
    {
        try
        {
            if (AppConfig.Get(StripsKey) is { } s && AllStrips.Contains(s)) Strips = s;
            if (AppConfig.Get(ColorKey) is { } c && AllColors.Contains(c)) ColorLook = c;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the defaults */ }
    }

    public static void SetStrips(string look)
    {
        if (!AllStrips.Contains(look) || look == Strips) return;
        Strips = look;
        Saved();
    }

    public static void SetColor(string look)
    {
        if (!AllColors.Contains(look) || look == ColorLook) return;
        ColorLook = look;
        Saved();
    }

    public static void Reset()
    {
        if (IsDefault) return;
        Strips = DefaultStrips;
        ColorLook = DefaultColor;
        Saved();
    }

    static void Saved()
    {
        Changed?.Invoke();
        string strips = Strips, color = ColorLook;
        _ = Task.Run(() =>
        {
            try
            {
                AppConfig.Set(StripsKey, strips);
                AppConfig.Set(ColorKey, color);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* keep this session's choice */ }
        });
    }

    // --- picker ---------------------------------------------------------------------------------

    /// <summary>A row of chips for the layout and one for the color, each with a sample (three made-up strips drawn
    /// the same way). Follows <see cref="Changed"/> while shown, so "Restablecer" updates it.</summary>
    public static Control Picker()
    {
        var buttons = new List<(ToggleButton Button, bool Layout)>();
        UniformGrid Row(string[] ids, bool layout)
        {
            var grid = new UniformGrid { Columns = ids.Length };
            foreach (string id in ids)
            {
                string name = Loc.T((layout ? "set.look.mixer.strips." : "set.look.mixer.color.") + id);
                var b = new ToggleButton
                {
                    Classes = { "chip" }, Tag = id, Margin = new Thickness(3), Padding = new Thickness(6, 12, 6, 8),
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                    Content = new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            new Sample(layout ? id : null, layout ? null : id) { Width = layout ? 96 : 76, Height = 48, HorizontalAlignment = HorizontalAlignment.Center },
                            new TextBlock { Text = name, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center },
                        },
                    },
                };
                AutomationProperties.SetName(b, name);
                b.Click += (_, _) => { if (layout) SetStrips(id); else SetColor(id); };
                buttons.Add((b, layout));
                grid.Children.Add(b);
            }
            return grid;
        }
        var colorTitle = new TextBlock { Text = Loc.T("set.look.mixer.colorTitle"), Classes = { "label" }, Margin = new Thickness(3, 10, 3, 2) };
        var panel = new StackPanel { Children = { Row(AllStrips, true), colorTitle, Row(AllColors, false) } };
        void Sync()
        {
            foreach (var (b, layout) in buttons) b.IsChecked = (string)b.Tag! == (layout ? Strips : ColorLook);
            foreach (var (b, _) in buttons) ((StackPanel)b.Content!).Children[0].InvalidateVisual();
        }
        Sync();
        panel.AttachedToVisualTree += (_, _) => { Changed += Sync; Sync(); };
        panel.DetachedFromVisualTree += (_, _) => Changed -= Sync;
        return panel;
    }

    /// <summary>Three made-up strips: a layout's sample shows them in the chosen color, a color's in the chosen
    /// layout, so each chip shows what it changes over what is set. The middle one has the accent, the others two fixed
    /// colors, as channels would.</summary>
    sealed class Sample(string? strips, string? color) : Control
    {
        static readonly Color[] Colors = [Color.FromRgb(0xf5, 0x9e, 0x0b), default, Color.FromRgb(0xa7, 0x8b, 0xfa)];

        public override void Render(DrawingContext ctx)
        {
            var size = Bounds.Size;
            if (size.Width < 12 || size.Height < 12) return;
            bool joined = (strips ?? Strips) == JoinedId;
            string look = color ?? ColorLook;
            var area = new Rect(size).Deflate(0.5);
            ctx.DrawRectangle(Palette.App, Palette.Pen(Palette.Line), area, 5, 5);
            double gap = joined ? 0 : 3, pad = joined ? 0 : 3;
            var inner = area.Deflate(joined ? 1 : 0);
            double w = (inner.Width - 2 * pad - 2 * gap) / 3;
            using (ctx.PushClip(new RoundedRect(inner, joined ? 4 : 5)))
                for (int i = 0; i < 3; i++)
                {
                    var c = i == 1 ? Palette.AccentColor : Colors[i];
                    var r = new Rect(inner.X + pad + i * (w + gap), inner.Y + pad, w, inner.Height - 2 * pad);
                    if (joined) ctx.FillRectangle(Palette.Panel, r);
                    else ctx.DrawRectangle(Palette.Panel, Palette.Pen(Palette.Line), r.Deflate(0.5), 3, 3);
                    double mark = look == Band ? BandH - 2 : EdgeH;
                    if (joined) ctx.FillRectangle(Palette.Alpha(c, 1), new Rect(r.X, r.Y, r.Width, mark));
                    else ctx.DrawRectangle(Palette.Alpha(c, 1), null, new Rect(r.X + 2, r.Y + 2, r.Width - 4, mark), 1, 1);
                    // a fader's slot and cap, so it reads as a strip
                    double fx = r.X + r.Width * 0.38, fy = r.Y + r.Height * 0.32;
                    ctx.FillRectangle(Palette.Hover, new Rect(fx, fy, 2, r.Height * 0.56));
                    ctx.FillRectangle(Palette.Alpha(Palette.FgColor, 0.55), new Rect(fx - 3, fy + r.Height * 0.2, 8, 4));
                    if (joined && i < 2)
                    {
                        ctx.FillRectangle(Palette.App, new Rect(r.Right - 1, r.Y, 1, r.Height));
                        ctx.FillRectangle(Palette.Alpha(Palette.FgColor, GrooveLitA), new Rect(r.Right - 2, r.Y, 1, r.Height));
                    }
                }
        }
    }
}
