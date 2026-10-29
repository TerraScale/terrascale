using TerraBase.Primitives;
using Turso;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

internal static class TursoCopy
{
    internal const long ChunkRows = 10_000;

    internal const long ChunkBytes = 32 * 1024 * 1024;

    public static void Write(TursoConnection source, string target, CancellationToken cancellationToken) =>
    Write(source, target, ChunkRows, ChunkBytes, cancellationToken);

    internal static void Write(TursoConnection source, string target, long chunkRows, long chunkBytes, CancellationToken cancellationToken)
    {
        var objects = new List<(string Type, string Name, string Table, string Sql)>();
        using (var schema = source.CreateCommand())
        {
            schema.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY rowid;";
            using var rows = schema.ExecuteReader();
            while (rows.Read())
            {
                if (!ChangeLog.EngineTable(rows.GetString(2)))
                {
                    objects.Add((rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3)));
                }
            }
        }

        var version = Scalar(source, "PRAGMA user_version;");

        var targetOptions = new SqliteConnectionStringBuilder(target) { Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        using var copy = new SqliteConnection(targetOptions.ToString());
        copy.Open();

        foreach (var (_, name, _, sql) in objects.Where(item => item.Type == "table"))
        {
            Execute(copy, sql);

            if (name is not ("_tb_shape_chunk" or "_tb_shape_snapshot"))
            {
                CopyRows(source, copy, name, chunkRows, chunkBytes, cancellationToken);
            }
        }

        if (objects.Any(item => item is { Type: "table", Name: "commit_offset" }))
        {
            using var identity = copy.CreateCommand();
            identity.CommandText = "UPDATE commit_offset SET incarnation = $incarnation;";
            identity.Parameters.AddWithValue("$incarnation", EntityIds.New().ToString());
            identity.ExecuteNonQuery();
        }

        foreach (var (_, _, _, sql) in objects.Where(item => item.Type != "table"))
        {
            Execute(copy, sql);
        }

        Execute(copy, $"PRAGMA user_version = {Convert.ToInt64(version, System.Globalization.CultureInfo.InvariantCulture)};");

        Execute(copy, "PRAGMA wal_checkpoint(TRUNCATE);");
    }

    internal static void CopyRows(TursoConnection source, System.Data.Common.DbConnection copy, string table, long chunkRows, long chunkBytes, CancellationToken cancellationToken)
    {
        var (columns, alias) = TableShape.Read(source, table);

        var names = alias >= 0 ? columns.Select(TableShape.Quote).ToList() : ["rowid", .. columns.Select(TableShape.Quote)];
        var insert = $"INSERT INTO {TableShape.Quote(table)} ({string.Join(", ", names)}) VALUES ({string.Join(", ", names.Select((_, i) => $"$v{i}"))});";
        using var read = source.CreateCommand();
        read.CommandText = $"SELECT {string.Join(", ", names)} FROM {TableShape.Quote(table)};";
        // One native program per table, reset and rebound by the driver across chunk commits. No DDL runs until it is disposed.
        using var write = copy.CreateCommand();
        write.CommandText = insert;
        if (write is SqliteCommand prepared)
        {
            prepared.ReuseStatement = true;
        }
        for (var i = 0; i < names.Count; i++)
        {
            var parameter = write.CreateParameter();
            parameter.ParameterName = $"$v{i}";
            parameter.Value = DBNull.Value;
            write.Parameters.Add(parameter);
        }

        var rowsInChunk = 0L;
        var bytesInChunk = 0L;

        Execute(copy, "BEGIN IMMEDIATE;");
        using var rows = read.ExecuteReader();
        if (rows.FieldCount != names.Count)
        {
            throw new InvalidDataException("a copied table's columns do not match its shape");
        }

        while (rows.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < rows.FieldCount; i++)
            {
                var value = rows.GetValue(i);
                write.Parameters[i].Value = value;
                bytesInChunk += value switch { string text => text.Length, byte[] blob => blob.LongLength, null => 0, _ => sizeof(long) };
            }

            write.ExecuteNonQuery();
            if (++rowsInChunk < chunkRows && bytesInChunk < chunkBytes)
            {
                continue;
            }

            Execute(copy, "COMMIT;");
            rowsInChunk = 0;
            bytesInChunk = 0;
            Execute(copy, "BEGIN IMMEDIATE;");
        }

        Execute(copy, "COMMIT;");
    }

    private static void Execute(System.Data.Common.DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(TursoConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}

internal static class TableShape
{
    public static (string[] Columns, int Alias) Read(TursoConnection connection, string table)
    {
        var columns = new List<string>();
        var keys = new List<(int Ordinal, string Type)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info({Quote(table)});";
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                columns.Add(rows.GetString(1));
                if (rows.GetInt64(5) > 0)
                {
                    keys.Add((columns.Count - 1, rows.IsDBNull(2) ? "" : rows.GetString(2)));
                }
            }
        }

        if (columns.Count == 0)
        {
            throw new InvalidDataException("the table does not exist");
        }

        return ([.. columns], keys is [var key] && key.Type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase) ? key.Ordinal : -1);
    }

    public static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
