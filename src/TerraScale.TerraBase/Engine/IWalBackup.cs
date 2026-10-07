using TerraBase.Tenancy;

namespace TerraBase.Engine;

/// <summary>What one <see cref="IWalTap"/> call appended to its destination.</summary>
/// <param name="Frames">Committed log frames appended, ending at a commit; 0 when nothing new committed.</param>
/// <param name="Continuous">
/// <see langword="false"/> when frames were lost since the previous call: the stream has a hole, so its backup
/// generation must end.
/// </param>
public readonly record struct WalRead(long Frames, bool Continuous);

/// <summary>
/// The committed write-ahead log of one open storage file, for continuous backup (TS-633). While a tap is open the log
/// is checkpointed only by the tap, after it has read every committed frame, so no frame is lost between two reads.
/// Calls must not overlap. Each appended frame is the log's own: a 24-byte header (page number, then the database size
/// in pages on a commit) and one page.
/// </summary>
public interface IWalTap : IAsyncDisposable
{
    /// <summary>Appends the frames committed since the previous call, or since the tap opened, to <paramref name="destination"/>.</summary>
    ValueTask<WalRead> ReadAsync(Stream destination, CancellationToken cancellationToken);

    /// <summary>
    /// Appends frames like <see cref="ReadAsync"/>, then writes a consistent copy of the file, exactly as of the last frame
    /// appended, to a new file at <paramref name="snapshotPath"/>. Frames appended later apply on top of it.
    /// </summary>
    ValueTask<WalRead> SnapshotAsync(Stream destination, string snapshotPath, CancellationToken cancellationToken);
}

/// <summary>Continuous physical backup of this node's storage files (TS-633): one file per database, plus the system database.</summary>
public interface IWalBackup
{
    /// <summary>Opens a tap on <paramref name="database"/>, or on the system database when it is null; null when it does not exist.</summary>
    ValueTask<IWalTap?> OpenTapAsync(TenantContext? database, CancellationToken cancellationToken);

    /// <summary>
    /// Rolls <paramref name="file"/>, a snapshot a tap wrote, forward through <paramref name="segments"/> (frames as taps
    /// appended them, in order), then checks its integrity. <see langword="false"/> when the result is not a sound database.
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
