using TerraBase.Tenancy;

namespace TerraBase.Engine;

public readonly record struct WalRead(long Bytes, bool Continuous, bool Remaining, DateTimeOffset? Mark);

public readonly record struct WalSnapshot(WalRead Read, DateTimeOffset At, Ulid Incarnation = default, long Offset = 0);

public readonly record struct LogCut(long Bytes, bool Reached, long? Last, string? Unproven);

/// <summary>
/// The committed changes of one open storage file, in commit order, for continuous backup (TS-633): a log that
/// <see cref="IWalBackup.RollForwardAsync"/> replays on a snapshot. Calls must not overlap. Positions in a backup
/// generation count bytes of this log.
/// </summary>
public interface IWalTap : IAsyncDisposable
{
    ValueTask<WalRead> ReadAsync(Stream destination, CancellationToken cancellationToken);

    ValueTask<WalSnapshot> SnapshotAsync(Stream destination, string snapshotPath, CancellationToken cancellationToken);

    bool Ended => false;
}

/// <summary>Continuous physical backup of this node's storage files (TS-633): one file per database, plus the system database.</summary>
public interface IWalBackup
{
    ValueTask<IWalTap?> OpenTapAsync(TenantContext? database, CancellationToken cancellationToken);

    ValueTask<bool> RollForwardAsync(string file, IAsyncEnumerable<Stream> segments, CancellationToken cancellationToken);

    LogCut CutLog(Stream segment, DateTimeOffset at, long? previous);

    DateTimeOffset? ReadMark(Stream segment);

    ValueTask ImportDatabaseAsync(TenantContext database, string file, CancellationToken cancellationToken);

    ValueTask<byte[]> ChecksumAsync(string file, CancellationToken cancellationToken);
}
