namespace TerraBase.Engine.Sqlite;

/// <summary>
/// The on-disk storage format: <see cref="Version"/>, then the first 12 hex digits of the schema digest. A data directory
/// records the format of the build that created it, and a build of another format refuses the directory (no migration).
/// The deploy gate (<c>scripts/fly/deploy.sh</c>) compares it per region before switching a region's image.
/// </summary>
public static class StorageFormat
{
    /// <summary>
    /// Bumped by hand for every change the schema digest cannot see (value encodings, file layout); the digest changes with
    /// every schema change by itself. CI requires a bump when a storage file changes (docs/terrabase/ci.md, storage train).
    /// 3: the Turso engine (one writer connection with change capture, rowid tables only). 4: no change_log, shape_history
    /// or collections.generation (R2c-2). 5: the system database is a lattice database (R10a): document states, HLC
    /// versions and tombstones, no catalog journal.
    /// </summary>
    public const int Version = 5;

    /// <summary>The format this build writes, for example <c>2-3f1a09c4e7b2</c>.</summary>
    public static string Id { get; } = $"{Version}-{Convert.ToHexStringLower(SqliteDatabaseStore.FormatDigest)[..12]}";
}
