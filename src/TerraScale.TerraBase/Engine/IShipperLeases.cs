using TerraBase.Tenancy;

namespace TerraBase.Engine;

/// <summary>
/// Chooses the one member that ships each storage file's continuous backup (TS-633): <c>Engine/Cluster</c> picks the
/// database's primary member while it holds its member lease (TS-647).
/// </summary>
public interface IShipperLeases
{
    /// <summary>
    /// The epoch of this member's lease on <paramref name="database"/> (null: the system database) while this member ships
    /// it, or <see langword="null"/> when it must not. An implementation returns null before its lease could have expired
    /// for the other members. The epoch changes whenever the lease was lost and taken again: shipping then starts a new
    /// generation, since the log may come from another member's file.
    /// </summary>
    ValueTask<ulong?> ShipperEpochAsync(TenantContext? database, CancellationToken cancellationToken);
}
