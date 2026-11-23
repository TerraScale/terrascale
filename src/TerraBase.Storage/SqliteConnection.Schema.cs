namespace TerraBase.Storage;

internal sealed partial class SqliteConnection
{
    public SqliteStatus ValidateSchemaCache()
    {
        var status = QueryFirst("PRAGMA schema_version", out var row);
        if (!status.IsOk || row is null || !row[0].TryGetIntegral(out var cookie))
        {
            return status.IsOk ? SqliteStatus.Fail("schema_version unavailable") : status;
        }

        var deltaVersion = _options.DeltaColumns.Version;
        if (cookie == _schemaCookie && deltaVersion == _deltaVersion)
        {
            return SqliteStatus.Success;
        }

        _schemaCookie = cookie;
        _deltaVersion = deltaVersion;
        _schemas.Clear();
        List<SqlValue[]> tables = [];
        status = Query("SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' AND name NOT LIKE '\\_tb\\_%' ESCAPE '\\'", tables);
        if (!status.IsOk)
        {
            return status;
        }

        foreach (var table in tables)
        {
            if (table[0].TryGetText(out var name))
            {
                _ = Schema(name);
            }
        }

        return SqliteStatus.Success;
    }

    public void InvalidateSchemaCache()
    {
        _schemas.Clear();
        _schemaCookie = -1;
    }

    public TableSchema? Schema(string table)
    {
        if (_schemas.TryGetValue(table, out var cached))
        {
            return cached;
        }

        var status = QueryFirst("SELECT type, wr FROM pragma_table_list(?1) WHERE schema = 'main'", out var listing, SqlValue.FromText(table));
        if (!status.IsOk || listing is null || !listing[0].TryGetText(out var type) || type is not ("table" or "virtual" or "shadow"))
        {
            return null;
        }

        _ = listing[1].TryGetIntegral(out var withoutRowId);
        List<SqlValue[]> rows = [];
        status = Query("SELECT name, type, \"notnull\", pk, hidden FROM pragma_table_xinfo(?1, 'main')", rows, SqlValue.FromText(table));
        if (!status.IsOk || rows.Count == 0)
        {
            return null;
        }

        List<TableColumn> columns = [];
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            _ = row[4].TryGetIntegral(out var hidden);
            if (hidden == 1 || !row[0].TryGetText(out var name))
            {
                continue;
            }

            _ = row[1].TryGetText(out var declared);
            _ = row[2].TryGetIntegral(out var notNull);
            _ = row[3].TryGetIntegral(out var pk);
            columns.Add(new TableColumn(
                name,
                index,
                declared,
                notNull != 0,
                (int)pk,
                hidden is 2 or 3,
                _options.DeltaColumns.IsDeltaColumn(_options.DatabaseId, table, name)));
        }

        List<SqlValue[]> indexes = [];
        status = Query("SELECT name, \"unique\", origin FROM pragma_index_list(?1, 'main')", indexes, SqlValue.FromText(table));
        if (!status.IsOk)
        {
            return null;
        }

        var secondaryUnique = false;
        string? primaryKeyIndex = null;
        foreach (var index in indexes)
        {
            _ = index[0].TryGetText(out var indexName);
            _ = index[1].TryGetIntegral(out var unique);
            _ = index[2].TryGetText(out var origin);
            if (string.Equals(origin, "pk", StringComparison.Ordinal))
            {
                primaryKeyIndex = indexName;
            }
            else if (unique != 0)
            {
                secondaryUnique = true;
            }
        }

        var schema = new TableSchema(
            table,
            columns,
            rows.Count,
            withoutRowId != 0,
            string.Equals(type, "virtual", StringComparison.Ordinal),
            string.Equals(type, "shadow", StringComparison.Ordinal),
            secondaryUnique,
            primaryKeyIndex);
        _schemas[table] = schema;
        return schema;
    }
}
