namespace TerraBase.Features.Replication;

/// <summary>
/// How recently this machine's catalog (API keys, memberships, projects) was proved to hold every other listed machine's.
/// Peer streams deliver a revocation in about a second, but nothing tells a partitioned machine what it missed; only a
/// completed anti-entropy comparison of the system database with a peer proves that peer's catalog, as of when it began,
/// is held here.
/// </summary>
public interface ICatalogFreshness
{
    /// <summary>
    /// Whether every machine the membership lists now was proved held less than <paramref name="window"/> ago. A listed
    /// machine never compared, or unreachable since, makes it false; a machine that leaves the directory stops counting.
    /// </summary>
    ValueTask<bool> IsFreshAsync(TimeSpan window, CancellationToken cancellationToken);
}
