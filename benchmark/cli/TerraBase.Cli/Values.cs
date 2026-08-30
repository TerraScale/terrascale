// <copyright file="Values.cs" company="TerraScale">
// TerraBase (SE-13). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli;

using System.Text.Json;
using TerraBase.Protocol;

/// <summary>
/// The document-body value conversion of the CLI: the operator writes
/// and reads plain JSON; the host's JSON surface carries the typed
/// one-discriminator value form, and the direct TBEP mode carries the
/// protocol's <see cref="TbepValue"/>. The two conversions are exact —
/// no value is coerced, reordered, or dropped.
/// </summary>
public static class Values
{
    /// <summary>Converts a plain JSON element into the host's discriminated value form.</summary>
    /// <param name="plain">The plain JSON element.</param>
    /// <returns>The discriminated JSON object.</returns>
    /// <exception cref="SurfaceUsageException">The element cannot be represented.</exception>
    public static JsonElement ToDiscriminated(JsonElement plain)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteDiscriminated(writer, plain);
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    /// <summary>Converts the host's discriminated value form into plain JSON.</summary>
    /// <param name="discriminated">The discriminated JSON object.</param>
    /// <returns>The plain JSON element.</returns>
    /// <exception cref="SurfaceUsageException">The discriminator set is not exactly one.</exception>
    public static JsonElement ToPlain(JsonElement discriminated)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WritePlain(writer, discriminated);
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    /// <summary>Converts the protocol's value into plain JSON (the direct mode's read path).</summary>
    /// <param name="value">The protocol value.</param>
    /// <returns>The plain JSON element.</returns>
    public static JsonElement ToPlain(TbepValue value)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WritePlain(writer, value);
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    /// <summary>Converts plain JSON into the protocol's value (the direct mode's write path).</summary>
    /// <param name="plain">The plain JSON element.</param>
    /// <returns>The protocol value.</returns>
    public static TbepValue ToValue(JsonElement plain) => plain.ValueKind switch
    {
        JsonValueKind.Null => TbepValue.Null,
        JsonValueKind.True => TbepValue.Of(true),
        JsonValueKind.False => TbepValue.Of(false),
        JsonValueKind.Number => NumberOf(plain),
        JsonValueKind.String => TbepValue.Of(plain.GetString()!),
        JsonValueKind.Array => TbepValue.Of([.. plain.EnumerateArray().Select(ToValue)]),
        JsonValueKind.Object => TbepValue.Of([.. plain.EnumerateObject().Select(field => new ObjectField(field.Name, ToValue(field.Value)))]),
        _ => throw new SurfaceUsageException("the document body is not valid JSON"),
    };

    private static TbepValue NumberOf(JsonElement plain)
    {
        var raw = plain.GetRawText();
        if (raw.IndexOfAny(['.', 'e', 'E']) < 0 && plain.TryGetInt64(out var integer))
        {
            return TbepValue.Of(integer);
        }

        return TbepValue.Of(plain.GetDouble());
    }

    private static void WriteDiscriminated(Utf8JsonWriter writer, JsonElement plain)
    {
        switch (plain.ValueKind)
        {
            case JsonValueKind.Null:
                writer.WriteStartObject();
                writer.WriteBoolean("null", true);
                writer.WriteEndObject();
                break;
            case JsonValueKind.True or JsonValueKind.False:
                writer.WriteStartObject();
                writer.WriteBoolean("boolean", plain.GetBoolean());
                writer.WriteEndObject();
                break;
            case JsonValueKind.Number:
                writer.WriteStartObject();
                if (NumberOf(plain).Payload is long integer)
                {
                    writer.WriteNumber("integer", integer);
                }
                else
                {
                    writer.WriteNumber("number", plain.GetDouble());
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.String:
                writer.WriteStartObject();
                writer.WriteString("string", plain.GetString());
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartObject();
                writer.WritePropertyName("array");
                writer.WriteStartArray();
                foreach (var item in plain.EnumerateArray())
                {
                    WriteDiscriminated(writer, item);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                break;
            case JsonValueKind.Object:
                writer.WriteStartObject();
                writer.WritePropertyName("object");
                writer.WriteStartObject();
                foreach (var field in plain.EnumerateObject())
                {
                    writer.WritePropertyName(field.Name);
                    WriteDiscriminated(writer, field.Value);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
                break;
            default:
                throw new SurfaceUsageException("the document body is not valid JSON");
        }
    }

    private static void WritePlain(Utf8JsonWriter writer, JsonElement discriminated)
    {
        if (discriminated.ValueKind is not JsonValueKind.Object)
        {
            throw new SurfaceUsageException("a value carries exactly one discriminator");
        }

        var set = 0;
        foreach (var field in discriminated.EnumerateObject())
        {
            if (set++ > 0)
            {
                throw new SurfaceUsageException("a value carries exactly one discriminator");
            }

            switch (field.Name)
            {
                case "null":
                    writer.WriteNullValue();
                    break;
                case "boolean":
                    writer.WriteBooleanValue(field.Value.GetBoolean());
                    break;
                case "integer":
                    writer.WriteNumberValue(field.Value.GetInt64());
                    break;
                case "number":
                    writer.WriteNumberValue(field.Value.GetDouble());
                    break;
                case "string":
                    writer.WriteStringValue(field.Value.GetString());
                    break;
                case "array":
                    writer.WriteStartArray();
                    foreach (var item in field.Value.EnumerateArray())
                    {
                        WritePlain(writer, item);
                    }

                    writer.WriteEndArray();
                    break;
                case "object":
                    writer.WriteStartObject();
                    foreach (var entry in field.Value.EnumerateObject())
                    {
                        writer.WritePropertyName(entry.Name);
                        WritePlain(writer, entry.Value);
                    }

                    writer.WriteEndObject();
                    break;
                default:
                    throw new SurfaceUsageException($"unknown value discriminator {field.Name}");
            }
        }

        if (set == 0)
        {
            throw new SurfaceUsageException("a value carries exactly one discriminator");
        }
    }

    private static void WritePlain(Utf8JsonWriter writer, TbepValue value)
    {
        switch (value.Tag)
        {
            case 0:
                writer.WriteNullValue();
                break;
            case 1:
                writer.WriteBooleanValue((bool)value.Payload!);
                break;
            case 2:
                writer.WriteNumberValue((long)value.Payload!);
                break;
            case 3:
                writer.WriteNumberValue((double)value.Payload!);
                break;
            case 4:
                writer.WriteStringValue((string)value.Payload!);
                break;
            case 5:
                writer.WriteStartArray();
                foreach (var item in (TbepValue[])value.Payload!)
                {
                    WritePlain(writer, item);
                }

                writer.WriteEndArray();
                break;
            case 6:
                writer.WriteStartObject();
                foreach (var field in (ObjectField[])value.Payload!)
                {
                    writer.WritePropertyName(field.Name);
                    WritePlain(writer, field.Value);
                }

                writer.WriteEndObject();
                break;
            default:
                throw new SurfaceUsageException($"unknown value tag {value.Tag}");
        }
    }
}

/// <summary>A usage error of the CLI grammar: fail closed with a bounded message, never guess.</summary>
/// <param name="message">The bounded message.</param>
public sealed class SurfaceUsageException(string message) : Exception(message);

/// <summary>The CLI's variant-name rendering: the engine's and host's variant names in snake_case, the surfaces' shared convention.</summary>
internal static class VariantNaming
{
    /// <summary>Renders a variant name in snake_case.</summary>
    /// <param name="variant">The variant name.</param>
    /// <returns>The snake_case name.</returns>
    public static string SnakeCase(this string variant) =>
        string.Concat(variant.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
