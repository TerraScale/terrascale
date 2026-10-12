using TerraBase.Tenancy;

namespace TerraBase.Engine;

/// <summary>What one <see cref="IWalTap"/> call appended to its destination.</summary>
/// <param name="Bytes">Bytes of committed changes appended, ending at a commit; 0 when nothing new committed.</param>
/// <param name="Continuous">
/// <see langword="false"/> when changes were lost since the previous call: the log has a hole, so its backup
/// generation must end.
/// </param>
public readonly record struct WalRead(long Bytes, bool Continuous);

/// <summary>A consistent snapshot and the log read before it was pinned.</summary>
/// <param name="Read">Committed changes appended before the snapshot, including whether they remain continuous.</param>
/// <param name="At">UTC time when the snapshot's state was pinned, before copying it.</param>
public readonly record struct WalSnapshot(WalRead Read, DateTimeOffset At);

/// <summary>
/// The committed changes of one open storage file, in commit order, for continuous backup (TS-633): a log that
/// <see cref="IWalBackup.RollForwardAsync"/> replays on a snapshot. Calls must not overlap. Positions in a backup
/// generation count bytes of this log.
/// </summary>
public interface IWalTap : IAsyncDisposable
{
    /// <summary>Appends the changes committed since the previous call, or since the tap opened, to <paramref name="destination"/>.</summary>
    ValueTask<WalRead> ReadAsync(Stream destination, CancellationToken cancellationToken);

    /// <summary>
    /// Appends changes like <see cref="ReadAsync"/>, then writes a consistent copy of the file, exactly as of the last change
    /// appended, to a new file at <paramref name="snapshotPath"/>. Changes appended later apply on top of it.
    /// </summary>
    ValueTask<WalSnapshot> SnapshotAsync(Stream destination, string snapshotPath, CancellationToken cancellationToken);
}

/// <summary>Continuous physical backup of this node's storage files (TS-633): one file per database, plus the system database.</summary>
public interface IWalBackup
{
    /// <summary>Opens a tap on <paramref name="database"/>, or on the system database when it is null; null when it does not exist.</summary>
    ValueTask<IWalTap?> OpenTapAsync(TenantContext? database, CancellationToken cancellationToken);

    /// <summary>
    /// Rolls <paramref name="file"/>, a snapshot a tap wrote, forward through <paramref name="segments"/> (the log as taps
    /// appended it, in order), then checks its integrity. <see langword="false"/> when the result is not a sound database.
    /// </summary>
    ValueTask<bool> RollForwardAsync(string file, IAsyncEnumerable<Stream> segments, CancellationToken cancellationToken);

    /// <summary>
    /// Creates <paramref name="database"/> from <paramref name="file"/>, which it takes over, as a copy: its replication
    /// starts over from a baseline. Throws when the database already exists.
    /// </summary>
    ValueTask ImportDatabaseAsync(TenantContext database, string file, CancellationToken cancellationToken);

    /// <summary>
    /// A SHA-256 of the logical content of the database file <paramref name="file"/>: its schema and every row of every
    /// table in a fixed order, so two copies of the same state match whatever their page layout. For restore drills.
    /// </summary>
    ValueTask<byte[]> ChecksumAsync(string file, CancellationToken cancellationToken);
}
