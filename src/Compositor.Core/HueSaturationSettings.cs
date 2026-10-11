using System.Text.Json.Serialization;

namespace Compositor.Core;

/// <summary>
/// Photoshop's per-range Hue/Saturation settings, the payload of an adjustment layer of kind
/// <c>Hue/Saturation</c> (HueSaturation.swift:170-206).
/// </summary>
/// <remarks>
/// See <see cref="SwiftEnumDictionaryConverter{TValue}"/> for why <see cref="Adjustments"/> and
/// <see cref="Bands"/> serialise as flat arrays and not JSON objects. That is the single most
/// surprising part of this format and the one most likely to be implemented wrongly.
/// </remarks>
public sealed record HueSaturationSettings
{
    /// <summary>Per-range hue/saturation/lightness offsets, keyed by range name.</summary>
    [JsonPropertyName("adjustments")]
    [JsonConverter(typeof(SwiftEnumDictionaryConverter<RangeAdjustment>))]
    public Dictionary<string, RangeAdjustment> Adjustments { get; init; } = [];

    /// <summary>Per-range hue bands, keyed by range name.</summary>
    [JsonPropertyName("bands")]
    [JsonConverter(typeof(SwiftEnumDictionaryConverter<HueBand>))]
    public Dictionary<string, HueBand> Bands { get; init; } = [];

    [JsonPropertyName("colorize")]
    public bool Colorize { get; init; }

    /// <summary>-360…360.</summary>
    [JsonPropertyName("hue")]
    public double Hue { get; init; }

    /// <summary>-100…100.</summary>
    [JsonPropertyName("lightness")]
    public double Lightness { get; init; }

    /// <summary>-100…100.</summary>
    [JsonPropertyName("saturation")]
    public double Saturation { get; init; }
}

/// <summary>One range's offsets (HueSaturation.swift:162-168).</summary>
public sealed record RangeAdjustment
{
    /// <summary>-360…360.</summary>
    [JsonPropertyName("hue")]
    public double Hue { get; init; }

    /// <summary>-100…100.</summary>
    [JsonPropertyName("lightness")]
    public double Lightness { get; init; }

    /// <summary>-100…100.</summary>
    [JsonPropertyName("saturation")]
    public double Saturation { get; init; }
}

/// <summary>
/// One range's hue band in degrees, wrapping at 360 (HueSaturation.swift:26-30).
/// </summary>
public sealed record HueBand
{
    [JsonPropertyName("falloffStart")]
    public double FalloffStart { get; init; }

    [JsonPropertyName("rangeStart")]
    public double RangeStart { get; init; }

    [JsonPropertyName("rangeEnd")]
    public double RangeEnd { get; init; }

    [JsonPropertyName("falloffEnd")]
    public double FalloffEnd { get; init; }
}

/// <summary>The seven colour ranges, with the names that appear as dictionary keys.</summary>
public static class ColorRanges
{
    /// <summary>Every range, in the app's order (HueSaturation.swift:6-7).</summary>
    public static readonly IReadOnlyList<string> All =
    [
        "Master", "Reds", "Yellows", "Greens", "Cyans", "Blues", "Magentas",
    ];

    /// <summary>Photoshop's starting band for each range (HueSaturation.swift:10-19).</summary>
    public static HueBand DefaultBand(string range) => range switch
    {
        "Master" => new HueBand { FalloffStart = 0, RangeStart = 0, RangeEnd = 360, FalloffEnd = 360 },
        "Reds" => new HueBand { FalloffStart = 315, RangeStart = 345, RangeEnd = 15, FalloffEnd = 45 },
        "Yellows" => new HueBand { FalloffStart = 15, RangeStart = 45, RangeEnd = 75, FalloffEnd = 105 },
        "Greens" => new HueBand { FalloffStart = 75, RangeStart = 105, RangeEnd = 135, FalloffEnd = 165 },
        "Cyans" => new HueBand { FalloffStart = 135, RangeStart = 165, RangeEnd = 195, FalloffEnd = 225 },
        "Blues" => new HueBand { FalloffStart = 195, RangeStart = 225, RangeEnd = 255, FalloffEnd = 285 },
        "Magentas" => new HueBand { FalloffStart = 255, RangeStart = 285, RangeEnd = 315, FalloffEnd = 345 },
        _ => throw new ArgumentOutOfRangeException(nameof(range), range, "Not a Compositor colour range."),
    };

    /// <summary>The bands a fresh settings value carries: every range at its default.</summary>
    public static Dictionary<string, HueBand> DefaultBands() =>
        All.ToDictionary(range => range, DefaultBand, StringComparer.Ordinal);
}
