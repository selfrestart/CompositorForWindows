using System.Text.Json;
using System.Text.Json.Serialization;

namespace Compositor.Core;

/// <summary>
/// Reads and writes Swift's encoding of a <c>[StringEnum: Value]</c> dictionary, which is a flat
/// array of alternating keys and values rather than a JSON object.
/// </summary>
/// <remarks>
/// <b>This converter exists because of a subtle Swift rule, and getting it wrong silently loses data.</b>
///
/// <para>
/// Swift's <c>Dictionary</c> normally encodes as <c>["key", value, "key", value]</c>. The exception
/// is SE-0320 (<c>CodingKeyRepresentable</c>), which makes it encode as a JSON object — but that
/// conformance must be declared <i>explicitly</i>, and it is not automatic for a
/// <c>String</c>-backed enum.
/// </para>
///
/// <para>
/// In Compositor, <c>HueSaturationSettings.adjustments</c> and <c>.bands</c> are typed
/// <c>[ColorRange: …]</c> (HueSaturation.swift:176-177) and <c>ColorRange</c> declares only
/// <c>String, CaseIterable, Sendable, Hashable, Codable</c> (HueSaturation.swift:5) — no
/// <c>CodingKeyRepresentable</c> anywhere in the tree, no custom <c>Codable</c> conformance, and
/// the encoder sets only <c>.prettyPrinted, .sortedKeys</c> (ProjectStore.swift:108-109).
/// Therefore the array form is what a real <c>.comp</c> contains.
/// </para>
///
/// <para>
/// Both forms are accepted on read, because an object form is what a naive re-implementation would
/// produce and a project that arrived that way is still better read than rejected. Writes always
/// use the array form.
/// </para>
/// </remarks>
/// <typeparam name="TValue">The dictionary's value type.</typeparam>
public sealed class SwiftEnumDictionaryConverter<TValue> : JsonConverter<Dictionary<string, TValue>>
{
    public override Dictionary<string, TValue> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                throw new JsonException("A Compositor dictionary is never null; use an empty array instead.");

            case JsonTokenType.StartArray:
            {
                // ["Master", {…}, "Reds", {…}] — the form Foundation actually writes.
                var result = new Dictionary<string, TValue>(StringComparer.Ordinal);
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        return result;
                    }

                    if (reader.TokenType != JsonTokenType.String)
                    {
                        throw new JsonException(
                            $"Expected a string key in the Swift dictionary array, found {reader.TokenType}.");
                    }

                    var key = reader.GetString()!;
                    if (!reader.Read())
                    {
                        throw new JsonException("The Swift dictionary array ended after a key with no value.");
                    }

                    var value = JsonSerializer.Deserialize<TValue>(ref reader, options)
                        ?? throw new JsonException($"The value for '{key}' deserialized to null.");

                    if (!result.TryAdd(key, value))
                    {
                        throw new JsonException($"Duplicate key '{key}' in the Swift dictionary array.");
                    }
                }

                throw new JsonException("Unterminated Swift dictionary array.");
            }

            case JsonTokenType.StartObject:
            {
                // The SE-0320 form. Accepted for robustness; never written.
                var result = new Dictionary<string, TValue>(StringComparer.Ordinal);
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                    {
                        return result;
                    }

                    var key = reader.GetString()!;
                    if (!reader.Read())
                    {
                        throw new JsonException("The dictionary object ended after a key with no value.");
                    }

                    var value = JsonSerializer.Deserialize<TValue>(ref reader, options)
                        ?? throw new JsonException($"The value for '{key}' deserialized to null.");

                    result[key] = value;
                }

                throw new JsonException("Unterminated dictionary object.");
            }

            default:
                throw new JsonException(
                    $"Expected an array or object for the Swift dictionary, found {reader.TokenType}.");
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        Dictionary<string, TValue> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var (key, item) in value)
        {
            writer.WriteStringValue(key);
            JsonSerializer.Serialize(writer, item, options);
        }

        writer.WriteEndArray();
    }
}
