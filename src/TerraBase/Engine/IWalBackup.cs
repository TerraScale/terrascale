using TerraBase.Tenancy;

namespace TerraBase.Engine;

public readonly record struct WalRead(long Bytes, bool Continuous, bool Remaining, DateTimeOffset? Mark);

public readonly record struct WalSnapshot(WalRead Read, DateTimeOffset At, Ulid Incarnation = default, long Offset = 0);

public readonly record struct LogCut(long Bytes, bool Reached, long? Last, string? Unproven);

public interface IWalTap : IAsyncDisposable
{
    CancellationToken Refused { get; }

    void ThrowIfUnavailable();

    ValueTask<WalRead> ReadAsync(Stream destination, CancellationToken cancellationToken);

    ValueTask<WalSnapshot> SnapshotAsync(Stream destination, string snapshotPath, CancellationToken cancellationToken);

    bool Ended => false;
}

public interface IWalBackup
{
    ValueTask<IWalTap?> OpenTapAsync(TenantContext? database, CancellationToken cancellationToken);

    ValueTask<bool> RollForwardAsync(string file, IAsyncEnumerable<Stream> segments, CancellationToken cancellationToken);

    LogCut CutLog(Stream segment, DateTimeOffset at, long? previous);

    DateTimeOffset? ReadMark(Stream segment);

    ValueTask ImportDatabaseAsync(TenantContext database, string file, CancellationToken cancellationToken);

    ValueTask<byte[]> ChecksumAsync(string file, CancellationToken cancellationToken);
}
