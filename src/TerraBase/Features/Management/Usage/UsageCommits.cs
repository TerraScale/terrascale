using TerraBase.Cluster;
using TerraBase.Engine;
using TerraBase.Features.Health;
using TerraBase.Hosting;
using TerraScale.Contracts.Cluster;

namespace TerraBase.Features.Management.Usage;

internal readonly union UsageCommitOutcome(CommitMetadata, BackendUnavailable);

internal sealed class UsageCommits(SystemDatabase system, CommitFinalizer finalizer, ClusterOptions cluster, NodeReadiness readiness)
{
    public string NodeId => cluster.NodeId;

    public bool Ready => readiness.Blocked is null;

    public async ValueTask<UsageCommitOutcome> CommitLocalAsync(IReadOnlyList<SqlStatement> statements, CancellationToken cancellationToken)
    {
        var local = await system.WriteLocalTelemetryAsync(statements, cancellationToken);
        return local switch
        {
            SqlWriteResult written => written.Commit,
            SqlError error => new BackendUnavailable(error.Message, "system_catalog_error"),
            StrongRequired => new BackendUnavailable("usage counters cannot be written with a fast commit", "usage_strong_required"),
            SchemaMismatch => new BackendUnavailable("the system catalog schema is changing; retry", "system_catalog_busy"),
            StorageBusy busy => new BackendUnavailable(busy.Message, "storage_busy"),
            StorageTimeout timeout => new BackendUnavailable(timeout.Message, "system_catalog_timeout"),
        };
    }

    public async ValueTask<bool> FinalizeAsync(CommitMetadata commit, CancellationToken cancellationToken) =>
        await finalizer.FinalizeFastAsync(SystemDatabase.Id, commit, cancellationToken) switch
        {
            InRegionReplicated => true,
            ClusterUnavailable or ClusterTimeout => false,
        };
}
