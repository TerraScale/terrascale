using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace TerraBase.Engine.Sqlite;

/// <remarks>
/// SQL-subset queries and indexes compile to SQL over the live rows of the <c>documents</c> table (<c>body IS NOT NULL</c>;
/// a tombstone has a NULL body). Fields become <c>id</c>, <c>version</c> or <c>json_extract(body, '$.path')</c>, and every
/// index is a partial expression index restricted to the live documents of its collection. The key starts with
/// <c>collection</c>, which the query repeats as an equality, so the index can also return a collection in field order.
/// The collection and the paths are embedded as literals, never as parameters: SQLite uses an expression index only for
/// a textually equal expression, and a partial index only when the query repeats its <c>WHERE</c> terms. Both are safe to
/// embed because <see cref="DatabaseName"/> and <see cref="DocumentField"/> admit no quotes.
/// </remarks>
internal sealed partial class SqliteStorageTransaction
{
    private const string IndexPrefix = "tb_index_";

    public ValueTask<long> CountDocumentsAsync(string collection, CancellationToken cancellationToken) =>
        RunAsync(() => WithQueryBudget(() =>
            (long)Command(SqliteStatements.CountDocuments, collection).ExecuteScalar()!, cancellationToken), cancellationToken);

    public ValueTask<IReadOnlyList<CollectionStats>> ListCollectionStatsAsync(CancellationToken cancellationToken) =>
        RunAsync(() => WithQueryBudget<IReadOnlyList<CollectionStats>>(() =>
        {
            var collections = new List<CollectionStats>();
            using var reader = session.Command(SqliteStatements.ListCollectionStats, transaction).ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                collections.Add(new CollectionStats(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
            }

            return collections;
        }, cancellationToken), cancellationToken);

    public IAsyncEnumerable<StoredDocument> QuerySqlAsync(SqlQuery query, CancellationToken cancellationToken, QueryObservation? observation = null) =>
        ReadBudgetedAsync(() => QueryCommand(query, "SELECT id, version FROM documents", observation is not null), cancellationToken, query, observation);

    public ValueTask<SqlQueryPlan> ExplainSqlAsync(SqlQuery query, CancellationToken cancellationToken) =>
        RunAsync(() => WithQueryBudget(() => ReadSqlPlan(query, cancellationToken), cancellationToken), cancellationToken);

    private SqlQueryPlan ReadSqlPlan(SqlQuery query, CancellationToken cancellationToken)
    {
        using var command = QueryCommand(query, "EXPLAIN QUERY PLAN SELECT id, version FROM documents");
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
            // The primary-key range covers every collection with this prefix.
            var to = prefix[..^1] + (char)(prefix[^1] + 1);
            if (store.IsSystem && _changes is not null)
            {
                CaptureSystemDeletions(prefix, to, cancellationToken);
            }

            var documents = session.Command(SqliteStatements.DeleteCollectionDocuments, transaction);
            documents.Parameters.AddWithValue("$from", prefix);
            documents.Parameters.AddWithValue("$to", to);
            var deleted = documents.ExecuteNonQuery();
            cancellationToken.ThrowIfCancellationRequested();
            var names = session.Command(SqliteStatements.DeleteCollectionNames, transaction);
            names.Parameters.AddWithValue("$from", prefix);
            names.Parameters.AddWithValue("$to", to);
            names.ExecuteNonQuery();
            return (long)deleted;
        }, cancellationToken, mutation: true);

    public ValueTask<IReadOnlyList<IndexDefinition>> ListIndexesAsync(CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<IndexDefinition>>(() =>
        {
            var indexes = new List<IndexDefinition>();
            using var reader = session.Command(SqliteStatements.ListIndexes, transaction).ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                indexes.Add(ParseIndex(reader.GetString(0), reader.GetString(1)));
            }

            // The schema table has no name index; sort here instead of a temporary B-tree.
            indexes.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
            return indexes;
        }, cancellationToken);

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

            WriteMetadata(DocumentChange.Indexes, index.Name, IndexMetadata(index));
            return true;
        }, cancellationToken, mutation: true);
    }

    // Worker-only, inside a mutation.
    private bool CreateIndex(IndexDefinition index, CancellationToken cancellationToken)
    {
        if (!IndexName().IsMatch(index.Name) || !DatabaseName.TryParse(index.Collection, out _) || index.Fields.Count is < 1 or > 4)
        {
            throw new ArgumentException("invalid index definition", nameof(index));
        }

        if (IndexExists(index.Name))
        {
            return false;
        }

        // The collection leads the key, so the index also orders a whole collection by its fields (ORDER BY),
        // not only answers equality on them.
        var keys = "collection, " + string.Join(", ", index.Fields.Select(Column));
        // Without statistics the planner prefers the primary key or documents_live, whose leading column (collection) is
        // also an equality, over any secondary index. ANALYZE of the new index alone reads only that partial index, so
        // the write lock is held for its collection's size, not the database's. The two collection-leading indexes get
        // no full scan either: each is seeded or raised from the new index's row count when that establishes a larger
        // lower bound for the table (SQLite lets applications write to sqlite_stat1), and the statistics are reloaded.
        // The primary key's statistics row is named after the table (WITHOUT ROWID).
        // ponytail: statistics are refreshed only when an index is built; run PRAGMA optimize periodically if data shifts a lot.
        var name = IndexPrefix + index.Name;
        var command = PlainCommand($"""
        CREATE INDEX "{name}" ON documents ({keys}) WHERE collection = '{index.Collection}' AND body IS NOT NULL;
        ANALYZE "{name}";
        DELETE FROM sqlite_stat1
            WHERE tbl = 'documents' AND idx IN ('documents', 'documents_live')
            AND CAST(substr(stat, 1, instr(stat || ' ', ' ') - 1) AS INTEGER)
                < (SELECT CAST(substr(stat, 1, instr(stat || ' ', ' ') - 1) AS INTEGER) FROM sqlite_stat1 WHERE idx = '{name}');
        INSERT INTO sqlite_stat1 (tbl, idx, stat)
            SELECT 'documents', seeded.idx, rows || ' ' || rows || ' 1'
            FROM (SELECT substr(stat, 1, instr(stat || ' ', ' ') - 1) AS rows FROM sqlite_stat1 WHERE idx = '{name}'),
                (SELECT 'documents' AS idx UNION ALL SELECT 'documents_live') AS seeded
            WHERE NOT EXISTS (SELECT 1 FROM sqlite_stat1 WHERE tbl = 'documents' AND idx = seeded.idx);
        ANALYZE sqlite_schema;
        """);
        using (command)
        {
            raw.sqlite3_progress_handler(session.Connection.Handle, 1000, _ => cancellationToken.IsCancellationRequested ? 1 : 0, null);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                command.ExecuteNonQuery();
            }
            catch (SqliteException error) when (error.SqliteErrorCode == raw.SQLITE_INTERRUPT && cancellationToken.IsCancellationRequested)
            {
                _faulted = true;
                throw new OperationCanceledException("index build canceled", error, cancellationToken);
            }
            catch
            {
                _faulted = true;
                throw;
            }
            finally
            {
                StopQueryBudget();
            }
        }

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

            WriteMetadata(DocumentChange.Indexes, name, null);
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
        var command = session.Command(SqliteStatements.IndexExists, transaction);
        command.Parameters.AddWithValue("$name", IndexPrefix + name);
        return command.ExecuteScalar() is not null;
    }

    // Recovers a definition from the CREATE INDEX statement CreateIndexAsync wrote.
    private static IndexDefinition ParseIndex(string name, string sql)
    {
        var keys = sql[(sql.IndexOf('(', StringComparison.Ordinal) + 1)..sql.LastIndexOf(") WHERE", StringComparison.Ordinal)];
        // Not a split on ", ": json_extract(body, '$.path') contains one.
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

    private SqliteCommand QueryCommand(SqlQuery query, string select, bool observe = false)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!DatabaseName.TryParse(query.Collection, out _))
        {
            throw new ArgumentException("invalid collection name", nameof(query));
        }

        var command = PlainCommand(string.Empty);
        // Tombstones are excluded with the same term the indexes' WHERE carries, so a partial index stays usable.
        // The visit runs before residual rejection, without wrapping predicates that SQLite can use as index constraints.
        var sql = new StringBuilder(select).Append(CultureInfo.InvariantCulture,
            $" WHERE collection = '{query.Collection}' AND {(observe ? "tb_query_scan() AND " : "")}body IS NOT NULL");
        if (query.Filter is not null)
        {
            sql.Append(" AND ");
            AppendFilter(sql, command, query.Filter);
        }

        sql.Append(" ORDER BY ");
        foreach (var order in query.Order)
        {
            sql.Append(Column(order.Field)).Append(order.Descending ? " DESC, " : ", ");
        }

        // Ties break by id in the sort's direction, like the document query compiler, so an index on the sort fields read
        // backwards also serves a descending order and its tie-break instead of a temporary sort.
        sql.Append(query.Order is [{ Descending: true }, ..] ? "id DESC LIMIT $limit;" : "id LIMIT $limit;");
        command.Parameters.AddWithValue("$limit", query.Limit);
        command.CommandText = sql.ToString();
        return command;
    }

    private static void AppendFilter(StringBuilder sql, SqliteCommand command, SqlFilter filter)
    {
        switch (filter)
        {
            case SqlAndFilter and:
                AppendGroup(sql, command, and.Filters, " AND ");
                break;
            case SqlOrFilter or:
                AppendGroup(sql, command, or.Filters, " OR ");
                break;
            case SqlNotFilter not:
                sql.Append("NOT (");
                AppendFilter(sql, command, not.Filter);
                sql.Append(')');
                break;
            case SqlCompareFilter compare:
                sql.Append(Column(compare.Field));
                switch (compare.Operator)
                {
                    case SqlOperator.IsNull:
                        sql.Append(" IS NULL");
                        break;
                    case SqlOperator.IsNotNull:
                        sql.Append(" IS NOT NULL");
                        break;
                    case SqlOperator.In:
                        sql.Append(" IN (").AppendJoin(", ", compare.Values.Select(value => Parameter(command, value))).Append(')');
                        break;
                    default:
                        sql.Append(compare.Operator switch
                        {
                            SqlOperator.Equal => " = ",
                            SqlOperator.NotEqual => " != ",
                            SqlOperator.Less => " < ",
                            SqlOperator.LessOrEqual => " <= ",
                            SqlOperator.Greater => " > ",
                            SqlOperator.GreaterOrEqual => " >= ",
                            _ => throw new ArgumentOutOfRangeException(nameof(filter)),
                        }).Append(Parameter(command, compare.Values[0]));
                        break;
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(filter));
        }
    }

    private static void AppendGroup(StringBuilder sql, SqliteCommand command, IReadOnlyList<SqlFilter> filters, string separator)
    {
        sql.Append('(');
        for (var i = 0; i < filters.Count; i++)
        {
            sql.Append(i == 0 ? string.Empty : separator);
            AppendFilter(sql, command, filters[i]);
        }

        sql.Append(')');
    }

    // JSON true and false extract as 1 and 0, so booleans compare as those integers.
    private static string Parameter(SqliteCommand command, object value)
    {
        var name = "$p" + command.Parameters.Count.ToString(CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue(name, value is bool flag ? (flag ? 1L : 0L) : value);
        return name;
    }

    private static string Column(DocumentField field) => field.Path switch
    {
        "_id" => "id",
        "_rev" => "version",
        _ => $"json_extract(body, '$.{field.Path}')",
    };

    private SqliteCommand PlainCommand(string sql)
    {
        // Not cached on the session: these statements embed collections and paths, so their text varies.
        var command = session.Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    [GeneratedRegex(@"\A[a-z][a-z0-9_]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IndexName();

    [GeneratedRegex(@"WHERE collection = '([^']*)' AND body IS NOT NULL\z", RegexOptions.CultureInvariant)]
    private static partial Regex IndexCollection();

    [GeneratedRegex(@"json_extract\(body, '\$\.([^']*)'\)|\bid\b|\bversion\b", RegexOptions.CultureInvariant)]
    private static partial Regex IndexKey();

    [GeneratedRegex(@"INDEX tb_index_([a-z0-9_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex UsedIndex();
}
