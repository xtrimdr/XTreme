using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;

/// <summary>
/// Draws the icon's record pixel by pixel (tools/make-icon.ps1 compiles it, so it stays in C# 5): a black vinyl with a
/// spun orange label and a black metal X on it, transparent around the disc. Everything is a formula of the pixel's
/// position, seen from straight above: every size is computed at its own resolution, so grooves never turn into moiré
/// from scaling a big image down. A surface with circular grooves shows a light as a wedge pointing at the centre,
/// so that is what is drawn instead of tracing the light. Small sizes get a flat version, which still reads at 16 px.
/// </summary>
public static class Vinilo
{
    // Geometry, in record radii
    const double Margin = 1.01;
    const double Label = 0.72;
    const double LabelRim = 0.012;
    const double LabelGap = 0.009;
    const double RingR = 0.682;
    const double RingW = 0.0045;
    const double Spindle = 0.04;
    const double SpindleHole = 0.021;
    // The X: half the stroke's width, half its height (a flat cut, so it reads as a letter), corner rounding and chisel
    const double XWidth = 0.118;
    const double XHeight = 0.365;
    const double XRound = 0.012;
    const double XBevel = 0.021;
    const double XBevelSlope = 0.85;
    // Where the two lights fall, as wedge angles; the wedge shows on both sides of the centre
    const double KeyAngle = 2.05;
    const double FillAngle = 0.42;
    // Grooves per radius unit, and the smooth bands between tracks as (radius, half width)
    const double Grooves = 90;
    static readonly double[,] Bands = { { 0.738, 0.018 }, { 0.825, 0.0035 }, { 0.905, 0.0035 }, { 0.978, 0.022 } };
    // Light direction over the record, for rims and bevels
    const double LightX = -0.56, LightY = 0.72, LightZ = 0.41;
    // Samples per pixel side in the rendered and the flat version
    const int Oversample = 8;

    // Flat version: the vinyl around the label (at least 1 px) and how much bigger than in the render the X is.
    // Brighter orange: at this size the label is one color and has to carry the contrast
    const double MarkVinyl = 0.14;
    const double MarkScale = 1.3;
    static readonly double[] MarkOrange = { 1.0, 0.478, 0.102 };
    static readonly double[] MarkBlack = { 0.043, 0.043, 0.051 };

    public static byte[] Png(int size, bool flat)
    {
        var px = new byte[size * size * 4];
        var vinyl = Math.Max(MarkVinyl, 2.0 / size);
        Parallel.For(0, size, row =>
        {
            for (var col = 0; col < size; col++)
            {
                double r = 0, g = 0, b = 0, a = 0;
                for (var sy = 0; sy < Oversample; sy++)
                    for (var sx = 0; sx < Oversample; sx++)
                    {
                        var x = Margin * (2.0 * (col + (sx + 0.5) / Oversample) / size - 1);
                        var y = Margin * (1 - 2.0 * (row + (sy + 0.5) / Oversample) / size);
                        var c = new double[3];
                        if (Math.Sqrt(x * x + y * y) > 1) continue;
                        if (flat) Mark(x, y, vinyl, c);
                        else Shade(x, y, c);
                        if (!flat)
                            for (var k = 0; k < 3; k++) c[k] = Tone(c[k]);
                        r += c[0]; g += c[1]; b += c[2]; a++;
                    }
                if (a == 0) continue;
                var i = (row * size + col) * 4;
                px[i] = Byte(b / a, flat);
                px[i + 1] = Byte(g / a, flat);
                px[i + 2] = Byte(r / a, flat);
                px[i + 3] = (byte)Math.Round(255 * a / (Oversample * Oversample));
            }
        });
        using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        using (var ms = new MemoryStream())
        {
            var data = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, bmp.PixelFormat);
            System.Runtime.InteropServices.Marshal.Copy(px, 0, data.Scan0, px.Length);
            bmp.UnlockBits(data);
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    static void Mark(double x, double y, double vinyl, double[] c)
    {
        var rad = Math.Sqrt(x * x + y * y);
        var color = rad > 1 - vinyl || XDist(x / MarkScale, y / MarkScale) < 0 ? MarkBlack : MarkOrange;
        Array.Copy(color, c, 3);
    }

    static void Shade(double x, double y, double[] c)
    {
        var rad = Math.Sqrt(x * x + y * y);
        var phi = Math.Atan2(y, x);
        if (rad > Label + LabelGap) Vinyl(rad, phi, x, y, c);
        else if (rad > Label)
        {
            // The step down from the label to the record
            c[0] = c[1] = c[2] = 0.002;
        }
        else
        {
            LabelFace(rad, phi, x, y, c);
            var d = XDist(x, y);
            if (d < 0) XFace(x, y, d, c);
            if (rad < Spindle) SpindleFace(rad, x, y, c);
        }
    }

    static void Vinyl(double rad, double phi, double x, double y, double[] c)
    {
        var smooth = 0.0;
        for (var k = 0; k < Bands.GetLength(0); k++)
            smooth = Math.Max(smooth, 1 - Step(Bands[k, 1], Bands[k, 1] + 0.002, Math.Abs(rad - Bands[k, 0])));
        var line = 0.6 + 0.4 * Math.Sin(2 * Math.PI * Grooves * rad);
        // Each track takes the light a little differently
        var ring = 0.72 + 0.56 * Noise(rad * 150) * (0.7 + 0.6 * Noise(rad * 31));
        var tight = 1.0 * Wedge(phi, KeyAngle, 0.13) + 0.5 * Wedge(phi, FillAngle, 0.11);
        var wide = 0.2 * Wedge(phi, KeyAngle, 0.42) + 0.1 * Wedge(phi, FillAngle, 0.38);
        var grooved = (tight + wide) * line * ring * 0.62;
        var gloss = 0.05 * Wedge(phi, KeyAngle, 0.5) + 0.02 * Wedge(phi, FillAngle, 0.5);
        var spec = grooved * (1 - smooth) + gloss * smooth + 0.012 * (1 - smooth) * ring;
        var warm = Wedge(phi, KeyAngle, 0.6);
        c[0] = 0.008 + spec * (0.92 + 0.08 * warm);
        c[1] = 0.008 + spec * 0.95;
        c[2] = 0.0095 + spec * (1.04 - 0.1 * warm);
        // The rounded outer edge
        var edge = Step(0.986, 1.0, rad);
        if (edge > 0)
        {
            var lit = Math.Max(0, (x * LightX + y * LightY) / rad);
            var add = edge * (0.025 + 0.55 * lit * lit);
            for (var k = 0; k < 3; k++) c[k] += add;
        }
    }

    static void LabelFace(double rad, double phi, double x, double y, double[] c)
    {
        var lightK = Wedge(phi, KeyAngle, 0.34);
        var lightF = Wedge(phi, FillAngle, 0.3);
        var sheen = 0.3 + 0.85 * lightK + 0.42 * lightF + 0.12 * Wedge(phi, KeyAngle, 0.9);
        var fine = 1 + 0.07 * (Noise(rad * 700) - 0.5) + 0.05 * (Noise(rad * 110) - 0.5);
        var hot = 0.2 * Math.Pow(lightK, 2.5) * (0.5 + 0.5 * Step(0.12, 0.6, rad));
        c[0] = (1.0 * sheen + hot) * fine;
        c[1] = (0.155 * sheen + 0.42 * hot) * fine;
        c[2] = (0.012 * sheen + 0.16 * hot) * fine;

        var dir = rad > 1e-6 ? (x * LightX + y * LightY) / rad : 0;
        // Engraved ring: its far wall catches the light, its near wall falls into shade
        var ring = rad - RingR;
        if (Math.Abs(ring) < RingW)
            Scale(c, ring * dir > 0 ? 1.5 : 0.28);
        // Raised lip around the label
        if (rad > Label - LabelRim)
            Scale(c, 0.55 + 1.15 * Math.Max(0, dir) + 0.1 * Math.Max(0, -dir));

        // The X sits above the label: a soft cast shadow and a tight contact one around it
        var cast = Step(-0.015, 0.06, XDist(x + 0.016, y - -0.03));
        var contact = Step(0, 0.02, XDist(x, y));
        Scale(c, (0.38 + 0.62 * cast) * (0.72 + 0.28 * contact));
    }

    static void XFace(double x, double y, double d, double[] c)
    {
        double nx, ny;
        if (-d < XBevel)
        {
            const double e = 0.0006;
            nx = (XDist(x + e, y) - XDist(x - e, y)) / (2 * e);
            ny = (XDist(x, y + e) - XDist(x, y - e)) / (2 * e);
            nx *= XBevelSlope; ny *= XBevelSlope;
        }
        else
        {
            // A hair of dome, so the top face isn't one flat value
            nx = x * 0.11; ny = y * 0.11;
        }
        var n = 1 / Math.Sqrt(nx * nx + ny * ny + 1);
        var nz = n; nx *= n; ny *= n;
        // Mirror of a straight-down view: what the metal reflects
        var rx = 2 * nz * nx; var ry = 2 * nz * ny; var rz = 2 * nz * nz - 1;
        var toward = rx * LightX + ry * LightY + rz * LightZ;
        var mirror = 0.011 + 0.42 * Math.Pow(Step(0.25, 1.0, toward), 3);
        // Facets that look down reflect the orange label
        var bounce = -d < XBevel ? 0.14 * Math.Max(0, -ry * 0.6 + 0.5) : 0;
        c[0] = mirror * 0.96 + bounce;
        c[1] = mirror * 0.98 + bounce * 0.155;
        c[2] = mirror * 1.06 + bounce * 0.012;
    }

    static void SpindleFace(double rad, double x, double y, double[] c)
    {
        if (rad < SpindleHole)
        {
            c[0] = c[1] = c[2] = 0.003;
            return;
        }
        var phi = Math.Atan2(y, x);
        var v = 0.2 + 0.85 * Wedge(phi, KeyAngle, 0.5) + 0.3 * Wedge(phi, FillAngle, 0.4);
        c[0] = 1.0 * v; c[1] = 0.155 * v; c[2] = 0.012 * v;
    }

    /// <summary>Signed distance to the X (negative inside): two stripes at 45° cut flat at the top and bottom.</summary>
    static double XDist(double x, double y)
    {
        var s = Math.Sqrt(0.5);
        var u = Math.Min(Math.Abs(x - y) * s, Math.Abs(x + y) * s) - XWidth + XRound;
        var v = Math.Abs(y) - XHeight + XRound;
        var ou = Math.Max(u, 0);
        var ov = Math.Max(v, 0);
        return Math.Sqrt(ou * ou + ov * ov) + Math.Min(Math.Max(u, v), 0) - XRound;
    }

    /// <summary>The bright wedge of a light on circular grooves: 1 on its angle and on the opposite side of the centre.</summary>
    static double Wedge(double phi, double angle, double width)
    {
        var d = Math.IEEERemainder(phi - angle, Math.PI);
        return Math.Exp(-d * d / (width * width));
    }

    static void Scale(double[] c, double k)
    {
        c[0] *= k; c[1] *= k; c[2] *= k;
    }

    static double Step(double a, double b, double t)
    {
        t = Math.Max(0, Math.Min(1, (t - a) / (b - a)));
        return t * t * (3 - 2 * t);
    }

    static double Hash(double i)
    {
        var s = Math.Sin(i * 12.9898) * 43758.5453;
        return s - Math.Floor(s);
    }

    static double Noise(double t)
    {
        var i = Math.Floor(t);
        var f = t - i;
        f = f * f * (3 - 2 * f);
        return Hash(i) + (Hash(i + 1) - Hash(i)) * f;
    }

    // Filmic curve: highlights roll off instead of clipping to a flat color
    static double Tone(double c)
    {
        return Math.Max(0, Math.Min(1, c * (2.51 * c + 0.03) / (c * (2.43 * c + 0.59) + 0.14)));
    }

    static byte Byte(double c, bool display)
    {
        if (!display)
            c = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        return (byte)Math.Round(255 * Math.Max(0, Math.Min(1, c)));
    }
}
