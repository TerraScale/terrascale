using TerraBase.Engine;
using TerraBase.Tenancy;

namespace TerraBase.Features.Backup;

/// <summary>The times a database can be restored to.</summary>
/// <param name="Earliest">Its oldest snapshot.</param>
/// <param name="Latest">The newest data in the bucket.</param>
public sealed record RestoreWindow(DateTimeOffset Earliest, DateTimeOffset Latest);

/// <summary>Point-in-time restore from continuous backup (TS-633).</summary>
public interface IPointInTimeRestore
{
    /// <summary>The restore window of <paramref name="database"/>; <see langword="null"/> when no backup of it exists.</summary>
    Task<RestoreWindow?> GetWindowAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>
    /// Creates <paramref name="target"/>, a new engine database, holding <paramref name="source"/> as of <paramref name="at"/>:
    /// every transaction the backup read by then. <see langword="false"/> when no backup reaches back to <paramref name="at"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The backup is incomplete or fails authentication or its integrity check.</exception>
    Task<bool> RestoreAsync(TenantContext source, DateTimeOffset at, TenantContext target, CancellationToken cancellationToken);
}

/// <summary>Restores from the newest snapshot at or before the time, rolled forward through its generation's segments.</summary>
internal sealed class PointInTimeRestore(IWalBackup engine, IObjectStore store, BackupReader reader, IContinuousBackup shipper) : IPointInTimeRestore
{
    public async Task<RestoreWindow?> GetWindowAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var file = BackupLayout.File(database);
        SnapshotKey? first = null;
        SnapshotKey? last = null;
        await foreach (var snapshot in reader.SnapshotsAsync(file, cancellationToken).ConfigureAwait(false))
        {
            first ??= snapshot;
            last = snapshot;
        }

        if (first is null || last is null)
        {
            return null;
        }

        // The shipper knows its own progress; otherwise the newest segment after the newest snapshot tells.
        var latest = shipper.LastShipped(database) ?? last.At;
        if (shipper.LastShipped(database) is null)
        {
            await foreach (var item in store.ListAsync(BackupLayout.Segments(file, last.Generation),
                BackupLayout.SegmentsBefore(file, last.Generation, last.Position), cancellationToken).ConfigureAwait(false))
            {
                if (BackupLayout.ParseSegment(item.Key) is { } segment && segment.At > latest)
                {
                    latest = segment.At;
                }
            }
        }

        return new(first.At, latest > last.At ? latest : last.At);
    }

    public async Task<bool> RestoreAsync(TenantContext source, DateTimeOffset at, TenantContext target, CancellationToken cancellationToken)
    {
        var file = BackupLayout.File(source);
        SnapshotKey? chosen = null;
        await foreach (var snapshot in reader.SnapshotsAsync(file, cancellationToken).ConfigureAwait(false))
        {
            if (snapshot.At > at)
            {
                break;
            }

            chosen = snapshot;
        }

        if (chosen is null)
        {
            return false;
        }

        var (restored, _) = await reader.BuildAsync(file, BackupLayout.KeyScope(source), chosen, at, until: null, cancellationToken).ConfigureAwait(false);
        try
        {
            await engine.ImportDatabaseAsync(target, restored, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            BackupReader.Delete(restored);
        }
    }
}
