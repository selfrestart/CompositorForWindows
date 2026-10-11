namespace Compositor.Codecs;

/// <summary>The twelve adjustment kinds an adjustment layer can carry (LayerAdjustment.swift:4-9).</summary>
/// <remarks>
/// The JSON value is the string in <see cref="AdjustmentKindJson"/>'s map, which is what the manifest
/// stores. Two of them — <c>Gaussian Blur</c>, <c>Motion Blur</c> and <c>Add Noise</c> — arrived in
/// format version 9 and are gated by <see cref="Core.LayerValidator"/>.
/// </remarks>
public enum AdjustmentKind
{
    HueSaturation,
    Levels,
    Curves,
    Exposure,
    GradientMap,
    Grain,
    Invert,
    BlackWhite,
    ColorBalance,
    GaussianBlur,
    MotionBlur,
    AddNoise,
}

/// <summary>Maps <see cref="AdjustmentKind"/> to and from the exact JSON string.</summary>
public static class AdjustmentKindJson
{
    /// <summary>Every kind with its wire name, copied from LayerAdjustment.swift:5-9.</summary>
    public static readonly IReadOnlyList<(AdjustmentKind Kind, string Name)> All =
    [
        (AdjustmentKind.HueSaturation, "Hue/Saturation"),
        (AdjustmentKind.Levels, "Levels"),
        (AdjustmentKind.Curves, "Curves"),
        (AdjustmentKind.Exposure, "Exposure"),
        (AdjustmentKind.GradientMap, "Gradient Map"),
        (AdjustmentKind.Grain, "Grain"),
        (AdjustmentKind.Invert, "Invert"),
        (AdjustmentKind.BlackWhite, "Black & White"),
        (AdjustmentKind.ColorBalance, "Color Balance"),
        (AdjustmentKind.GaussianBlur, "Gaussian Blur"),
        (AdjustmentKind.MotionBlur, "Motion Blur"),
        (AdjustmentKind.AddNoise, "Add Noise"),
    ];

    private static readonly Dictionary<AdjustmentKind, string> ToName =
        All.ToDictionary(pair => pair.Kind, pair => pair.Name);

    private static readonly Dictionary<string, AdjustmentKind> FromName =
        All.ToDictionary(pair => pair.Name, pair => pair.Kind, StringComparer.Ordinal);

    /// <summary>The kind's wire name.</summary>
    public static string Name(AdjustmentKind kind) =>
        ToName.TryGetValue(kind, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a known adjustment kind.");

    /// <summary>Parses a wire name. Unknown names throw rather than silently degrading.</summary>
    public static AdjustmentKind Parse(string name) =>
        FromName.TryGetValue(name, out var kind)
            ? kind
            : throw new FormatException($"'{name}' is not a Compositor adjustment kind.");
}

/// <summary>
/// One channel's response curve as a 256-entry lookup table of values in 0…1.
/// </summary>
/// <remarks>
/// The native <c>levels_apply</c> kernel takes exactly this shape: three tables of 256 floats, and it
/// interpolates between neighbouring entries of the normalized value rather than indexing bytes
/// (LevelsPixels.c:3-16). Expressing every channel-wise adjustment as one of these means one
/// implementation of the apply step serves Levels, Curves, Black &amp; White and Exposure.
/// </remarks>
public sealed class ChannelTables
{
    /// <summary>Entries per channel.</summary>
    public const int Entries = 256;

    private readonly float[] _values;

    /// <summary>Creates tables from three channel LUTs, each of <see cref="Entries"/> values in 0…1.</summary>
    public ChannelTables(float[] red, float[] green, float[] blue)
    {
        ArgumentNullException.ThrowIfNull(red);
        ArgumentNullException.ThrowIfNull(green);
        ArgumentNullException.ThrowIfNull(blue);

        foreach (var (table, name) in new[] { (red, nameof(red)), (green, nameof(green)), (blue, nameof(blue)) })
        {
            if (table.Length != Entries)
            {
                throw new ArgumentException(
                    $"The {name} table holds {table.Length} entries; exactly {Entries} are required.",
                    name);
            }

            foreach (var value in table)
            {
                if (!float.IsFinite(value))
                {
                    throw new ArgumentException($"The {name} table holds a non-finite value.", name);
                }
            }
        }

        _values = [.. red, .. green, .. blue];
    }

    /// <summary>The three tables end to end, channel-major: 768 floats.</summary>
    public float[] Values => _values;

    /// <summary>Whether every channel is the identity, so applying this changes nothing.</summary>
    public bool IsIdentity
    {
        get
        {
            for (var channel = 0; channel < 3; channel++)
            {
                for (var index = 0; index < Entries; index++)
                {
                    var expected = index / 255f;
                    if (Math.Abs(_values[(channel * Entries) + index] - expected) > 1e-6)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }

    /// <summary>The identity tables: every value maps to itself.</summary>
    public static ChannelTables Identity()
    {
        var table = new float[Entries];
        for (var index = 0; index < Entries; index++)
        {
            table[index] = index / 255f;
        }

        return new ChannelTables(table, table, table);
    }

    /// <summary>
    /// Maps one normalized channel value through one of the tables, interpolating between entries.
    /// </summary>
    /// <remarks>
    /// This reproduces the kernel's own arithmetic (LevelsPixels.c:9-13): scale to 0…255, take the
    /// floor as the low entry, and lerp. The result is clamped to 0…1 so a table that overshoots
    /// cannot push a byte out of range.
    /// </remarks>
    public float Apply(int channel, float normalized)
    {
        var x = Math.Clamp(normalized, 0f, 1f) * 255f;
        var lo = (int)x;
        if (lo > 255)
        {
            lo = 255;
        }

        var hi = lo < 255 ? lo + 1 : 255;
        var offset = channel * Entries;
        var low = _values[offset + lo];
        var high = _values[offset + hi];
        return Math.Clamp(low + ((high - low) * (x - lo)), 0f, 1f);
    }
}

/// <summary>A resolved adjustment: what to do to a buffer, independent of how it was stored.</summary>
/// <remarks>
/// An adjustment layer affects everything composited below it within its folder
/// (docs/project-format.md:29), so the compositor applies this to the canvas in place at the layer's
/// position in the stack.
///
/// <para>
/// <b>Scope of the port so far.</b> Four kinds are implemented, all of them expressible as
/// per-channel lookup tables, which is also the shape the native kernel takes. The remaining eight
/// need either the native kernel's own entry points (Gradient Map, Grain, Color Balance), a
/// neighbouring-pixel pass (the two blurs, Add Noise), or the hue-band arithmetic that
/// <c>HueSaturation.swift</c> carries — and each of those is a separate piece of work rather than a
/// variation on this one.
/// </para>
/// </remarks>
public sealed class Adjustment
{
    private Adjustment(AdjustmentKind kind, ChannelTables tables)
    {
        Kind = kind;
        Tables = tables;
    }

    /// <summary>Which adjustment this is.</summary>
    public AdjustmentKind Kind { get; }

    /// <summary>The per-channel response, already resolved.</summary>
    public ChannelTables Tables { get; }

    /// <summary>Whether applying this would leave the buffer untouched.</summary>
    /// <remarks>
    /// A Black &amp; White adjustment is never the identity: even the most balanced weights turn a
    /// coloured pixel grey. Its tables are the identity because the grey conversion is not a
    /// per-channel operation.
    /// </remarks>
    public bool IsIdentity => Kind != AdjustmentKind.BlackWhite && Tables.IsIdentity;

    /// <summary>Invert: every channel becomes its complement (PixelInvert.swift:29-35).</summary>
    /// <remarks>
    /// The upstream implementation works in premultiplied space, where inverting is
    /// <c>c' = α − c</c> so transparency is preserved. In straight alpha that is simply
    /// <c>c' = 255 − c</c> with alpha untouched, which is what this table computes.
    /// </remarks>
    public static Adjustment Invert()
    {
        var table = new float[ChannelTables.Entries];
        for (var index = 0; index < ChannelTables.Entries; index++)
        {
            table[index] = (255 - index) / 255f;
        }

        return new Adjustment(AdjustmentKind.Invert, new ChannelTables(table, table, table));
    }

    /// <summary>
    /// Black &amp; White: a weighted grey, optionally tinted, as Photoshop's Black &amp; White does.
    /// </summary>
    /// <param name="weights">
    /// Three channel weights summing to 1, in the order red, green, blue. Photoshop's default is
    /// 0.4 red, 0.4 green, 0.2 blue — the same default the native kernel documents at
    /// AdjustPixels.h:20.
    /// </param>
    /// <param name="tint">Whether a tint is applied after the grey conversion.</param>
    /// <param name="tintHue">Tint hue in degrees.</param>
    /// <param name="tintSaturation">Tint saturation, 0…1.</param>
    public static Adjustment BlackWhite(
        IReadOnlyList<float>? weights = null,
        bool tint = false,
        double tintHue = 0,
        double tintSaturation = 0)
    {
        var w = weights ?? [0.4f, 0.4f, 0.2f];
        if (w.Count != 3)
        {
            throw new ArgumentException("Black & White needs exactly three channel weights.", nameof(weights));
        }

        var (wr, wg, wb) = (w[0], w[1], w[2]);
        var (tr, tg, tb) = tint ? TintFactors(tintHue, tintSaturation) : (1f, 1f, 1f);

        // The grey conversion reads all three channels at once, so it cannot be a per-channel table.
        // The tables stay the identity and ApplyTo takes the Black & White path.
        return new Adjustment(AdjustmentKind.BlackWhite, ChannelTables.Identity())
        {
            BlackWhiteWeights = (wr, wg, wb),
            BlackWhiteTint = (tr, tg, tb),
        };
    }

    /// <summary>
    /// Exposure: a linear gain in stops, with an optional offset and gamma.
    /// </summary>
    /// <param name="stops">Exposure in stops; each stop doubles the value.</param>
    /// <param name="offset">A value added after the gain, in 0…1 units.</param>
    /// <param name="gamma">Gamma applied last; 1 leaves it alone.</param>
    public static Adjustment Exposure(double stops = 0, double offset = 0, double gamma = 1)
    {
        if (!double.IsFinite(stops) || !double.IsFinite(offset) || !double.IsFinite(gamma) || gamma <= 0)
        {
            throw new ArgumentException("Exposure needs finite values and a positive gamma.");
        }

        var gain = Math.Pow(2, stops);
        var table = new float[ChannelTables.Entries];
        for (var index = 0; index < ChannelTables.Entries; index++)
        {
            var value = (index / 255.0 * gain) + offset;
            table[index] = (float)Math.Clamp(
                gamma == 1 ? value : Math.Pow(Math.Clamp(value, 0, 1), 1 / gamma),
                0,
                1);
        }

        return new Adjustment(AdjustmentKind.Exposure, new ChannelTables(table, table, table));
    }

    /// <summary>
    /// Curves: up to four channels of monotone cubic interpolation, mirroring
    /// <c>CurvesSettings</c> (Curves.swift:7-32).
    /// </summary>
    /// <param name="channels">
    /// Four point lists: composite first, then red, green, blue. Each must hold 2…32 points, start at
    /// x = 0 and end at x = 255, be strictly increasing in x, and stay within 0…255.
    /// </param>
    /// <remarks>
    /// <b>A deliberate divergence from the upstream implementation.</b> <c>CurvesSettings.apply</c>
    /// builds its table as <c>(1...3).flatMap { channel in (0...255).map { value(value($0, channel: channel), channel: 0) / 255 } }</c>
    /// (Curves.swift:37), which maps each pixel channel through that channel's own curve <i>and then</i>
    /// through the composite curve — so the composite channel is applied a second time to every
    /// channel, and only ever after the per-channel curve. Photographers expect the composite curve
    /// first and the per-channel curve after it, once. This port applies composite-then-channel, once
    /// each. The two agree whenever either curve is the identity, which is what the tests assert;
    /// they diverge on a non-identity composite plus a non-identity per-channel curve, and that case
    /// needs a macOS golden sample before it is settled.
    /// </remarks>
    public static Adjustment Curves(IReadOnlyList<IReadOnlyList<CurvePoint>> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Count != 4)
        {
            throw new ArgumentException($"Curves needs exactly four channels, got {channels.Count}.", nameof(channels));
        }

        var tables = new float[3][];
        for (var channel = 0; channel < 3; channel++)
        {
            var own = CurveTable(channels[channel + 1]);
            var composite = CurveTable(channels[0]);
            var table = new float[ChannelTables.Entries];
            for (var index = 0; index < ChannelTables.Entries; index++)
            {
                table[index] = own[(int)Math.Round(composite[index] * 255)];
            }

            tables[channel] = table;
        }

        return new Adjustment(AdjustmentKind.Curves, new ChannelTables(tables[0], tables[1], tables[2]));
    }

    /// <summary>
    /// Levels: per-channel input black and white points, gamma, and output range.
    /// </summary>
    /// <param name="channels">Four ranges: composite first, then red, green, blue; each 0…255.</param>
    /// <param name="gammas">Four gamma values: composite first, then red, green, blue.</param>
    public static Adjustment Levels(
        IReadOnlyList<(double Black, double White)> channels,
        IReadOnlyList<double>? gammas = null)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Count != 4)
        {
            throw new ArgumentException($"Levels needs exactly four channels, got {channels.Count}.", nameof(channels));
        }

        var gammaValues = gammas ?? [1, 1, 1, 1];
        if (gammaValues.Count != 4)
        {
            throw new ArgumentException($"Levels needs exactly four gammas, got {gammaValues.Count}.", nameof(gammas));
        }

        // The composite range and gamma run first, then the channel's own. A channel left at its
        // default 0…255 range with gamma 1 is the identity, so a composite-only adjustment behaves
        // exactly as if the per-channel step were absent.
        var composite = LevelsTable(channels[0], gammaValues[0]);
        var tables = new float[3][];
        for (var channel = 0; channel < 3; channel++)
        {
            var own = LevelsTable(channels[channel + 1], gammaValues[channel + 1]);
            var table = new float[ChannelTables.Entries];
            for (var index = 0; index < ChannelTables.Entries; index++)
            {
                var afterComposite = composite[index] * 255;
                var lo = (int)afterComposite;
                if (lo > 255)
                {
                    lo = 255;
                }

                var hi = lo < 255 ? lo + 1 : 255;
                var low = own[lo];
                var high = own[hi];
                table[index] = (float)Math.Clamp(low + ((high - low) * (afterComposite - lo)), 0, 1);
            }

            tables[channel] = table;
        }

        return new Adjustment(AdjustmentKind.Levels, new ChannelTables(tables[0], tables[1], tables[2]));
    }

    /// <summary>Black &amp; White's channel weights, or null for every other kind.</summary>
    public (float Red, float Green, float Blue)? BlackWhiteWeights { get; private init; }

    /// <summary>Black &amp; White's tint multipliers, or null when untinted.</summary>
    public (float Red, float Green, float Blue)? BlackWhiteTint { get; private init; }

    /// <summary>Applies this adjustment to a buffer in place.</summary>
    /// <remarks>
    /// Alpha is never touched: every adjustment here is a colour operation, and a mask's coverage is
    /// carried in its own buffer rather than in alpha. A fully transparent pixel is skipped, since
    /// it carries no colour to adjust.
    /// </remarks>
    public void ApplyTo(PixelBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (Kind == AdjustmentKind.BlackWhite && BlackWhiteWeights is { } weights)
        {
            ApplyBlackWhite(buffer, weights, BlackWhiteTint ?? (1f, 1f, 1f));
            return;
        }

        if (IsIdentity)
        {
            return;
        }

        for (var offset = 0; offset < buffer.Pixels.Length; offset += PixelBuffer.BytesPerPixel)
        {
            if (buffer.Pixels[offset + 3] == 0)
            {
                continue;
            }

            for (var channel = 0; channel < 3; channel++)
            {
                var normalized = buffer.Pixels[offset + channel] / 255f;
                var mapped = Tables.Apply(channel, normalized);
                buffer.Pixels[offset + channel] = (byte)Math.Round(Math.Clamp(mapped, 0f, 1f) * 255f);
            }
        }
    }

    /// <summary>
    /// The grey conversion: one weighted luminance, then an optional per-channel tint.
    /// </summary>
    /// <remarks>
    /// The weights are what make this different from a plain desaturation: the default 0.4/0.4/0.2 is
    /// Photoshop's, not Rec. 601's 0.299/0.587/0.114, and the two give visibly different reds.
    /// </remarks>
    private static void ApplyBlackWhite(
        PixelBuffer buffer,
        (float Red, float Green, float Blue) weights,
        (float Red, float Green, float Blue) tint)
    {
        var untinted = tint == (1f, 1f, 1f);

        for (var offset = 0; offset < buffer.Pixels.Length; offset += PixelBuffer.BytesPerPixel)
        {
            if (buffer.Pixels[offset + 3] == 0)
            {
                continue;
            }

            var grey = (buffer.Pixels[offset] * weights.Red)
                + (buffer.Pixels[offset + 1] * weights.Green)
                + (buffer.Pixels[offset + 2] * weights.Blue);

            if (untinted)
            {
                var value = (byte)Math.Round(Math.Clamp(grey, 0, 255));
                buffer.Pixels[offset] = value;
                buffer.Pixels[offset + 1] = value;
                buffer.Pixels[offset + 2] = value;
                continue;
            }

            buffer.Pixels[offset] = (byte)Math.Round(Math.Clamp(grey * tint.Red, 0, 255));
            buffer.Pixels[offset + 1] = (byte)Math.Round(Math.Clamp(grey * tint.Green, 0, 255));
            buffer.Pixels[offset + 2] = (byte)Math.Round(Math.Clamp(grey * tint.Blue, 0, 255));
        }
    }

    private static (float Red, float Green, float Blue) TintFactors(double hueDegrees, double saturation)
    {
        var hue = ((hueDegrees % 360) + 360) % 360;
        var s = Math.Clamp(saturation, 0, 1);

        // A fully saturated hue's channel weights, interpolating round the wheel: at 0° red is 1 and
        // green and blue are 0. Saturation scales each toward neutral, so 0 leaves the grey alone.
        var sector = (hue / 60) % 6;
        var red = Math.Clamp(Math.Abs(sector - 3) - 1, 0, 1);
        var green = Math.Clamp(2 - Math.Abs(sector - 2), 0, 1);
        var blue = Math.Clamp(2 - Math.Abs(sector - 4), 0, 1);

        return (
            (float)(1 - (s * (1 - red))),
            (float)(1 - (s * (1 - green))),
            (float)(1 - (s * (1 - blue))));
    }

    /// <summary>A curve's 256-entry table, by monotone cubic interpolation.</summary>
    private static float[] CurveTable(IReadOnlyList<CurvePoint> points)
    {
        ValidatePoints(points);

        var count = points.Count;
        var slopes = new double[count - 1];
        for (var index = 0; index < count - 1; index++)
        {
            slopes[index] = (points[index + 1].Y - points[index].Y)
                / (points[index + 1].X - points[index].X);
        }

        var table = new float[ChannelTables.Entries];
        for (var x = 0; x < ChannelTables.Entries; x++)
        {
            table[x] = (float)(Evaluate(points, slopes, x) / 255.0);
        }

        return table;
    }

    /// <summary>
    /// The shape-preserving cubic Hermite the upstream uses (Curves.swift:18-32).
    /// </summary>
    /// <remarks>
    /// The interior slopes are the harmonic mean of the neighbouring secants, and are zeroed at a
    /// sign change. That is what keeps the curve from overshooting between handles — a plain cubic
    /// spline would bulge past a control point and lighten or darken pixels that were meant to be
    /// left alone.
    /// </remarks>
    private static double Evaluate(IReadOnlyList<CurvePoint> points, double[] slopes, double x)
    {
        var index = 0;
        for (var i = points.Count - 1; i >= 0; i--)
        {
            if (points[i].X <= x)
            {
                index = Math.Min(points.Count - 2, i);
                break;
            }
        }

        double Slope(int j)
        {
            if (j == 0)
            {
                return slopes[0];
            }

            if (j == points.Count - 1)
            {
                return slopes[^1];
            }

            return slopes[j - 1] * slopes[j] <= 0
                ? 0
                : 2 / ((1 / slopes[j - 1]) + (1 / slopes[j]));
        }

        var h = points[index + 1].X - points[index].X;
        var t = Math.Clamp((x - points[index].X) / h, 0, 1);
        var y = ((2 * t * t * t) - (3 * t * t) + 1) * points[index].Y
            + ((t * t * t) - (2 * t * t) + t) * h * Slope(index)
            + ((-2 * t * t * t) + (3 * t * t)) * points[index + 1].Y
            + ((t * t * t) - (t * t)) * h * Slope(index + 1);

        return Math.Clamp(y, 0, 255);
    }

    private static void ValidatePoints(IReadOnlyList<CurvePoint> points)
    {
        if (points.Count is < 2 or > 32)
        {
            throw new ArgumentException($"A curve needs 2…32 points, got {points.Count}.");
        }

        if (points[0].X != 0 || points[^1].X != 255)
        {
            throw new ArgumentException("A curve must start at x = 0 and end at x = 255.");
        }

        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)
                || point.X is < 0 or > 255 || point.Y is < 0 or > 255)
            {
                throw new ArgumentException($"Curve point {index} is outside 0…255.");
            }

            if (index > 0 && points[index - 1].X >= point.X)
            {
                throw new ArgumentException("Curve points must be strictly increasing in x.");
            }
        }
    }

    /// <summary>A levels range's table.</summary>
    private static float[] LevelsTable((double Black, double White) range, double gamma)
    {
        if (!double.IsFinite(range.Black) || !double.IsFinite(range.White)
            || range.Black < 0 || range.White > 255 || range.White <= range.Black
            || !double.IsFinite(gamma) || gamma <= 0)
        {
            throw new ArgumentException(
                $"A levels range must be finite with 0 ≤ black < white ≤ 255 and a positive gamma, "
                + $"got {range} and gamma {gamma}.");
        }

        var span = range.White - range.Black;
        var table = new float[ChannelTables.Entries];
        for (var index = 0; index < ChannelTables.Entries; index++)
        {
            var normalized = Math.Clamp((index - range.Black) / span, 0, 1);
            table[index] = (float)(gamma == 1 ? normalized : Math.Pow(normalized, 1 / gamma));
        }

        return table;
    }
}

/// <summary>One handle on a curve, in 0…255 (Curves.swift:3-6).</summary>
public readonly record struct CurvePoint(double X, double Y);
