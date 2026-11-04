using System.Collections.Concurrent;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Features.Regions;
using TerraBase.Tenancy;

namespace TerraBase.Features.Replicas;

internal sealed record ReplicaHeldNotice(string OrganizationId, string ProjectId, string DatabaseId, string Region, string Revision);

internal sealed class ReplicaHolds(TerraBaseOptions options, IReplicaCatalog catalog, IDatabaseRevisions revisions, IRegionCatalog regions)
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<(string Database, string Region), DatabaseRevision> _held = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _changed = new(StringComparer.Ordinal);

    internal static int Compare(DatabaseRevision left, DatabaseRevision right)
    {
        var incarnation = left.Incarnation.CompareTo(right.Incarnation);
        return incarnation != 0 ? incarnation : left.Generation != right.Generation ? left.Generation.CompareTo(right.Generation) : left.Frame.CompareTo(right.Frame);
    }

    public void Held(ReplicaHeldNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (!DatabaseRevision.TryParse(notice.Revision, out var revision))
        {
            return;
        }

        _ = _held.AddOrUpdate((notice.DatabaseId, notice.Region), revision, (_, known) => Compare(revision, known) > 0 ? revision : known);
        if (_changed.TryRemove(notice.DatabaseId, out var changed))
        {
            changed.TrySetResult();
        }
    }

    public async ValueTask<bool> WaitAllAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (await catalog.FindAsync(database, cancellationToken).ConfigureAwait(false) is not TenantDatabaseRecord { Placement: var placement } || placement.Home != options.Region)
        {
            return true;
        }

        IReadOnlyList<string> waiting = placement.AllRegions
            ? [.. (await regions.ServingAsync(cancellationToken).ConfigureAwait(false)).Where(region => region != options.Region)]
            : placement.Replicas;
        if (waiting.Count == 0 || await revisions.CurrentRevisionAsync(database, cancellationToken).ConfigureAwait(false) is not { } target)
        {
            return true;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        var id = database.DatabaseId!;
        while (true)
        {
            var changed = _changed.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            if (waiting.All(region => _held.TryGetValue((id, region), out var held) && Compare(held, target) >= 0))
            {
                return true;
            }

            try
            {
                await changed.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }
    }
}
