namespace Compositor.Core;

/// <summary>
/// Photoshop's blend modes, in the order the app groups them (LayerAppearance.swift:4-24).
/// </summary>
/// <remarks>
/// The set is 24 modes, not the 9 that docs/project-format.md mentions for format version 3:
/// the format doc describes what version 3 *introduced*, while the app carries the full set and
/// its validator does not restrict the value set by version (LayerAppearance.swift:4-13).
/// Taking the doc at its word would reject perfectly legal projects.
///
/// The JSON form is the string in <see cref="BlendModeJson.Name"/>, not the C# member name.
/// </remarks>
public enum BlendMode
{
    Normal,
    Darken,
    Multiply,
    ColorBurn,
    LinearBurn,
    Lighten,
    Screen,
    ColorDodge,
    LinearDodge,
    Overlay,
    SoftLight,
    HardLight,
    VividLight,
    LinearLight,
    PinLight,
    HardMix,
    Difference,
    Exclusion,
    Subtract,
    Divide,
    Hue,
    Saturation,
    Color,
    Luminosity,
}

/// <summary>Maps <see cref="BlendMode"/> to and from its exact JSON string.</summary>
public static class BlendModeJson
{
    /// <summary>Every mode with its wire name, copied verbatim from LayerAppearance.swift:5-13.</summary>
    public static readonly IReadOnlyList<(BlendMode Mode, string Name)> All =
    [
        (BlendMode.Normal, "Normal"),
        (BlendMode.Darken, "Darken"),
        (BlendMode.Multiply, "Multiply"),
        (BlendMode.ColorBurn, "Color Burn"),
        (BlendMode.LinearBurn, "Linear Burn"),
        (BlendMode.Lighten, "Lighten"),
        (BlendMode.Screen, "Screen"),
        (BlendMode.ColorDodge, "Color Dodge"),
        (BlendMode.LinearDodge, "Linear Dodge (Add)"),
        (BlendMode.Overlay, "Overlay"),
        (BlendMode.SoftLight, "Soft Light"),
        (BlendMode.HardLight, "Hard Light"),
        (BlendMode.VividLight, "Vivid Light"),
        (BlendMode.LinearLight, "Linear Light"),
        (BlendMode.PinLight, "Pin Light"),
        (BlendMode.HardMix, "Hard Mix"),
        (BlendMode.Difference, "Difference"),
        (BlendMode.Exclusion, "Exclusion"),
        (BlendMode.Subtract, "Subtract"),
        (BlendMode.Divide, "Divide"),
        (BlendMode.Hue, "Hue"),
        (BlendMode.Saturation, "Saturation"),
        (BlendMode.Color, "Color"),
        (BlendMode.Luminosity, "Luminosity"),
    ];

    private static readonly Dictionary<BlendMode, string> ToName =
        All.ToDictionary(pair => pair.Mode, pair => pair.Name);

    private static readonly Dictionary<string, BlendMode> FromName =
        All.ToDictionary(pair => pair.Name, pair => pair.Mode, StringComparer.Ordinal);

    /// <summary>The mode's wire name. Throws for a value outside the enum.</summary>
    public static string Name(BlendMode mode) =>
        ToName.TryGetValue(mode, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a known blend mode.");

    /// <summary>Parses a wire name. Unknown names throw rather than silently degrading.</summary>
    public static BlendMode Parse(string name) =>
        FromName.TryGetValue(name, out var mode)
            ? mode
            : throw new FormatException($"'{name}' is not a Compositor blend mode.");
}
