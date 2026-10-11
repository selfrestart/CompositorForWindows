namespace Compositor.Codecs;

/// <summary>
/// The separable blend functions, defined on values in <b>0…1 in the sRGB encoding</b>.
/// </summary>
/// <remarks>
/// <b>Do not linearise before calling these.</b> The upstream project records the real numbers
/// that this costs (SeparableBlend.swift:14-16): over 40% grey, an 80% grey layer dodges to 62%
/// instead of Photoshop's 100%, and burns to 0% instead of 25%. The error is subtle on screen and
/// total in the numbers, and only a golden comparison catches it.
///
/// The formulas are the standard separable blend functions (the W3C compositing specification,
/// which is also what Photoshop and Core Image implement). Upstream reaches them two ways —
/// Core Graphics for the modes it gets right, Core Image for the eleven it does not
/// (LayerAppearance.swift:51-69) — and this port implements them directly, which is both simpler
/// and testable without a graphics stack.
/// </remarks>
internal static class Blend
{
    /// <summary>Multiplies: <c>cb × cs</c>. Photoshop's Multiply.</summary>
    public static double Multiply(double cb, double cs) => cb * cs;

    /// <summary>Screens: <c>cb + cs − cb × cs</c>.</summary>
    public static double Screen(double cb, double cs) => cb + cs - (cb * cs);

    /// <summary>Overlay: Hard Light with the operands swapped.</summary>
    public static double Overlay(double cb, double cs) => HardLight(cs, cb);

    public static double Darken(double cb, double cs) => Math.Min(cb, cs);

    public static double Lighten(double cb, double cs) => Math.Max(cb, cs);

    public static double Difference(double cb, double cs) => Math.Abs(cb - cs);

    /// <summary>Exclusion: <c>cb + cs − 2 × cb × cs</c>.</summary>
    public static double Exclusion(double cb, double cs) => cb + cs - (2 * cb * cs);

    /// <summary>Color Dodge: <c>cb / (1 − cs)</c>, with a white source forcing white.</summary>
    public static double ColorDodge(double cb, double cs) =>
        cs >= 1 ? 1 : Math.Min(1, cb / (1 - cs));

    /// <summary>Color Burn: <c>1 − (1 − cb) / cs</c>, with a black source forcing black.</summary>
    public static double ColorBurn(double cb, double cs) =>
        cs <= 0 ? 0 : 1 - Math.Min(1, (1 - cb) / cs);

    /// <summary>Hard Light: the linear continuation of Multiply and Screen, keyed on the source.</summary>
    public static double HardLight(double cb, double cs) =>
        cs <= 0.5 ? Multiply(cb, 2 * cs) : Screen(cb, (2 * cs) - 1);

    /// <summary>
    /// Soft Light, in the piecewise form Photoshop and Core Image share, keyed on the source.
    /// </summary>
    /// <remarks>
    /// Three branches, and the middle one is the one people get wrong:
    ///
    /// <code>
    /// cs ≤ 0.25 : cb − (1 − 2·cs)·cb·(1 − cb) − cs·(D(cb) − cb)
    /// cs ≤ 0.50 : cb + (2·cs − 1)·(D(cb) − cb)
    /// otherwise : cb + (2·cs − 1)·(D(cb) − cb)
    /// </code>
    ///
    /// The first two branches differ only in the extra term, and the second and third share the
    /// same expression — so the real structure is "a correction below 0.25, then a linear ramp".
    /// <c>D(cb)</c> is the polynomial <c>((16·cb − 12)·cb + 4)·cb</c> below the 0.25 threshold and
    /// <c>sqrt(cb)</c> above it, which is what makes the two halves meet.
    /// </remarks>
    public static double SoftLight(double cb, double cs)
    {
        if (cs <= 0.25)
        {
            return cb - ((1 - (2 * cs)) * cb * (1 - cb)) - (cs * (Curve(cb) - cb));
        }

        return cb + (((2 * cs) - 1) * (Root(cb) - cb));
    }

    /// <summary>Linear Burn: <c>cb + cs − 1</c>, floored at 0.</summary>
    public static double LinearBurn(double cb, double cs) => Math.Max(0, cb + cs - 1);

    /// <summary>Linear Dodge (Add): <c>cb + cs</c>, capped at 1.</summary>
    public static double LinearDodge(double cb, double cs) => Math.Min(1, cb + cs);

    /// <summary>Vivid Light: Color Burn below mid source, Color Dodge above.</summary>
    public static double VividLight(double cb, double cs) =>
        cs <= 0.5 ? ColorBurn(cb, 2 * cs) : ColorDodge(cb, (2 * cs) - 1);

    /// <summary>Linear Light: Linear Burn below mid source, Linear Dodge above.</summary>
    public static double LinearLight(double cb, double cs) =>
        cs <= 0.5 ? LinearBurn(cb, 2 * cs) : LinearDodge(cb, (2 * cs) - 1);

    /// <summary>
    /// Pin Light: Darken below mid source, Lighten above — a per-channel choice of one operand or
    /// the other rather than a computed value.
    /// </summary>
    public static double PinLight(double cb, double cs) =>
        cs <= 0.5 ? Darken(cb, 2 * cs) : Lighten(cb, (2 * cs) - 1);

    /// <summary>Hard Mix: each channel forced to black or white by whether Vivid Light reaches mid.</summary>
    public static double HardMix(double cb, double cs) => VividLight(cb, cs) < 0.5 ? 0 : 1;

    /// <summary>Subtract: <c>cb − cs</c>, floored at 0.</summary>
    public static double Subtract(double cb, double cs) => Math.Max(0, cb - cs);

    /// <summary>Divide: <c>cb / cs</c>, capped at 1, with a black divisor forcing white.</summary>
    public static double Divide(double cb, double cs) =>
        cs <= 0 ? 1 : Math.Min(1, cb / cs);

    /// <summary>
    /// <c>D(cb)</c> below the threshold: the polynomial that meets <c>sqrt</c> at 0.25.
    /// </summary>
    private static double Curve(double cb) => (((16 * cb) - 12) * cb + 4) * cb;

    /// <summary><c>D(cb)</c> above the threshold.</summary>
    private static double Root(double cb) => Math.Sqrt(cb);
}
