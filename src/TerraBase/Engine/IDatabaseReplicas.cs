using TerraBase.Tenancy;

namespace TerraBase.Engine;

/// <summary>A completed pull.</summary>
/// <param name="Changed">Whether the pull applied pages.</param>
internal sealed record ReplicaPullResult(bool Changed);

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
}

/// <summary>One read transaction on a replica. Disposing it rolls back.</summary>
internal interface IReplicaReader : IAsyncDisposable
{
    /// <summary>Runs one read statement; a statement that would write fails with <see cref="SqlFailure.ReadOnly"/>.</summary>
    ValueTask<SqlStatementResult> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken);

    /// <summary>Prepares one statement without running it: its parameters and result columns.</summary>
    ValueTask<SqlStatementShape> DescribeAsync(string sql, CancellationToken cancellationToken);
}
