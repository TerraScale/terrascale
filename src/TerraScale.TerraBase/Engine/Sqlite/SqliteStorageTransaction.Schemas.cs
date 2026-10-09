using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using TerraBase.Features.Memory;
using TerraBase.Primitives;

namespace TerraBase.Engine.Sqlite;

internal sealed partial class SqliteStorageTransaction
{
    private const int MaxCollectionSchemaBytes = 64 * 1024;
    private const string CollectionSchemaDialect = "https://json-schema.org/draft/2020-12/schema";

    public ValueTask<JsonElement?> GetCollectionSchemaAsync(string name, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            using var reservation = ReserveCollectionMetadata(name);
            return ReadCollectionSchema(name);
        }, cancellationToken);

    private IMemoryReservation? ReserveCollectionMetadata(string name)
    {
        var command = Command("SELECT length(CAST(body AS BLOB)) FROM documents WHERE collection = $collection AND id = $id;", DocumentChange.Collections);
        command.Parameters.AddWithValue("$id", name);
        var bytes = command.ExecuteScalar();
        return bytes is long length ? ReserveSchemaMemory(8L * length, MemoryLane.Foreground) : null;
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
        if (Command(SqliteStatements.GetCollection, name).ExecuteScalar() is null)
        {
            return null;
        }

        using var metadata = ReadCollectionMetadata(name);
        return metadata is not null && metadata.RootElement.TryGetProperty("schema", out var schema) ? schema.Clone() : null;
    }

    public ValueTask<bool> SetCollectionSchemaAsync(string name, JsonElement? schema, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            ThrowIfReadOnly();
            DocumentMutation(replicated: false);
            long createdAt;
            using (var reader = Command(SqliteStatements.GetCollection, name).ExecuteReader())
            {
                if (!reader.Read())
                {
                    return false;
                }

                createdAt = reader.GetInt64(1);
            }

            if (!Shaped)
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

            using var metadataReservation = ReserveCollectionMetadata(name);
            using var metadata = ReadCollectionMetadata(name);
            // Count the exact encoded bytes before allocating the output and admitting its write.
            var bytes = 2;
            var members = 0;
            var hasCreatedAt = false;
            if (metadata is not null)
            {
                foreach (var property in metadata.RootElement.EnumerateObject())
                {
                    if (property.Name is "schema" or "schema_dialect")
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

            if (schema is { } value)
            {
                CountMember("schema", JsonMarshal.GetRawUtf8Value(value).Length);
                CountMember("schema_dialect", CollectionSchemaDialect.Length + 2);
            }

            Reserve(DocumentChange.Collections, name, bytes);
            var buffer = new ArrayBufferWriter<byte>(bytes);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                if (metadata is not null)
                {
                    foreach (var property in metadata.RootElement.EnumerateObject())
                    {
                        if (property.Name is "schema" or "schema_dialect")
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

                if (schema is { } definition)
                {
                    writer.WritePropertyName("schema");
                    writer.WriteRawValue(JsonMarshal.GetRawUtf8Value(definition), skipInputValidation: true);
                    writer.WriteString("schema_dialect", CollectionSchemaDialect);
                }

                writer.WriteEndObject();
            }

            cancellationToken.ThrowIfCancellationRequested();
            // The complete envelope must be admissible on replication as well as locally.
            var body = DocumentJson.Read(buffer.WrittenMemory, requireObject: true);
            WriteMetadata(DocumentChange.Collections, name, body);
            _collectionsChanged = true;
            return true;

            void CountMember(string propertyName, int valueBytes)
            {
                bytes += JsonEncodedText.Encode(propertyName).EncodedUtf8Bytes.Length + 3 + valueBytes + (members++ == 0 ? 0 : 1);
                if (bytes > MaxDocumentBytes)
                {
                    throw new StorageWriteLimitException("collection metadata is at most 1 MiB");
                }
            }
        }, cancellationToken, mutation: true);

    private JsonDocument? ReadCollectionMetadata(string name)
    {
        var stored = ReadDocument(DocumentChange.Collections, name);
        if (stored is null)
        {
            return null;
        }

        if (stored.Json.Length > MaxDocumentBytes)
        {
            throw new StorageWriteLimitException("collection metadata is at most 1 MiB");
        }

        var metadata = JsonDocument.Parse(stored.Json, new JsonDocumentOptions { MaxDepth = DocumentJson.MaxStoredDepth });
        if (metadata.RootElement.ValueKind != JsonValueKind.Object)
        {
            metadata.Dispose();
            throw new InvalidDataException("collection metadata must be a JSON object");
        }

        return metadata;
    }
}
