using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Engine;
using TerraBase.Hosting;

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

        var body = JsonNode.Parse(json.Span, documentOptions: StoredOptions)!.AsObject();
        Remove(body, id, nested);
        return JsonSerializer.SerializeToUtf8Bytes(body, TerraBaseJsonContext.Default.JsonObject);
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

    private static void Remove(JsonObject body, string id, bool nested)
    {
        if (body["id"] is JsonValue field && field.TryGetValue<string>(out var value) && value == id)
        {
            body.Remove("id");
        }

        if (nested)
        {
            foreach (var property in body)
            {
                if (property.Value is JsonObject child)
                {
                    Remove(child, id, nested: true);
                }
            }
        }
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
