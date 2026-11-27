using System.Globalization;
using System.Text;

namespace TerraBase.Storage;

internal sealed class TableSql
{
    private readonly TableSchema _schema;
    private readonly string _table;
    private readonly string _where;
    private readonly string _insertAll;
    private readonly string _updateAll;
    private readonly string _upsertAll;
    private readonly Dictionary<string, string> _updates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _inserts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _upserts = new(StringComparer.Ordinal);

    public TableSql(TableSchema schema)
    {
        _schema = schema;
        _table = Quote(schema.Table);
        var where = new StringBuilder();
        for (var index = 0; index < schema.PrimaryKeyColumnNames.Count; index++)
        {
            if (index > 0)
            {
                where.Append(" AND ");
            }

            where.Append(KeyName(index)).Append(" = ?").Append((index + 1).ToString(CultureInfo.InvariantCulture));
        }

        _where = where.ToString();
        var columns = schema.DataColumns.Count == 0 ? "1" : string.Join(", ", schema.DataColumns.Select(static column => Quote(column.Name)));
        SelectRow = string.Concat("SELECT ", columns, " FROM ", _table, " WHERE ", _where);
        DeleteRow = string.Concat("DELETE FROM ", _table, " WHERE ", _where);
        _insertAll = BuildInsert(schema.DataColumns);
        _updateAll = schema.DataColumns.Count > 0 ? BuildUpdate(schema.DataColumns) : string.Empty;
        _upsertAll = SupportsUpsert ? BuildUpsert(schema.DataColumns) : string.Empty;
    }

    public string SelectRow { get; }

    public string DeleteRow { get; }

    public bool SupportsUpsert => !_schema.UsesImplicitRowId && !_schema.IsVirtual;

    public int KeyCount => _schema.PrimaryKeyColumnNames.Count;

    public static string Quote(string identifier) =>
        string.Concat("\"", identifier.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");

    public string Update(IReadOnlyList<TableColumn> columns)
    {
        if (MatchesAllDataColumns(columns))
        {
            return _updateAll;
        }

        var key = columns.Count == 1 ? columns[0].Name : BuildKey(columns);
        if (_updates.TryGetValue(key, out var sql))
        {
            return sql;
        }

        sql = BuildUpdate(columns);
        _updates[key] = sql;
        return sql;
    }

    public string Insert(IReadOnlyList<TableColumn> columns)
    {
        if (MatchesAllDataColumns(columns))
        {
            return _insertAll;
        }

        var key = columns.Count == 1 ? columns[0].Name : BuildKey(columns);
        if (_inserts.TryGetValue(key, out var sql))
        {
            return sql;
        }

        sql = BuildInsert(columns);
        _inserts[key] = sql;
        return sql;
    }

    public string Upsert(IReadOnlyList<TableColumn> columns)
    {
        if (MatchesAllDataColumns(columns))
        {
            return _upsertAll;
        }

        var key = columns.Count == 1 ? columns[0].Name : BuildKey(columns);
        if (_upserts.TryGetValue(key, out var sql))
        {
            return sql;
        }

        sql = BuildUpsert(columns);
        _upserts[key] = sql;
        return sql;
    }

    private bool MatchesAllDataColumns(IReadOnlyList<TableColumn> columns)
    {
        var data = _schema.DataColumns;
        if (columns.Count != data.Count)
        {
            return false;
        }

        for (var index = 0; index < columns.Count; index++)
        {
            if (!ReferenceEquals(columns[index], data[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildKey(IReadOnlyList<TableColumn> columns)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < columns.Count; index++)
        {
            if (index > 0)
            {
                builder.Append('\u001f');
            }

            builder.Append(columns[index].Name);
        }

        return builder.ToString();
    }

    private string BuildUpdate(IReadOnlyList<TableColumn> columns)
    {
        var builder = new StringBuilder("UPDATE ").Append(_table).Append(" SET ");
        for (var index = 0; index < columns.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Quote(columns[index].Name)).Append(" = ?").Append((KeyCount + index + 1).ToString(CultureInfo.InvariantCulture));
        }

        return builder.Append(" WHERE ").Append(_where).ToString();
    }

    private string BuildInsert(IReadOnlyList<TableColumn> columns)
    {
        var names = new StringBuilder();
        var parameters = new StringBuilder();
        for (var index = 0; index < KeyCount + columns.Count; index++)
        {
            if (index > 0)
            {
                names.Append(", ");
                parameters.Append(", ");
            }

            names.Append(index < KeyCount ? KeyName(index) : Quote(columns[index - KeyCount].Name));
            parameters.Append('?').Append((index + 1).ToString(CultureInfo.InvariantCulture));
        }

        return string.Concat("INSERT INTO ", _table, " (", names.ToString(), ") VALUES (", parameters.ToString(), ")");
    }

    private string BuildUpsert(IReadOnlyList<TableColumn> columns)
    {
        var pkNames = new StringBuilder();
        var names = new StringBuilder();
        var parameters = new StringBuilder();
        for (var index = 0; index < KeyCount; index++)
        {
            if (index > 0)
            {
                pkNames.Append(", ");
                names.Append(", ");
                parameters.Append(", ");
            }

            var pk = KeyName(index);
            pkNames.Append(pk);
            names.Append(pk);
            parameters.Append('?').Append((index + 1).ToString(CultureInfo.InvariantCulture));
        }

        if (columns.Count == 0)
        {
            return string.Concat("INSERT OR IGNORE INTO ", _table, " (", names.ToString(), ") VALUES (", parameters.ToString(), ")");
        }

        var updates = new StringBuilder();
        for (var index = 0; index < columns.Count; index++)
        {
            var quoted = Quote(columns[index].Name);
            names.Append(", ").Append(quoted);
            parameters.Append(", ?").Append((KeyCount + index + 1).ToString(CultureInfo.InvariantCulture));
            if (index > 0)
            {
                updates.Append(", ");
            }

            updates.Append(quoted).Append(" = excluded.").Append(quoted);
        }

        return string.Concat(
            "INSERT INTO ",
            _table,
            " (",
            names.ToString(),
            ") VALUES (",
            parameters.ToString(),
            ") ON CONFLICT (",
            pkNames.ToString(),
            ") DO UPDATE SET ",
            updates.ToString());
    }

    private string KeyName(int index) => _schema.UsesImplicitRowId ? "rowid" : Quote(_schema.PrimaryKeyColumnNames[index]);
}

internal sealed class WriteScope(SqliteConnection connection, MetaCache? metaCache = null)
{
    private byte[] _metaScratch = new byte[512];
    private SqlValue[] _parametersScratch = [];

    public SqliteConnection Connection { get; } = connection;

    public MetaCache? MetaCache { get; } = metaCache;

    public byte[] HlcScratch { get; } = new byte[HlcTimestamp.EncodedSize];

    public byte[] UlidScratch { get; } = new byte[16];

    public List<TableColumn> ApplyColumns { get; } = [];

    public List<SqlValue> ApplyValues { get; } = [];

    public SqliteStatus Failure { get; private set; } = SqliteStatus.Success;

    public bool Failed => !Failure.IsOk;

    public byte[] GetMetaScratch(int minLength)
    {
        if (_metaScratch.Length < minLength)
        {
            _metaScratch = new byte[Math.Max(_metaScratch.Length * 2, minLength)];
        }

        return _metaScratch;
    }

    public SqlValue[] GetParameterScratch(int count)
    {
        if (_parametersScratch.Length < count)
        {
            _parametersScratch = new SqlValue[Math.Max(count, 8)];
        }

        return _parametersScratch;
    }

    public void Fail(SqliteStatus status)
    {
        if (!Failed && !status.IsOk)
        {
            Failure = status;
        }
    }

    public void Reset() => Failure = SqliteStatus.Success;

    public void Run(string sql, params ReadOnlySpan<SqlValue> parameters)
    {
        if (!Failed)
        {
            Fail(Connection.Run(sql, parameters));
        }
    }

    public long RunChanges(string sql, params ReadOnlySpan<SqlValue> parameters)
    {
        if (Failed)
        {
            return 0;
        }

        var status = Connection.Run(sql, parameters);
        Fail(status);
        return status.IsOk ? Connection.Changes64 : 0;
    }

    public SqlValue[]? First(string sql, params ReadOnlySpan<SqlValue> parameters)
    {
        if (Failed)
        {
            return null;
        }

        var status = Connection.QueryFirst(sql, out var row, parameters);
        Fail(status);
        return row;
    }
}
