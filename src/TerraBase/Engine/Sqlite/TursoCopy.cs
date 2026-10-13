using TerraBase.Primitives;
using Turso;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// Copies a database row by row, as of a read transaction: continuous backup's snapshots and database copies. Turso has no
/// online backup API, its <c>VACUUM INTO</c> on a connection other than the writer copies an older view, and a checkpoint
/// that would let the file be copied as is waits for every reader (docs/terrabase/backup.md).
/// </summary>
/// <remarks>
/// Every rowid is kept, so a change log that names rows by rowid replays on the copy. The schema is recreated from its SQL,
/// so <c>sqlite_schema</c> rowids may differ: the log names schema objects by name. The engine's own tables (change capture,
/// statistics) are left out, and every peer's acknowledged change id is reset to 0.
/// </remarks>
internal static class TursoCopy
{
    /// <summary>
    /// Writes the database <paramref name="source"/> reads, as of its open read transaction, to a new file at
    /// <paramref name="target"/>, with its log checkpointed into it.
    /// </summary>
    public static void Write(TursoConnection source, string target, CancellationToken cancellationToken)
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
        using var copy = new TursoConnection($"Data Source={target}");
        copy.Open();
        Execute(copy, "BEGIN IMMEDIATE;");
        // Tables and their rows first, then indexes, which build faster over the loaded rows.
        foreach (var (_, name, _, sql) in objects.Where(item => item.Type == "table"))
        {
            Execute(copy, sql);
            CopyRows(source, copy, name, cancellationToken);
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
            identity.Parameters.Add(new TursoParameter("$incarnation", EntityIds.New().ToString()));
            identity.ExecuteNonQuery();
        }

        foreach (var (_, _, _, sql) in objects.Where(item => item.Type != "table"))
        {
            Execute(copy, sql);
        }

        Execute(copy, $"PRAGMA user_version = {Convert.ToInt64(version, System.Globalization.CultureInfo.InvariantCulture)};");
        Execute(copy, "COMMIT;");
        // Turso leaves the log next to the file when the connection closes; the copy is moved without it.
        Execute(copy, "PRAGMA wal_checkpoint(TRUNCATE);");
    }

    private static void CopyRows(TursoConnection source, TursoConnection copy, string table, CancellationToken cancellationToken)
    {
        var (columns, alias) = TableShape.Read(source, table);
        // The rowid alias column is the rowid: name it once.
        var names = alias >= 0 ? columns.Select(TableShape.Quote).ToList() : ["rowid", .. columns.Select(TableShape.Quote)];
        var insert = $"INSERT INTO {TableShape.Quote(table)} ({string.Join(", ", names)}) VALUES ({string.Join(", ", names.Select((_, i) => $"$v{i}"))});";
        using var read = source.CreateCommand();
        read.CommandText = alias >= 0 ? $"SELECT * FROM {TableShape.Quote(table)};" : $"SELECT rowid, * FROM {TableShape.Quote(table)};";
        using var rows = read.ExecuteReader();
        while (rows.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var write = copy.CreateCommand();
            write.CommandText = insert;
            for (var i = 0; i < rows.FieldCount; i++)
            {
                write.Parameters.Add(new TursoParameter($"$v{i}", rows.GetValue(i)));
            }

            write.ExecuteNonQuery();
        }
    }

    private static void Execute(TursoConnection connection, string sql)
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
