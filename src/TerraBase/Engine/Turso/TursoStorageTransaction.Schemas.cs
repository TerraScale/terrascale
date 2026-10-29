using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Memory;
using TerraBase.Primitives;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{
    private const int MaxCollectionSchemaBytes = 64 * 1024;
    private const string CollectionSchemaDialect = "https://json-schema.org/draft/2020-12/schema";

    public ValueTask<JsonElement?> GetCollectionSchemaAsync(string name, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            using var reservation = ReserveCollectionMetadata(name, out _);
            return ReadCollectionSchema(name);
        }, cancellationToken);

    private IMemoryReservation? ReserveCollectionMetadata(string name, out long? bytes)
    {
        var command = store.IsSystem
            ? Command("SELECT octet_length(body) FROM documents WHERE collection = $collection AND id = $id;", DocumentDelta.Collections)
            : TableCommand("SELECT octet_length(doc) FROM {0} WHERE id = $id;", DocumentDelta.Collections);
        command.Parameters.AddWithValue("$id", name);
        bytes = command.ExecuteScalar() as long?;
        return bytes is { } length ? ReserveSchemaMemory(8L * length, MemoryLane.Foreground) : null;
    }

    private IMemoryReservation? ReserveSchemaMemory(long bytes, MemoryLane lane)
    {
        var reservation = store.SchemaMemory?.TryReserve(lane, bytes);
        if (store.SchemaMemory is not null && reservation is null)
        {
            throw new StorageBusyException();
        }

        return reservation;
    }

    // Worker-only: also usable by document admission without reentering the transaction gate.
    private JsonElement? ReadCollectionSchema(string name)
    {
        if (Command(TursoStatements.GetCollection, name).ExecuteScalar() is null)
        {
            return null;
        }

        using var metadata = ReadCollectionMetadata(name);
        return metadata is not null && metadata.RootElement.TryGetProperty("schema", out var schema) ? schema.Clone() : null;
    }

    private static JsonElement? ReadCollectionSchema(DocumentState state)
    {
        if (state.Materialize() is not { } json)
        {
            return null;
        }

        using var metadata = ParseCollectionMetadata(json);
        return metadata.RootElement.TryGetProperty("schema", out var schema) ? schema.Clone() : null;
    }

    public ValueTask<bool> SetCollectionSchemaAsync(string name, JsonElement? schema, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            ThrowIfReadOnly();
            long createdAt;
            using (var reader = Command(TursoStatements.GetCollection, name).ExecuteReader())
            {
                if (!reader.Read())
                {
                    return false;
                }

                createdAt = reader.GetInt64(1);
            }

            if (!IsClient)
            {
                throw new InvalidOperationException("collection schemas belong to client databases");
            }


            if (schema is { } attached && JsonMarshal.GetRawUtf8Value(attached).Length > MaxCollectionSchemaBytes)
            {
                throw new DocumentValidationException("a collection schema is at most 64 KiB", tooLarge: true);
            }

            if (schema is { } declared && CollectionSchemaValidation.Check(declared, store.SchemaMemory, cancellationToken: cancellationToken, evaluations: store.SchemaEvaluations) is not null)
            {
                throw new InvalidDataException("invalid collection schema");
            }

            List<(string, ReadOnlyMemory<byte>)> added = schema is { } value
                ? [("schema", JsonMarshal.GetRawUtf8Value(value).ToArray()), ("schema_dialect", Encoding.UTF8.GetBytes($"\"{CollectionSchemaDialect}\""))]
                : [];
            RewriteCollectionMetadata(name, createdAt, static member => member is "schema" or "schema_dialect", added, cancellationToken);
            return true;
        }, cancellationToken, mutation: true);

    public ValueTask<string?> GetCollectionTtlAsync(string name, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            if (!IsClient || Command(TursoStatements.GetCollection, name).ExecuteScalar() is null)
            {
                return null;
            }

            using var reservation = ReserveCollectionMetadata(name, out _);
            using var metadata = ReadCollectionMetadata(name);
            return metadata is not null && metadata.RootElement.TryGetProperty(TtlMember, out var path) && path.ValueKind == JsonValueKind.String
                ? path.GetString()
                : null;
        }, cancellationToken);

    public ValueTask<bool> SetCollectionTtlAsync(string name, string? path, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            ThrowIfReadOnly();
            if (!IsClient)
            {
                throw new InvalidOperationException("collection TTLs belong to client databases");
            }

            long createdAt;
            using (var reader = Command(TursoStatements.GetCollection, name).ExecuteReader())
            {
                if (!reader.Read())
                {
                    return false;
                }

                createdAt = reader.GetInt64(1);
            }

            // Encoded by the writer: the pointer is client text.
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStringValue(path ?? string.Empty);
            }

            RewriteCollectionMetadata(name, createdAt, static member => member == TtlMember,
                path is null ? [] : [(TtlMember, buffer.WrittenMemory)], cancellationToken);
            return true;
        }, cancellationToken, mutation: true);

    private const string TtlMember = "ttl_path";

    // Worker-only: rewrites a collection's metadata document with the members `replaced` selects dropped and `added`
    // appended, keeping every other member (so a schema and a table definition never overwrite each other).
    private void RewriteCollectionMetadata(
        string name, long createdAt, Func<string, bool> replaced, IReadOnlyList<(string Name, ReadOnlyMemory<byte> Raw)> added, CancellationToken cancellationToken)
    {
        using var metadataReservation = ReserveCollectionMetadata(name, out _);
        using var metadata = ReadCollectionMetadata(name);
        // Count the exact encoded bytes before allocating the output and admitting its write.
        var bytes = 2;
        var members = 0;
        var hasCreatedAt = false;
        if (metadata is not null)
        {
            foreach (var property in metadata.RootElement.EnumerateObject())
            {
                if (replaced(property.Name))
                {
                    continue;
                }

                hasCreatedAt |= property.Name == "created_at";
                CountMember(property.Name, JsonMarshal.GetRawUtf8Value(property.Value).Length);
            }
        }

        if (!hasCreatedAt)
        {
            CountMember("created_at", createdAt.ToString(CultureInfo.InvariantCulture).Length);
        }

        foreach (var (member, raw) in added)
        {
            CountMember(member, raw.Length);
        }

        Reserve(name, bytes);
        var buffer = new ArrayBufferWriter<byte>(bytes);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (metadata is not null)
            {
                foreach (var property in metadata.RootElement.EnumerateObject())
                {
                    if (replaced(property.Name))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    writer.WriteRawValue(JsonMarshal.GetRawUtf8Value(property.Value), skipInputValidation: true);
                }
            }

            if (!hasCreatedAt)
            {
                writer.WriteNumber("created_at", createdAt);
            }

            foreach (var (member, raw) in added)
            {
                writer.WritePropertyName(member);
                writer.WriteRawValue(raw.Span, skipInputValidation: true);
            }

            writer.WriteEndObject();
        }

        cancellationToken.ThrowIfCancellationRequested();
        // The complete envelope must be admissible on replication as well as locally.
        var body = DocumentJson.Read(buffer.WrittenMemory, requireObject: true);
        WriteMetadata(DocumentDelta.Collections, name, body);
        ChangedReaderState = true;

        void CountMember(string propertyName, int valueBytes)
        {
            bytes += JsonEncodedText.Encode(propertyName).EncodedUtf8Bytes.Length + 3 + valueBytes + (members++ == 0 ? 0 : 1);
            if (bytes > MaxDocumentBytes)
            {
                throw new StorageWriteLimitException("collection metadata is at most 1 MiB");
            }
        }
    }

    private JsonDocument? ReadCollectionMetadata(string name)
    {
        var stored = ReadDocument(DocumentDelta.Collections, name);
        if (stored is null)
        {
            return null;
        }

        return ParseCollectionMetadata(stored.Json);
    }

    private static JsonDocument ParseCollectionMetadata(ReadOnlyMemory<byte> json)
    {
        if (json.Length > MaxDocumentBytes)
        {
            throw new StorageWriteLimitException("collection metadata is at most 1 MiB");
        }

        var metadata = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = DocumentJson.MaxStoredDepth });
        if (metadata.RootElement.ValueKind != JsonValueKind.Object)
        {
            metadata.Dispose();
            throw new InvalidDataException("collection metadata must be a JSON object");
        }

        return metadata;
    }
}
