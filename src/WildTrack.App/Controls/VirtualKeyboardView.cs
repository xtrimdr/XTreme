using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using WildTrack.Audio.Midi;

namespace WildTrack.App.Controls;

/// <summary>
/// The strip of the virtual keyboard: a stretch of the real keyboard, each key on the note it sounds: the computer's
/// letters sit where they land, each in its own small box, so the octave and the transposition move them and leave the
/// view alone, and the notes sounding light up in the accent. A click plays and dragging slides over the keys; the right
/// button dragged moves along the keyboard and the wheel scales it smoothly around the mouse, any size in between
/// (asked by the user: a scale, not jumps). Redrawn only when something changed or while the scale settles. It never
/// takes the focus, so the computer's keys keep playing.
/// </summary>
public sealed class VirtualKeyboardView : Control
{
    public const int MinOctaves = 1, MaxOctaves = 7, DefaultOctaves = 2;
    /// <summary>The white keys by semitone within the octave; -1 = a black key.</summary>
    static readonly int[] WhiteAt = [0, -1, 1, -1, 2, 3, -1, 4, -1, 5, -1, 6];
    /// <summary>The computer's key of each semitone, as this layout names it.</summary>
    static readonly string[] Letters = [.. Enumerable.Range(0, VirtualKeyboard.KeyCount).Select(s => RawKeys.Label(VirtualKeyboard.ScanOf(s)))];
    /// <summary>"C4" and so on, by octave counted from pitch 0.</summary>
    static readonly string[] OctaveNames = [.. Enumerable.Range(0, 11).Select(o => "C" + (o - 1))];
    const double BlackWidth = 0.62, BlackHeight = 0.6, Gap = 1;
    const int TopPitch = 127;
    /// <summary>How much a notch of the wheel scales it.</summary>
    const double ZoomPerNotch = 1.15;
    /// <summary>How fast the scale reaches where the wheel left it: most of the way in this many seconds.</summary>
    const double ZoomSettleSec = 0.07;
    /// <summary>Close enough to stop animating, in white keys.</summary>
    const double ZoomNear = 0.01;
    /// <summary>Under this width of a white key the letters don't fit, and under the next one neither do the C's.</summary>
    const double LetterWidth = 18, OctaveNameWidth = 20;
    /// <summary>The box around a letter.</summary>
    const double BadgeSize = 16, BadgeRadius = 4;

    readonly VirtualKeyboard keyboard;
    readonly Action<TimeSpan> nextFrame;
    readonly Action changed;
    int playing = -1;
    /// <summary>The white key at the left edge, counted from pitch 0; between two keys while it moves.</summary>
    double left;
    /// <summary>White keys on screen now, and where the wheel wants them.</summary>
    double shown = Whites(DefaultOctaves), shownTarget = Whites(DefaultOctaves);
    /// <summary>What stays still while it scales: this white key, this far across the strip.</summary>
    double anchorKey, anchorAcross;
    TimeSpan lastFrame;
    bool animating;
    bool scrolling;
    double scrollX, scrollLeft;

    public VirtualKeyboardView(VirtualKeyboard keyboard)
    {
        this.keyboard = keyboard;
        nextFrame = Frame;
        Focusable = false;
        ClipToBounds = true;
        Left = WhiteOf(Math.Clamp(keyboard.BasePitch + keyboard.Transpose, 0, TopPitch));
        changed = () => Dispatcher.UIThread.Post(InvalidateVisual);
        keyboard.Changed += changed;
    }

    /// <summary>It is gone: the keyboard stops telling it.</summary>
    public void Detach() => keyboard.Changed -= changed;

    static double Whites(int octaves) => 7 * octaves + 1;

    /// <summary>The wheel took it to another whole number of octaves.</summary>
    public event Action<int>? Zoomed;

    /// <summary>How many octaves it shows, to the nearest: setting another number scales it there around its middle.</summary>
    public int Octaves
    {
        get => (int)Math.Round((shownTarget - 1) / 7);
        set
        {
            int octaves = Math.Clamp(value, MinOctaves, MaxOctaves);
            if (octaves != Octaves) ScaleTo(Whites(octaves), 0.5);
        }
    }

    double Left
    {
        get => left;
        set
        {
            left = Math.Clamp(value, 0, Math.Max(0, WhiteOf(TopPitch) + 1 - shown));
            InvalidateVisual();
        }
    }

    double WhiteW => Bounds.Width / shown;

    /// <summary>The white key of a pitch, or the one left of a black key, counted from pitch 0.</summary>
    static int WhiteOf(int pitch)
    {
        int inside = pitch % 12;
        return pitch / 12 * 7 + (WhiteAt[inside] >= 0 ? WhiteAt[inside] : WhiteAt[inside - 1]);
    }

    static bool IsBlack(int pitch) => WhiteAt[pitch % 12] < 0;

    /// <summary>Where a key starts on screen.</summary>
    double KeyX(int pitch)
    {
        double w = WhiteW;
        return IsBlack(pitch) ? (WhiteOf(pitch) + 1 - left) * w - w * BlackWidth / 2 : (WhiteOf(pitch) - left) * w;
    }

    /// <summary>Starts scaling to <paramref name="whites"/> keys on screen, the point <paramref name="across"/> of the width standing still.</summary>
    void ScaleTo(double whites, double across)
    {
        shownTarget = Math.Clamp(whites, Whites(MinOctaves), Whites(MaxOctaves));
        anchorAcross = across;
        anchorKey = left + across * shown;
        if (animating) return;
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            shown = shownTarget;
            Left = anchorKey - across * shown;
            return;
        }
        animating = true;
        lastFrame = default;
        top.RequestAnimationFrame(nextFrame);
    }

    void Frame(TimeSpan now)
    {
        double dt = lastFrame == default ? 1.0 / 60 : (now - lastFrame).TotalSeconds;
        lastFrame = now;
        shown += (shownTarget - shown) * (1 - Math.Exp(-dt / ZoomSettleSec));
        bool settled = Math.Abs(shownTarget - shown) < ZoomNear;
        if (settled) shown = shownTarget;
        Left = anchorKey - anchorAcross * shown;
        if (settled) animating = false;
        else TopLevel.GetTopLevel(this)?.RequestAnimationFrame(nextFrame);
    }

    int PitchAt(Point p)
    {
        double w = WhiteW;
        if (w <= 0) return -1;
        if (p.Y < Bounds.Height * BlackHeight)
            for (int k = 0; k <= TopPitch; k++)
                if (IsBlack(k) && p.X >= KeyX(k) && p.X < KeyX(k) + w * BlackWidth) return k;
        for (int k = 0; k <= TopPitch; k++)
            if (!IsBlack(k) && p.X >= KeyX(k) && p.X < KeyX(k) + w) return k;
        return -1;
    }

    /// <summary>The computer's key that sounds this note; "" = none.</summary>
    string LetterOf(int pitch)
    {
        int semitone = pitch - keyboard.BasePitch - keyboard.Transpose;
        return semitone >= 0 && semitone < Letters.Length ? Letters[semitone] : "";
    }

    public override void Render(DrawingContext ctx)
    {
        double w = WhiteW, h = Bounds.Height, width = Bounds.Width;
        for (int k = 0; k <= TopPitch; k++)
        {
            double x = KeyX(k);
            if (IsBlack(k) || x + w <= 0 || x >= width) continue;
            bool on = keyboard.IsSounding(k);
            ctx.FillRectangle(on ? Palette.Accent : Palette.Dim, new Rect(x, 0, Math.Max(1, w - Gap), h), 3);
            var ink = on ? Palette.AccentFg : Palette.App;
            if (k % 12 == 0 && w >= OctaveNameWidth) ctx.DrawText(Palette.Text(OctaveNames[k / 12], 10.5, ink), new Point(x + 4, h - 16));
            if (w >= LetterWidth && LetterOf(k) is { Length: > 0 } letter) DrawLetter(ctx, letter, x + (w - Gap) / 2, h - 36, on);
        }
        double bw = w * BlackWidth;
        for (int k = 0; k <= TopPitch; k++)
        {
            double x = KeyX(k);
            if (!IsBlack(k) || x + bw <= 0 || x >= width) continue;
            bool on = keyboard.IsSounding(k);
            ctx.FillRectangle(on ? Palette.Accent : Palette.App, new Rect(x, 0, bw, h * BlackHeight), 3);
            if (w >= LetterWidth && LetterOf(k) is { Length: > 0 } letter) DrawLetter(ctx, letter, x + bw / 2, h * BlackHeight - BadgeSize - 4, on);
        }
    }

    /// <summary>A computer key's letter in its box, the same on a white key and a black one, like a keycap.</summary>
    static void DrawLetter(DrawingContext ctx, string letter, double center, double top, bool on)
    {
        var box = new Rect(center - BadgeSize / 2, top, BadgeSize, BadgeSize);
        ctx.DrawRectangle(on ? Palette.AccentFg : Palette.Raised, Palette.Pen(on ? Palette.AccentFg : Palette.Faint), box.Deflate(0.5), BadgeRadius, BadgeRadius);
        var text = Palette.Text(letter, 10.5, on ? Palette.Accent : Palette.Fg, Palette.UiBold);
        ctx.DrawText(text, new Point(center - text.Width / 2, top + (BadgeSize - text.Height) / 2));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            scrolling = true;
            scrollX = point.Position.X;
            scrollLeft = left;
        }
        else Play(PitchAt(point.Position));
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        if (scrolling && WhiteW > 0)
        {
            Left = scrollLeft - (at.X - scrollX) / WhiteW;
            anchorKey = left + anchorAcross * shown;
        }
        else if (playing >= 0) Play(PitchAt(at));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        scrolling = false;
        Play(-1);
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        scrolling = false;
        Play(-1);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        if (Bounds.Width <= 0) return;
        int octaves = Octaves;
        ScaleTo(shownTarget * Math.Pow(ZoomPerNotch, -e.Delta.Y), e.GetPosition(this).X / Bounds.Width);
        if (Octaves != octaves) Zoomed?.Invoke(Octaves);
    }

    /// <summary>The mouse's note moves to <paramref name="pitch"/> (-1 = none): it is the note under it, already transposed.</summary>
    void Play(int pitch)
    {
        if (pitch == playing) return;
        if (playing >= 0) keyboard.Release(playing);
        playing = pitch;
        if (pitch >= 0) keyboard.PressExact(pitch);
    }
}
