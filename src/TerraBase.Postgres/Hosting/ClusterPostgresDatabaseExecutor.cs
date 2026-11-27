using TerraScale.Contracts.Cluster;
using TerraScale.Contracts.Consistency;
using TerraScale.Contracts.Continuity;

namespace TerraBase.Postgres.Hosting;

public sealed class ClusterPostgresDatabaseExecutor(ISqlDatabaseSession session, IClusterCoordinator coordinator) : IPostgresDatabaseExecutor
{
    public Ulid DatabaseId => session.DatabaseId;

    public ulong? SchemaEpoch => session.SchemaEpoch;

    public CausalBookmark CurrentBookmark => coordinator.CurrentBookmark(session.DatabaseId).Merge(session.CurrentBookmark);

    public ValueTask<SqlReadOutcome> ReadAsync(
        SqlStatementRequest request,
        CancellationToken cancellationToken) => session.ExecuteReadAsync(request, cancellationToken);

    public ValueTask<ClusterWriteOutcome> WriteAsync(
        SqlStatementRequest request,
        CancellationToken cancellationToken) => coordinator.ExecuteWriteAsync(session, request, cancellationToken);

    public async ValueTask<ClusterBeginTransactionOutcome> BeginTransactionAsync(
        SqlTransactionOptions options,
        CancellationToken cancellationToken)
    {
        var outcome = await session.BeginTransactionAsync(options, cancellationToken);
        return outcome switch
        {
            ISqlTransaction transaction => new ClusterBeginTransactionOutcome(transaction),
            StorageBusy busy => busy,
            StorageTimeout timeout => new ClusterTimeout(timeout.Message, timeout.Code),
        };
    }

    public ValueTask<ClusterCommitTransactionOutcome> CommitAsync(
        ISqlTransaction transaction,
        CancellationToken cancellationToken) => coordinator.CommitTransactionAsync(session, transaction, cancellationToken);

    public IAsyncEnumerable<ChangefeedEntry> SubscribeChangesAsync(CausalBookmark after, CancellationToken cancellationToken) =>
        session.SubscribeChangesAsync(after, cancellationToken);
}
