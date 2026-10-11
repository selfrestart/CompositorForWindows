using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Compositor.Core;

/// <summary>
/// The manifest of a <c>.comp</c> project: <c>manifest.json</c> at the package root.
/// Mirrors <c>ProjectManifest</c> in ProjectStore.swift:13-31.
/// </summary>
/// <remarks>
/// Properties are declared in alphabetical order so the emitted JSON matches Swift's
/// <c>.sortedKeys</c> output. See <see cref="CompJson"/> for why that matters.
///
/// Nullability is load-bearing. In Swift, <c>let imageFile: String?</c> has no default and is
/// therefore always written (as <c>null</c> when absent), while <c>var opacity: Double? = nil</c>
/// uses <c>encodeIfPresent</c> and omits the key entirely. Both behaviours are reproduced below
/// with <see cref="JsonIgnoreCondition.WhenWritingNull"/>.
/// </remarks>
public sealed record ProjectManifest
{
    /// <summary>The format version new saves write (ProjectStore.swift:15).</summary>
    public const int CurrentVersion = 11;

    /// <summary>Every version <c>load</c> accepts (ProjectStore.swift:18).</summary>
    public const int MinSupportedVersion = 1;

    /// <summary>Optional; when present it must name a layer in <see cref="Layers"/>.</summary>
    [JsonConverter(typeof(UpperCaseNullableGuidConverter))]
    [JsonPropertyName("activeLayerID")]
    public Guid? ActiveLayerID { get; init; }

    [JsonPropertyName("colorSpace")]
    public string ColorSpace { get; init; } = "sRGB";

    [JsonConverter(typeof(UpperCaseGuidConverter))]
    [JsonPropertyName("documentID")]
    public required Guid DocumentID { get; init; }

    [JsonPropertyName("format")]
    public string Format { get; init; } = "com.compositor.project";

    /// <summary>Alignment guides. Missing on format versions 1-7.</summary>
    [JsonPropertyName("guides")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CanvasGuide>? Guides { get; init; }

    [JsonPropertyName("height")]
    public required int Height { get; init; }

    /// <summary>Bottom-to-top sibling order.</summary>
    [JsonPropertyName("layers")]
    public required List<ProjectLayerRecord> Layers { get; init; }

    /// <summary>Pixels per inch. Older version-1 projects default to 72.</summary>
    [JsonPropertyName("resolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Resolution { get; init; }

    [JsonPropertyName("version")]
    public int Version { get; init; } = CurrentVersion;

    [JsonPropertyName("width")]
    public required int Width { get; init; }
}

/// <summary>One layer or group record (ProjectStore.swift:33-56).</summary>
public sealed record ProjectLayerRecord
{
    /// <summary>
    /// An adjustment layer's settings. Captured as raw JSON for Wave 0: the sub-schema differs per
    /// <c>kind</c> and is not needed to composite a plain layer stack yet. Round-trips losslessly.
    /// </summary>
    [JsonPropertyName("adjustment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Adjustment { get; init; }

    /// <summary>Missing means Normal.</summary>
    [JsonConverter(typeof(BlendModeConverter))]
    [JsonPropertyName("blendMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BlendMode? BlendMode { get; init; }

    /// <summary>Stroke, shadow, glow. Raw JSON for Wave 0; round-trips losslessly.</summary>
    [JsonPropertyName("effects")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Effects { get; init; }

    [JsonConverter(typeof(UpperCaseGuidConverter))]
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    /// <summary>
    /// Always written, <c>null</c> included: the Swift property is <c>let imageFile: String?</c>
    /// with no default. Blank layers and groups have no image asset.
    /// </summary>
    [JsonPropertyName("imageFile")]
    public string? ImageFile { get; init; }

    [JsonPropertyName("isGroup")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsGroup { get; init; }

    [JsonPropertyName("isVisible")]
    public required bool IsVisible { get; init; }

    /// <summary>Defaults to true when a mask exists.</summary>
    [JsonPropertyName("maskEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MaskEnabled { get; init; }

    /// <summary><c>&lt;layer UUID&gt;.mask.png</c> under <c>images/</c>.</summary>
    [JsonPropertyName("maskFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MaskFile { get; init; }

    /// <summary>Null (older projects) means linked.</summary>
    [JsonPropertyName("maskLinked")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MaskLinked { get; init; }

    /// <summary>A mask moved apart from its layer: where it sits on the document.</summary>
    [JsonPropertyName("maskPlacement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LayerTransform? MaskPlacement { get; init; }

    /// <summary>The lower sibling supplying live alpha; the app calls this a clipping mask.</summary>
    [JsonConverter(typeof(UpperCaseNullableGuidConverter))]
    [JsonPropertyName("maskSourceID")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? MaskSourceID { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Finite, 0-1. Missing means full opacity.</summary>
    [JsonPropertyName("opacity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Opacity { get; init; }

    [JsonConverter(typeof(UpperCaseNullableGuidConverter))]
    [JsonPropertyName("parentID")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ParentID { get; init; }

    /// <summary>A shape layer's style. Raw JSON for Wave 0; round-trips losslessly.</summary>
    [JsonPropertyName("shape")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Shape { get; init; }

    /// <summary>Text metadata. Raw JSON for Wave 0; round-trips losslessly.</summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Text { get; init; }

    [JsonPropertyName("transform")]
    public required LayerTransform Transform { get; init; }
}

/// <summary>
/// The JSON dialect used by <c>manifest.json</c>, matching Swift's encoder byte for byte.
/// </summary>
/// <remarks>
/// Three details are load-bearing. The first two were verified experimentally; the third comes from
/// reading ProjectStore.swift:108-109.
///
/// <list type="number">
/// <item>
/// <b>Sorted keys.</b> ProjectStore.swift:109 sets <c>.sortedKeys</c>. .NET has no equivalent
/// switch, so <see cref="SortedProperties"/> reorders every object's properties by name during
/// metadata construction. A source-generated context would emit declaration order instead.
/// </item>
/// <item>
/// <b>Relaxed escaping.</b> .NET's default encoder writes <c>&lt; &gt; &amp; +</c> and non-ASCII as
/// <c>\uXXXX</c>, while Foundation writes UTF-8 directly. Verified: a layer named
/// <c>Layer &lt;A&gt; &amp; "B" + 中文</c> serializes by default as
/// <c>"Layer \u003CA\u003E \u0026 \u0022B\u0022 \u002B \u4E2D\u6587"</c>. The two platforms would
/// disagree on bytes for any such name, which would also trip macOS's external-change watcher.
/// </item>
/// <item>
/// <b>Two-space indent.</b> <c>WriteIndented</c> already emits two spaces and <c>\n</c>, matching
/// <c>prettyPrinted</c>. Verified.
/// </item>
/// </list>
/// </remarks>
public static class CompJson
{
    /// <summary>Options for reading and writing a manifest.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        TypeInfoResolver = SortedProperties,

        // Swift's JSONEncoder defaults to .useDefaultKeys, which emits property names unchanged --
        // and those names are camelCase. .NET's default is PascalCase, so without this every key
        // would differ from the macOS writer's. Verified: omitting it produced "Height", "Layers",
        // "BlendMode".
        //
        // This also makes the ordinal sort below correct, because the name a property reports is
        // already the camelCase wire name by the time the modifier runs.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // Custom converters live here, not on the type info: .NET 8's JsonTypeInfo has no
        // Converters collection. They take effect ahead of the built-in enum handling.
        Converters =
        {
            new BlendModeConverter(),
            new LayerSamplingConverter(),
            new GuideAxisConverter(),
            new UpperCaseGuidConverter(),
        },
    };

    /// <summary>Single-line options for small internal payloads.</summary>
    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// A resolver that puts every object's properties in name order, reproducing Swift's
    /// <c>.sortedKeys</c>. Ordinal comparison matches Foundation's ordering of ASCII keys.
    /// </summary>
    /// <remarks>
    /// The custom converters live in <see cref="Options"/>.Converters rather than here: .NET 8 has
    /// no <c>JsonTypeInfo.Converters</c> collection, and a converter added to the options is
    /// consulted before the built-in enum handling.
    /// </remarks>
    public static readonly IJsonTypeInfoResolver SortedProperties =
        new DefaultJsonTypeInfoResolver
        {
            Modifiers = { SortProperties },
        };

    private static void SortProperties(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object || typeInfo.Properties.Count < 2)
        {
            return;
        }

        var sorted = typeInfo.Properties.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
        typeInfo.Properties.Clear();
        foreach (var property in sorted)
        {
            typeInfo.Properties.Add(property);
        }
    }
}
