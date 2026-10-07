using Grpc.Core;
using TerraBase.Configuration;
using TerraBase.Features.Replication.V1;

namespace TerraBase.Features.Management.Usage;

/// <summary>
/// A catalog primary's side of <c>CatalogUsage.Add</c>: adds the counts a follower sends to its own pending counts, which its
/// next flush saves. Each follower process (epoch) numbers its batches and sends the next only once the previous was
/// acknowledged, so a batch at or below the highest sequence added for its epoch is a retry, or an attempt that arrived late
/// (<see cref="UsageMeter.AcceptAsync"/>, which saves those sequences with the counts).
/// </summary>
internal sealed class UsageReceiver(TerraBaseOptions options, UsageMeter meter, SystemCatalog catalog, TimeProvider time) : CatalogUsage.CatalogUsageBase
{
    /// <summary>Usage protocol version.</summary>
    internal const uint Protocol = 1;

    public override async Task<UsageAck> Add(UsageBatch request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (options.CatalogPrimary is not null)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "this member is not a catalog primary"));
        }

        // A day of clock skew is tolerated; later hours could not be stored as a bucket.
        var latest = (time.GetUtcNow().ToUnixTimeSeconds() / 3600) + 24;
        if (request.Protocol != Protocol || request.Epoch.Length != 16 || request.Sequence is 0 or > long.MaxValue
            || request.Sender.Length is < 1 or > 128 || request.Counts.Count > UsageMeter.MaxBatch || !request.Counts.All(count => Valid(count, latest)))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid usage batch"));
        }

        // Only databases this catalog names get pending counts, as a flush would keep only theirs: so what followers send is
        // bounded by the databases that exist, whatever ids a batch carries. Each project is checked once (two point reads),
        // then each database of an existing one (one).
        var owners = request.Counts.Select(count => new DatabaseOwner(count.OrganizationId, count.ProjectId, count.DatabaseId)).ToHashSet();
        var known = await catalog.ReadAsync(async transaction =>
        {
            var found = new HashSet<DatabaseOwner>();
            foreach (var project in owners.GroupBy(owner => (owner.OrganizationId, owner.ProjectId)))
            {
                if (!await EngineManagementService.ScopeExistsUncheckedAsync(
                    transaction, project.Key.OrganizationId, project.Key.ProjectId, databaseId: null, context.CancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                foreach (var owner in project)
                {
                    if (await EngineManagementService.DatabaseUncheckedAsync(transaction, owner.ProjectId, owner.DatabaseId, context.CancellationToken)
                        .ConfigureAwait(false) is not null)
                    {
                        found.Add(owner);
                    }
                }
            }

            return found;
        }, context.CancellationToken).ConfigureAwait(false);
        var counts = request.Counts
            .Select(count => KeyValuePair.Create(
                new UsageKey(new DatabaseOwner(count.OrganizationId, count.ProjectId, count.DatabaseId), count.Hour, (UsageScope)count.Scope),
                new OpCounts((long)count.Reads, (long)count.Writes)))
            .Where(entry => known.Contains(entry.Key.Database))
            .ToList();
        return await meter.AcceptAsync(request.Sender, request.Epoch.ToBase64(), (long)request.Sequence, counts, context.CancellationToken)
            .ConfigureAwait(false) == UsageAccept.TooManySenders
            ? throw new RpcException(new Status(StatusCode.ResourceExhausted, "too many usage senders"))
            : new UsageAck();
    }

    // Ids are those of the catalog the follower replicates; a flush drops the counts of a database it does not name. Counts
    // are capped, so a whole batch adds without overflow and is checked before any of it is added.
    private static bool Valid(UsageCount count, long latest) =>
        count.OrganizationId.Length is > 0 and <= 128 && count.ProjectId.Length is > 0 and <= 128 && count.DatabaseId.Length is > 0 and <= 128
        && count.Hour >= 0 && count.Hour <= latest && count.Reads <= UsageMeter.MaxCount && count.Writes <= UsageMeter.MaxCount
        && (UsageScope)count.Scope switch
        {
            // As compaction files them: a day at its first hour, lifetime counts at hour 0.
            UsageScope.Hour => true,
            UsageScope.Day => count.Hour % 24 == 0,
            UsageScope.Lifetime => count.Hour == 0,
            _ => false,
        };
}
