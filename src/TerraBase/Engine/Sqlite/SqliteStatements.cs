namespace TerraBase.Engine.Sqlite;

/// <summary>
/// Every fixed statement the engine runs. Their query plans are recorded in <c>docs/sqlite-query-plans.md</c>
/// and asserted by the Fast suite, so a plan that falls back to a full scan fails the build. Queries and
/// counts are compiled per request by <see cref="SqliteQueryCompiler"/> and are not cached. Reads run on client and system
/// databases alike; <see cref="ClientOnly"/> and <see cref="SystemOnly"/> name the statements of one kind only.
/// </summary>
internal static class SqliteStatements
{
    // A tombstone has a NULL body.
    public const string Get = "SELECT version, body, octet_length(body) FROM documents WHERE collection = $collection AND id = $id;";

    // octet_length inspects stored JSON size without materializing or decoding its body.
    public const string DocumentBytes = """
        SELECT coalesce(octet_length(c.body), octet_length(d.body), 0)
        FROM documents d LEFT JOIN unique_conflicts c ON c.collection = d.collection AND c.id = d.id
        WHERE d.collection = $collection AND d.id = $id;
        """;

    // One scalar result, independent of the number of collections; metadata outside collections never counts.
    public const string StoredDocumentBytes = """
        SELECT coalesce(sum((SELECT sum(coalesce(octet_length(c.body), octet_length(d.body), 0))
            FROM documents d LEFT JOIN unique_conflicts c ON c.collection = d.collection AND c.id = d.id
            WHERE d.collection = collections.name)), 0)
        FROM collections;
        """;

    public const string UniqueRawDocument = """
        SELECT d.version, coalesce(c.body, d.body), c.body IS NOT NULL,
            coalesce(octet_length(c.body), octet_length(d.body), 0)
        FROM documents d LEFT JOIN unique_conflicts c ON c.collection = d.collection AND c.id = d.id
        WHERE d.collection = $collection AND d.id = $id;
        """;

    // typeof() reads the column's type from the record header, not its content: no body is loaded.
    public const string Version = "SELECT version, typeof(body) = 'null' FROM documents WHERE collection = $collection AND id = $id;";

    // Client databases (docs/terrabase/multi-region-replication.md section 5): a document's state, and the materialized
    // JSON it is stored beside (a hidden uniqueness loser's is kept aside), which the stored form leaves its values to.
    public const string GetState = """
        SELECT s.state, coalesce(c.body, d.body) FROM _tb_state s
        LEFT JOIN documents d ON d.collection = s.collection AND d.id = s.id
        LEFT JOIN unique_conflicts c ON c.collection = s.collection AND c.id = s.id
        WHERE s.collection = $collection AND s.id = $id;
        """;

    public const string PutDocument = """
        INSERT INTO documents (collection, id, version, body) VALUES ($collection, $id, $version, $body)
        ON CONFLICT (collection, id) DO UPDATE SET version = excluded.version, body = excluded.body;
        """;

    public const string UpdateState = """
        UPDATE _tb_state SET state = $state, stamp = $stamp, origin = $origin, live = $live, h1 = $h1, h2 = $h2
        WHERE collection = $collection AND id = $id;
        """;

    public const string InsertState = """
        INSERT INTO _tb_state (collection, id, bucket, state, stamp, origin, live, h1, h2)
        VALUES ($collection, $id, $bucket, $state, $stamp, $origin, $live, $h1, $h2);
        """;

    public const string DropStates = "DELETE FROM _tb_state WHERE collection = $collection;";

    // The system database: the version is the commit offset of the writing transaction.
    public const string Put = """
        INSERT INTO documents (collection, id, version, body) VALUES ($collection, $id, $version, $body)
        ON CONFLICT (collection, id) DO UPDATE SET version = excluded.version, body = excluded.body;
        """;

    public const string Delete = "DELETE FROM documents WHERE collection = $collection AND id = $id;";

    public const string Tombstone = "UPDATE documents SET version = $version, body = NULL WHERE collection = $collection AND id = $id;";

    // Two statements, not `$after IS NULL OR id > $after`: the OR keeps SQLite from seeking to the
    // cursor, so every page would walk the collection from its first id. Tombstones are not listed.
    public const string ListFirst = """
        SELECT id, version, body, octet_length(body) FROM documents INDEXED BY documents_live
        WHERE collection = $collection AND body IS NOT NULL
        ORDER BY id LIMIT $limit;
        """;

    public const string ListAfter = """
        SELECT id, version, body, octet_length(body) FROM documents INDEXED BY documents_live
        WHERE collection = $collection AND id > $after AND body IS NOT NULL
        ORDER BY id LIMIT $limit;
        """;

    public const string NextOffset = "UPDATE commit_offset SET value = value + 1 WHERE id = 1 AND value < 9223372036854775807 RETURNING value;";

    public const string CurrentOffset = "SELECT value FROM commit_offset WHERE id = 1;";

    public const string Incarnation = "SELECT incarnation FROM commit_offset WHERE id = 1;";

    public const string RenewIncarnation = "UPDATE commit_offset SET incarnation = $incarnation WHERE id = 1;";

    // Every creation draws a new generation, so a recreated collection starts a new shape lineage.
    public const string CreateCollection = """
        INSERT INTO collections (name, created_at, generation, entity_id) VALUES ($collection, $created, random(), $entityId)
        ON CONFLICT (name) DO NOTHING;
        """;

    // The system database's catalog checks: retained journal deletions (changes with a NULL body) count as state.
    public const string HasCollectionState = """
        SELECT EXISTS(SELECT 1 FROM documents WHERE collection >= $from AND collection < $to)
            OR EXISTS(SELECT 1 FROM collections WHERE name >= $from AND name < $to)
            OR EXISTS(SELECT 1 FROM changes WHERE collection >= $from AND collection < $to AND body IS NULL)
            OR EXISTS(SELECT 1 FROM change_log INDEXED BY change_log_by_offset WHERE collection >= $from AND collection < $to);
        """;

    public const string HasCollectionStateFrom = """
        SELECT EXISTS(SELECT 1 FROM documents WHERE collection >= $from)
            OR EXISTS(SELECT 1 FROM collections WHERE name >= $from)
            OR EXISTS(SELECT 1 FROM changes WHERE collection >= $from AND body IS NULL)
            OR EXISTS(SELECT 1 FROM change_log INDEXED BY change_log_by_offset WHERE collection >= $from);
        """;

    // A client database keeps no journal deletions to look at.
    public const string ClientHasCollectionState = """
        SELECT EXISTS(SELECT 1 FROM documents WHERE collection >= $from AND collection < $to)
            OR EXISTS(SELECT 1 FROM collections WHERE name >= $from AND name < $to)
            OR EXISTS(SELECT 1 FROM change_log INDEXED BY change_log_by_offset WHERE collection >= $from AND collection < $to);
        """;

    public const string ClientHasCollectionStateFrom = """
        SELECT EXISTS(SELECT 1 FROM documents WHERE collection >= $from)
            OR EXISTS(SELECT 1 FROM collections WHERE name >= $from)
            OR EXISTS(SELECT 1 FROM change_log INDEXED BY change_log_by_offset WHERE collection >= $from);
        """;

    public const string GetCollection = "SELECT name, created_at, generation, entity_id FROM collections WHERE name = $collection;";

    // The first page walks the primary key in order and stops at the limit.
    public const string ListCollectionsFirst = "SELECT name, created_at, generation, entity_id FROM collections ORDER BY name LIMIT $limit;";

    public const string ListCollectionsAfter = "SELECT name, created_at, generation, entity_id FROM collections WHERE name > $after ORDER BY name LIMIT $limit;";

    // Shape history: every write of a client database, a NULL body for a tombstone. No row is
    // inserted when the collection has no metadata, which the writer refuses.
    public const string AppendChange = """
        INSERT INTO change_log (collection, generation, id, version, document_version, body)
        SELECT name, generation, $id, $version, $documentVersion, $body FROM collections WHERE name = $collection;
        """;

    public const string HistoryFloor = "SELECT floor FROM shape_history WHERE id = 1;";

    // A dropped collection keeps only tombstones of its history: no body outlives the drop.
    public const string DropHistory = """
        UPDATE change_log SET body = NULL
        WHERE collection = $collection AND generation = (SELECT generation FROM collections WHERE name = $collection) AND body IS NOT NULL;
        """;

    public const string DropDocuments = "DELETE FROM documents WHERE collection = $collection;";

    public const string DropCollection = "DELETE FROM collections WHERE name = $collection;";

    // Management API (TS-463). Counts walk the collection's primary-key range and test each record header's body type,
    // never the body: documents_live would not cover the body test and would add a lookup per row.
    public const string CountDocuments = "SELECT count(*) FROM documents WHERE collection = $collection AND body IS NOT NULL;";

    // octet_length reads each live body's size from its record header, not the body.
    public const string ListCollectionStats = """
        SELECT collections.name, count(documents.id), coalesce(sum(octet_length(documents.body)), 0)
        FROM collections LEFT JOIN documents ON documents.collection = collections.name AND documents.body IS NOT NULL
        GROUP BY collections.name ORDER BY collections.name;
        """;

    // A prefix is the name range [$from, $to) of each primary key; documents and tombstones go with their collections.
    public const string DeleteCollectionDocuments = "DELETE FROM documents WHERE collection >= $from AND collection < $to;";

    public const string DeleteCollectionNames = "DELETE FROM collections WHERE name >= $from AND name < $to;";

    // Secondary indexes are read back from the schema table, which is small and has no index of its own.
    public const string ListIndexes = "SELECT name, sql FROM sqlite_schema WHERE type = 'index' AND name LIKE 'tb\\_index\\_%' ESCAPE '\\';";

    public const string IndexExists = "SELECT 1 FROM sqlite_schema WHERE type = 'index' AND name = $name;";

    // The system database: a catalog follower's commit offset reaches its primary's.
    public const string ObserveOffset = "UPDATE commit_offset SET value = max(value, $version) WHERE id = 1;";

    // The system database's origin is its persisted incarnation, a ULID.
    public const string Origin = "SELECT incarnation FROM commit_offset WHERE id = 1;";

    // The system database's journal: every write in order, with a SHA-256 prefix digest per group (docs/decisions/0009).
    public const string LastSequence = "SELECT coalesce(max(sequence), 0) FROM changes;";

    public const string AppendReplicationChange = """
        INSERT INTO changes (sequence, ordinal, collection, id, version, body)
        VALUES ($sequence, $ordinal, $collection, $id, $version, $body);
        """;

    public const string GetChangeDigest = "SELECT digest FROM journal_prefixes WHERE sequence = $sequence;";

    public const string AppendChangeDigest = "INSERT INTO journal_prefixes (sequence, digest) VALUES ($sequence, $digest);";

    // System database replication (docs/decisions/0009). A snapshot reads live documents in key order, any collection.
    public const string SystemDocumentsFirst = "SELECT collection, id, version, body FROM documents WHERE body IS NOT NULL ORDER BY collection, id LIMIT $limit;";

    public const string SystemDocumentsAfter = """
        SELECT collection, id, version, body FROM documents WHERE (collection, id) > ($collection, $id) AND body IS NOT NULL
        ORDER BY collection, id LIMIT $limit;
        """;

    public const string GroupSize = "SELECT count(*) FROM changes WHERE sequence = $sequence;";

    // Ordinals of a group are 0, 1, …, so a page resumes at an index.
    public const string ReadGroupPage = "SELECT collection, id, version, body FROM changes WHERE sequence = $sequence AND ordinal >= $skip ORDER BY ordinal LIMIT $limit;";

    // A system prefix drop captures each deletion: the same range as DeleteCollectionDocuments.
    public const string CollectionRangeIds = "SELECT collection, id FROM documents WHERE collection >= $from AND collection < $to;";

    public const string ReplaceDocument = """
        INSERT INTO documents (collection, id, version, body) VALUES ($collection, $id, $version, $body)
        ON CONFLICT (collection, id) DO UPDATE SET version = excluded.version, body = excluded.body;
        """;

    // The system journal keeps a bounded tail (and a follower only its head), so its head is the last stored digest.
    public const string SystemLastSequence = "SELECT coalesce(max(sequence), 0) FROM journal_prefixes;";

    public const string TruncateChanges = "DELETE FROM changes WHERE sequence <= $floor;";

    public const string TruncatePrefixes = "DELETE FROM journal_prefixes WHERE sequence <= $floor;";

    // A follower's snapshot replaces its documents and journal: whole-table deletes, once per snapshot, so not in All (no
    // plan to check). commit_offset keeps its value (its incarnation is renewed separately); change_log, its births and
    // highs, shape_history and replication_state hold nothing for _system, which keeps no shape history.
    public const string ResetReplica =
        "DELETE FROM documents; DELETE FROM collections; DELETE FROM changes; DELETE FROM journal_prefixes; DELETE FROM applied;";

    /// <summary>Statements both client and system databases run, by name, for query-plan checks.</summary>
    public static IReadOnlyDictionary<string, string> Shared { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(Get)] = Get,
        [nameof(UniqueRawDocument)] = UniqueRawDocument,
        [nameof(DocumentBytes)] = DocumentBytes,
        [nameof(StoredDocumentBytes)] = StoredDocumentBytes,
        [nameof(Version)] = Version,
        [nameof(ListFirst)] = ListFirst,
        [nameof(ListAfter)] = ListAfter,
        [nameof(NextOffset)] = NextOffset,
        [nameof(CurrentOffset)] = CurrentOffset,
        [nameof(Incarnation)] = Incarnation,
        [nameof(RenewIncarnation)] = RenewIncarnation,
        [nameof(CreateCollection)] = CreateCollection,
        [nameof(GetCollection)] = GetCollection,
        [nameof(ListCollectionsFirst)] = ListCollectionsFirst,
        [nameof(ListCollectionsAfter)] = ListCollectionsAfter,
        [nameof(DropCollection)] = DropCollection,
        [nameof(AppendChange)] = AppendChange,
        [nameof(HistoryFloor)] = HistoryFloor,
        [nameof(DropHistory)] = DropHistory,
        [nameof(CountDocuments)] = CountDocuments,
        [nameof(ListCollectionStats)] = ListCollectionStats,
        [nameof(ListIndexes)] = ListIndexes,
        [nameof(IndexExists)] = IndexExists,
        [nameof(DropDocuments)] = DropDocuments,
    };

    /// <summary>Statements only client databases run, by name, for query-plan checks.</summary>
    public static IReadOnlyDictionary<string, string> ClientOnly { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(GetState)] = GetState,
        [nameof(ClientHasCollectionState)] = ClientHasCollectionState,
        [nameof(ClientHasCollectionStateFrom)] = ClientHasCollectionStateFrom,
        [nameof(PutDocument)] = PutDocument,
        [nameof(UpdateState)] = UpdateState,
        [nameof(InsertState)] = InsertState,
        [nameof(DropStates)] = DropStates,
    };

    /// <summary>Statements only the system database runs, by name, for query-plan checks (TS-647 step 5 removes them).</summary>
    public static IReadOnlyDictionary<string, string> SystemOnly { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(Put)] = Put,
        [nameof(Delete)] = Delete,
        [nameof(Tombstone)] = Tombstone,
        [nameof(DeleteCollectionDocuments)] = DeleteCollectionDocuments,
        [nameof(DeleteCollectionNames)] = DeleteCollectionNames,
        [nameof(ObserveOffset)] = ObserveOffset,
        [nameof(HasCollectionState)] = HasCollectionState,
        [nameof(HasCollectionStateFrom)] = HasCollectionStateFrom,
        [nameof(Origin)] = Origin,
        [nameof(LastSequence)] = LastSequence,
        [nameof(AppendReplicationChange)] = AppendReplicationChange,
        [nameof(GetChangeDigest)] = GetChangeDigest,
        [nameof(AppendChangeDigest)] = AppendChangeDigest,
        [nameof(SystemDocumentsFirst)] = SystemDocumentsFirst,
        [nameof(SystemDocumentsAfter)] = SystemDocumentsAfter,
        [nameof(GroupSize)] = GroupSize,
        [nameof(ReadGroupPage)] = ReadGroupPage,
        [nameof(CollectionRangeIds)] = CollectionRangeIds,
        [nameof(ReplaceDocument)] = ReplaceDocument,
        [nameof(SystemLastSequence)] = SystemLastSequence,
        [nameof(TruncateChanges)] = TruncateChanges,
        [nameof(TruncatePrefixes)] = TruncatePrefixes,
    };
}
