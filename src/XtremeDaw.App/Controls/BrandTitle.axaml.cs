using Avalonia.Controls;

namespace XtremeDaw.App.Controls;

/// <summary>
/// The splash's and "Acerca de"'s title block: the name, a strip of bars that reads like a waveform (built once, a
/// swell that fades out to the right, in the theme's accent) and the tagline in capitals. Static on purpose.
/// </summary>
public sealed partial class BrandTitle : UserControl
{
    const int Bars = 31;
    const double BarWidth = 3, BarGap = 3, StripHeight = 22, MinBar = 3;

    public BrandTitle()
    {
        InitializeComponent();
        TaglineText.Text = Loc.T("splash.tagline").ToUpperInvariant();
        for (int i = 0; i < Bars; i++)
        {
            // Two sines of different length make an irregular envelope; the ramp makes it swell, then thin out
            double t = (double)i / (Bars - 1);
            double wave = Math.Abs(Math.Sin(i * 0.62) * Math.Cos(i * 0.21 + 0.4));
            double h = MinBar + (StripHeight - MinBar) * Math.Clamp(wave * (0.35 + 1.1 * Math.Sin(Math.PI * Math.Pow(t, 0.7))), 0, 1);
            var bar = new Border
            {
                Width = BarWidth,
                Height = h,
                CornerRadius = new Avalonia.CornerRadius(1.5),
                Background = Palette.Accent,
                Opacity = 1 - 0.75 * t,
            };
            Canvas.SetLeft(bar, i * (BarWidth + BarGap));
            Canvas.SetTop(bar, (StripHeight - h) / 2);
            Beat.Children.Add(bar);
        }
    }

    /// <summary>Font size of the name; the rest of the block keeps its size.</summary>
    public double NameSize
    {
        get => NameText.FontSize;
        set => NameText.FontSize = value;
    }
}
