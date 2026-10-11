using System.Text.Json;
using System.Text.Json.Serialization;

namespace Compositor.Core;

/// <summary>
/// Writes a <see cref="Guid"/> the way Swift's <c>UUID</c> does: an upper-case hyphenated string.
/// </summary>
/// <remarks>
/// .NET's default is lower case, so every identifier in a manifest would differ from the macOS
/// writer's output — and <c>imageFile</c> names like <c>AAAAAAAA-….png</c> would stop matching.
/// Reading accepts either case, because a hand-edited manifest should still open.
/// </remarks>
public sealed class UpperCaseGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a string for a UUID, found {reader.TokenType}.");
        }

        var text = reader.GetString()!;
        return Guid.TryParse(text, out var value)
            ? value
            : throw new JsonException($"'{text}' is not a UUID.");
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("D").ToUpperInvariant());
}

/// <summary>The same rule for an optional UUID, so <c>null</c> still round-trips.</summary>
public sealed class UpperCaseNullableGuidConverter : JsonConverter<Guid?>
{
    private static readonly UpperCaseGuidConverter Inner = new();

    public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : Inner.Read(ref reader, typeof(Guid), options);

    public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
    {
        if (value is { } id)
        {
            Inner.Write(writer, id, options);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
