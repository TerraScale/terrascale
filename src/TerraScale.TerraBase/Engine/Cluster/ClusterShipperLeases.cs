using System.Collections.Concurrent;
using TerraBase.Tenancy;

namespace TerraBase.Engine.Cluster;

/// <summary>
/// <see cref="IShipperLeases"/> from the cell's membership (agreed with TS-633 on Ku TS-647): a database's primary ships
/// its backup while it holds its member lease. A member that becomes primary starts at once when the previous primary
/// left the view (its lease ended before its key expired, so it already stopped); otherwise it waits out the lease
/// fence, so the previous primary has either seen the new ring or lost its lease. The epoch is the membership revision
/// its current unbroken term began at; a static cell (one member, tests) ships with epoch 1.
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
                (term.Since, term.Epoch, term.Previous) = (null, null, primary?.Name);
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
            term.Since ??= term.Previous is { } previous && !view.Contains(previous) ? time.GetTimestamp() - Ticks(fence) : time.GetTimestamp();
            if (time.GetElapsedTime(term.Since.Value) < fence)
            {
                return null;
            }

            return term.Epoch = view.Revision;
        }
    }

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * time.TimestampFrequency);

    // One database's term as primary on this member, touched under its own lock.
    private sealed class Term
    {
        public long? Since { get; set; }
        public ulong? Epoch { get; set; }
        public string? Previous { get; set; }
    }
}
