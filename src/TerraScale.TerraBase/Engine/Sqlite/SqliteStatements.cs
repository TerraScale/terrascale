namespace TerraBase.Engine.Sqlite;

/// <summary>
/// Every fixed statement the engine runs. Their query plans are recorded in <c>docs/sqlite-query-plans.md</c>
/// and asserted by the Fast suite, so a plan that falls back to a full scan fails the build. Queries and
/// counts are compiled per request by <see cref="SqliteQueryCompiler"/> and are not cached.
/// </summary>
internal static class SqliteStatements
{
    // A tombstone has a NULL body.
    public const string Get = "SELECT version, body FROM documents WHERE collection = $collection AND id = $id;";

    // typeof() reads the column's type from the record header, not its content: no body is loaded.
    public const string Version = "SELECT version, typeof(body) = 'null' FROM documents WHERE collection = $collection AND id = $id;";

    // The version is the commit offset of the writing transaction.
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
        INSERT INTO collections (name, created_at, generation) VALUES ($collection, $created, random())
        ON CONFLICT (name) DO NOTHING;
        """;

    public const string GetCollection = "SELECT name, created_at, generation FROM collections WHERE name = $collection;";

    // The first page walks the primary key in order and stops at the limit.
    public const string ListCollectionsFirst = "SELECT name, created_at, generation FROM collections ORDER BY name LIMIT $limit;";

    public const string ListCollectionsAfter = "SELECT name, created_at, generation FROM collections WHERE name > $after ORDER BY name LIMIT $limit;";

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

    // Incoming versions advance the shared clock even when their document state loses the merge.
    public const string ObserveOffset = "UPDATE commit_offset SET value = max(value, $version) WHERE id = 1;";

    // Physical deletes retain their merge evidence in the journal, while public version reads stay absent.
    // The partial index seeks one matching deletion, without scanning the journal or loading JSON.
    public const string RetainedDeletion = """
        SELECT 1 FROM changes INDEXED BY changes_deleted
        WHERE collection = $collection AND id = $id AND version >= $version AND body IS NULL LIMIT 1;
        """;

    // On a copy only: retain physical-delete/drop evidence before its journal is reset. The deletion
    // index groups keys without a sorter; newer live documents and tombstones keep their current state.
    public const string MaterializeRetainedDeletions = """
        INSERT INTO documents (collection, id, version, body)
        SELECT collection, id, max(version), NULL FROM changes INDEXED BY changes_deleted
        WHERE body IS NULL GROUP BY collection, id
        ON CONFLICT (collection, id) DO UPDATE SET version = excluded.version, body = NULL
        WHERE excluded.version > documents.version
            OR (excluded.version = documents.version AND documents.body IS NOT NULL);
        """;

    // Compare live JSON as UTF-8 bytes. Equal-version tombstones beat every live body.
    public const string ApplyDocument = """
        INSERT INTO documents (collection, id, version, body) VALUES ($collection, $id, $version, $body)
        ON CONFLICT (collection, id) DO UPDATE SET version = excluded.version, body = excluded.body
        WHERE excluded.version > documents.version
            OR (excluded.version = documents.version AND documents.body IS NOT NULL
                AND CAST(excluded.body AS BLOB) > CAST(documents.body AS BLOB));
        """;

    public const string ApplyTombstone = """
        INSERT INTO documents (collection, id, version, body) VALUES ($collection, $id, $version, NULL)
        ON CONFLICT (collection, id) DO UPDATE SET version = excluded.version, body = NULL
        WHERE excluded.version > documents.version
            OR (excluded.version = documents.version AND documents.body IS NOT NULL);
        """;

    // A replicated collection drop (a tombstone in _collections) removed every state of the collection older than itself.
    public const string DroppedCollection =
        $"SELECT 1 FROM documents WHERE collection = '{DocumentChange.Collections}' AND id = $collection AND version > $version AND body IS NULL;";

    // A replicated drop ends the live states older than itself, as the dropping member did; ties go to the document.
    public const string TombstoneCollection =
        "UPDATE documents SET version = $version, body = NULL WHERE collection = $collection AND version < $version AND body IS NOT NULL RETURNING id;";

    public const string LiveDocument = "SELECT 1 FROM documents INDEXED BY documents_live WHERE collection = $collection AND body IS NOT NULL LIMIT 1;";

    // A drop captures all live documents and tombstones without reading JSON.
    public const string CollectionDocumentIds = "SELECT id, octet_length(id) FROM documents WHERE collection = $collection ORDER BY id LIMIT $limit;";

    // The origin is the existing persisted incarnation in RFC byte order.
    public const string Origin = "SELECT unhex(incarnation) FROM commit_offset WHERE id = 1;";

    public const string Complete = "SELECT complete FROM replication_state WHERE id = 1;";

    public const string MarkComplete = "UPDATE replication_state SET complete = 1 WHERE id = 1;";

    public const string LastSequence = "SELECT coalesce(max(sequence), 0) FROM changes;";

    public const string NextSequence = "SELECT coalesce(max(sequence), 0) + 1 FROM changes;";

    public const string AppendReplicationChange = """
        INSERT INTO changes (sequence, ordinal, collection, id, version, body)
        VALUES ($sequence, $ordinal, $collection, $id, $version, $body);
        """;

    public const string ReadChanges = """
        SELECT collection, id, version,
            CASE WHEN octet_length(collection) + octet_length(id) + coalesce(octet_length(body), 0) <= $maxBytes THEN body END,
            octet_length(collection) + octet_length(id) + coalesce(octet_length(body), 0)
        FROM changes WHERE sequence = $sequence ORDER BY ordinal LIMIT $limit;
        """;

    public const string GetChangeDigest = "SELECT digest FROM journal_prefixes WHERE sequence = $sequence;";

    public const string AppendChangeDigest = "INSERT INTO journal_prefixes (sequence, digest) VALUES ($sequence, $digest);";

    public const string GetApplied = "SELECT sequence FROM applied WHERE origin = $origin;";

    public const string GetAppliedFrontier = "SELECT sequence, digest FROM applied WHERE origin = $origin;";

    public const string SetApplied = """
        INSERT INTO applied (origin, sequence, digest) VALUES ($origin, $sequence, $digest)
        ON CONFLICT (origin) DO UPDATE SET sequence = excluded.sequence, digest = excluded.digest;
        """;

    // Filter key byte lengths in SQLite before projection. Only bounded keys enter managed memory
    // or the seek cursor; the excluded rows are counted once, including an entirely inadmissible file.
    private const string BoundedBaselineKeys = "octet_length(collection) <= 63 AND octet_length(id) <= 1024";
    public const string CountOversizedBaselineKeys = "SELECT count(*) FROM documents WHERE NOT (" + BoundedBaselineKeys + ");";

    // Baselines seek through the same documents table; NULL bodies are tombstones.
    public const string BaselineDocumentsFirst = $$"""
        SELECT collection, id, version, CASE WHEN octet_length(body) <= $maxBytes THEN body END, octet_length(body)
        FROM documents WHERE body IS NOT NULL AND {{BoundedBaselineKeys}} ORDER BY collection, id LIMIT $limit;
        """;

    public const string BaselineDocumentsAfter = $$"""
        SELECT collection, id, version, CASE WHEN octet_length(body) <= $maxBytes THEN body END, octet_length(body)
        FROM documents WHERE (collection, id) > ($collection, $id) AND body IS NOT NULL AND {{BoundedBaselineKeys}}
        ORDER BY collection, id LIMIT $limit;
        """;

    public const string BaselineTombstonesFirst = "SELECT collection, id, version, NULL, 0 FROM documents WHERE body IS NULL AND " + BoundedBaselineKeys + " ORDER BY collection, id LIMIT $limit;";

    public const string BaselineTombstonesAfter = $$"""
        SELECT collection, id, version, NULL, 0 FROM documents
        WHERE (collection, id) > ($collection, $id) AND body IS NULL AND {{BoundedBaselineKeys}}
        ORDER BY collection, id LIMIT $limit;
        """;

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

    /// <summary>Statement name and text, for query-plan checks.</summary>
    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(Get)] = Get,
        [nameof(Version)] = Version,
        [nameof(Put)] = Put,
        [nameof(Delete)] = Delete,
        [nameof(Tombstone)] = Tombstone,
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
        [nameof(DropDocuments)] = DropDocuments,
        [nameof(DropCollection)] = DropCollection,
        [nameof(AppendChange)] = AppendChange,
        [nameof(HistoryFloor)] = HistoryFloor,
        [nameof(DropHistory)] = DropHistory,
        [nameof(CountDocuments)] = CountDocuments,
        [nameof(ListCollectionStats)] = ListCollectionStats,
        [nameof(DeleteCollectionDocuments)] = DeleteCollectionDocuments,
        [nameof(DeleteCollectionNames)] = DeleteCollectionNames,
        [nameof(ListIndexes)] = ListIndexes,
        [nameof(IndexExists)] = IndexExists,

        [nameof(ObserveOffset)] = ObserveOffset,
        [nameof(RetainedDeletion)] = RetainedDeletion,
        [nameof(MaterializeRetainedDeletions)] = MaterializeRetainedDeletions,
        [nameof(ApplyDocument)] = ApplyDocument,
        [nameof(ApplyTombstone)] = ApplyTombstone,
        [nameof(DroppedCollection)] = DroppedCollection,
        [nameof(TombstoneCollection)] = TombstoneCollection,
        [nameof(LiveDocument)] = LiveDocument,
        [nameof(CollectionDocumentIds)] = CollectionDocumentIds,
        [nameof(Origin)] = Origin,
        [nameof(Complete)] = Complete,
        [nameof(MarkComplete)] = MarkComplete,
        [nameof(LastSequence)] = LastSequence,
        [nameof(NextSequence)] = NextSequence,
        [nameof(AppendReplicationChange)] = AppendReplicationChange,
        [nameof(ReadChanges)] = ReadChanges,
        [nameof(GetChangeDigest)] = GetChangeDigest,
        [nameof(AppendChangeDigest)] = AppendChangeDigest,
        [nameof(GetApplied)] = GetApplied,
        [nameof(GetAppliedFrontier)] = GetAppliedFrontier,
        [nameof(SetApplied)] = SetApplied,
        [nameof(CountOversizedBaselineKeys)] = CountOversizedBaselineKeys,
        [nameof(BaselineDocumentsFirst)] = BaselineDocumentsFirst,
        [nameof(BaselineDocumentsAfter)] = BaselineDocumentsAfter,
        [nameof(BaselineTombstonesFirst)] = BaselineTombstonesFirst,
        [nameof(BaselineTombstonesAfter)] = BaselineTombstonesAfter,
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
