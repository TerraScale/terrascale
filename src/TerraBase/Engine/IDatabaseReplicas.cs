using TerraBase.Engine.Revisions;
using TerraBase.Features.Shapes;
using TerraBase.Tenancy;

namespace TerraBase.Engine;

internal sealed record ReplicaPullResult(bool Changed, bool Pushed, bool Pending);

internal closed record class ReplicaVerdict;

internal sealed record class Equal : ReplicaVerdict;

internal sealed record class Diverged : ReplicaVerdict;

internal sealed record class Moved : ReplicaVerdict;

internal sealed record ReplicaComparison(ReplicaVerdict Verdict, DatabaseRevision? Revision, string? Where);

internal interface IDatabaseReplicas
{
    ValueTask OpenAsync(TenantContext database, Uri source, HttpClient http, CancellationToken cancellationToken);

    ValueTask<ReplicaPullResult?> PullAsync(TenantContext database, bool fromHome, CancellationToken cancellationToken);

    bool Offer(TenantContext database, DatabaseRevision from, DatabaseRevision to, ReadOnlyMemory<byte> answer, long? cdcPosition = null);

    ValueTask<bool> DeleteAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask<string?> DetachAsync(TenantContext database, CancellationToken cancellationToken);

    bool Holds(TenantContext database);

    IReadOnlyList<TenantContext> List();

    DatabaseRevision? HeldRevision(TenantContext database);

    ValueTask<ReplicaComparison?> CompareAsync(TenantContext database, DatabaseRevision revision, IReadOnlyList<RangeChecksum> home, CancellationToken cancellationToken);

    ValueTask<IReplicaReader?> BeginReadAsync(TenantContext database, CancellationToken cancellationToken);

    long Position { get; }

    ValueTask<IReplicaShapeReader?> BeginShapeReadAsync(TenantContext database, CancellationToken cancellationToken);

    ValueTask<IReplicaReader?> BeginReadAsync(TenantContext database, long position, CancellationToken cancellationToken);

    ValueTask<IReplicaWriter?> BeginWriteAsync(TenantContext database, CancellationToken cancellationToken);

    ReplicaOverlay? Overlay { get; set; }
}

internal delegate ValueTask<bool> ReplicaOverlay(TenantContext database, DatabaseRevision? pulled, IReplicaWriter writer, CancellationToken cancellationToken);

internal interface IReplicaWriter : IAsyncDisposable
{
    ValueTask<SqlStatementResult> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<CapturedRowChange>> CaptureAsync(CancellationToken cancellationToken);

    ValueTask CommitAsync(CancellationToken cancellationToken);
}

internal interface IReplicaReader : IAsyncDisposable
{
    ValueTask<SqlStatementResult> ExecuteAsync(SqlStatement statement, CancellationToken cancellationToken);

    ValueTask<SqlStatementShape> DescribeAsync(string sql, CancellationToken cancellationToken);
}
