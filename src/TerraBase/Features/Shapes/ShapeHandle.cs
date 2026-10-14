using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;

namespace TerraBase.Features.Shapes;

/// <summary>V1 identity of a shape in the serving machine's current database and collection lineage.</summary>
internal readonly record struct ShapeHandle(string Handle, string ShapeId, string CanonicalFilter)
{
    /// <summary>
    /// Computes both identities from the current store and collection metadata. The caller validates the filter using
    /// the query grammar and reads the collection in the same transaction used to serve the shape.
    /// </summary>
    public static ShapeHandle Compute(IDatabaseStore store, ReplicaId replica, CollectionInfo collection, JsonElement? where = null) =>
        ComputeCanonical(store, replica, collection, where is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } ? string.Empty : Canonicalize(where.Value));

    /// <summary>
    /// Computes both identities from a filter that is already canonical (<see cref="ShapeFilter.Canonical"/>), hashed verbatim
    /// so the identity and the continuation <c>where</c> are the same bytes.
    /// </summary>
    public static ShapeHandle ComputeCanonical(IDatabaseStore store, ReplicaId replica, CollectionInfo collection, string canonicalFilter)
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
            replica.Value.ToString(CultureInfo.InvariantCulture), replica.Epoch.ToString(CultureInfo.InvariantCulture),
            collection.Name, collection.Id, canonicalFilter, "1");

        return new ShapeHandle("h1-" + Digest("TB-SHAPE-HANDLE-V1", fields, 8), Digest("TB-SHAPE-V1", fields, 16), canonicalFilter);
    }

    /// <summary>Checks a continuation against an identity freshly computed from the serving transaction's lineage.</summary>
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

    // Only commutative operators may reorder arrays; comparison and membership pairs keep [path, value].
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
