using System.Globalization;
using System.Text.Json;

namespace TerraBase.Engine.Sqlite;

internal sealed partial class SqliteStorageTransaction
{
    // Derived tables contain strings only. Native FTS owns tokenization, postings and BM25; metadata replicates the definition.
    private static string TextTable(string name, int field) => $"\"_tb_text_{name}__{field.ToString(CultureInfo.InvariantCulture)}\"";

    private List<IndexDefinition> ReadTextIndexes(CancellationToken cancellationToken)
    {
        var indexes = new List<IndexDefinition>();
        using var command = PlainCommand("SELECT name, collection, fields FROM _tb_text_indexes;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var fields = JsonDocument.Parse(reader.GetString(2));
            indexes.Add(new IndexDefinition(reader.GetString(0), reader.GetString(1), [.. fields.RootElement.EnumerateArray().Select(path =>
                DocumentField.TryParse(path.GetString(), out var field) ? field : throw new InvalidDataException("invalid stored text index field"))], "text"));
        }

        return indexes;
    }

    private bool TextIndexExists(string name)
    {
        using var command = PlainCommand("SELECT 1 FROM _tb_text_indexes WHERE name = $name;");
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    private void CreateTextIndex(IndexDefinition index, CancellationToken cancellationToken)
    {
        if (index.Fields.Any(field => field.IsMetadata))
        {
            throw new ArgumentException("text indexes require string member paths", nameof(index));
        }

        for (var i = 0; i < index.Fields.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var table = TextTable(index.Name, i);
            var path = index.Fields[i].Path;
            using var create = PlainCommand($"""
                CREATE TABLE {table} (id TEXT NOT NULL UNIQUE, text TEXT NOT NULL);
                INSERT INTO {table} (id, text) SELECT id, json_extract(doc, '$.{path}') FROM {Table(index.Collection)}
                    WHERE json_type(doc, '$.{path}') = 'text';
                CREATE INDEX "tb_text_{index.Name}__{i.ToString(CultureInfo.InvariantCulture)}" ON {table} USING fts (text);
                """);
            create.ExecuteNonQuery();
        }

        using var definition = JsonDocument.Parse(IndexMetadata(index));
        using var save = PlainCommand("INSERT INTO _tb_text_indexes (name, collection, fields) VALUES ($name, $collection, $fields);");
        save.Parameters.AddWithValue("$name", index.Name);
        save.Parameters.AddWithValue("$collection", index.Collection);
        save.Parameters.AddWithValue("$fields", definition.RootElement.GetProperty("fields").GetRawText());
        save.ExecuteNonQuery();
    }

    private void DropTextIndex(IndexDefinition index, CancellationToken cancellationToken)
    {
        for (var i = 0; i < index.Fields.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var drop = PlainCommand($"DROP TABLE {TextTable(index.Name, i)};");
            drop.ExecuteNonQuery();
        }

        using var remove = PlainCommand("DELETE FROM _tb_text_indexes WHERE name = $name;");
        remove.Parameters.AddWithValue("$name", index.Name);
        remove.ExecuteNonQuery();
        _statisticsChanged = true;
    }

    private void PutTextRows(string collection, string id, string? body)
    {
        foreach (var index in ReadTextIndexes(CancellationToken.None).Where(index => index.Collection == collection))
        {
            for (var i = 0; i < index.Fields.Count; i++)
            {
                var table = TextTable(index.Name, i);
                var path = index.Fields[i].Path;
                using var remove = PlainCommand($"DELETE FROM {table} WHERE id = $id;");
                remove.Parameters.AddWithValue("$id", id);
                remove.ExecuteNonQuery();
                if (body is not null)
                {
                    using var insert = PlainCommand($"INSERT INTO {table} (id, text) SELECT $id, json_extract($body, '$.{path}') WHERE json_type($body, '$.{path}') = 'text';");
                    insert.Parameters.AddWithValue("$id", id);
                    insert.Parameters.AddWithValue("$body", body);
                    insert.ExecuteNonQuery();
                }
            }
        }
    }

    private IReadOnlyDictionary<SearchFilter, string> SearchTables(string collection, DocumentFilter? filter, CancellationToken cancellationToken)
    {
        var searches = Searches(filter).ToList();
        var tables = new Dictionary<SearchFilter, string>();
        if (searches.Count == 0)
        {
            return tables;
        }

        var indexes = store.IsSystem ? [] : ReadTextIndexes(cancellationToken);
        foreach (var search in searches)
        {
            var match = indexes.Where(index => index.Collection == collection)
                .SelectMany(index => index.Fields.Select((field, ordinal) => (index.Name, Field: field, Ordinal: ordinal)))
                .FirstOrDefault(candidate => search.Path.Segments is { } segments && candidate.Field.Path.Split('.').SequenceEqual(segments, StringComparer.Ordinal));
            if (match.Name is null)
            {
                throw new QueryValidationException("search requires a text index covering its path");
            }

            tables[search] = TextTable(match.Name, match.Ordinal);
        }

        return tables;
    }

    internal static IEnumerable<SearchFilter> Searches(DocumentFilter? filter) => filter switch
    {
        SearchFilter search => [search],
        AndFilter and => and.Children.SelectMany(Searches),
        OrFilter or => or.Children.SelectMany(Searches),
        NotFilter not => Searches(not.Child),
        _ => [],
    };
}
