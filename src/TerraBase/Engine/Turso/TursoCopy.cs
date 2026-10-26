using TerraBase.Primitives;
using Turso;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

/// <summary>
/// Copies a database row by row, as of a read transaction: continuous backup's snapshots and database copies. Turso has no
/// online backup API, its <c>VACUUM INTO</c> on a connection other than the writer copies an older view, and a checkpoint
/// that would let the file be copied as is waits for every reader (docs/terrabase/backup.md).
/// </summary>
/// <remarks>
/// Every rowid is kept, so a change log that names rows by rowid replays on the copy. The schema is recreated from its SQL,
/// so <c>sqlite_schema</c> rowids may differ: the log names schema objects by name. The engine's own tables (change capture,
/// statistics) are left out, derived shape tables are copied empty, and every peer's acknowledged change id is reset to 0.
/// </remarks>
internal static class TursoCopy
{
    /// <summary>
    /// Rows one copy transaction holds at most (with <see cref="ChunkBytes"/>): Turso reads every dirty page a
    /// transaction spilled back in at commit, so one transaction over every row of a large database peaks at about
    /// 2.2× the file in memory (P11). The target file is scratch until <see cref="Write(TursoConnection,string,long,long,CancellationToken)"/> returns, so committing
    /// in bounded chunks changes nothing the reader can see.
    /// </summary>
    internal const long ChunkRows = 10_000;

    /// <summary>Bytes of row data one copy transaction holds at most (with <see cref="ChunkRows"/>).</summary>
    internal const long ChunkBytes = 32 * 1024 * 1024;

    /// <summary>
    /// Writes the database <paramref name="source"/> reads, as of its open read transaction, to a new file at
    /// <paramref name="target"/> (its encrypted connection string), with its log checkpointed into it.
    /// </summary>
    public static void Write(TursoConnection source, string target, CancellationToken cancellationToken) =>
        Write(source, target, ChunkRows, ChunkBytes, cancellationToken);

    /// <summary>Writes with an overriding chunk policy, so tests can exercise many commits on a small database.</summary>
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
        // Rewrite derives this string from an existing source (Mode=ReadWrite); this target is a new scratch file.
        // Pooling is a provider option: the scratch handle must close before the file is published.
        var targetOptions = new SqliteConnectionStringBuilder(target) { Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        using var copy = new SqliteConnection(targetOptions.ToString());
        copy.Open();
        // Tables and their rows first, then indexes, which build faster over the loaded rows.
        foreach (var (_, name, _, sql) in objects.Where(item => item.Type == "table"))
        {
            Execute(copy, sql);
            // Sealed shapes belong to the source lineage: copies and backups rebuild them under a new handle.
            if (name is not ("_tb_shape_chunk" or "_tb_shape_snapshot"))
            {
                CopyRows(source, copy, name, chunkRows, chunkBytes, cancellationToken);
            }
        }

        // Peers' acknowledged ids name rows of the source's turso_cdc, which the copy does not continue (its ids restart at 1):
        // no peer has acknowledged anything of the copy. What they applied and the copy holds stays. Every copy, restore and
        // backup snapshot goes through here; PITR staging replays captured _tb_peers rows (the source's acks) and is safe
        // only because CopyRestoredAsync copies it again: a restore that skipped this copy would bring the stale acks back.
        if (objects.Any(item => item is { Type: "table", Name: "_tb_peers" }))
        {
            Execute(copy, "UPDATE _tb_peers SET acked = 0;");
        }

        // A snapshot is a different file history: its regenerated CDC ids cannot prove the source's anchored head.
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
        // Turso leaves the log next to the file when the connection closes; the copy is moved without it.
        Execute(copy, "PRAGMA wal_checkpoint(TRUNCATE);");
    }

    internal static void CopyRows(TursoConnection source, System.Data.Common.DbConnection copy, string table, long chunkRows, long chunkBytes, CancellationToken cancellationToken)
    {
        var (columns, alias) = TableShape.Read(source, table);
        // The rowid alias column is the rowid: name it once.
        var names = alias >= 0 ? columns.Select(TableShape.Quote).ToList() : ["rowid", .. columns.Select(TableShape.Quote)];
        var insert = $"INSERT INTO {TableShape.Quote(table)} ({string.Join(", ", names)}) VALUES ({string.Join(", ", names.Select((_, i) => $"$v{i}"))});";
        using var read = source.CreateCommand();
        read.CommandText = alias >= 0 ? $"SELECT * FROM {TableShape.Quote(table)};" : $"SELECT rowid, * FROM {TableShape.Quote(table)};";
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
        // An unfinished chunk rolls back when the connection closes: the target is scratch until Write returns.
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

/// <summary>A table's columns in order and the ordinal of its rowid alias (an INTEGER PRIMARY KEY), or -1.</summary>
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
