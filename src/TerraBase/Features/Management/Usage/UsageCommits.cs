using TerraBase.Cluster;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraScale.Contracts.Cluster;

namespace TerraBase.Features.Management.Usage;

internal readonly union UsageCommitOutcome(UsageCommitted, BackendUnavailable);

internal sealed record UsageCommitted(SqlWriteResult Written, bool Durable);

internal sealed class UsageCommits(SystemDatabase system, CommitFinalizer finalizer, InRegionRaftRouter raft)
{
    public async ValueTask<UsageCommitOutcome> WriteAsync(IReadOnlyList<SqlStatement> statements, CancellationToken cancellationToken)
    {
        var request = new SqlStatementRequest(SystemDatabase.Id, statements, SqlAccess.Write, ConsistencyMode.Fast, CausalBookmark.Empty);
        var local = await system.Session.ExecuteWriteAsync(request, cancellationToken);
        return local switch
        {
            SqlWriteResult written => new UsageCommitted(written, Durable(await finalizer.FinalizeFastAsync(SystemDatabase.Id, written.Commit, cancellationToken))),
            SqlError error => new BackendUnavailable(error.Message, "system_catalog_error"),
            StrongRequired => new BackendUnavailable("usage counters cannot be written with a fast commit", "usage_strong_required"),
            SchemaMismatch => new BackendUnavailable("the system catalog schema is changing; retry", "system_catalog_busy"),
            StorageBusy busy => new BackendUnavailable(busy.Message, "storage_busy"),
            StorageTimeout timeout => new BackendUnavailable(timeout.Message, "system_catalog_timeout"),
        };
    }

    public async ValueTask<bool> ConfirmAsync(CommitMetadata commit, CancellationToken cancellationToken) =>
        Durable(await raft.ReplicateAsync(SystemDatabase.Id, commit, cancellationToken));

    private static bool Durable(InRegionReplicationOutcome outcome) => outcome switch
    {
        InRegionReplicated => true,
        ClusterUnavailable or ClusterTimeout => false,
    };
}
