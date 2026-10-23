using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TerraBase.Features.Qos;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
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
                CREATE INDEX "_tb_fts_index_{index.Name}__{i.ToString(CultureInfo.InvariantCulture)}" ON {table} USING fts (text);
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

    private Dictionary<SearchFilter, string> SearchTables(string collection, DocumentFilter? filter, CancellationToken cancellationToken)
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
            var (name, _, ordinal) = indexes.Where(index => index.Collection == collection)
                .SelectMany(index => index.Fields.Select((field, ordinal) => (index.Name, Field: field, Ordinal: ordinal)))
                .FirstOrDefault(candidate => search.Path.Segments is { } segments && candidate.Field.Path.Split('.').SequenceEqual(segments, StringComparer.Ordinal));
            if (name is null)
            {
                throw new QueryValidationException("search requires a text index covering its path");
            }

            tables[search] = TextTable(name, ordinal);
        }

        return tables;
    }

    private SearchResults PrepareSearch(string collection, DocumentFilter? filter, CancellationToken cancellationToken)
    {
        // Native FTS scores and accounts against a collection-wide corpus. Owner-scoped reads must not
        // reveal hidden documents through scores or resource failures; require a collection-wide grant.
        if (filter is AuthorizedFilter && Searches(filter).Any())
        {
            throw new QueryValidationException("search requires collection-wide read access");
        }

        var results = new SearchResults(session, store.SchemaMemory is { } memory ? new QosContext.ResultMemory(memory) : null);
        try
        {
            foreach (var (search, table) in SearchTables(collection, filter, cancellationToken))
            {
                var hits = results.Add(search);
                using var command = PlainCommand($"""
                    SELECT id, fts_score(text, $terms), octet_length(id) FROM {table}
                    WHERE fts_match(text, $terms) LIMIT 10001;
                    """);
                command.Parameters.AddWithValue("$terms", search.Terms);
                CountRead("query");
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    session.ThrowIfStopped();
                    if (_queryBudget!.Row())
                    {
                        throw new QueryCapacityException("query execution budget exceeded");
                    }

                    results.Reserve(reader.GetInt64(2));
                    hits.Add(reader.GetString(0), reader.GetDouble(1));
                }
            }

            results.Initialize(filter);
            return results;
        }
        catch (SqliteException error) when (error.Message.Contains("FTS parse error:", StringComparison.Ordinal)
            || error.Message.Contains("FTS query is too long", StringComparison.Ordinal)
            || error.Message.Contains("FTS query nests deeper", StringComparison.Ordinal))
        {
            results.Dispose();
            // Native parser diagnostics can contain the customer's terms or field names.
            throw new QueryValidationException("invalid search terms");
        }
        catch
        {
            results.Dispose();
            throw;
        }
    }

    private sealed class SearchResults(TursoSession session, QosContext.ResultMemory? memory) : IDisposable
    {
        private readonly Dictionary<SearchFilter, Dictionary<string, double>> _hits = [];
        private int _count;
        private long _bytes;
        private bool _matchRegistered;
        private bool _scoreRegistered;

        public Dictionary<SearchFilter, int> Ordinals { get; } = [];

        public string? Candidates { get; private set; }

        public Dictionary<string, double> Add(SearchFilter filter)
        {
            var hits = new Dictionary<string, double>(StringComparer.Ordinal);
            Ordinals.Add(filter, _hits.Count);
            _hits.Add(filter, hits);
            return hits;
        }

        public void Reserve(long idBytes)
        {
            if (++_count > 10000 || idBytes < 0 || idBytes > 8 * 1024 * 1024 - _bytes - 64)
            {
                throw new QueryCapacityException("query search result budget exceeded");
            }

            _bytes += idBytes + 64;
            // Covers dictionaries, decoded ids, unions, UTF-8 JSON and its parameter copy before any of them allocate.
            memory?.Reserve(512 + 16 * idBytes, QosContext.Current);
        }

        public void Initialize(DocumentFilter? filter)
        {
            if (_hits.Count == 0)
            {
                return;
            }

            var candidates = Required(filter);
            if (candidates is not null)
            {
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartArray();
                    foreach (var id in candidates.Distinct(StringComparer.Ordinal))
                    {
                        writer.WriteStringValue(id);
                    }

                    writer.WriteEndArray();
                }

                Candidates = Encoding.UTF8.GetString(buffer.WrittenSpan);
            }

            var lookups = _hits.Values.ToArray();
            session.Connection.CreateFunction<long, string, bool>("tb_search_match", (ordinal, id) => lookups[(int)ordinal].ContainsKey(id));
            _matchRegistered = true;
            session.Connection.CreateFunction<string, double>("tb_search_score", id => lookups[0].GetValueOrDefault(id));
            _scoreRegistered = true;
        }

        // An AND can seek any required positive child. An OR can seek a union only if every branch is bounded.
        private IEnumerable<string>? Required(DocumentFilter? filter) => filter switch
        {
            AuthorizedFilter authorized => Required(authorized.ClientFilter),
            SearchFilter search => _hits[search].Keys,
            AndFilter and => and.Children.Select(Required).FirstOrDefault(ids => ids is not null),
            OrFilter or => Union(or.Children),
            _ => null,
        };

        private IEnumerable<string>? Union(IReadOnlyList<DocumentFilter> filters)
        {
            var candidates = filters.Select(Required).ToArray();
            return candidates.Any(ids => ids is null) ? null : candidates.SelectMany(ids => ids!);
        }

        public void Dispose()
        {
            try
            {
                if (_matchRegistered)
                {
                    session.Connection.CreateFunction<long, string, bool>("tb_search_match", null);
                    _matchRegistered = false;
                }

                if (_scoreRegistered)
                {
                    session.Connection.CreateFunction<string, double>("tb_search_score", null);
                    _scoreRegistered = false;
                }
            }
            finally
            {
                memory?.Dispose();
            }
        }
    }

    internal static IEnumerable<SearchFilter> Searches(DocumentFilter? filter) => filter switch
    {
        AuthorizedFilter authorized => Searches(authorized.ClientFilter),
        SearchFilter search => [search],
        AndFilter and => and.Children.SelectMany(Searches),
        OrFilter or => or.Children.SelectMany(Searches),
        NotFilter not => Searches(not.Child),
        _ => [],
    };
}
