using TerraBase.Engine.Revisions;
using TerraBase.Features.Shapes;
using TerraBase.Tenancy;

namespace TerraBase.Engine;

internal sealed record ReplicaPullResult(bool Changed);

internal enum ReplicaVerdict
{
    Equal,
    Diverged,
    Moved,
}

internal sealed record ReplicaComparison(ReplicaVerdict Verdict, DatabaseRevision? Revision, string? Where);

internal interface IDatabaseReplicas
{
    ValueTask OpenAsync(TenantContext database, Uri source, HttpClient http, CancellationToken cancellationToken);

    ValueTask<ReplicaPullResult?> PullAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask<bool> DeleteAsync(TenantContext database, CancellationToken cancellationToken);

    bool Holds(TenantContext database);

    IReadOnlyList<TenantContext> List();

    DatabaseRevision? HeldRevision(TenantContext database);

    ValueTask<ReplicaComparison?> CompareAsync(TenantContext database, DatabaseRevision revision, IReadOnlyList<RangeChecksum> home, CancellationToken cancellationToken);

    ValueTask<IReplicaReader?> BeginReadAsync(TenantContext database, CancellationToken cancellationToken);

    long Position { get; }

    ValueTask<IReplicaShapeReader?> BeginShapeReadAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask<IReplicaReader?> BeginReadAsync(TenantContext database, long position, CancellationToken cancellationToken);
}

internal interface IReplicaReader : IAsyncDisposable
{
    ValueTask<SqlStatementResult> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken);

    ValueTask<SqlStatementShape> DescribeAsync(string sql, CancellationToken cancellationToken);
}
