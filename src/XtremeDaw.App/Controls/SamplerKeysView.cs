using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The sampler's keyboard: a click plays (lower on the key, harder), dragging slides over the keys and the right button
/// makes that key the root, marked in the accent. It shows seven octaves from C0, a piano's useful range, and raises
/// events: the window plays the notes. It never takes the focus, so the transport's shortcuts keep working.
/// </summary>
public sealed class SamplerKeysView : Control
{
    const int First = 12, Last = 108;
    static readonly bool[] Black = [false, true, false, true, false, false, true, false, true, false, true, false];
    const double BlackWidth = 0.62, BlackHeight = 0.6;
    const double Gap = 1, RootMarkH = 3;
    const int MinVelocity = 40;

    int root = 60, playing = -1;
    int whites;

    public SamplerKeysView()
    {
        Focusable = false;
        Height = 58;
        ClipToBounds = true;
        for (int k = First; k <= Last; k++)
            if (!Black[k % 12]) whites++;
    }

    public event Action<int, int>? Pressed;
    public event Action<int>? Lifted;
    public event Action<int>? RootPicked;

    public int Root
    {
        get => root;
        set
        {
            if (root == value) return;
            root = value;
            InvalidateVisual();
        }
    }

    double WhiteW => Bounds.Width / whites;

    /// <summary>The white keys left of <paramref name="pitch"/>.</summary>
    static int WhitesBefore(int pitch)
    {
        int n = 0;
        for (int k = First; k < pitch; k++)
            if (!Black[k % 12]) n++;
        return n;
    }

    double KeyX(int pitch)
    {
        double w = WhiteW, x = WhitesBefore(pitch) * w;
        return Black[pitch % 12] ? x - w * BlackWidth / 2 : x;
    }

    public override void Render(DrawingContext ctx)
    {
        double w = WhiteW, h = Bounds.Height, bw = w * BlackWidth;
        for (int k = First; k <= Last; k++)
        {
            if (Black[k % 12]) continue;
            double x = KeyX(k);
            ctx.FillRectangle(k == playing ? Palette.Accent : Palette.Dim, new Rect(x, 0, Math.Max(1, w - Gap), h), 2);
            if (k == root && k != playing) ctx.FillRectangle(Palette.Accent, new Rect(x, h - RootMarkH, Math.Max(1, w - Gap), RootMarkH), 1);
            // "Do4" / "C4" (cached by NoteNames and Palette), only where it fits whole on the key
            if (k % 12 == 0 && w >= 15 && Palette.Text(NoteNames.Octave(k / 12 - 1), 9, Palette.App) is var octave && octave.Width + 3 <= w)
                ctx.DrawText(octave, new Point(x + 2, h - 14));
        }
        for (int k = First; k <= Last; k++)
        {
            if (!Black[k % 12]) continue;
            double x = KeyX(k);
            ctx.FillRectangle(k == playing ? Palette.Accent : Palette.App, new Rect(x, 0, bw, h * BlackHeight), 2);
            if (k == root && k != playing) ctx.FillRectangle(Palette.Accent, new Rect(x, h * BlackHeight - RootMarkH, bw, RootMarkH), 1);
        }
    }

    int PitchAt(Point p)
    {
        double w = WhiteW;
        if (w <= 0) return -1;
        if (p.Y < Bounds.Height * BlackHeight)
            for (int k = First; k <= Last; k++)
                if (Black[k % 12] && p.X >= KeyX(k) && p.X < KeyX(k) + w * BlackWidth) return k;
        for (int k = First; k <= Last; k++)
            if (!Black[k % 12] && p.X >= KeyX(k) && p.X < KeyX(k) + w) return k;
        return -1;
    }

    int VelocityAt(Point p) => (int)Math.Clamp(MinVelocity + (127 - MinVelocity) * p.Y / Math.Max(1, Bounds.Height), MinVelocity, 127);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        int pitch = PitchAt(point.Position);
        e.Handled = true;
        if (point.Properties.IsRightButtonPressed)
        {
            if (pitch >= 0) RootPicked?.Invoke(pitch);
            return;
        }
        Play(pitch, VelocityAt(point.Position));
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (playing >= 0) Play(PitchAt(e.GetPosition(this)), VelocityAt(e.GetPosition(this)));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Play(-1, 0);
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        Play(-1, 0);
    }

    void Play(int pitch, int velocity)
    {
        if (pitch == playing) return;
        if (playing >= 0) Lifted?.Invoke(playing);
        playing = pitch;
        if (pitch >= 0) Pressed?.Invoke(pitch, velocity);
        InvalidateVisual();
    }
}
