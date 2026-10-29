using System.Text.Json;
using TerraBase.Engine.Lattices;
using TerraBase.Primitives;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{
    private void IndexDefinitionMetadata(string name, string? body, CancellationToken cancellationToken)
    {
        if (body is null)
        {
            _ = DropIndex(name, cancellationToken);
            return;
        }

        using var parsed = JsonDocument.Parse(body, DocumentState.JsonOptions);
        if (!parsed.RootElement.TryGetProperty("collection", out var named) || named.GetString() is not { } collection
            || !DatabaseName.TryParse(collection, out _) || CollectionName.IsReserved(collection)
            || !parsed.RootElement.TryGetProperty("fields", out var paths) || paths.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var kind = parsed.RootElement.TryGetProperty("kind", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : "btree";
        if (kind is not ("btree" or "text" or "vector"))
        {
            return;
        }

        var dimensions = kind == "vector" && parsed.RootElement.TryGetProperty("dimensions", out var length) && length.TryGetInt32(out var count) ? count : 0;

        List<DocumentField> fields = [];
        foreach (var path in paths.EnumerateArray())
        {
            if (path.ValueKind != JsonValueKind.String || !DocumentField.TryParse(path.GetString(), out var field))
            {
                return;
            }

            fields.Add(field);
        }

        if (ReadState(DocumentDelta.Collections, collection).Clear is { Deleted: true } drop
            && (!HasTable(collection) || !(ReadState(DocumentDelta.Indexes, name).Clear?.Version > drop.Version)))
        {
            _ = DropIndex(name, cancellationToken);
            return;
        }

        if (ReadIndexes(cancellationToken).Find(index => index.Name == name) is { } existing)
        {
            if (existing.Kind == kind && existing.Collection == collection && existing.Fields.SequenceEqual(fields) && existing.Dimensions == dimensions)
            {
                return;
            }

            _ = DropIndex(name, cancellationToken);
        }

        if (!HasTable(collection))
        {
            AddCollection(collection, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), EntityIds.New().ToString());
        }

        try
        {
            _ = CreateIndex(new IndexDefinition(name, collection, fields, kind, dimensions), cancellationToken);
        }
        catch (ArgumentException)
        {

        }
    }

    private void ReprojectUnique(string collection, CancellationToken cancellationToken)
    {
        if (!HasTable(collection))
        {
            return;
        }

        var constraints = ReadUniqueConstraints(collection);
        Command("DELETE FROM _tb_unique_keys WHERE collection = $collection;", collection).ExecuteNonQuery();
        Command("DELETE FROM _tb_unique_invalid WHERE collection = $collection;", collection).ExecuteNonQuery();
        BeginUniqueAffected();
        Command("DELETE FROM _tb_unique_affected;").ExecuteNonQuery();
        TableCommand("INSERT OR IGNORE INTO _tb_unique_affected (id) SELECT id FROM {0};", collection).ExecuteNonQuery();
        Command("INSERT OR IGNORE INTO _tb_unique_affected (id) SELECT id FROM _tb_unique_conflicts WHERE collection = $collection;", collection).ExecuteNonQuery();
        foreach (var id in UniqueProjectionIds(cancellationToken))
        {
            if (ReadUniqueDocument(collection, id) is { } document)
            {
                IndexUniqueKeys(collection, id, StrictUtf8.GetString(document.Json.Span), constraints, local: false, cancellationToken);
            }
        }

        ReconcileUnique(collection, UniqueProjectionIds(cancellationToken), cancellationToken);
    }

    private void AddCollection(string name, long created, string entity)
    {
        var command = Command(TursoStatements.CreateCollection, name);
        command.Parameters.AddWithValue("$created", created);
        command.Parameters.AddWithValue("$entityId", entity);
        command.ExecuteNonQuery();
        using (var table = PlainCommand(CreateTable(name)))
        {
            table.ExecuteNonQuery();
        }

        ChangedReaderState = true;
    }

    private void RestoreDroppedCollectionIndexes(string collection, ClearEntry drop, CancellationToken cancellationToken)
    {
        var after = string.Empty;
        while (true)
        {
            var command = TableCommand("SELECT id FROM {0} WHERE id > $after ORDER BY id LIMIT 128;", DocumentDelta.Indexes);
            command.Parameters.AddWithValue("$after", after);
            List<string> ids = [];
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ids.Add(reader.GetString(0));
                }
            }

            if (ids.Count == 0)
            {
                return;
            }

            foreach (var id in ids)
            {
                var state = ReadState(DocumentDelta.Indexes, id);
                if (state.Clear?.Version > drop.Version && Materialize(state) is { } body)
                {
                    using var parsed = JsonDocument.Parse(body, DocumentState.JsonOptions);
                    if (parsed.RootElement.TryGetProperty("collection", out var named) && named.GetString() == collection)
                    {
                        IndexDefinitionMetadata(id, body, cancellationToken);
                    }
                }
            }

            after = ids[^1];
        }
    }
}
