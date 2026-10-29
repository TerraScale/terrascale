namespace TerraBase.Engine.Turso;

internal sealed partial class TursoDatabaseStore
{
    private const string ShapeCleanupSchemaSql = """
        CREATE TABLE _ts_shape_cleanup (
            object_key TEXT PRIMARY KEY, organization_id TEXT NOT NULL, project_id TEXT NOT NULL, database_id TEXT NOT NULL,
            incarnation TEXT NOT NULL, handle TEXT NOT NULL, floor_offset INTEGER NOT NULL, epoch INTEGER NOT NULL, key_id TEXT NOT NULL,
            deadline INTEGER NOT NULL, due INTEGER NOT NULL, state INTEGER NOT NULL, reason INTEGER NOT NULL, published INTEGER NOT NULL,
            attempt INTEGER NOT NULL, next_attempt INTEGER NOT NULL, parked INTEGER NOT NULL, upload_until INTEGER NOT NULL,
            lease TEXT NOT NULL, lease_until INTEGER NOT NULL, bytes INTEGER NOT NULL
        );
        CREATE INDEX _ts_shape_cleanup_due ON _ts_shape_cleanup (state, due);
        CREATE INDEX _ts_shape_cleanup_handle ON _ts_shape_cleanup (handle, state, floor_offset);
        CREATE INDEX _ts_shape_cleanup_database ON _ts_shape_cleanup (database_id, state);
        CREATE TABLE _ts_shape_cleanup_handles (handle TEXT PRIMARY KEY, objects INTEGER NOT NULL);
        CREATE TABLE _ts_shape_cleanup_usage (
            id INTEGER PRIMARY KEY CHECK (id = 1), objects INTEGER NOT NULL, bytes INTEGER NOT NULL, handles INTEGER NOT NULL
        );
        INSERT INTO _ts_shape_cleanup_usage (id, objects, bytes, handles) VALUES (1, 0, 0, 0);
        """;
}
