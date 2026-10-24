namespace TerraBase.Engine.Turso;

/// <summary>
/// The on-disk storage format: <see cref="Version"/>, then the first 12 hex digits of the schema digest. A data directory
/// records the format of the build that created it, and a build of another format refuses the directory (no migration).
/// The deploy gate compares it per region before switching a region's image.
/// </summary>
public static class StorageFormat
{
    /// <summary>
    /// Bumped by hand for every change the schema digest cannot see (value encodings, file layout); the digest changes with
    /// every schema change by itself. CI requires a bump when a storage file changes (docs/terrabase/ci.md, storage train).
    /// 3: the Turso engine (one writer connection with change capture, rowid tables only). 4: no change_log, shape_history
    /// or collections.generation (R2c-2). 5: the system database is a lattice database (R10a): document states, HLC
    /// versions and tombstones, no catalog journal. 6: persisted feed sequence and derived shape storage.
    /// 7: document digests include the length-prefixed collection and id.
    /// 8: <c>_tb_peers.incarnation</c> scopes each peer's ack to the
    /// incarnation of the file whose change log it names (R3). 9: persisted change-feed prefix hashes.
    /// 10: node-local copy faults, write intents and pinned refill certificates (A6).
    /// 11: FULL operation intents in <c>_intents.db</c>, the node-bound replica identity (R6) and maintained SYSTEM
    /// <c>_tb_merkle</c> bucket digests (R9), batched reset 2026-10-22.
    /// 12: persisted collected-state join floors (<c>_tb_gc_floor</c>), batch 3 reset 2026-10-23.
    /// 13: regional auth credential ancestry and authentication-state stamps, batch 5 reset 2026-10-23.
    /// 14: persisted full-text indexes and derived text tables, batch 5 reset 2026-10-23.
    /// 15: maintained per-collection document counts and bytes (<c>_tb_collection_stats</c>).
    /// 16: collection access proofs and collection generations in the persisted change feed.
    /// 17: aegis256 database/WAL pages, wrapped-key sidecars and encrypted snapshot bundles, batch 5.
    /// 18: vector indexes (<c>_tb_vector_indexes</c>) and their derived slot and sign-code chunk tables.
    /// </summary>
    public const int Version = 18;

    /// <summary>The format this build writes, for example <c>2-3f1a09c4e7b2</c>.</summary>
    public static string Id { get; } = $"{Version}-{Convert.ToHexStringLower(TursoDatabaseStore.FormatDigest)[..12]}";
}
