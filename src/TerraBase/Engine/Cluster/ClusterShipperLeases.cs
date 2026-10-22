using System.Collections.Concurrent;
using TerraBase.Tenancy;

namespace TerraBase.Engine.Cluster;

/// <summary>
/// <see cref="IShipperLeases"/> from the cell's membership (agreed with TS-633 on Ku TS-647): a database's primary ships
/// its backup while it holds its member lease. A member that becomes primary waits out the lease fence, including when
/// the previous primary left its view. Each observed loss of ownership ends the term; its next term has a new epoch
/// even when the membership revision stayed the same. A static cell (one member, tests) ships with epoch 1.
/// </summary>
internal sealed class ClusterShipperLeases(IMembership membership, TimeProvider time) : IShipperLeases
{
    private readonly ConcurrentDictionary<string, Term> _terms = new(StringComparer.Ordinal);

    public async ValueTask<ulong?> ShipperEpochAsync(TenantContext? database, CancellationToken cancellationToken)
    {
        var view = await membership.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var key = database is null ? "system" : $"{database.OrganizationId}/{database.ProjectId}/{database.DatabaseId}";
        var primary = new Ring(view).Primary(key);
        var term = _terms.GetOrAdd(key, static _ => new Term());
        lock (term)
        {
            if (!membership.HoldsLease || primary?.Name != membership.Self.Name)
            {
                (term.Since, term.Epoch) = (null, null);
                return null;
            }

            if (term.Epoch is { } epoch)
            {
                return epoch;
            }

            if (!membership.Dynamic)
            {
                return term.Epoch = 1;
            }

            var fence = membership.Fence;
            term.Since ??= time.GetTimestamp();
            if (time.GetElapsedTime(term.Since.Value) < fence)
            {
                return null;
            }

            return term.Epoch = checked(++term.LastEpoch);
        }
    }

    // One database's term as primary on this member, touched under its own lock.
    private sealed class Term
    {
        public long? Since { get; set; }
        public ulong? Epoch { get; set; }
        public ulong LastEpoch { get; set; }
    }
}
