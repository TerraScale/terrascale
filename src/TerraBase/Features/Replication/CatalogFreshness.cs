using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;

namespace TerraBase.Features.Replication;

/// <summary>
/// How recently this machine's catalog (API keys, memberships, projects) was proved to hold the other machines'. Peer
/// streams deliver a revocation in about a second, but nothing tells a partitioned machine what it missed; only a
/// completed anti-entropy comparison of the system database with a peer proves that peer's catalog, as of when it began,
/// is held here.
/// </summary>
public interface ICatalogFreshness
{
    /// <summary>
    /// Whether the last directory read listed this machine, every machine it listed was proved held less than
    /// <paramref name="window"/> ago, and so was at least one machine of every other region the catalog records, listed
    /// or not. A directory that omits a machine never proves that machine's revocations reached this one.
    /// </summary>
    ValueTask<bool> IsFreshAsync(TimeSpan window, CancellationToken cancellationToken);
}

/// <summary>The catalog proofs of <see cref="AntiEntropy"/> against the directory's view and the catalog's recorded regions.</summary>
internal sealed class CatalogFreshness(IMembership membership, AntiEntropy antiEntropy, IStorageEngine engine, TerraBaseOptions options) : ICatalogFreshness
{
    public async ValueTask<bool> IsFreshAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        var view = await membership.RefreshAsync(cancellationToken).ConfigureAwait(false);
        // A read that did not list this machine (Fly's DNS answers a lost name with no records) says nothing about the others.
        if (!view.Contains(membership.Self.Name))
        {
            return false;
        }

        var held = await antiEntropy.CatalogHeldWithinAsync(window, cancellationToken).ConfigureAwait(false);
        var listed = AntiEntropy.Peers(view, membership.Self.Name);
        if (!listed.All(peer => held.Contains(peer.Name)))
        {
            return false;
        }

        // A region the directory dropped (a partial or self-only view) still needs a proof from one of its machines, so a
        // partition stays bounded by the window whatever the directory lists. A record without a region counts as its own.
        var recorded = engine.SystemDatabaseExists ? await CatalogGate.RecordedAsync(engine, cancellationToken).ConfigureAwait(false) : [];
        return recorded.Where(machine => machine.Value != options.Region)
            .GroupBy(machine => machine.Value ?? machine.Key, StringComparer.Ordinal)
            .All(region => listed.Any(peer => peer.Region == region.Key) || region.Any(machine => held.Contains(machine.Key)));
    }
}
