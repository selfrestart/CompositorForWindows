using Compositor.Core;

namespace Compositor.Codecs;

/// <summary>
/// The four blend modes that work on a pixel's whole colour rather than channel by channel.
/// </summary>
/// <remarks>
/// Hue, Saturation, Color and Luminosity are independent of Photoshop — the W3C compositing
/// specification defines them on top of a luminosity function and two helpers, and that is what
/// Core Image implements too. Upstream reaches these four through Core Graphics
/// (LayerAppearance.swift:42-45), so this port is a reimplementation of a documented algorithm.
///
/// All functions operate on non-premultiplied values in 0…1, in the sRGB encoding.
/// </remarks>
internal static class NonSeparable
{
    /// <summary>Applies the mode to a backdrop and source colour, returning the blended colour.</summary>
    public static (double R, double G, double B) Composite(
        BlendMode mode,
        double br, double bg, double bb,
        double sr, double sg, double sb) => mode switch
    {
        // Hue: the source's hue and saturation, the backdrop's luminosity.
        BlendMode.Hue => SetLum(SetSat(sr, sg, sb, Sat(br, bg, bb)), Lum(br, bg, bb)),

        // Saturation: the backdrop's hue and luminosity, the source's saturation.
        BlendMode.Saturation => SetLum(SetSat(br, bg, bb, Sat(sr, sg, sb)), Lum(br, bg, bb)),

        // Color: the source's hue and saturation, the backdrop's luminosity.
        BlendMode.Color => SetLum(sr, sg, sb, Lum(br, bg, bb)),

        // Luminosity: the backdrop's hue and saturation, the source's luminosity.
        BlendMode.Luminosity => SetLum(br, bg, bb, Lum(sr, sg, sb)),

        _ => throw new NotSupportedException($"{mode} is a separable mode."),
    };

    /// <summary>
    /// <see cref="SetLum(double,double,double,double)"/> for a colour held as a triple, so the
    /// <c>Hue</c> and <c>Saturation</c> cases can pass the result of <c>SetSat</c> straight in.
    /// </summary>
    private static (double R, double G, double B) SetLum(
        (double R, double G, double B) color,
        double target) => SetLum(color.R, color.G, color.B, target);

    /// <summary>Rec. 601 weights, as the specification's luminosity function uses.</summary>
    private static double Lum(double r, double g, double b) =>
        (0.3 * r) + (0.59 * g) + (0.11 * b);

    /// <summary>
    /// Forces the colour's luminosity to <paramref name="target"/>, desaturating toward it where
    /// the result would leave the 0…1 cube.
    /// </summary>
    private static (double R, double G, double B) SetLum(double r, double g, double b, double target)
    {
        var d = target - Lum(r, g, b);
        var (cr, cg, cb) = Clip(r + d, g + d, b + d);
        var l = Lum(cr, cg, cb);

        if (l > 0 && l < 1)
        {
            var n = (1 - l) / l;
            cr = ((cr - l) * n) + cr;
            cg = ((cg - l) * n) + cg;
            cb = ((cb - l) * n) + cb;
        }

        return Clip(cr, cg, cb);
    }

    /// <summary>Pulls an out-of-range colour back into the cube by moving it toward its luminosity.</summary>
    private static (double R, double G, double B) Clip(double r, double g, double b)
    {
        var l = Lum(r, g, b);
        var min = Math.Min(r, Math.Min(g, b));
        var max = Math.Max(r, Math.Max(g, b));

        if (min < 0)
        {
            var n = l / (l - min);
            r = (l * (1 - n)) + (r * n);
            g = (l * (1 - n)) + (g * n);
            b = (l * (1 - n)) + (b * n);
        }

        if (max > 1)
        {
            var n = (1 - l) / (max - l);
            r = (l * (1 - n)) + (r * n);
            g = (l * (1 - n)) + (g * n);
            b = (l * (1 - n)) + (b * n);
        }

        return (r, g, b);
    }

    /// <summary>
    /// Forces the colour's saturation to <paramref name="target"/>, keeping the channels' relative
    /// order.
    /// </summary>
    /// <remarks>
    /// Written as the specification's <c>Swap(Min, Mid, Max)</c>: identify which channel is the
    /// smallest, middle and largest, then write the remapped values back in those positions. A
    /// naive three-expression version does not work, because the minimum and middle channels must
    /// be measured against the maximum.
    /// </remarks>
    private static (double R, double G, double B) SetSat(double r, double g, double b, double target)
    {
        var min = Math.Min(r, Math.Min(g, b));
        var max = Math.Max(r, Math.Max(g, b));
        if (max <= min)
        {
            // A grey has no saturation to set.
            return (0, 0, 0);
        }

        return (Scale(r), Scale(g), Scale(b));

        double Scale(double channel) => channel <= min
            ? 0
            : channel >= max
                ? target
                : (channel - min) * target / (max - min);
    }

    /// <summary>The saturation of a colour: maximum channel minus minimum channel.</summary>
    private static double Sat(double r, double g, double b) =>
        Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
}
