using System.Globalization;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using TerraBase.Engine;
using TerraBase.Tenancy;

namespace TerraBase.Features.Sql.Routing;

internal sealed record TableFacts(
    string Name, bool Rowid, bool Autoincrement, IReadOnlySet<string> Key, IReadOnlyList<IReadOnlySet<string>> Unique, bool UniqueExpression,
    IReadOnlySet<string> ForeignKeyColumns, IReadOnlySet<string> ReferencedColumns, bool Referenced, bool Triggers);

internal sealed record SchemaFacts(IReadOnlyDictionary<string, TableFacts> Tables);

internal static class SchemaDdl
{
    public static bool WithoutRowid(string createSql) =>
        SqlText.TryTokenize(createSql, out var tokens) && WithoutRowid(tokens);

    public static bool WithoutRowid(List<SqlToken> tokens) =>
        tokens.Select((token, i) => token.Is("WITHOUT") && i + 1 < tokens.Count && tokens[i + 1].Is("ROWID")).Any(match => match);
}

internal sealed class SchemaFactsCache
{
    private const int MaxEntries = 4096;
    private const int MaxTables = 512;
    private static readonly IReadOnlyDictionary<string, object?> NoNamed = new Dictionary<string, object?>();
    private readonly ConcurrentDictionary<(string Database, string Schema), SchemaFacts?> _facts = new();
    private readonly ConcurrentQueue<(string Database, string Schema)> _entries = new();

    public async ValueTask<SchemaFacts?> ReadAsync(TenantContext database, IReplicaReader reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(reader);
        var schema = await QueryAsync(reader, "SELECT type, name, tbl_name, sql FROM sqlite_schema ORDER BY type, name", cancellationToken).ConfigureAwait(false);
        var key = (database.DatabaseId ?? "", Hash(schema.Rows));
        if (_facts.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var facts = await BuildAsync(reader, schema.Rows, cancellationToken).ConfigureAwait(false);
        _entries.Enqueue(key);
        while (_facts.Count > MaxEntries && _entries.TryDequeue(out var oldest))
        {
            _facts.TryRemove(oldest, out _);
        }

        _facts[key] = facts;
        return facts;
    }

    private static async ValueTask<SchemaFacts?> BuildAsync(IReplicaReader reader, IReadOnlyList<object?[]> schema, CancellationToken cancellationToken)
    {
        var tables = schema.Where(row => row[0] as string == "table" && row[1] is string name && !SqlScript.IsReservedName(name)).ToList();
        if (tables.Count > MaxTables)
        {
            return null;
        }

        var triggers = schema.Where(row => row[0] as string == "trigger").Select(row => (row[2] as string ?? "").ToLowerInvariant()).ToHashSet();
        var parents = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var built = new List<(string Name, string Sql, HashSet<string> Key, bool IntegerKey, List<IReadOnlySet<string>> Unique, bool Expression, HashSet<string> Foreign)>();
        foreach (var row in tables)
        {
            var name = (string)row[1]!;
            var quoted = SqlScript.Quote(name);
            var info = await QueryAsync(reader, $"PRAGMA table_info({quoted})", cancellationToken).ConfigureAwait(false);
            var (columnName, columnType, columnKey) = (info.Index("name"), info.Index("type"), info.Index("pk"));
            var key = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var integerKey = false;
            foreach (var column in info.Rows)
            {
                if (Convert.ToInt64(column[columnKey], CultureInfo.InvariantCulture) > 0)
                {
                    key.Add((string)column[columnName]!);
                    integerKey = string.Equals(column[columnType] as string, "INTEGER", StringComparison.OrdinalIgnoreCase);
                }
            }

            var unique = new List<IReadOnlySet<string>>();
            var expression = false;
            var indexes = await QueryAsync(reader, $"PRAGMA index_list({quoted})", cancellationToken).ConfigureAwait(false);
            var (indexName, indexUnique, indexOrigin) = (indexes.Index("name"), indexes.Index("unique"), indexes.Index("origin"));
            foreach (var index in indexes.Rows)
            {
                if (Convert.ToInt64(index[indexUnique], CultureInfo.InvariantCulture) == 0
                    || (indexOrigin >= 0 && index[indexOrigin] as string == "pk") || index[indexName] is not string named)
                {
                    continue;
                }

                var parts = await QueryAsync(reader, $"PRAGMA index_info({SqlScript.Quote(named)})", cancellationToken).ConfigureAwait(false);
                var partName = parts.Index("name");
                var columns = parts.Rows.Select(part => part[partName] as string).ToList();
                expression |= columns.Count == 0 || columns.Exists(column => column is null);
                var set = columns.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!set.SetEquals(key))
                {
                    unique.Add(set);
                }
            }

            var foreign = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keys = await QueryAsync(reader, $"PRAGMA foreign_key_list({quoted})", cancellationToken).ConfigureAwait(false);
            var (foreignTable, foreignFrom, foreignTo) = (keys.Index("table"), keys.Index("from"), keys.Index("to"));
            foreach (var reference in keys.Rows)
            {
                foreign.Add((string)reference[foreignFrom]!);
                var parent = (string)reference[foreignTable]!;
                if (!parents.TryGetValue(parent, out var referenced))
                {
                    parents[parent] = referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                if (reference[foreignTo] is string to)
                {
                    referenced.Add(to);
                }
            }

            built.Add((name, row[3] as string ?? "", key, integerKey, unique, expression, foreign));
        }

        var facts = new Dictionary<string, TableFacts>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, sql, key, integerKey, unique, expression, foreign) in built)
        {
            var words = SqlText.TryTokenize(sql, out var tokens) ? tokens : [];
            var withoutRowid = SchemaDdl.WithoutRowid(words);
            var rowid = !withoutRowid && (key.Count == 0 || (key.Count == 1 && integerKey));
            var referenced = parents.TryGetValue(name, out var columns);
            var referencedColumns = new HashSet<string>(columns ?? [], StringComparer.OrdinalIgnoreCase);
            if (referenced && referencedColumns.Count == 0)
            {
                referencedColumns.UnionWith(key);
            }

            facts[name] = new TableFacts(name, rowid, words.Exists(token => token.Is("AUTOINCREMENT")), key, unique, expression, foreign, referencedColumns,
                referenced, triggers.Contains(name.ToLowerInvariant()));
        }

        return new SchemaFacts(facts);
    }

    private static string Hash(IReadOnlyList<object?[]> rows)
    {
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            foreach (var value in row)
            {
                text.Append(value as string).Append('\u001f');
            }

            text.Append('\u001e');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static async ValueTask<QueryRows> QueryAsync(IReplicaReader reader, string sql, CancellationToken cancellationToken)
    {
        var result = await reader.ExecuteAsync(new SqlStatement(sql, [], NoNamed, 4096, 4 * 1024 * 1024, SqlLimits.MaxRequestTime), cancellationToken)
            .ConfigureAwait(false);
        return new QueryRows(result.Columns, result.Rows);
    }

    private sealed record QueryRows(IReadOnlyList<SqlStatementColumn> Columns, IReadOnlyList<object?[]> Rows)
    {
        public int Index(string name)
        {
            for (var i = 0; i < Columns.Count; i++)
            {
                if (string.Equals(Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
