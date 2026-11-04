using TerraBase.Engine.Revisions;
using TerraBase.Tenancy;

namespace TerraBase.Engine;

/// <summary>A completed pull.</summary>
/// <param name="Changed">Whether the pull applied pages.</param>
internal sealed record ReplicaPullResult(bool Changed);

internal enum ReplicaVerdict
{
    Equal,
    Diverged,
    Moved,
}

internal sealed record ReplicaComparison(ReplicaVerdict Verdict, DatabaseRevision? Revision, string? Where);

/// <summary>
/// This node's read-only copies of databases homed in another region (docs/terrabase/region-replicas.md). A replica pulls the
/// home's pages through its internal listener and is never written locally; it can always be deleted and pulled again.
/// </summary>
internal interface IDatabaseReplicas
{
    /// <summary>
    /// Opens this node's replica of <paramref name="database"/>, creating and bootstrapping it from <paramref name="source"/> (the
    /// home's pull address) when it does not exist. <paramref name="http"/> sends every pull to the home's internal listener.
    /// Idempotent; an existing replica keeps its source.
    /// </summary>
    ValueTask OpenAsync(TenantContext database, Uri source, HttpClient http, CancellationToken cancellationToken);

    /// <summary>Pulls the home's newer pages into an open replica; <see langword="null"/> when none is open.</summary>
    ValueTask<ReplicaPullResult?> PullAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>Closes and deletes a replica's files; <see langword="false"/> when none existed.</summary>
    ValueTask<bool> DeleteAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>Whether this node holds or is opening a replica of <paramref name="database"/>; an in-memory check.</summary>
    bool Holds(TenantContext database);

    /// <summary>The open replicas.</summary>
    IReadOnlyList<TenantContext> List();

    DatabaseRevision? HeldRevision(TenantContext database);

    ValueTask<ReplicaComparison?> CompareAsync(TenantContext database, DatabaseRevision revision, IReadOnlyList<RangeChecksum> home, CancellationToken cancellationToken);

    /// <summary>
    /// A read snapshot of an open replica that holds at least one pull, or <see langword="null"/>. Every statement of the
    /// reader sees the same revision; nothing it runs is kept.
    /// </summary>
    ValueTask<IReplicaReader?> BeginReadAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// This node's pull position: it grows when any replica's pull starts. A session that just wrote at the home takes it
    /// after the write's answer; a replica whose last completed pull started after that position holds the write
    /// (docs/terrabase/region-replicas.md#read-your-writes).
    /// </summary>
    long Position { get; }

    /// <summary>
    /// As <see cref="BeginReadAsync(TenantContext, CancellationToken)"/>, but only from a replica whose last completed pull
    /// started after <paramref name="position"/>; <see langword="null"/> otherwise.
    /// </summary>
    ValueTask<IReplicaReader?> BeginReadAsync(TenantContext database, long position, CancellationToken cancellationToken);

    /// <summary>
    /// A local write transaction on an open replica that holds at least one pull, or <see langword="null"/>
    /// (docs/terrabase/local-overlay.md). Writes and pulls of one replica run one at a time; the writer holds the replica
    /// until it is disposed, so its caller publishes the transaction before a pull can revert it.
    /// </summary>
    ValueTask<IReplicaWriter?> BeginWriteAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// Runs after every pull that changed a replica, before any later read starts, with a writer whose statements fire no
    /// triggers and capture nothing. It answers whether to commit what it wrote.
    /// </summary>
    ReplicaOverlay? Overlay { get; set; }
}

internal delegate ValueTask<bool> ReplicaOverlay(TenantContext database, DatabaseRevision? pulled, IReplicaWriter writer, CancellationToken cancellationToken);

/// <summary>One write transaction on a replica. Disposing it rolls back what was not committed.</summary>
internal interface IReplicaWriter : IAsyncDisposable
{
    ValueTask<SqlStatementResult> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken);

    /// <summary>The changes the transaction made since the last call, in order, without internal tables.</summary>
    ValueTask<IReadOnlyList<CapturedRowChange>> CaptureAsync(CancellationToken cancellationToken);

    ValueTask CommitAsync(CancellationToken cancellationToken);
}

/// <summary>One read transaction on a replica. Disposing it rolls back.</summary>
internal interface IReplicaReader : IAsyncDisposable
{
    /// <summary>Runs one read statement; a statement that would write fails with <see cref="SqlFailure.ReadOnly"/>.</summary>
    ValueTask<SqlStatementResult> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken);

    /// <summary>Prepares one statement without running it: its parameters and result columns.</summary>
    ValueTask<SqlStatementShape> DescribeAsync(string sql, CancellationToken cancellationToken);
}
