using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Primitives;
using ZLogger;

namespace TerraBase.Features.Backup;

internal sealed record BackupStored(CdcCursor Cursor, Ulid Generation, string GenerationKey, long Position,
    DateTimeOffset SnapshotAt, DateTimeOffset ShippedAt, DateTimeOffset? PlatformAt, string Destination);

internal sealed record BackupUpload(BackupStored Stored, string Key, string Hash);

internal sealed record BackupContinuity(BackupStored? Stored, BackupUpload? Upload, string? EndReason, long Holes, long? HoleEnd);

internal sealed partial class ContinuousBackup
{
    private ICdcBackupRetention Accounting => (ICdcBackupRetention)_files[BackupLayout.File(null, _node)].Tap;

    private async Task SaveContinuityAsync(ShippedFile file, BackupContinuity record, CancellationToken cancellationToken)
    {
        await Accounting.SaveAccountingAsync(file.Prefix,
            JsonSerializer.SerializeToUtf8Bytes(record, BackupJsonContext.Default.BackupContinuity), cancellationToken).ConfigureAwait(false);
        file.Continuity = record;
    }

    private async Task RecoverAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        var store = await _storage.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using (var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false))
        {
            var saved = await transaction.GetAsync("backup_continuity", file.Prefix, cancellationToken).ConfigureAwait(false);
            file.Continuity = saved is null ? new(null, null, null, 0, null)
                : JsonSerializer.Deserialize(saved.Json.Span, BackupJsonContext.Default.BackupContinuity)
                    ?? throw new InvalidDataException("the backup continuity record is empty");
        }

        if (file.Continuity is { Stored: { } prior, EndReason: null } && prior.Destination == file.Target.Revision)
        {
            _ = await ((ICdcBackupRetention)file.Tap).ResumeAsync(prior.Cursor, cancellationToken).ConfigureAwait(false);
        }

        if (file.Continuity.Upload is { } upload && upload.Stored.Destination == file.Target.Revision)
        {
            var found = await file.Target.Store.ReadAsync(upload.Key, async (stream, token) =>
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                if (hash != upload.Hash)
                {
                    throw new InvalidDataException("the interrupted backup upload differs from its durable intent");
                }
            }, cancellationToken).ConfigureAwait(false);
            await SaveContinuityAsync(file, found
                ? file.Continuity with { Stored = upload.Stored, Upload = null, EndReason = null, HoleEnd = null }
                : file.Continuity with { Upload = null }, cancellationToken).ConfigureAwait(false);
        }

        if (file.Continuity is { Stored: { } stored, EndReason: null } && stored.Destination == file.Target.Revision
            && await ((ICdcBackupRetention)file.Tap).ResumeAsync(stored.Cursor, cancellationToken).ConfigureAwait(false))
        {
            (file.Generation, file.GenerationKey, file.Position, file.PendingStart, file.LastSnapshot, file.LastShipped) =
                (stored.Generation, stored.GenerationKey, stored.Position, stored.Position, stored.SnapshotAt, stored.ShippedAt);
            if (stored.PlatformAt is { } at)
            {
                Prove(file, at);
            }
            await ((ICdcBackupRetention)file.Tap).AcknowledgeAsync(stored.Cursor.Position, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await EndContinuityAsync(file, "reopen", cancellationToken).ConfigureAwait(false);
        }

        await ObserveAsync(file, cancellationToken).ConfigureAwait(false);
        file.Recovered = true;
    }

    private async Task StorePositionAsync(ShippedFile file, BackupStored stored, string key, FileStream content, string kind,
        CancellationToken cancellationToken)
    {
        content.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false));
        var record = file.Continuity ?? throw new InvalidOperationException("backup continuity was not recovered");
        await SaveContinuityAsync(file, record with { Upload = new(stored, key, hash) }, cancellationToken).ConfigureAwait(false);
        await PutAsync(file, key, content, kind, cancellationToken).ConfigureAwait(false);
        await SaveContinuityAsync(file, record with { Stored = stored, Upload = null, EndReason = null, HoleEnd = null }, cancellationToken).ConfigureAwait(false);
        await ((ICdcBackupRetention)file.Tap).AcknowledgeAsync(stored.Cursor.Position, cancellationToken).ConfigureAwait(false);
    }

    private async Task ObserveAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        var state = await ((ICdcBackupRetention)file.Tap).StateAsync(file.Continuity?.Stored?.Cursor, cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        if (file.Continuity?.EndReason is null && state.Continuous && state.End == file.Continuity?.Stored?.Cursor.Position)
        {
            file.UnstoredSince = null;
        }
        else if (file.Continuity?.EndReason is not null || !state.Continuous)
        {
            var oldest = state.Oldest ?? now;
            file.UnstoredSince = file.UnstoredSince is { } previous && previous < oldest ? previous : oldest;
        }
        else
        {
            file.UnstoredSince = state.Oldest ?? file.UnstoredSince ?? now;
        }
    }

    private async Task<bool> WithinRetentionAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        var bytes = await ((ICdcBackupRetention)file.Tap).RetainedBytesAsync(cancellationToken).ConfigureAwait(false);
        var limit = MaxPendingBytes;
        try
        {
            using var claim = _disk.Reserve(bytes);
        }
        catch (DiskCapacityException)
        {
            limit = 0;
        }

        if (bytes <= limit)
        {
            return true;
        }

        await EndContinuityAsync(file, "disk", cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async Task EndContinuityAsync(ShippedFile file, string reason, CancellationToken cancellationToken)
    {
        file.Side?.Cancellation.Cancel();
        await file.StopSideAsync().ConfigureAwait(false);
        var retention = (ICdcBackupRetention)file.Tap;
        var record = file.Continuity ?? new BackupContinuity(null, null, null, 0, null);
        var state = await retention.StateAsync(record.Stored?.Cursor, cancellationToken).ConfigureAwait(false);
        var unstored = state.End > (record.Stored?.Cursor.Position ?? 0)
            || record.Stored is not null && !state.Continuous || file.Buffered > 0;
        var lost = record.HoleEnd is null && unstored;
        var ending = unstored || record.EndReason is not null || record.Stored is null
            || record.Stored.Destination != file.Target.Revision || reason is "missing_key" or "shredded";
        if (ending)
        {
            await SaveContinuityAsync(file, record with { Upload = null, EndReason = lost ? reason : record.EndReason ?? reason, Holes = record.Holes + (lost ? 1 : 0), HoleEnd = lost ? state.End : record.HoleEnd }, cancellationToken).ConfigureAwait(false);
        }
        if (lost)
        {
            _holes.Add(1);
            _abandoned.Add(1, new KeyValuePair<string, object?>("reason", reason));
            _logger.ZLogWarning($"Continuous backup of {file.Prefix:@File} ended its generation with unstored changes ({reason:@Reason}); it requires a snapshot");
        }

        if (ending)
        {
            Abandon(file);
        }
        await retention.SkipAsync(state.End, cancellationToken).ConfigureAwait(false);
        await retention.AcknowledgeAsync(state.End, cancellationToken).ConfigureAwait(false);
    }
}
