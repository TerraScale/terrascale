using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cysharp.Text;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

/// <remarks>
/// SQL-subset queries and indexes compile to SQL over a client collection's table, which holds its live documents only.
/// Fields become <c>id</c>, the version from <c>_tb_state</c>, or <c>json_extract(doc, '$.path')</c>, and every index is an
/// expression index on the collection's table. The collection and the paths are embedded as literals, never as
/// parameters: Turso uses an expression index only for a textually equal expression. Both are safe to embed because
/// <see cref="DatabaseName"/> and <see cref="DocumentField"/> admit no quotes.
/// </remarks>
internal sealed partial class TursoStorageTransaction
{
    private const string IndexPrefix = "tb_index_";

    public ValueTask<long> CountDocumentsAsync(string collection, CancellationToken cancellationToken) =>
        RunAsync(() => WithQueryBudget(() =>
        {
            var rows = Rows(collection);
            using var command = Uncached(collection);
            // tb_qos_scan accounts each row against the plan and stops the count once its budget is spent.
            command.CommandText = $"SELECT count(*) FROM {rows.From} WHERE {rows.Live} AND tb_qos_scan(octet_length({rows.Json}));";
            CountRead("query");
            return (long)command.ExecuteScalar()!;
        }, cancellationToken), cancellationToken);

    // A client database reads its counts from _tb_collection_stats, one row per collection whatever its size, so only the
    // query's time budget can refuse it; the system database still counts its documents table under the scan budget.
    public ValueTask<IReadOnlyList<CollectionStats>> ListCollectionStatsAsync(CancellationToken cancellationToken) =>
        RunAsync(() => WithQueryBudget<IReadOnlyList<CollectionStats>>(() =>
        {
            if (!store.IsSystem)
            {
                return ReadCollectionStats(Command(TursoStatements.ClientCollectionStats), cancellationToken);
            }

            using var command = PlainCommand(TursoStatements.ListCollectionStats.Replace(
                "documents.body IS NOT NULL", "documents.body IS NOT NULL AND tb_qos_scan(octet_length(documents.body))", StringComparison.Ordinal));
            return ReadCollectionStats(command, cancellationToken);
        }, cancellationToken), cancellationToken);

    private static List<CollectionStats> ReadCollectionStats(SqliteCommand command, CancellationToken cancellationToken)
    {
        var collections = new List<CollectionStats>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            collections.Add(new CollectionStats(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        return collections;
    }

    public IAsyncEnumerable<StoredDocument> QuerySqlAsync(SqlQuery query, CancellationToken cancellationToken, QueryObservation? observation = null) =>
        ReadBudgetedAsync(() => QueryCommand(query, explain: false, observation is not null), query, observation, cancellationToken);

    public ValueTask<SqlQueryPlan> ExplainSqlAsync(SqlQuery query, CancellationToken cancellationToken) =>
        RunAsync(() => WithQueryBudget(() => ReadSqlPlan(query, cancellationToken), cancellationToken), cancellationToken);

    private SqlQueryPlan ReadSqlPlan(SqlQuery query, CancellationToken cancellationToken)
    {
        using var command = QueryCommand(query, explain: true);
        var steps = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            steps.Add(reader.GetString(3));
        }

        var indexes = steps.SelectMany(step => UsedIndex().Matches(step).Select(match => match.Groups[1].Value)).Distinct().ToList();
        return new SqlQueryPlan(steps, indexes);
    }

    public ValueTask<long> DeleteCollectionsAsync(string prefix, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            ThrowIfReadOnly();
            ArgumentException.ThrowIfNullOrEmpty(prefix);
            if (!store.IsSystem)
            {
                throw new NotSupportedException("a client database drops collections one at a time, with tombstones");
            }

            // The key range covers every collection with this prefix. A purge, not tombstones: CDC ships no deletes, so it
            // stays on this machine (see IStorageTransaction.DeleteCollectionsAsync).
            var to = prefix[..^1] + (char)(prefix[^1] + 1);
            var deleted = 0;
            RemoveMerkleStates(prefix, to);
            foreach (var sql in (ReadOnlySpan<string>)[TursoStatements.DeleteCollectionDocuments, TursoStatements.DeleteCollectionStates, TursoStatements.DeleteCollectionNames])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var command = session.Command(sql, transaction);
                command.Parameters.AddWithValue("$from", prefix);
                command.Parameters.AddWithValue("$to", to);
                var rows = command.ExecuteNonQuery();
                deleted = sql == TursoStatements.DeleteCollectionDocuments ? rows : deleted;
            }

            return (long)deleted;
        }, cancellationToken, mutation: true);

    public ValueTask<IReadOnlyList<IndexDefinition>> ListIndexesAsync(CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<IndexDefinition>>(() => ReadIndexes(cancellationToken), cancellationToken);

    // Worker-only.
    private List<IndexDefinition> ReadIndexes(CancellationToken cancellationToken)
    {
        var indexes = new List<IndexDefinition>();
        using (var reader = session.Command(TursoStatements.ListIndexes, transaction).ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                indexes.Add(ParseIndex(reader.GetString(0), reader.GetString(1)));
            }
        }

        if (!store.IsSystem)
        {
            indexes.AddRange(ReadTextIndexes(cancellationToken));
        }

        // The schema table has no name index; sort here instead of a temporary B-tree.
        indexes.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        return indexes;
    }

    /// <remarks>ponytail: builds synchronously inside the write transaction, so writers wait for the build; build in the background if collections get large.</remarks>
    public ValueTask<bool> CreateIndexAsync(IndexDefinition index, CancellationToken cancellationToken)
    {
        return RunAsync(() =>
        {
            ArgumentNullException.ThrowIfNull(index);
            ThrowIfReadOnly();
            if (!CreateIndex(index, cancellationToken))
            {
                return false;
            }

            WriteMetadata(DocumentDelta.Indexes, index.Name, IndexMetadata(index));
            return true;
        }, cancellationToken, mutation: true);
    }

    // Worker-only, inside a mutation.
    private bool CreateIndex(IndexDefinition index, CancellationToken cancellationToken)
    {
        if (!IndexName().IsMatch(index.Name) || !DatabaseName.TryParse(index.Collection, out _) || index.Fields.Count is < 1 or > 4 || index.Kind is not ("btree" or "text"))
        {
            throw new ArgumentException("invalid index definition", nameof(index));
        }

        if (IndexExists(index.Name))
        {
            return false;
        }

        // An expression index on the collection's own table; versions live in _tb_state, so no key reads them.
        if (store.IsSystem || index.Fields.Any(field => field == DocumentField.Version))
        {
            throw new ArgumentException("an index is built on a client collection's id and fields", nameof(index));
        }

        if (!HasTable(index.Collection))
        {
            CreateCollection(new CollectionInfo(index.Collection, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), cancellationToken);
            if (IndexExists(index.Name))
            {
                return false;
            }
        }

        if (index.Kind == "text")
        {
            CreateTextIndex(index, cancellationToken);
            _statisticsChanged = true;
            return true;
        }

        // ANALYZE of the new index alone reads only that index, so the write lock is held for its collection's size.
        // ponytail: statistics are refreshed only when an index is built; run PRAGMA optimize periodically if data shifts a lot.
        // The id closes the key: queries break ties by id, so the index also serves their whole ORDER BY.
        var keys = string.Join(", ", index.Fields.Select(field => Column(field, "doc"))) + ", id";
        var name = IndexPrefix + index.Name;
        var command = PlainCommand($"""
        CREATE INDEX "{name}" ON {Table(index.Collection)} ({keys});
        ANALYZE "{name}";
        ANALYZE sqlite_schema;
        """);
        using (command)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                command.ExecuteNonQuery();
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }

        _statisticsChanged = true;
        return true;
    }

    public ValueTask<bool> DropIndexAsync(string name, CancellationToken cancellationToken)
    {
        return RunAsync(() =>
        {
            ThrowIfReadOnly();
            if (!DropIndex(name, cancellationToken))
            {
                return false;
            }

            WriteMetadata(DocumentDelta.Indexes, name, null);
            return true;
        }, cancellationToken, mutation: true);
    }

    // Worker-only, inside a mutation.
    private bool DropIndex(string name, CancellationToken cancellationToken)
    {
        if (!IndexName().IsMatch(name) || !IndexExists(name))
        {
            return false;
        }

        if (!store.IsSystem && ReadTextIndexes(cancellationToken).Find(index => index.Name == name) is { } text)
        {
            DropTextIndex(text, cancellationToken);
            return true;
        }

        var command = PlainCommand($"DROP INDEX \"{IndexPrefix}{name}\";");
        using (command)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                command.ExecuteNonQuery();
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }

        return true;
    }

    // The replicated definition of an index, as ReadMetadata reads it back.
    private static string IndexMetadata(IndexDefinition index)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("collection", index.Collection);
            writer.WriteString("kind", index.Kind);
            writer.WriteStartArray("fields");
            foreach (var field in index.Fields)
            {
                writer.WriteStringValue(field.Path);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private bool IndexExists(string name)
    {
        var command = session.Command(TursoStatements.IndexExists, transaction);
        command.Parameters.AddWithValue("$name", IndexPrefix + name);
        return command.ExecuteScalar() is not null || (!store.IsSystem && TextIndexExists(name));
    }

    // Recovers a definition from the CREATE INDEX statement CreateIndexAsync wrote.
    private static IndexDefinition ParseIndex(string name, string sql)
    {
        // The key ends with the id every index carries as its tie-break (CreateIndex).
        var keys = sql[(sql.IndexOf('(', StringComparison.Ordinal) + 1)..sql.LastIndexOf(", id)", StringComparison.Ordinal)];
        // Not a split on ", ": json_extract(doc, '$.path') contains one.
        var fields = IndexKey().Matches(keys).Select(key => key.Value switch
        {
            "id" => DocumentField.Id,
            "version" => DocumentField.Version,
            _ => DocumentField.TryParse(key.Groups[1].Value, out var field)
                ? field
                : throw new InvalidOperationException($"index '{name}' has an unrecognized key"),
        }).ToList();
        var collection = IndexCollection().Match(sql).Groups[1].Value;
        return new IndexDefinition(name[IndexPrefix.Length..], collection, fields);
    }

    private SqliteCommand QueryCommand(SqlQuery query, bool explain, bool observe = false)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!DatabaseName.TryParse(query.Collection, out _))
        {
            throw new ArgumentException("invalid collection name", nameof(query));
        }

        var rows = Rows(query.Collection);
        var command = Uncached(query.Collection);
        // The visit runs before residual rejection, without wrapping predicates that Turso can use as index constraints.
        // A using variable cannot be passed by ref, so the builder is disposed in finally.
        var sql = ZString.CreateStringBuilder();
        try
        {
            sql.AppendFormat("{0}SELECT id, {1} AS version FROM {2}", explain ? "EXPLAIN QUERY PLAN " : "", rows.Version, rows.From);
            // Turso's planner ignores LIMIT: an unfiltered sort on an indexed field reads every row and sorts them instead
            // of walking that index. Name the index then; with a filter the planner chooses.
            if (query.Filter is null && query.Order is [{ Field: var first }, ..] && !first.IsMetadata && !store.IsSystem
                && ReadIndexes(CancellationToken.None).FirstOrDefault(index => index.Kind == "btree" && index.Collection == query.Collection && index.Fields[0] == first) is { } sorted)
            {
                sql.AppendFormat(" INDEXED BY \"{0}{1}\"", IndexPrefix, sorted.Name);
            }

            sql.AppendFormat(" WHERE {0}{1}", observe ? "tb_query_scan(id) AND " : "", rows.Live);
            // Keep indexable predicates visible to the planner. Index seeks narrow the candidates;
            // the scan guard precedes residual JSON predicates in Turso's WHERE evaluation.
            sql.AppendFormat(" AND tb_qos_scan(octet_length({0}))", rows.Json);

            if (query.Filter is not null)
            {
                sql.Append(" AND ");
                AppendFilter(ref sql, command, query.Filter, rows.Json);
            }

            sql.Append(" ORDER BY ");
            foreach (var order in query.Order)
            {
                sql.Append(Column(order.Field, rows.Json));
                sql.Append(order.Descending ? " DESC, " : ", ");
            }

            // Ties break by id in the sort's direction, like the document query compiler, so an index on the sort fields read
            // backwards also serves a descending order and its tie-break instead of a temporary sort.
            sql.Append(query.Order is [{ Descending: true }, ..] ? "id DESC LIMIT $limit;" : "id LIMIT $limit;");
            command.Parameters.AddWithValue("$limit", query.Limit);
            command.CommandText = sql.ToString();
        }
        finally
        {
            sql.Dispose();
        }
        return command;
    }

    private static void AppendFilter(ref Utf16ValueStringBuilder sql, SqliteCommand command, SqlFilter filter, string json)
    {
        switch (filter)
        {
            case SqlAndFilter and:
                AppendGroup(ref sql, command, and.Filters, " AND ", json);
                break;
            case SqlOrFilter or:
                AppendGroup(ref sql, command, or.Filters, " OR ", json);
                break;
            case SqlNotFilter not:
                sql.Append("NOT (");
                AppendFilter(ref sql, command, not.Filter, json);
                sql.Append(')');
                break;
            case SqlCompareFilter compare:
                var column = Column(compare.Field, json);
                switch (compare.Operator)
                {
                    case SqlOperator.IsNull:
                        sql.Append($"{column} IS NULL");
                        break;
                    case SqlOperator.IsNotNull:
                        sql.Append($"{column} IS NOT NULL");
                        break;
                    case SqlOperator.In:
                        // Equalities joined by OR: the Turso planner uses an expression index for OR, not for IN.
                        sql.Append(TursoQueryCompiler.Balanced([.. compare.Values.Select(value => $"{column} = {Parameter(command, value)}")], " OR "));
                        break;
                    case SqlOperator.Prefix:
                        // A half-open range, which an index can seek (TursoQueryCompiler.PrefixSuccessor).
                        var prefix = (string)compare.Values[0];
                        sql.Append(TursoQueryCompiler.PrefixSuccessor(prefix) is { } successor
                            ? $"({column} >= {Parameter(command, prefix)} AND {column} < {Parameter(command, successor)})"
                            : $"{column} >= {Parameter(command, prefix)}");
                        break;
                    default:
                        sql.Append(column);
                        sql.Append(compare.Operator switch
                        {
                            SqlOperator.Equal => " = ",
                            SqlOperator.NotEqual => " != ",
                            SqlOperator.Less => " < ",
                            SqlOperator.LessOrEqual => " <= ",
                            SqlOperator.Greater => " > ",
                            SqlOperator.GreaterOrEqual => " >= ",
                            _ => throw new ArgumentOutOfRangeException(nameof(filter)),
                        });
                        sql.Append(Parameter(command, compare.Values[0]));
                        break;
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(filter));
        }
    }

    // A balanced tree of terms: Turso refuses an expression deeper than 100, and a flat chain of 256 parses 256 deep.
    private static void AppendGroup(ref Utf16ValueStringBuilder sql, SqliteCommand command, IReadOnlyList<SqlFilter> filters, string separator, string json)
    {
        if (filters.Count == 1)
        {
            AppendFilter(ref sql, command, filters[0], json);
            return;
        }

        sql.Append('(');
        AppendGroup(ref sql, command, [.. filters.Take(filters.Count / 2)], separator, json);
        sql.Append(separator);
        AppendGroup(ref sql, command, [.. filters.Skip(filters.Count / 2)], separator, json);
        sql.Append(')');
    }

    // JSON true and false extract as 1 and 0, so booleans compare as those integers.
    private static string Parameter(SqliteCommand command, object value)
    {
        var name = "$p" + command.Parameters.Count.ToString(CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue(name, value is bool flag ? (flag ? 1L : 0L) : value);
        return name;
    }

    private static string Column(DocumentField field, string json) => field.Path switch
    {
        "_id" => "id",
        "_rev" => "version",
        _ => $"json_extract({json}, '$.{field.Path}')",
    };

    private SqliteCommand PlainCommand(string sql)
    {
        // Not cached on the session: these statements embed collections and paths, so their text varies.
        _ranPlain = true;
        var command = session.Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    [GeneratedRegex(@"\A[a-z][a-z0-9_]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IndexName();

    [GeneratedRegex(@"\bON ""([^""]*)"" \(", RegexOptions.CultureInvariant)]
    private static partial Regex IndexCollection();

    // Turso stores the SQL it parsed, with a space after a function name.
    [GeneratedRegex(@"json_extract\s*\(doc, '\$\.([^']*)'\)|\bid\b|\bversion\b", RegexOptions.CultureInvariant)]
    private static partial Regex IndexKey();

    // The JSON reads a statement may evaluate per candidate row, which the query work budget charges.
    [GeneratedRegex(@"\b(?:json_(?:type|extract)|tb_near)\(", RegexOptions.CultureInvariant)]
    private static partial Regex JsonRead();

    // Also inside Turso's "MULTI-INDEX OR people (tb_index_a, tb_index_a)", its plan for an IN list rewritten to OR.
    [GeneratedRegex(@"\btb_index_([a-z0-9_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex UsedIndex();
}
