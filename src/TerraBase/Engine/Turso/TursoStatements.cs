namespace TerraBase.Engine.Turso;

internal static class TursoStatements
{
    public const string Get = "SELECT version, body, octet_length(body) FROM _tb_documents WHERE collection = $collection AND id = $id;";

    public const string Version = "SELECT version FROM _tb_documents WHERE collection = $collection AND id = $id;";

    public const string Put = """
        INSERT INTO _tb_documents (collection, id, version, body) VALUES ($collection, $id, $version, $body)
        ON CONFLICT (collection, id) DO UPDATE SET version = excluded.version, body = excluded.body;
        """;

    public const string Delete = "DELETE FROM _tb_documents WHERE collection = $collection AND id = $id;";

    public const string StoredDocumentBytes = "SELECT coalesce(sum(octet_length(body)), 0) FROM _tb_documents;";

    public const string SqlStorageBytes = "SELECT bytes FROM _tb_sql_storage WHERE id = 1;";

    public const string AddSqlStorage = "UPDATE _tb_sql_storage SET bytes = bytes + $bytes WHERE id = 1;";

    public const string ListFirst = """
        SELECT id, version, body, octet_length(body) FROM _tb_documents
        WHERE collection = $collection AND tb_qos_scan(octet_length(body)) ORDER BY id LIMIT $limit;
        """;

    public const string ListAfter = """
        SELECT id, version, body, octet_length(body) FROM _tb_documents
        WHERE collection = $collection AND id > $after AND tb_qos_scan(octet_length(body)) ORDER BY id LIMIT $limit;
        """;

    public const string DropDocuments = "DELETE FROM _tb_documents WHERE collection = $collection;";

    public const string DeletePrefixDocuments = "DELETE FROM _tb_documents WHERE collection >= $from AND collection < $to;";

    public const string DeletePrefixDocumentsFrom = "DELETE FROM _tb_documents WHERE collection >= $from;";

    public const string DeletePrefixCollections = "DELETE FROM _tb_collections WHERE name >= $from AND name < $to;";

    public const string DeletePrefixCollectionsFrom = "DELETE FROM _tb_collections WHERE name >= $from;";

    public const string CreateCollection = """
        INSERT INTO _tb_collections (name, created_at, entity_id) VALUES ($collection, $created, $entityId)
        ON CONFLICT (name) DO NOTHING;
        """;

    public const string GetCollection = "SELECT name, created_at, entity_id FROM _tb_collections WHERE name = $collection;";

    public const string ListCollectionsFirst = "SELECT name, created_at, entity_id FROM _tb_collections ORDER BY name LIMIT $limit;";

    public const string ListCollectionsAfter = "SELECT name, created_at, entity_id FROM _tb_collections WHERE name > $after ORDER BY name LIMIT $limit;";

    public const string DropCollection = "DELETE FROM _tb_collections WHERE name = $collection;";

    public const string SetOffset = "UPDATE commit_offset SET value = $value WHERE id = 1;";

    public const string CurrentOffset = "SELECT value FROM commit_offset WHERE id = 1;";

    public const string Incarnation = "SELECT incarnation FROM commit_offset WHERE id = 1;";

    public const string RenewIncarnation = "UPDATE commit_offset SET incarnation = $incarnation WHERE id = 1;";

    public const string NewestChange = "SELECT coalesce((SELECT max(change_id) FROM turso_cdc), 0);";
}
