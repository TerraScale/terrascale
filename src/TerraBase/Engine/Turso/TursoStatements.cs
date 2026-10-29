namespace TerraBase.Engine.Turso;

internal static class TursoStatements
{

    public const string Get = """
        SELECT (SELECT s.stamp FROM _tb_state s WHERE s.collection = $collection AND s.id = $id), body, octet_length(body)
        FROM documents WHERE collection = $collection AND id = $id;
        """;

    public const string DocumentBytes = """
        SELECT coalesce(octet_length(c.body), octet_length(d.body), 0)
        FROM documents d LEFT JOIN _tb_unique_conflicts c ON c.collection = d.collection AND c.id = d.id
        WHERE d.collection = $collection AND d.id = $id;
        """;

    public const string StoredDocumentBytes = """
        SELECT coalesce(sum((SELECT sum(coalesce(octet_length(c.body), octet_length(d.body), 0))
            FROM documents d LEFT JOIN _tb_unique_conflicts c ON c.collection = d.collection AND c.id = d.id
            WHERE d.collection = collections.name)), 0)
        FROM collections;
        """;

    public const string Version = """
        SELECT (SELECT s.stamp FROM _tb_state s WHERE s.collection = $collection AND s.id = $id),
            NOT EXISTS (SELECT 1 FROM documents WHERE collection = $collection AND id = $id);
        """;

    public const string SystemGetStateBytes = """
        SELECT octet_length(s.state), coalesce(octet_length(d.body), 0),
            typeof(s.h1) = 'integer' AND typeof(s.h2) = 'integer'
        FROM _tb_state s
        LEFT JOIN documents d ON d.collection = s.collection AND d.id = s.id
        WHERE s.collection = $collection AND s.id = $id;
        """;

    public const string SystemGetState = """
        SELECT s.state, d.body, s.h1, s.h2 FROM _tb_state s LEFT JOIN documents d ON d.collection = s.collection AND d.id = s.id
        WHERE s.collection = $collection AND s.id = $id;
        """;

    public const string SystemUpdateRow = "UPDATE documents SET body = $body WHERE collection = $collection AND id = $id;";

    public const string SystemInsertRow = "INSERT INTO documents (collection, id, body) VALUES ($collection, $id, $body);";

    public const string SystemDeleteRow = "DELETE FROM documents WHERE collection = $collection AND id = $id;";

    public const string ClientDocumentBytes = """
        SELECT coalesce((SELECT octet_length(body) FROM _tb_unique_conflicts WHERE collection = $collection AND id = $id),
            (SELECT octet_length(doc) FROM {0} WHERE id = $id), 0);
        """;

    public const string ClientListFirst = """
        SELECT d.id, (SELECT s.stamp FROM _tb_state s WHERE s.collection = $collection AND s.id = d.id), d.doc, octet_length(d.doc)
        FROM {0} d WHERE tb_qos_scan(octet_length(d.doc)) ORDER BY d.id LIMIT $limit;
        """;

    public const string ClientListAfter = """
        SELECT d.id, (SELECT s.stamp FROM _tb_state s WHERE s.collection = $collection AND s.id = d.id), d.doc, octet_length(d.doc)
        FROM {0} d WHERE d.id > $after AND tb_qos_scan(octet_length(d.doc)) ORDER BY d.id LIMIT $limit;
        """;

    public const string ClientCollectionStats = """
        SELECT c.name, coalesce(s.documents, 0), coalesce(s.bytes, 0) FROM collections c
        LEFT JOIN _tb_collection_stats s ON s.collection = c.name ORDER BY c.name;
        """;

    public const string ClientStoredBytes = "SELECT coalesce(sum(bytes), 0) FROM _tb_collection_stats;";

    public const string AddCollectionStats = """
        INSERT INTO _tb_collection_stats (collection, documents, bytes) VALUES ($collection, $documents, $bytes)
        ON CONFLICT (collection) DO UPDATE SET documents = documents + excluded.documents, bytes = bytes + excluded.bytes;
        """;

    public const string DropCollectionStats = "DELETE FROM _tb_collection_stats WHERE collection = $collection;";

    public const string ClientConflictBytes = "SELECT coalesce(sum(octet_length(body)), 0) FROM _tb_unique_conflicts;";

    public const string ClientCollectionNames = "SELECT name FROM collections ORDER BY name;";

    public const string GetStateBytes = """
        SELECT octet_length(s.state), coalesce(octet_length(c.body), octet_length(d.doc), 0),
            typeof(s.h1) = 'integer' AND typeof(s.h2) = 'integer'
        FROM _tb_state s
        LEFT JOIN {0} d ON d.id = s.id
        LEFT JOIN _tb_unique_conflicts c ON c.collection = s.collection AND c.id = s.id
        WHERE s.collection = $collection AND s.id = $id;
        """;

    public const string GetState = """
        SELECT s.state, coalesce(c.body, d.doc), s.h1, s.h2 FROM _tb_state s
        LEFT JOIN {0} d ON d.id = s.id
        LEFT JOIN _tb_unique_conflicts c ON c.collection = s.collection AND c.id = s.id
        WHERE s.collection = $collection AND s.id = $id;
        """;

    public const string UpdateRow = "UPDATE {0} SET doc = $body WHERE id = $id;";

    public const string InsertRow = "INSERT INTO {0} (id, doc) VALUES ($id, $body);";

    public const string DeleteRow = "DELETE FROM {0} WHERE id = $id;";

    public const string RowBytes = "SELECT octet_length(doc) FROM {0} WHERE id = $id;";

    public const string UpdateState = """
        UPDATE _tb_state SET state = $state, stamp = $stamp, origin = $origin, live = $live, h1 = $h1, h2 = $h2
        WHERE collection = $collection AND id = $id;
        """;

    public const string InsertState = """
        INSERT INTO _tb_state (collection, id, state, stamp, origin, live, h1, h2)
        VALUES ($collection, $id, $state, $stamp, $origin, $live, $h1, $h2);
        """;

    public const string DropStates = "DELETE FROM _tb_state WHERE collection = $collection;";

    public const string UpdateCollection = "UPDATE collections SET created_at = $created, entity_id = $entityId WHERE name = $collection;";

    public const string ListFirst = """
        SELECT d.id, (SELECT s.stamp FROM _tb_state s WHERE s.collection = $collection AND s.id = d.id), d.body, octet_length(d.body)
        FROM documents d WHERE d.collection = $collection AND tb_qos_scan(octet_length(d.body)) ORDER BY d.id LIMIT $limit;
        """;

    public const string ListAfter = """
        SELECT d.id, (SELECT s.stamp FROM _tb_state s WHERE s.collection = $collection AND s.id = d.id), d.body, octet_length(d.body)
        FROM documents d WHERE d.collection = $collection AND d.id > $after AND tb_qos_scan(octet_length(d.body)) ORDER BY d.id LIMIT $limit;
        """;

    public const string SetOffset = "UPDATE commit_offset SET value = $value WHERE id = 1;";

    public const string CurrentOffset = "SELECT value FROM commit_offset WHERE id = 1;";

    public const string Incarnation = "SELECT incarnation FROM commit_offset WHERE id = 1;";

    public const string RenewIncarnation = "UPDATE commit_offset SET incarnation = $incarnation WHERE id = 1;";

    public const string NewestChange = "SELECT coalesce((SELECT max(change_id) FROM turso_cdc), 0);";

    public const string OldestChange = "SELECT coalesce((SELECT min(change_id) FROM turso_cdc), 0);";

    public const string ChangeRows = "SELECT coalesce((SELECT max(change_id) FROM turso_cdc) - (SELECT min(change_id) FROM turso_cdc) + 1, 0);";

    public const string CreateCollection = """
        INSERT INTO collections (name, created_at, entity_id) VALUES ($collection, $created, $entityId)
        ON CONFLICT (name) DO NOTHING;
        """;

    public const string HasCollectionState = """
        SELECT EXISTS(SELECT 1 FROM _tb_state WHERE collection >= $from AND collection < $to)
            OR EXISTS(SELECT 1 FROM collections WHERE name >= $from AND name < $to);
        """;

    public const string HasCollectionStateFrom = """
        SELECT EXISTS(SELECT 1 FROM _tb_state WHERE collection >= $from)
            OR EXISTS(SELECT 1 FROM collections WHERE name >= $from);
        """;

    public const string GetCollection = "SELECT name, created_at, entity_id FROM collections WHERE name = $collection;";

    public const string ListCollectionsFirst = "SELECT name, created_at, entity_id FROM collections ORDER BY name LIMIT $limit;";

    public const string ListCollectionsAfter = "SELECT name, created_at, entity_id FROM collections WHERE name > $after ORDER BY name LIMIT $limit;";

    public const string DropDocuments = "DELETE FROM documents WHERE collection = $collection;";

    public const string DropCollection = "DELETE FROM collections WHERE name = $collection;";

    public const string CountDocuments = "SELECT count(*) FROM documents WHERE collection = $collection;";

    public const string ListCollectionStats = """
        SELECT collections.name, count(documents.id), coalesce(sum(octet_length(documents.body)), 0)
        FROM collections LEFT JOIN documents ON documents.collection = collections.name
        GROUP BY collections.name ORDER BY collections.name;
        """;

    public const string DeleteCollectionDocuments = "DELETE FROM documents WHERE collection >= $from AND collection < $to;";

    public const string DeleteCollectionStates = "DELETE FROM _tb_state WHERE collection >= $from AND collection < $to;";

    public const string DeleteCollectionNames = "DELETE FROM collections WHERE name >= $from AND name < $to;";

    public const string ListIndexes = "SELECT name, sql FROM sqlite_schema WHERE type = 'index' AND name LIKE 'tb\\_index\\_%' ESCAPE '\\';";

    public const string IndexExists = "SELECT 1 FROM sqlite_schema WHERE type = 'index' AND name = $name;";

    public static IReadOnlyDictionary<string, string> Shared { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(SetOffset)] = SetOffset,
        [nameof(CurrentOffset)] = CurrentOffset,
        [nameof(Incarnation)] = Incarnation,
        [nameof(RenewIncarnation)] = RenewIncarnation,
        [nameof(CreateCollection)] = CreateCollection,
        [nameof(GetCollection)] = GetCollection,
        [nameof(ListCollectionsFirst)] = ListCollectionsFirst,
        [nameof(ListCollectionsAfter)] = ListCollectionsAfter,
        [nameof(DropCollection)] = DropCollection,
        [nameof(ListIndexes)] = ListIndexes,
        [nameof(IndexExists)] = IndexExists,
        [nameof(HasCollectionState)] = HasCollectionState,
        [nameof(HasCollectionStateFrom)] = HasCollectionStateFrom,
        [nameof(DropStates)] = DropStates,
    };

    public static IReadOnlyDictionary<string, string> ClientOnly { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(GetStateBytes)] = GetStateBytes,
        [nameof(GetState)] = GetState,
        [nameof(ClientDocumentBytes)] = ClientDocumentBytes,
        [nameof(ClientListFirst)] = ClientListFirst,
        [nameof(ClientListAfter)] = ClientListAfter,
        [nameof(ClientCollectionStats)] = ClientCollectionStats,
        [nameof(ClientStoredBytes)] = ClientStoredBytes,
        [nameof(AddCollectionStats)] = AddCollectionStats,
        [nameof(DropCollectionStats)] = DropCollectionStats,
        [nameof(ClientConflictBytes)] = ClientConflictBytes,
        [nameof(ClientCollectionNames)] = ClientCollectionNames,
        [nameof(UpdateRow)] = UpdateRow,
        [nameof(InsertRow)] = InsertRow,
        [nameof(DeleteRow)] = DeleteRow,
        [nameof(RowBytes)] = RowBytes,
        [nameof(UpdateState)] = UpdateState,
        [nameof(InsertState)] = InsertState,
        [nameof(UpdateCollection)] = UpdateCollection,
    };

    public static IReadOnlyDictionary<string, string> SystemOnly { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(Get)] = Get,
        [nameof(DocumentBytes)] = DocumentBytes,
        [nameof(StoredDocumentBytes)] = StoredDocumentBytes,
        [nameof(Version)] = Version,
        [nameof(ListFirst)] = ListFirst,
        [nameof(ListAfter)] = ListAfter,
        [nameof(CountDocuments)] = CountDocuments,
        [nameof(ListCollectionStats)] = ListCollectionStats,
        [nameof(DropDocuments)] = DropDocuments,
        [nameof(DeleteCollectionDocuments)] = DeleteCollectionDocuments,
        [nameof(DeleteCollectionNames)] = DeleteCollectionNames,
        [nameof(DeleteCollectionStates)] = DeleteCollectionStates,
        [nameof(SystemGetStateBytes)] = SystemGetStateBytes,
        [nameof(SystemGetState)] = SystemGetState,
        [nameof(SystemUpdateRow)] = SystemUpdateRow,
        [nameof(SystemInsertRow)] = SystemInsertRow,
        [nameof(SystemDeleteRow)] = SystemDeleteRow,
    };
}
