using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cysharp.Text;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{
    private const string IndexPrefix = "tb_index_";

    public ValueTask<long> CountDocumentsAsync(string collection, CancellationToken cancellationToken) =>
        RunAsync(() => WithQueryBudget(() =>
        {
            var rows = Rows(collection);
            using var command = Uncached(collection);

            command.CommandText = $"SELECT count(*) FROM {rows.From} WHERE {rows.Live} AND tb_qos_scan(octet_length({rows.Json}));";
            CountRead("query");
            return (long)command.ExecuteScalar()!;
        }, cancellationToken), cancellationToken);

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

            var to = prefix[..^1] + (char)(prefix[^1] + 1);
            var deleted = 0;

            foreach (var sql in (ReadOnlySpan<string>)[TursoStatements.DeleteCollectionDocuments, TursoStatements.DeleteCollectionStates, TursoStatements.DeleteCollectionNames])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var command = session.Command(sql, transaction);
                command.Parameters.AddWithValue("$from", prefix);
                command.Parameters.AddWithValue("$to", to);
                var rows = command.ExecuteNonQuery();
                deleted = sql == TursoStatements.DeleteCollectionDocuments ? rows : deleted;
            }

            ChangedReaderState = true;
            return (long)deleted;
        }, cancellationToken, mutation: true);

    public ValueTask<IReadOnlyList<IndexDefinition>> ListIndexesAsync(CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<IndexDefinition>>(() => ReadIndexes(cancellationToken), cancellationToken);

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
            indexes.AddRange(ReadVectorIndexes());
        }

        indexes.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        return indexes;
    }

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

    private bool CreateIndex(IndexDefinition index, CancellationToken cancellationToken)
    {
        if (!IndexName().IsMatch(index.Name) || !DatabaseName.TryParse(index.Collection, out _) || index.Fields.Count is < 1 or > 4 || index.Kind is not ("btree" or "text" or "vector"))
        {
            throw new ArgumentException("invalid index definition", nameof(index));
        }

        if (IndexExists(index.Name))
        {
            return false;
        }

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

        if (index.Kind == "vector")
        {
            CreateVectorIndex(index);
            _statisticsChanged = true;
            return true;
        }

        if (index.Kind == "text")
        {
            CreateTextIndex(index, cancellationToken);
            _statisticsChanged = true;
            return true;
        }

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

        if (!store.IsSystem && ReadVectorIndexes().Exists(index => index.Name == name))
        {
            DropVectorIndex(name);
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
            if (index.Kind == "vector")
            {
                writer.WriteNumber("dimensions", index.Dimensions);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private bool IndexExists(string name)
    {
        var command = session.Command(TursoStatements.IndexExists, transaction);
        command.Parameters.AddWithValue("$name", IndexPrefix + name);
        return command.ExecuteScalar() is not null || (!store.IsSystem && (TextIndexExists(name) || ReadVectorIndexes().Exists(index => index.Name == name)));
    }

    private static IndexDefinition ParseIndex(string name, string sql)
    {

        var keys = sql[(sql.IndexOf('(', StringComparison.Ordinal) + 1)..sql.LastIndexOf(", id)", StringComparison.Ordinal)];

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

        var sql = ZString.CreateStringBuilder();
        try
        {
            sql.AppendFormat("{0}SELECT id, {1} AS version FROM {2}", explain ? "EXPLAIN QUERY PLAN " : "", rows.Version, rows.From);

            if (query.Filter is null && query.Order is [{ Field: var first }, ..] && !first.IsMetadata && !store.IsSystem
                && ReadIndexes(CancellationToken.None).FirstOrDefault(index => index.Kind == "btree" && index.Collection == query.Collection && index.Fields[0] == first) is { } sorted)
            {
                sql.AppendFormat(" INDEXED BY \"{0}{1}\"", IndexPrefix, sorted.Name);
            }

            sql.AppendFormat(" WHERE {0}{1}", observe ? "tb_query_scan(id) AND " : "", rows.Live);

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

                        sql.Append(TursoQueryCompiler.Balanced([.. compare.Values.Select(value => $"{column} = {Parameter(command, value)}")], " OR "));
                        break;
                    case SqlOperator.Prefix:

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

    [GeneratedRegex(@"json_extract\s*\(doc, '\$\.([^']*)'\)|\bid\b|\bversion\b", RegexOptions.CultureInvariant)]
    private static partial Regex IndexKey();

    [GeneratedRegex(@"\b(?:json_(?:type|extract)|tb_near)\(", RegexOptions.CultureInvariant)]
    private static partial Regex JsonRead();

    [GeneratedRegex(@"\btb_index_([a-z0-9_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex UsedIndex();
}
