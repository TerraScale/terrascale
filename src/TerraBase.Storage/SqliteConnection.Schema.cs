namespace TerraBase.Storage;

internal sealed record TableNotFound(string Table);

internal readonly union TableSchemaLookup(TableSchema, TableNotFound, SqliteStatus);

internal sealed unsafe partial class SqliteConnection
{
    public SqliteStatus ReadSchemaCookie(out long cookie)
    {
        cookie = 0;
        var statement = _schemaCookieStatement;
        if (statement == 0)
        {
            var utf8 = "PRAGMA schema_version"u8;
            byte* tail = null;
            int rc;
            fixed (byte* pointer = utf8)
            {
                rc = SqliteNative.PrepareV3(Handle, pointer, utf8.Length, SqliteNative.PreparePersistent, &statement, &tail);
            }

            if (rc != SqliteNative.Ok)
            {
                return Status(rc);
            }

            _schemaCookieStatement = statement;
        }

        var step = SqliteNative.Step(statement);
        if (step == SqliteNative.Row)
        {
            cookie = SqliteNative.ColumnInt64(statement, 0);
            var reset = SqliteNative.Reset(statement);
            return reset == SqliteNative.Ok ? SqliteStatus.Success : Status(reset);
        }

        var status = step == SqliteNative.Done ? SqliteStatus.Fail("schema_version unavailable") : Status(step);
        _ = SqliteNative.Reset(statement);
        return status;
    }

    public SqliteStatus ValidateSchemaCache()
    {
        var status = ReadSchemaCookie(out var cookie);
        if (!status.IsOk)
        {
            InvalidateSchemaCache();
            return status;
        }

        var deltaVersion = _options.DeltaColumns.Version;
        if (cookie == _schemaCookie && deltaVersion == _deltaVersion)
        {
            MarkUserCacheValidated(cookie, deltaVersion);
            return SqliteStatus.Success;
        }

        InvalidateSchemaCache();
        List<SqlValue[]> tables = [];
        status = Query("SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' AND name NOT LIKE '\\_tb\\_%' ESCAPE '\\'", tables);
        if (!status.IsOk)
        {
            return status;
        }

        foreach (var table in tables)
        {
            if (!table[0].TryGetText(out var name))
            {
                continue;
            }

            var lookup = Schema(name);
            status = lookup switch
            {
                TableSchema => SqliteStatus.Success,
                TableNotFound => SqliteStatus.Success,
                SqliteStatus failure => failure,
            };
            if (!status.IsOk)
            {
                InvalidateSchemaCache();
                return status;
            }
        }

        _schemaCookie = cookie;
        _deltaVersion = deltaVersion;
        MarkUserCacheValidated(cookie, deltaVersion);
        return SqliteStatus.Success;
    }

    public void InvalidateSchemaCache()
    {
        _schemas.Clear();
        _schemaCookie = -1;
        ClearStatementCaches();
    }

    public TableSchemaLookup Schema(string table)
    {
        if (_schemas.TryGetValue(table, out var cached))
        {
            return cached;
        }

        var status = QueryFirst("SELECT type, wr FROM pragma_table_list(?1) WHERE schema = 'main'", out var listing, SqlValue.FromText(table));
        if (!status.IsOk)
        {
            return status;
        }

        if (listing is null || !listing[0].TryGetText(out var type) || type is not ("table" or "virtual" or "shadow"))
        {
            return new TableNotFound(table);
        }

        _ = listing[1].TryGetIntegral(out var withoutRowId);
        List<SqlValue[]> rows = [];
        status = Query("SELECT name, type, \"notnull\", pk, hidden FROM pragma_table_xinfo(?1, 'main')", rows, SqlValue.FromText(table));
        if (!status.IsOk)
        {
            return status;
        }

        if (rows.Count == 0)
        {
            return new TableNotFound(table);
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
            return status;
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
