using System.Text.Json;
using TerraBase.Features.Documents;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Qos;
using TerraBase.Features.Memory;
using TerraBase.Primitives;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{
    public ValueTask<CollectionAccessRules?> GetCollectionRulesAsync(string name, CancellationToken cancellationToken) =>
        RunAsync(() => ReadCollectionRules(name), cancellationToken);

    private CollectionAccessRules? ReadCollectionRules(string name)
    {
        using var reservation = ReserveCollectionMetadata(name, out _);
        using var metadata = ReadCollectionMetadata(name);
        if (metadata is null || !metadata.RootElement.TryGetProperty("rules", out var rules))
        {
            return null;
        }

        return ParseCollectionRules(rules);
    }

    private CollectionAccessRules? ReadCollectionRules(DocumentState metadata)
    {
        if (CollectionField(metadata, "rules") is not { } raw)
        {
            return null;
        }

        using var reservation = ReserveSchemaMemory(8L * StrictUtf8.GetByteCount(raw), MemoryLane.Foreground);
        using var parsed = JsonDocument.Parse(raw, RequestJson.StoredDocumentOptions);
        return ParseCollectionRules(parsed.RootElement);
    }

    private static CollectionAccessRules? ParseCollectionRules(JsonElement rules)
    {
        try
        {
            var parsed = rules.Deserialize(CollectionRulesJsonContext.Default.CollectionAccessRules);
            // Replicated metadata is also a trust boundary. An invalid policy grants nothing.
            return parsed is not null && parsed.Check() is null ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public ValueTask<bool> SetCollectionRulesAsync(string name, CollectionAccessRules rules, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            ThrowIfReadOnly();
            if (!IsClient || rules.Check() is not null)
            {
                throw new InvalidDataException("invalid collection rules");
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

            RewriteCollectionMetadata(name, createdAt, static member => member == "rules",
                [("rules", JsonSerializer.SerializeToUtf8Bytes(rules, CollectionRulesJsonContext.Default.CollectionAccessRules))], cancellationToken);
            return true;
        }, cancellationToken, mutation: true);

    private static string? CollectionField(DocumentState metadata, string name) =>
        metadata.Exists && metadata.Fields.TryGetValue(name, out var field)
            && field.Value(metadata.Clear?.Version) is { Kind: FieldKind.Base, Json: { } raw } ? raw : null;

    private static string? CollectionGeneration(DocumentState metadata)
    {
        // Lattice values are canonical JSON, so a ULID is exactly 26 unescaped ASCII characters inside quotes.
        if (CollectionField(metadata, "entity_id") is not { Length: 28 } raw || raw[0] != '"' || raw[^1] != '"')
        {
            return null;
        }

        var generation = raw[1..^1];
        return EntityIds.IsValid(generation) ? generation : null;
    }

    private DocumentOwnerProof? OwnerProof(DocumentState state, DocumentState metadata)
    {
        if (!state.Exists || ReadCollectionRules(metadata) is not { Owner: not null } rules
            || CollectionGeneration(metadata) is not { } generation)
        {
            return null;
        }

        string[] segments = [.. rules.Owner.Split('/').Skip(1)
            .Select(segment => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))];
        if (!state.Fields.TryGetValue(segments[0], out var field) || field.Value(state.Clear?.Version) is not { Kind: FieldKind.Base, Json: { } raw })
        {
            return null;
        }

        // Parse only the owning field, admitted before allocation; never retain a complete document preimage.
        QosContext.ReserveResult(8L * System.Text.Encoding.UTF8.GetByteCount(raw) + 64 * 1024);
        using var parsed = JsonDocument.Parse(raw, RequestJson.StoredDocumentOptions);
        var value = parsed.RootElement;
        foreach (var member in segments.Skip(1))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(member, out value))
            {
                return null;
            }
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 256 } subject)
        {
            return null;
        }

        return new DocumentOwnerProof(generation, rules.Owner, subject, rules.Fingerprint());
    }

    public ValueTask<DocumentOwnerProof?> GetDocumentOwnerProofAsync(string collection, string id, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var command = Command("SELECT access_before, coalesce(octet_length(access_before),0) FROM _tb_feed WHERE collection = $collection AND id = $id ORDER BY seq DESC LIMIT 1;", collection);
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0))
            {
                return null;
            }

            var bytes = reader.GetInt64(1);
            if (bytes > 32 * 1024)
            {
                throw new InvalidDataException("an ownership proof exceeds its bound");
            }
            QosContext.ReserveResult(bytes * 8);
            return JsonSerializer.Deserialize(reader.GetString(0), CollectionRulesJsonContext.Default.DocumentOwnerProof);
        }, cancellationToken);
}
