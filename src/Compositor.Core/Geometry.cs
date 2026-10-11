using System.Text.Json.Serialization;

namespace Compositor.Core;

/// <summary>How a layer's pixels are resampled when it is scaled (LayerTransform.swift:4-12).</summary>
[JsonConverter(typeof(LayerSamplingConverter))]
public enum LayerSampling
{
    Nearest,
    Smooth,
    High,
}

/// <summary>
/// A point in document pixels. Serializes as <c>{"x":…,"y":…}</c>, which is what Swift's Codable
/// does for <c>CGPoint</c>.
/// </summary>
/// <remarks>
/// The members are lower case on purpose: the naming policy only rewrites PascalCase, and Swift
/// writes <c>x</c>/<c>y</c>.
/// </remarks>
public readonly record struct DocPoint
{
    public DocPoint(double x, double y)
    {
        X = x;
        Y = y;
    }

    public double X { get; init; }

    public double Y { get; init; }
}

/// <summary>A size in document pixels. Serializes as <c>{"width":…,"height":…}</c>.</summary>
public readonly record struct DocSize
{
    public DocSize(double width, double height)
    {
        Width = width;
        Height = height;
    }

    public double Width { get; init; }

    public double Height { get; init; }
}

/// <summary>
/// Where a layer (or a mask) sits on the canvas, and how it is oriented
/// (LayerTransform.swift:18-24).
/// </summary>
/// <remarks>
/// Field order and optionality matter: <c>rotation</c>, <c>flipX</c>, <c>flipY</c> and
/// <c>sampling</c> have defaults in Swift, so they are always written on save. They are not
/// omitted when they hold their default values.
/// </remarks>
public sealed record LayerTransform
{
    // The naming policy leaves these PascalCase (there is no lower-case run to trigger on), but
    // Swift writes them lower case, so each is pinned explicitly.
    [JsonPropertyName("origin")]
    public required DocPoint Origin { get; init; }

    [JsonPropertyName("size")]
    public required DocSize Size { get; init; }

    [JsonPropertyName("rotation")]
    public double Rotation { get; init; }

    [JsonPropertyName("flipX")]
    public bool FlipX { get; init; }

    [JsonPropertyName("flipY")]
    public bool FlipY { get; init; }

    [JsonPropertyName("sampling")]
    public LayerSampling Sampling { get; init; } = LayerSampling.High;

    /// <summary>Center of the rectangle, before rotation.</summary>
    [JsonIgnore]
    public DocPoint Center => new(Origin.X + Size.Width / 2, Origin.Y + Size.Height / 2);

    /// <summary>Rotation in radians, normalized to a full turn.</summary>
    [JsonIgnore]
    public double Radians => Rotation % 360 * Math.PI / 180;

    /// <summary>
    /// Whether every value is finite and the rectangle is a legal size.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>LayerTransform.isValid</c> (LayerTransform.swift:27-31). A non-finite value here
    /// would poison every downstream rectangle computation, and the manifest is untrusted input.
    /// </remarks>
    public bool IsValid()
    {
        if (!double.IsFinite(Origin.X) || !double.IsFinite(Origin.Y)
            || !double.IsFinite(Size.Width) || !double.IsFinite(Size.Height)
            || !double.IsFinite(Rotation))
        {
            return false;
        }

        return Size.Width > 0 && Size.Height > 0
            && Size.Width <= DocumentLimits.MaxSide
            && Size.Height <= DocumentLimits.MaxSide
            && Math.Abs(Origin.X) <= DocumentLimits.MaxGuidePosition
            && Math.Abs(Origin.Y) <= DocumentLimits.MaxGuidePosition;
    }
}

/// <summary>Which way an alignment guide runs (Guides.swift:6).</summary>
[JsonConverter(typeof(GuideAxisConverter))]
public enum GuideAxis
{
    Horizontal,
    Vertical,
}

/// <summary>
/// An alignment guide dragged off a ruler (Guides.swift:5-10). Stored from format version 8.
/// </summary>
public sealed record CanvasGuide
{
    [JsonConverter(typeof(UpperCaseGuidConverter))]
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("axis")]
    public required GuideAxis Axis { get; init; }

    /// <summary>Position in document pixels. Finite, at most 1,000,000 in magnitude.</summary>
    [JsonPropertyName("position")]
    public required double Position { get; init; }
}
