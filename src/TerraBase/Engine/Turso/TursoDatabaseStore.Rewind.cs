using System.Data.Common;
using Turso;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoDatabaseStore
{
    private static readonly string[] DropOrder = ["trigger", "view", "index", "table"];

    internal Task<bool> RewindFromAsync(string source, string sourcePath, CancellationToken cancellationToken) =>
        WithWriterAsync(writer =>
        {
            using var from = new TursoConnection(source);
            TursoSession.OpenExisting(from, sourcePath);
            Rewind(writer.Connection, from, cancellationToken);
            return true;
        }, "a point-in-time rewind", cancellationToken);

    private static void Rewind(DbConnection target, TursoConnection source, CancellationToken cancellationToken)
    {
        var current = UserObjects(target);
        var restored = UserObjects(source);
        RewindExecute(target, "PRAGMA foreign_keys = OFF;");
        try
        {
            RewindExecute(target, "BEGIN IMMEDIATE;");
            try
            {
                foreach (var type in DropOrder)
                {
                    foreach (var (_, name, _) in current.Where(item => item.Type == type))
                    {
                        RewindExecute(target, $"DROP {type.ToUpperInvariant()} IF EXISTS {TableShape.Quote(name)};");
                    }
                }

                foreach (var (_, name, sql) in restored.Where(item => item.Type == "table"))
                {
                    RewindExecute(target, sql);
                    TursoCopy.CopyRows(source, target, name, cancellationToken);
                }

                foreach (var (_, _, sql) in restored.Where(item => item.Type != "table"))
                {
                    RewindExecute(target, sql);
                }

                RewindExecute(target, "COMMIT;");
            }
            catch
            {
                RewindExecute(target, "ROLLBACK;");
                throw;
            }
        }
        finally
        {
            RewindExecute(target, "PRAGMA foreign_keys = ON;");
        }
    }

    private static List<(string Type, string Name, string Sql)> UserObjects(DbConnection connection)
    {
        var objects = new List<(string, string, string)>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY rowid;";
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            var (name, table) = (rows.GetString(1), rows.GetString(2));
            if (UserName(name) && UserName(table))
            {
                objects.Add((rows.GetString(0), name, rows.GetString(3)));
            }
        }

        return objects;
    }

    private static bool UserName(string name) => !ChangeLog.EngineTable(name) && !name.StartsWith("_tb_", StringComparison.Ordinal);

    private static void RewindExecute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
