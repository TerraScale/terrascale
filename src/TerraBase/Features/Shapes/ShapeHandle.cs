using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TerraBase.Engine;

namespace TerraBase.Features.Shapes;
internal readonly record struct ShapeHandle(string Handle, string ShapeId, string CanonicalFilter)
{
    public static ShapeHandle Compute(IDatabaseStore store, CollectionInfo collection, JsonElement? where = null) =>
        ComputeCanonical(store, collection, where is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } ? string.Empty : Canonicalize(where.Value));
    public static ShapeHandle ComputeCanonical(IDatabaseStore store, CollectionInfo collection, string canonicalFilter)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(canonicalFilter);
        ObjectDisposedException.ThrowIf(store.IsClosed, store);
        var tenant = store.Tenant ?? throw new InvalidOperationException("a shape requires a tenant database");
        ArgumentException.ThrowIfNullOrEmpty(tenant.OrganizationId);
        ArgumentException.ThrowIfNullOrEmpty(tenant.ProjectId);
        ArgumentException.ThrowIfNullOrEmpty(tenant.DatabaseId);
        ArgumentException.ThrowIfNullOrEmpty(collection.Id);

        var fields = string.Join('\0', tenant.OrganizationId, tenant.ProjectId, tenant.DatabaseId, store.Incarnation.ToString(),
            collection.Name, collection.Id, canonicalFilter, "1");

        return new ShapeHandle("h1-" + Digest("TB-SHAPE-HANDLE-V1", fields, 8), Digest("TB-SHAPE-V1", fields, 16), canonicalFilter);
    }
    public bool IsCurrent(string? handle) => Handle is not null && string.Equals(Handle, handle, StringComparison.Ordinal);

    private static string Digest(string domain, string fields, int bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(domain + '\0' + fields)).AsSpan(0, bytes));

    private static string Canonicalize(JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            WriteCanonical(writer, value);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value, bool unordered = false)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (property.Name is "in" or "nin")
                    {
                        writer.WriteStartArray();
                        WriteCanonical(writer, property.Value[0]);
                        WriteCanonical(writer, property.Value[1], unordered: true);
                        writer.WriteEndArray();
                    }
                    else
                    {
                        WriteCanonical(writer, property.Value, unordered: property.Name is "and" or "or");
                    }
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                if (unordered)
                {
                    foreach (var item in value.EnumerateArray().Select(item => Canonicalize(item)).Order(StringComparer.Ordinal))
                    {
                        writer.WriteRawValue(item, skipInputValidation: true);
                    }
                }
                else
                {
                    foreach (var item in value.EnumerateArray())
                    {
                        WriteCanonical(writer, item);
                    }
                }

                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
