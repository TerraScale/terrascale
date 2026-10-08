namespace TerraBase.Engine.Sqlite;

internal sealed partial class SqliteDatabaseStore
{
    // Part of the fresh schema only. Older files must be wiped, never upgraded.
    internal const string UniqueSchemaSql = """
        CREATE TABLE unique_keys (
            collection TEXT NOT NULL,
            constraint_name TEXT NOT NULL,
            value BLOB NOT NULL,
            id TEXT NOT NULL,
            PRIMARY KEY (collection, constraint_name, value, id)
        ) WITHOUT ROWID;
        CREATE INDEX unique_keys_by_document ON unique_keys (collection, id);
        CREATE TABLE unique_invalid (
            collection TEXT NOT NULL,
            id TEXT NOT NULL,
            PRIMARY KEY (collection, id)
        ) WITHOUT ROWID;
        CREATE TABLE unique_conflicts (
            collection TEXT NOT NULL,
            id TEXT NOT NULL,
            body TEXT NOT NULL,
            PRIMARY KEY (collection, id)
        ) WITHOUT ROWID;
        """;
}
