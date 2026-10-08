using System.Buffers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Engine;

namespace TerraBase.Primitives;

/// <summary>Entity identity lives in the document key; typed responses restore it from that key.</summary>
internal static class EntityPayload
{
    private static readonly JsonDocumentOptions StoredOptions = new() { MaxDepth = DocumentJson.MaxStoredDepth };

    public static ReadOnlyMemory<byte> WithoutId(ReadOnlyMemory<byte> json, string id, bool nested = false)
    {
        using var parsed = JsonDocument.Parse(json, StoredOptions);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object
            || (!nested && (!parsed.RootElement.TryGetProperty("id", out var field)
                || field.ValueKind != JsonValueKind.String || field.GetString() != id)))
        {
            return json;
        }

        var buffer = new ArrayBufferWriter<byte>(json.Length);
        WriteWithoutId(buffer, parsed.RootElement, id, nested);
        return buffer.WrittenMemory;
    }

    public static T? Read<T>(StoredDocument document, JsonTypeInfo<T> type)
    {
        if (type.Kind != JsonTypeInfoKind.Object)
        {
            return JsonSerializer.Deserialize(document.Json.Span, type);
        }

        var body = JsonNode.Parse(document.Json.Span, documentOptions: StoredOptions)!.AsObject();
        Restore(body, type, document.Id);
        return JsonSerializer.Deserialize(body, type);
    }

    private static void WriteWithoutId(ArrayBufferWriter<byte> buffer, JsonElement body, string id, bool nested)
    {
        buffer.Write("{"u8);
        var first = true;
        foreach (var property in body.EnumerateObject())
        {
            if (property.NameEquals("id") && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == id)
            {
                continue;
            }

            if (!first)
            {
                buffer.Write(","u8);
            }

            first = false;
            // Preserve parsed token bytes so removing identity cannot expand names or values.
            buffer.Write("\""u8);
            buffer.Write(JsonMarshal.GetRawUtf8PropertyName(property));
            buffer.Write("\":"u8);
            if (nested && property.Value.ValueKind == JsonValueKind.Object)
            {
                WriteWithoutId(buffer, property.Value, id, nested: true);
            }
            else
            {
                buffer.Write(JsonMarshal.GetRawUtf8Value(property.Value));
            }
        }

        buffer.Write("}"u8);
    }

    private static void Restore(JsonObject body, JsonTypeInfo type, string id)
    {
        foreach (var property in type.Properties)
        {
            if (property.Name == "id" && property.PropertyType == typeof(string) && !body.ContainsKey("id"))
            {
                body["id"] = id;
            }
            else if (body[property.Name] is JsonObject child)
            {
                Restore(child, type.Options.GetTypeInfo(property.PropertyType), id);
            }
        }
    }
}
