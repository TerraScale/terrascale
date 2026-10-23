using System.Text.Json;
using TerraBase.Features.Documents;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Qos;

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

    private DocumentOwnerProof? OwnerProof(string collection, DocumentState state)
    {
        if (!state.Exists || ReadCollectionRules(collection) is not { Owner: not null } rules)
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

        using var reader = Command(TursoStatements.GetCollection, collection).ExecuteReader();
        return reader.Read() ? new DocumentOwnerProof(reader.GetString(2), rules.Owner, subject, rules.Fingerprint()) : null;
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
