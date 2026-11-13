using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;


namespace TerraBase.Features.Backup;

internal sealed partial class ContinuousBackup
{
    private async Task ShipAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        try
        {
            file.Tap.ThrowIfUnavailable();
            if (file.Database is { } database)
            {
                var registered = await _fileCatalog.RegisteredFileAsync(database, cancellationToken).ConfigureAwait(false);
                if (registered is false)
                {
                    file.Removed = true;
                    file.Side?.Cancellation.Cancel();
                    await file.StopSideAsync().ConfigureAwait(false);
                    await file.Tap.DisposeAsync().ConfigureAwait(false);
                    return;
                }
            }

            if (file.Tap.Ended)
            {
                file.Removed = true;
                await file.Tap.DisposeAsync().ConfigureAwait(false);
                return;
            }

            await EnforceCapAsync(file, cancellationToken).ConfigureAwait(false);
            var due = _time.GetUtcNow() >= file.RetryAt;
            if (!file.Recovered)
            {
                if (!due)
                {
                    return;
                }

                if (file.Target.Revision != (file.Configuration?.Revision ?? BackupDestinationNames.Platform))
                {
                    file.Target = await _targets.ShippingAsync(file.Database, file.Configuration, cancellationToken).ConfigureAwait(false);
                }

                await RecoverAsync(file, cancellationToken).ConfigureAwait(false);
            }

            if (!await HoldAsync(file, cancellationToken).ConfigureAwait(false))
            {
                file.LastError = null;
                return;
            }

            var switched = false;
            if (due && file.Target.Revision != (file.Configuration?.Revision ?? BackupDestinationNames.Platform))
            {
                switched = true;
                file.Side?.Cancellation.Cancel();
                await file.StopSideAsync().ConfigureAwait(false);
                var target = await _targets.ShippingAsync(file.Database, file.Configuration, cancellationToken).ConfigureAwait(false);
                file.Target.Dispose();
                file.Target = target;
                (file.LastShipped, file.LastSnapshot, file.RetainedAt, file.DrilledAt, file.SizeBytes, file.SizedAt) = (null, default, default, default, null, default);
                (file.SnapshotFailures, file.SnapshotRetryAt, file.SnapshotError) = (0, DateTimeOffset.MinValue, null);
            }

            if (!await ContinueAsync(file, cancellationToken).ConfigureAwait(false))
            {
                if (due && (!_stopped || switched))
                {
                    await StartGenerationAsync(file, cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            if (file.Side is { Stored.Task.IsCompleted: true, Collected: false } side)
            {
                side.Collected = true;
                if (side.Snapshot.Generation == file.Generation && !side.Cancellation.IsCancellationRequested)
                {
                    CollectSnapshot(file, side);
                }
            }

            if (due && file.Unstored is not null)
            {
                await PutUnstoredAsync(file, cancellationToken).ConfigureAwait(false);
            }
            else if (!due && (file.Behind || file.Buffered >= MaxPendingBytes))
            {
                return;
            }
            if (due && file.Pending.Length > 0)
            {
                await UploadPendingAsync(file, cancellationToken).ConfigureAwait(false);
            }

            var read = await ReadPendingAsync(file, cancellationToken).ConfigureAwait(false);
            if (!read.Continuous)
            {
                file.Rebase = "log_unplaceable";
                return;
            }

            var now = _time.GetUtcNow();
            if (read.Bytes > 0)
            {
                file.Position += read.Bytes;
                file.PendingAt = now;
                (file.PendingMark, file.PendingEndsMarked) = (read.Mark ?? file.PendingMark, read.Mark is not null);
            }

            file.Behind = read.Remaining;
            if (!due)
            {
                return;
            }

            if (file.Buffered > 0)
            {
                await UploadPendingAsync(file, cancellationToken).ConfigureAwait(false);
            }

            if (file.Generation is not null && now - file.RetainedAt >= RetentionInterval)
            {
                try
                {
                    await RetainAsync(file, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.ZLogWarning(error, $"Retention of {file.Prefix:@File} failed; it retries next hour");
                }
            }
            if (now - file.LastSnapshot >= SnapshotInterval && now >= file.SnapshotRetryAt && !_stopped)
            {
                if (file.Behind)
                {
                    _deferred.Add(1, new KeyValuePair<string, object?>("reason", "backlog"));
                }
                else if (file.Side is { Completion.IsCompleted: false } || !_snapshotSlots.Wait(0, cancellationToken))
                {
                    _deferred.Add(1, new KeyValuePair<string, object?>("reason", "busy"));
                }
                else
                {
                    await SnapshotAsync(file, cancellationToken).ConfigureAwait(false);
                }
            }
            (file.LastError, file.Failures) = (file.SnapshotError, 0);
        }
        catch (ObjectDisposedException) when (file.Tap.Ended)
        {
            file.Removed = true;
            await file.Tap.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            await FailedAsync(file, error, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            file.PendingBytes = file.Buffered;
        }
    }
    private IEnumerable<ShippedFile> Shipped() => _files.Values.Where(file => file.Shipping);
    private async Task<long> WorkspaceAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        var source = file.Database is { } database
            ? await _storage.GetDatabaseSizeAsync(database, cancellationToken).ConfigureAwait(false) ?? throw new IOException("backup source disappeared")
            : new FileInfo(_systemPath).Length + (File.Exists(_systemPath + "-wal") ? new FileInfo(_systemPath + "-wal").Length : 0);
        return DiskBudget.WorkspaceBytes(source);
    }

    private async Task<WalRead> ReadPendingAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        var allowance = await WorkspaceAsync(file, cancellationToken).ConfigureAwait(false);
        var growth = allowance / 4;
        IDisposable? claim = _disk.Reserve(SealedPendingBytes(growth));
        try
        {
            file.Pending.Limit = checked(file.Pending.Length + growth);
            var (read, grew) = await AdvanceAsync(file, (pending, token) => file.Tap.ReadAsync(pending, token), cancellationToken).ConfigureAwait(false);
            if (grew)
            {
                file.PendingClaims.Add(claim);
                claim = null;
            }

            return read;
        }
        finally
        {
            claim?.Dispose();
        }
    }

    private async Task<(T Result, bool Grew)> AdvanceAsync<T>(ShippedFile file, Func<Stream, CancellationToken, ValueTask<T>> read, CancellationToken cancellationToken)
    {
        var retention = (ICdcBackupRetention)file.Tap;
        var cursor = retention.Cursor;
        var before = file.Pending.Length;
        try
        {
            var result = await read(file.Pending, cancellationToken).ConfigureAwait(false);
            var grew = file.Pending.Length > before;
            if (grew || retention.Position != cursor.Position)
            {
                file.PendingCursor = retention.Cursor;
            }

            return (result, grew);
        }
        catch
        {
            file.Pending.SetLength(before);
            file.Pending.Position = before;
            Rewind(file, retention, cursor);
            throw;
        }
    }

    private void Rewind(ShippedFile file, ICdcBackupRetention retention, CdcCursor cursor)
    {
        try
        {
            retention.Rewind(cursor);
        }
        catch (InvalidOperationException error)
        {
            file.Clear();
            file.Resumed = false;
            _logger.ZLogWarning(error, $"Continuous backup of {file.Prefix:@File} could not return its change tap to position {cursor.Position:@Position}; it drops its unstored log and resumes from its stored cursor");
        }
    }

    private async Task UploadPendingAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        await PutUnstoredAsync(file, cancellationToken).ConfigureAwait(false);
        if (file.Pending.Length == 0)
        {
            return;
        }

        var key = BackupLayout.Segment(file.Prefix, file.Generation!.Value, file.PendingStart, file.PendingAt);
        file.Pending.Position = 0;
        BackupScratchStream sealedObject;
        try
        {
            (sealedObject, _) = await SealAsync(file, key, file.Pending, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            file.Pending.Seek(0, SeekOrigin.End);
        }
        file.Unstored = new(key, sealedObject, file.PendingAt, file.PendingMark, file.PendingEndsMarked, file.PendingCursor ?? throw new InvalidOperationException("the pending log has no cursor"), file.Position);
        (file.PendingMark, file.PendingEndsMarked) = (null, false);
        file.Pending.SetLength(0);
        sealedObject.TakeClaims(file.PendingClaims);
        file.PendingStart = file.Position;
        await PutUnstoredAsync(file, cancellationToken).ConfigureAwait(false);
    }

    private async Task PutUnstoredAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        if (file.Unstored is not { } segment)
        {
            return;
        }

        var through = segment.Mark is { } storedMark
            ? segment.EndsMarked && segment.At.ToUnixTimeMilliseconds() == storedMark.ToUnixTimeMilliseconds()
                ? storedMark
                : DateTimeOffset.FromUnixTimeSeconds(storedMark.ToUnixTimeSeconds())
            : file.PlatformProgress?.At;
        var stored = new BackupStored(segment.Cursor, file.Generation!.Value, file.GenerationKey!, segment.Position,
            file.LastSnapshot, segment.At, through);
        await StorePositionAsync(file, stored, segment.Key, segment.Sealed, "segment", cancellationToken).ConfigureAwait(false);

        file.Unstored = null;
        await segment.Sealed.DisposeAsync().ConfigureAwait(false);
        file.LastShipped = segment.At;
        if (segment.Mark is { } mark)
        {
            Prove(file, segment.EndsMarked && segment.At.ToUnixTimeMilliseconds() == mark.ToUnixTimeMilliseconds()
                ? mark
                : DateTimeOffset.FromUnixTimeSeconds(mark.ToUnixTimeSeconds()));
        }
    }
    private async Task<string> StoreAsync(ShippedFile file, string key, string path, string kind, CancellationToken cancellationToken)
    {
        await using var bundle = new FileStream(ScratchFile(".snapshot"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await SnapshotBundle.WriteAsync(path, bundle, cancellationToken).ConfigureAwait(false);
        bundle.Position = 0;
        return await StoreAsync(file, key, bundle, kind, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> StoreAsync(ShippedFile file, string key, Stream content, string kind, CancellationToken cancellationToken)
    {
        var (sealedObject, keyId) = await SealAsync(file, key, content, cancellationToken).ConfigureAwait(false);
        await using (sealedObject.ConfigureAwait(false))
        {
            await PutAsync(file, key, sealedObject, kind, cancellationToken).ConfigureAwait(false);
        }
        return keyId;
    }
    private async Task<(BackupScratchStream Content, string KeyId)> SealAsync(ShippedFile file, string key, Stream content, CancellationToken cancellationToken)
    {
        if (file.GenerationKey is { } generationKey)
        {
            _ = await file.Target.Keys.GetAsync(file.Scope, generationKey, cancellationToken).ConfigureAwait(false);
        }
        var (keyId, dataKey) = await file.Target.Keys.CurrentAsync(file.Scope, cancellationToken).ConfigureAwait(false);
        var bytes = checked(content.Length + (content.Length / (1 << 20) + 1) * 24 + 62);
        var sealedObject = new BackupScratchStream(ScratchFile(".sealed"), null, bytes);
        try
        {
            await BackupObject.WriteAsync(content, sealedObject, key, keyId, dataKey, cancellationToken).ConfigureAwait(false);
            return (sealedObject, keyId);
        }
        catch
        {
            await sealedObject.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task PutAsync(ShippedFile file, string key, FileStream sealedObject, string kind, CancellationToken cancellationToken)
    {
        file.Tap.ThrowIfUnavailable();
        cancellationToken.ThrowIfCancellationRequested();
        if (Unrecorded(file))
        {
            throw new InvalidOperationException("history lost before it was stored is not recorded with the backup yet; nothing after it is uploaded first");
        }

        sealedObject.Position = 0;
        await file.Target.Store.PutAsync(key, sealedObject, cancellationToken).ConfigureAwait(false);
        file.Tap.ThrowIfUnavailable();
        _uploaded.Add(sealedObject.Length, new KeyValuePair<string, object?>("kind", kind));
    }

    private static TimeSpan Backoff(int failures) => TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(failures - 1, 9))));
    private static void ResetGeneration(ShippedFile file)
    {
        file.PlatformProgress = null;
        file.DrilledAt = DateTimeOffset.MinValue;
        file.Generation = null;
        file.GenerationKey = null;
        if (file.Side is { Completion.IsCompleted: false } side)
        {
            side.Cancellation.Cancel();
        }

        file.Clear();
    }
    private static void Prove(ShippedFile file, DateTimeOffset at)
    {
        at = DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds());
        if (!file.Target.IncludesPlatform)
        {
            file.PlatformProgress = null;
        }
        else if (file.PlatformProgress is not { } progress || progress.Generation != file.Generation || progress.At < at)
        {
            file.PlatformProgress = new(file.Generation!.Value, at);
        }
    }

}
