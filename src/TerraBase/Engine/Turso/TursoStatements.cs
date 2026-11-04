namespace TerraBase.Engine.Turso;

internal static class TursoStatements
{
    public const string Get = "SELECT version, body, octet_length(body) FROM _tb_records WHERE kind = $kind AND id = $id;";

    public const string Version = "SELECT version FROM _tb_records WHERE kind = $kind AND id = $id;";

    public const string Put = """
        INSERT INTO _tb_records (kind, id, version, body) VALUES ($kind, $id, $version, $body)
        ON CONFLICT (kind, id) DO UPDATE SET version = excluded.version, body = excluded.body;
        """;

    public const string Delete = "DELETE FROM _tb_records WHERE kind = $kind AND id = $id;";

    public const string StoredRecordBytes = "SELECT coalesce(sum(octet_length(body)), 0) FROM _tb_records;";

    public const string SqlStorageBytes = "SELECT bytes FROM _tb_sql_storage WHERE id = 1;";

    public const string AddSqlStorage = "UPDATE _tb_sql_storage SET bytes = bytes + $bytes WHERE id = 1;";

    public const string ListFirst = """
        SELECT id, version, body, octet_length(body) FROM _tb_records
        WHERE kind = $kind AND tb_qos_scan(octet_length(body)) ORDER BY id LIMIT $limit;
        """;

    public const string ListAfter = """
        SELECT id, version, body, octet_length(body) FROM _tb_records
        WHERE kind = $kind AND id > $after AND tb_qos_scan(octet_length(body)) ORDER BY id LIMIT $limit;
        """;

    public const string DropRecords = "DELETE FROM _tb_records WHERE kind = $kind;";

    public const string DeletePrefixRecords = "DELETE FROM _tb_records WHERE kind >= $from AND kind < $to;";

    public const string DeletePrefixRecordsFrom = "DELETE FROM _tb_records WHERE kind >= $from;";

    public const string DeletePrefixKinds = "DELETE FROM _tb_record_kinds WHERE name >= $from AND name < $to;";

    public const string DeletePrefixKindsFrom = "DELETE FROM _tb_record_kinds WHERE name >= $from;";

    public const string CreateKind = """
        INSERT INTO _tb_record_kinds (name, created_at, entity_id) VALUES ($kind, $created, $entityId)
        ON CONFLICT (name) DO NOTHING;
        """;

    public const string GetKind = "SELECT name FROM _tb_record_kinds WHERE name = $kind;";

    public const string ListKindsFirst = "SELECT name, created_at, entity_id FROM _tb_record_kinds ORDER BY name LIMIT $limit;";

    public const string ListKindsAfter = "SELECT name, created_at, entity_id FROM _tb_record_kinds WHERE name > $after ORDER BY name LIMIT $limit;";

    public const string DropKind = "DELETE FROM _tb_record_kinds WHERE name = $kind;";

    public const string SetOffset = "UPDATE commit_offset SET value = $value WHERE id = 1;";

    public const string CurrentOffset = "SELECT value FROM commit_offset WHERE id = 1;";

    public const string Incarnation = "SELECT incarnation FROM commit_offset WHERE id = 1;";

    public const string RenewIncarnation = "UPDATE commit_offset SET incarnation = $incarnation WHERE id = 1;";

    public const string NewestChange = "SELECT coalesce((SELECT max(change_id) FROM turso_cdc), 0);";
}
