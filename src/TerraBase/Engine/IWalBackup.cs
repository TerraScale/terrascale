using TerraBase.Tenancy;

namespace TerraBase.Engine;

/// <summary>What one <see cref="IWalTap"/> call appended to its destination.</summary>
/// <param name="Bytes">Bytes of committed changes appended, ending at a commit; 0 when nothing new committed.</param>
/// <param name="Continuous">
/// <see langword="false"/> when changes were lost since the previous call: the log has a hole, so its backup
/// generation must end.
/// </param>
/// <param name="Remaining">
/// <see langword="true"/> when the read stopped at its byte budget and later commits wait for the next call.
/// </param>
/// <param name="Mark">
/// The read mark it appended last: when a read that reached the newest commit started (every transaction committed before
/// then is in the log up to the mark); null when it appended none.
/// </param>
public readonly record struct WalRead(long Bytes, bool Continuous, bool Remaining, DateTimeOffset? Mark);

/// <summary>A consistent snapshot and the log read before it was pinned.</summary>
/// <param name="Read">Committed changes appended before the snapshot, including whether they remain continuous.</param>
/// <param name="At">UTC time when the snapshot's state was pinned, before copying it.</param>
/// <param name="Incarnation">The physical file identity read inside that same pinned transaction.</param>
/// <param name="Offset">The commit offset in that pinned file.</param>
public readonly record struct WalSnapshot(WalRead Read, DateTimeOffset At, Ulid Incarnation = default, long Offset = 0);

/// <summary>Where a restore at a time stops in one log segment, from the times its transactions were recorded with.</summary>
/// <param name="Bytes">Log bytes from the segment's start that the restore replays: whole transactions, each run before the time.</param>
/// <param name="Reached">
/// A transaction committed at or after the time follows them in the segment: every later one committed after it, so every
/// transaction acknowledged before the time is in the bytes up to here.
/// </param>
/// <param name="Last">The commit second (Unix) of the segment's last timed transaction read, or the previous one's.</param>
/// <param name="Unproven">Why the time cannot be placed between two transactions in the segment; null when it can.</param>
public readonly record struct LogCut(long Bytes, bool Reached, long? Last, string? Unproven);

/// <summary>
/// The committed changes of one open storage file, in commit order, for continuous backup (TS-633): a log that
/// <see cref="IWalBackup.RollForwardAsync"/> replays on a snapshot. Calls must not overlap. Positions in a backup
/// generation count bytes of this log.
/// </summary>
public interface IWalTap : IAsyncDisposable
{
    /// <summary>
    /// Appends the changes committed since the previous call, or since the tap opened, to <paramref name="destination"/>,
    /// stopping at the first commit past a fixed byte budget, so a backlog takes several calls
    /// (<see cref="WalRead.Remaining"/>). A call that throws has not moved the tap: the caller drops what it appended,
    /// and the next call appends the same changes again (or reports a hole).
    /// </summary>
    ValueTask<WalRead> ReadAsync(Stream destination, CancellationToken cancellationToken);

    /// <summary>
    /// Appends every change committed so far, with no byte budget, then writes a consistent copy of the file, exactly as of
    /// the last change appended, to a new file at <paramref name="snapshotPath"/>. Changes appended later apply on top of it.
    /// </summary>
    ValueTask<WalSnapshot> SnapshotAsync(Stream destination, string snapshotPath, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the file this tap reads was replaced, or a healthy rewrite replacing it waits for its handles: the owner disposes
    /// the tap without reading it, and opens a new one later.
    /// </summary>
    bool Ended => false;
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
    /// Places a restore at <paramref name="at"/> in <paramref name="segment"/> (a log segment, read from its current
    /// position), which follows transactions that ran before the time, the last committed in second
    /// <paramref name="previous"/> (Unix) when known.
    /// </summary>
    /// <exception cref="InvalidDataException">The log is malformed.</exception>
    LogCut CutLog(Stream segment, DateTimeOffset at, long? previous);

    /// <summary>
    /// When the read that appended the end of <paramref name="segment"/> started, if that read reached the newest commit:
    /// every transaction committed before then is in the log up to the segment's end. Null otherwise.
    /// </summary>
    /// <exception cref="InvalidDataException">The log is malformed.</exception>
    DateTimeOffset? ReadMark(Stream segment);

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
