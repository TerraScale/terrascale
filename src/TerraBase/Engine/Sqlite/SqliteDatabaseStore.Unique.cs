namespace TerraBase.Engine.Sqlite;

internal sealed partial class SqliteDatabaseStore
{
    // Part of the fresh schema only. Older files must be wiped, never upgraded. Rowid tables, like every table Turso
    // must update, delete from or index (docs/terrabase/multi-region-replication.md section 2).
    internal const string UniqueSchemaSql = """
        CREATE TABLE _tb_unique_keys (
            collection TEXT NOT NULL,
            constraint_name TEXT NOT NULL,
            value BLOB NOT NULL,
            id TEXT NOT NULL,
            UNIQUE (collection, constraint_name, value, id)
        );
        CREATE INDEX _tb_unique_keys_by_document ON _tb_unique_keys (collection, id);
        CREATE TABLE _tb_unique_invalid (
            collection TEXT NOT NULL,
            id TEXT NOT NULL,
            UNIQUE (collection, id)
        );
        CREATE TABLE _tb_unique_conflicts (
            collection TEXT NOT NULL,
            id TEXT NOT NULL,
            body TEXT NOT NULL,
            UNIQUE (collection, id)
        );
        """;
}
