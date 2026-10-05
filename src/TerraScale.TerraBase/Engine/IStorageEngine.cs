using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Engine;

/// <summary>
/// The database engine TerraBase stores JSON documents in. Each database is isolated; one
/// implementation per substrate (SQLite today, Turso or others later) lives under <c>Engine/&lt;Name&gt;/</c>.
/// </summary>
/// <remarks>
/// A client database is addressed only by a <see cref="TenantContext"/> naming its organization, project and
/// database, and is stored under that organization: storage of one tenant is never reachable through another's.
/// The data directory and its ancestors must be controlled by trusted operators; filesystem writers must not
/// replace paths while the engine is running. Link validation does not isolate hostile local processes.
/// </remarks>
public interface IStorageEngine : IAsyncDisposable
{
    /// <summary>Short substrate name, for diagnostics (for example <c>sqlite</c>).</summary>
    string Name { get; }

    /// <summary>Whether the system database already exists, without creating or opening it; refuses symbolic links.</summary>
    bool SystemDatabaseExists { get; }

    /// <summary>
    /// Creates a database, optionally copying <paramref name="source"/> within the same organization, after recording
    /// creation ownership. Throws <see cref="IOException"/> before invoking <paramref name="recordIntent"/> when the
    /// destination contains a database file, sidecars or private creation artifacts, including directories and case variants.
    /// Returns false only when the source is missing. The destination check, callback, publication and opening are
    /// serialized together with engine lifecycle operations. A reservation blocks competing creation until
    /// <see cref="CompleteDatabaseIntentAsync"/> is called after registration or cleanup commits, including after failure.
    /// </summary>
    /// <remarks>
    /// The callback may only persist ownership through an already opened system store; it must not create or open tenant
    /// storage or otherwise reenter engine lifecycle operations. It is not invoked when the source is missing.
    /// </remarks>
    ValueTask<bool> CreateDatabaseWithIntentAsync(
        TenantContext database, TenantContext? source, Func<CancellationToken, ValueTask> recordIntent, CancellationToken cancellationToken);

    /// <summary>Releases a lifecycle reservation after its cleanup intent was cleared or registration committed.</summary>
    ValueTask CompleteDatabaseIntentAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// Reserves a database and records a delete intent under the lifecycle lock. A false callback result releases the
    /// new reservation; success or failure retains it until catalog finalization and <see cref="CompleteDatabaseIntentAsync"/>.
    /// Returns false without invoking the callback when the target is already reserved by another operation.
    /// The callback must use an already opened system store without reentering engine lifecycle operations.
    /// </summary>
    ValueTask<bool> RecordDatabaseDeleteIntentAsync(
        TenantContext database, Func<CancellationToken, ValueTask<bool>> recordIntent, CancellationToken cancellationToken);

    /// <summary>
    /// Whether this engine is open and can create files in its data directory, for the readiness probe. A call creates and
    /// removes an empty probe file there, off the calling thread, reusing completed results for the configured readiness
    /// cache TTL (one second by default). Concurrent calls await the same probe task; a probe still running after two
    /// seconds means a stalled volume: not ready. Engine closure is checked on every call.
    /// </summary>
    ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates the database of <paramref name="database"/> atomically within this process: among concurrent calls for the same
    /// database exactly one returns <see langword="true"/>. Returns <see langword="false"/> when it already existed.
    /// An unresolved creation intent rejects competing creation with <see cref="IOException"/>.
    /// </summary>
    ValueTask<bool> CreateDatabaseAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>Opens an existing database, or <see langword="null"/> when it does not exist.</summary>
    ValueTask<IDatabaseStore?> OpenDatabaseAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// Creates <paramref name="target"/> as a consistent snapshot copy of <paramref name="source"/> in the same organization.
    /// Returns <see langword="false"/> when <paramref name="source"/> does not exist; throws when <paramref name="target"/>
    /// already exists or belongs to another organization.
    /// </summary>
    ValueTask<bool> CopyDatabaseAsync(TenantContext source, TenantContext target, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a database, its sidecars and private creation artifacts even when the main file is missing.
    /// Returns <see langword="true"/> only when the main database file existed.
    /// </summary>
    /// <param name="database">The target database.</param>
    /// <param name="cancellationToken">Cancels cleanup.</param>
    /// <param name="retainIntentOwnership">For an existing durable cleanup intent, reserve the target until catalog finalization.</param>
    ValueTask<bool> DeleteDatabaseAsync(TenantContext database, CancellationToken cancellationToken, bool retainIntentOwnership = false);

    /// <summary>
    /// Streams files and directory-shaped database artifacts, excluding exact root system and ownership files and without
    /// following links. Directory artifacts and files outside the recognized tenant layout have a null
    /// <see cref="StoredDatabaseFile.Database"/>.
    /// </summary>
    IEnumerable<StoredDatabaseFile> EnumerateDatabaseFiles(CancellationToken cancellationToken = default);

    /// <summary>Opens TerraBase's own database (catalog, API keys), creating it on first use. No tenant can address it.</summary>
    ValueTask<IDatabaseStore> OpenSystemDatabaseAsync(CancellationToken cancellationToken);
}
