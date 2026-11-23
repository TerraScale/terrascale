using TerraScale.Contracts.Cluster;
using TerraScale.Contracts.Consistency;
using TerraScale.Contracts.Continuity;

namespace TerraBase.Postgres.Hosting;

public sealed class ClusterPostgresDatabaseExecutor(ISqlDatabaseSession session, IClusterCoordinator coordinator) : IPostgresDatabaseExecutor
{
    public Ulid DatabaseId => session.DatabaseId;

    public CausalBookmark CurrentBookmark => coordinator.CurrentBookmark(session.DatabaseId).Merge(session.CurrentBookmark);

    public ValueTask<Result<SqlReadResult, SqlError, StorageBusy, RequestTimeout>> ReadAsync(
        SqlStatementRequest request,
        CancellationToken cancellationToken) => session.ExecuteReadAsync(request, cancellationToken);

    public ValueTask<Result<SqlWriteResult, SqlError, SerializationConflict, SchemaMismatch, Unavailable, RequestTimeout, StorageBusy>> WriteAsync(
        SqlStatementRequest request,
        CancellationToken cancellationToken) => coordinator.ExecuteWriteAsync(session, request, cancellationToken);

    public ValueTask<Result<ISqlTransaction, StorageBusy, RequestTimeout>> BeginTransactionAsync(
        SqlTransactionOptions options,
        CancellationToken cancellationToken) => session.BeginTransactionAsync(options, cancellationToken);

    public ValueTask<Result<CommitMetadata, SerializationConflict, SchemaMismatch, Unavailable, RequestTimeout, StorageBusy>> CommitAsync(
        ISqlTransaction transaction,
        CancellationToken cancellationToken) => coordinator.CommitTransactionAsync(session, transaction, cancellationToken);

    public IAsyncEnumerable<ChangefeedEntry> SubscribeChangesAsync(CausalBookmark after, CancellationToken cancellationToken) =>
        session.SubscribeChangesAsync(after, cancellationToken);
}
