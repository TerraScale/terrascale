namespace TerraBase;

using TerraBase.Replication;
using TerraBase.Storage.Backup;
using TerraBase.Storage.Compaction;

/// <summary>Options used when opening a TerraBase engine.</summary>
public sealed record TerraBaseOptions
{
    /// <summary>Creates default local options.</summary>
    public TerraBaseOptions()
    {
    }

    /// <summary>The region label used by request metrics.</summary>
    public Region Region { get; init; } = Region.Unknown;
}

/// <summary>
/// The C# TerraBase composition root. Storage and telemetry remain interfaces
/// so a durable backend can be introduced without changing document callers.
/// </summary>
public sealed class TerraBaseEngine : IAsyncDisposable
{
    private readonly IStorageProvider provider;
    private bool disposed;

    /// <summary>Opens an engine over a supplied storage provider.</summary>
    /// <param name="provider">The storage provider.</param>
    /// <param name="metrics">The metrics registry, or a new registry.</param>
    /// <param name="options">The engine options.</param>
    public TerraBaseEngine(
        IStorageProvider provider,
        MetricsRegistry? metrics = null,
        TerraBaseOptions? options = null)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Metrics = metrics ?? new MetricsRegistry();
        Options = options ?? new TerraBaseOptions();
        Documents = new DocumentStore(provider, Metrics);
        Replication = new LeaderlessReplicationEngine();
        Metrics.SetComponentHealth(Component.Storage, HealthStatus.Healthy);
        Metrics.SetComponentHealth(Component.Replication, HealthStatus.Healthy);
        Metrics.SetComponentHealth(Component.Membership, HealthStatus.Healthy);
        Metrics.SetComponentHealth(Component.ControlPlane, HealthStatus.Unknown);
        Metrics.SetComponentHealth(Component.Backup, HealthStatus.Unknown);
        Metrics.SetComponentHealth(Component.Metrics, HealthStatus.Healthy);
        Metrics.SetServiceState(ServiceState.Serving);
    }

    /// <summary>Opens a local in-memory engine.</summary>
    /// <param name="metrics">The metrics registry, or a new registry.</param>
    /// <param name="options">The engine options.</param>
    /// <returns>The opened engine.</returns>
    public static TerraBaseEngine Open(MetricsRegistry? metrics = null, TerraBaseOptions? options = null) =>
        new(new InMemoryStorageProvider(), metrics, options);

    /// <summary>Opens a durable file-backed engine.</summary>
    /// <param name="path">The file that stores the document snapshot.</param>
    /// <param name="metrics">The metrics registry, or a new registry.</param>
    /// <param name="options">The engine options.</param>
    /// <returns>The opened engine.</returns>
    public static TerraBaseEngine Open(string path, MetricsRegistry? metrics = null, TerraBaseOptions? options = null) =>
        new(new FileStorageProvider(path), metrics, options);

    /// <summary>Opens a durable SQLite-backed engine.</summary>
    /// <param name="path">The SQLite database file path.</param>
    /// <param name="metrics">The metrics registry, or a new registry.</param>
    /// <param name="options">The engine options.</param>
    /// <returns>The opened engine.</returns>
    public static TerraBaseEngine OpenSqlite(string path, MetricsRegistry? metrics = null, TerraBaseOptions? options = null) =>
        new(new SqliteStorageProvider(path), metrics, options);

    /// <summary>The configured options.</summary>
    public TerraBaseOptions Options { get; }

    /// <summary>The metrics registry for this engine.</summary>
    public MetricsRegistry Metrics { get; }

    /// <summary>The abstract document operation surface.</summary>
    public DocumentStore Documents { get; }

    /// <summary>The deterministic leaderless replication state machine.</summary>
    public LeaderlessReplicationEngine Replication { get; }

    /// <summary>Creates a native online backup manager for a SQLite engine.</summary>
    public SqliteBackupManager CreateBackupManager(SqliteBackupOptions options)
    {
        if (provider is not SqliteStorageProvider sqlite)
            throw new NotSupportedException("Native SQLite backups require a SQLite storage provider.");
        return new SqliteBackupManager(sqlite.StoragePath, options);
    }

    /// <summary>Compacts the SQLite file while retaining WAL durability semantics.</summary>
    public Task<SqliteCompactionResult> CompactAsync(SqliteCompactionOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (provider is not SqliteStorageProvider sqlite)
            throw new NotSupportedException("SQLite compaction requires a SQLite storage provider.");
        return SqliteCompactor.CompactAsync(sqlite.StoragePath, options, cancellationToken);
    }

    /// <summary>
    /// Applies several document mutations through the configured provider as
    /// one atomic transaction.
    /// </summary>
    /// <param name="mutations">The ordered mutations to evaluate.</param>
    /// <param name="cancellationToken">The cancellation signal.</param>
    /// <returns>The committed writes and journal sequence range.</returns>
    /// <exception cref="NotSupportedException">
    /// The configured provider does not expose transactional batches.
    /// </exception>
    public ValueTask<StorageBatchResult> ApplyBatchAsync(
        IReadOnlyList<StorageMutation> mutations,
        CancellationToken cancellationToken = default)
    {
        if (provider is not ITransactionalStorageProvider transactional)
        {
            throw new NotSupportedException("The configured storage provider does not support atomic batches.");
        }

        return transactional.ApplyBatchAsync(mutations, cancellationToken);
    }

    /// <summary>Returns a current health report.</summary>
    public HealthReport Health() => Metrics.Health();

    /// <summary>Stops the engine and releases its storage provider.</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Metrics.SetServiceState(ServiceState.Stopped);
        await provider.DisposeAsync().ConfigureAwait(false);
    }
}
