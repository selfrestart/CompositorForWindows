using System.Text.Json;
using System.Text.Json.Serialization;

namespace Compositor.Core;

/// <summary>
/// Reads and writes an enum by an explicit wire name instead of .NET's enum-name convention.
/// </summary>
/// <remarks>
/// Compositor's names do not match C# identifiers and are not uniformly cased: <c>"High quality"</c>
/// has a space and a lower-case q, <c>"Linear Dodge (Add)"</c> has parentheses, and the guide axes
/// are lower-case while everything else is title-case. .NET 8 has no
/// <c>[JsonStringEnumMemberName]</c> (that arrived in .NET 9), so the map is explicit.
///
/// An unknown name throws. That is deliberate: silently falling back to a default would corrupt a
/// project on the next save, and the whole point of this port is that a `.comp` written on either
/// platform round-trips unchanged.
/// </remarks>
public abstract class WireEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private readonly Dictionary<TEnum, string> _toName;
    private readonly Dictionary<string, TEnum> _fromName;

    protected WireEnumConverter(IEnumerable<(TEnum Value, string Name)> pairs)
    {
        var list = pairs.ToList();
        _toName = list.ToDictionary(pair => pair.Value, pair => pair.Name);
        _fromName = list.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);

        if (_toName.Count != list.Count)
        {
            throw new InvalidOperationException($"{typeof(TEnum).Name} has duplicate wire names.");
        }
    }

    public sealed override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a string for {typeof(TEnum).Name}, found {reader.TokenType}.");
        }

        var text = reader.GetString()!;
        return _fromName.TryGetValue(text, out var value)
            ? value
            : throw new JsonException($"'{text}' is not a known {typeof(TEnum).Name}.");
    }

    public sealed override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(
            _toName.TryGetValue(value, out var name)
                ? name
                : throw new JsonException($"Cannot write unknown {typeof(TEnum).Name} value '{value}'."));
}

/// <summary><see cref="LayerSampling"/> to and from its wire name.</summary>
public sealed class LayerSamplingConverter : WireEnumConverter<LayerSampling>
{
    public LayerSamplingConverter()
        : base(
        [
            (LayerSampling.Nearest, "Nearest"),
            (LayerSampling.Smooth, "Smooth"),
            (LayerSampling.High, "High quality"),
        ])
    {
    }
}

/// <summary><see cref="GuideAxis"/> to and from its wire name.</summary>
public sealed class GuideAxisConverter : WireEnumConverter<GuideAxis>
{
    public GuideAxisConverter()
        : base(
        [
            (GuideAxis.Horizontal, "horizontal"),
            (GuideAxis.Vertical, "vertical"),
        ])
    {
    }
}

/// <summary><see cref="BlendMode"/> to and from its wire name.</summary>
public sealed class BlendModeConverter : WireEnumConverter<BlendMode>
{
    public BlendModeConverter()
        : base(BlendModeJson.All)
    {
    }
}
