using System.Globalization;
using System.Text;

namespace TerraBase.Storage;

internal sealed class TableSql
{
    private readonly TableSchema _schema;
    private readonly string _table;
    private readonly string _where;
    private readonly Dictionary<string, string> _updates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _inserts = new(StringComparer.Ordinal);

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
    }

    public string SelectRow { get; }

    public string DeleteRow { get; }

    public int KeyCount => _schema.PrimaryKeyColumnNames.Count;

    public static string Quote(string identifier) =>
        string.Concat("\"", identifier.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");

    public string Update(IReadOnlyList<TableColumn> columns)
    {
        var key = string.Join('\u001f', columns.Select(static column => column.Name));
        if (_updates.TryGetValue(key, out var sql))
        {
            return sql;
        }

        var builder = new StringBuilder("UPDATE ").Append(_table).Append(" SET ");
        for (var index = 0; index < columns.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Quote(columns[index].Name)).Append(" = ?").Append((KeyCount + index + 1).ToString(CultureInfo.InvariantCulture));
        }

        sql = builder.Append(" WHERE ").Append(_where).ToString();
        _updates[key] = sql;
        return sql;
    }

    public string Insert(IReadOnlyList<TableColumn> columns)
    {
        var key = string.Join('\u001f', columns.Select(static column => column.Name));
        if (_inserts.TryGetValue(key, out var sql))
        {
            return sql;
        }

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

        sql = string.Concat("INSERT INTO ", _table, " (", names.ToString(), ") VALUES (", parameters.ToString(), ")");
        _inserts[key] = sql;
        return sql;
    }

    private string KeyName(int index) => _schema.UsesImplicitRowId ? "rowid" : Quote(_schema.PrimaryKeyColumnNames[index]);
}

internal sealed class WriteScope(SqliteConnection connection)
{
    private readonly Dictionary<TableSchema, TableSql> _sql = [];

    public SqliteConnection Connection { get; } = connection;

    public SqliteStatus Failure { get; private set; } = SqliteStatus.Success;

    public bool Failed => !Failure.IsOk;

    public void Fail(SqliteStatus status)
    {
        if (!Failed && !status.IsOk)
        {
            Failure = status;
        }
    }

    public void Reset() => Failure = SqliteStatus.Success;

    public TableSql Sql(TableSchema schema)
    {
        if (!_sql.TryGetValue(schema, out var sql))
        {
            sql = new TableSql(schema);
            _sql[schema] = sql;
        }

        return sql;
    }

    public void Run(string sql, params ReadOnlySpan<SqlValue> parameters)
    {
        if (!Failed)
        {
            Fail(Connection.Run(sql, parameters));
        }
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
