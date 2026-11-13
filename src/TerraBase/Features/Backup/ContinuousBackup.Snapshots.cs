using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.ExceptionServices;
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
    private void CollectSnapshot(ShippedFile file, SideWork side)
    {
        if (side.Stored!.Task.Exception?.GetBaseException() is { } error)
        {
            if (GenerationLost(error))
            {
                ExceptionDispatchInfo.Throw(error);
            }
            _failures.Add(1);
            file.SnapshotFailures++;
            (file.SnapshotRetryAt, file.SnapshotError) = (_time.GetUtcNow() + Backoff(file.SnapshotFailures), error.GetType().Name);
            _logger.ZLogWarning(error, $"The daily snapshot of {file.Prefix:@File} failed; it retries after a backoff while segments ship");
            return;
        }

        (file.LastSnapshot, file.SnapshotFailures, file.SnapshotRetryAt, file.SnapshotError) = (side.Snapshot.At, 0, DateTimeOffset.MinValue, null);
        Prove(file, side.Snapshot.At);
    }
    private async Task<bool> HoldAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        if (await _shippers.ShipperEpochAsync(file.Database, cancellationToken).ConfigureAwait(false) is not { } epoch)
        {
            if (file.Shipping)
            {
                (file.Shipping, file.Epoch) = (false, null);
                await EndContinuityAsync(file, "lease", cancellationToken).ConfigureAwait(false);
            }

            return false;
        }

        if (file.Epoch != epoch)
        {
            if (file.Epoch is not null)
            {
                await EndContinuityAsync(file, "lease", cancellationToken).ConfigureAwait(false);
            }
            (file.Shipping, file.Epoch) = (true, epoch);
            _logger.ZLogInformation($"This member now ships {file.Prefix:@File}");
        }

        return true;
    }

    internal static long SealedPendingBytes(long growth) => checked(2 * growth + (1L << 20));
    private async Task StartGenerationAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        _ = await file.Target.Keys.CurrentAsync(file.Scope, cancellationToken).ConfigureAwait(false);
        var claim = _disk.Reserve(await WorkspaceAsync(file, cancellationToken).ConfigureAwait(false));
        var generation = EntityIds.New(_time);
        var snapshot = ScratchFile(".db");
        try
        {
            await file.StopSideAsync().ConfigureAwait(false);
            file.Clear();
            var captured = await file.Tap.SnapshotAsync(Stream.Null, snapshot, cancellationToken).ConfigureAwait(false);
            var at = captured.At;
            var key = BackupLayout.Snapshot(file.Prefix, at, generation, 0);
            var live = !_stopped && at - file.DrilledAt >= DrillInterval
                ? await _engine.ChecksumAsync(snapshot, cancellationToken).ConfigureAwait(false)
                : null;
            await StoreGenerationProofAsync(file, generation, key, captured, cancellationToken).ConfigureAwait(false);
            await using var bundle = new FileStream(ScratchFile(".snapshot"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            await SnapshotBundle.WriteAsync(snapshot, bundle, cancellationToken).ConfigureAwait(false);
            bundle.Position = 0;
            var (sealedObject, generationKey) = await SealAsync(file, key, bundle, cancellationToken).ConfigureAwait(false);
            await using (sealedObject.ConfigureAwait(false))
            {
                var cursor = await ((ICdcBackupRetention)file.Tap).CursorAsync(cancellationToken).ConfigureAwait(false);
                await StorePositionAsync(file, new(cursor, generation, generationKey, 0, at, at, at, file.Target.Revision),
                    key, sealedObject, "snapshot", cancellationToken).ConfigureAwait(false);
            }

            (file.Generation, file.GenerationKey, file.Position, file.PendingStart, file.LastSnapshot, file.LastShipped, file.Behind) =
                (generation, generationKey, 0, 0, at, at, false);
            Prove(file, at);
            _generations.Add(1);
            (file.LastError, file.Failures) = (null, 0);
            (file.SnapshotFailures, file.SnapshotRetryAt, file.SnapshotError) = (0, DateTimeOffset.MinValue, null);
            if (live is not null)
            {
                file.DrilledAt = at;
                StartSide(file, new SnapshotKey(key, at, generation, 0), snapshot: null, live, drill: true);
            }
        }
        catch
        {
            await EndContinuityAsync(file, "read", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            DeleteSnapshot(snapshot);
            claim.Dispose();
        }
    }
    private async Task SnapshotAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        var snapshot = ScratchFile(".db");
        var handedOff = false;
        IDisposable? claim = null;
        try
        {
            var allowance = await WorkspaceAsync(file, cancellationToken).ConfigureAwait(false);
            claim = _disk.Reserve(allowance);
            await file.StopSideAsync().ConfigureAwait(false);
            file.PendingClaims.Add(_disk.Reserve(SealedPendingBytes(allowance / 4)));
            file.Pending.Limit = checked(file.Pending.Length + allowance / 4);
            WalSnapshot captured;
            try
            {
                captured = await file.Tap.SnapshotAsync(file.Pending, snapshot, cancellationToken).ConfigureAwait(false);
                file.PendingCursor = await ((ICdcBackupRetention)file.Tap).CursorAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await EndContinuityAsync(file, "read", CancellationToken.None).ConfigureAwait(false);

                throw;
            }

            var read = captured.Read;
            if (!read.Continuous)
            {
                await EndContinuityAsync(file, "read", cancellationToken).ConfigureAwait(false);
                return;
            }

            var at = captured.At;
            file.Position += read.Bytes;
            if (read.Bytes > 0)
            {
                file.PendingAt = at;
                (file.PendingMark, file.PendingEndsMarked) = (read.Mark ?? file.PendingMark, read.Mark is not null);
            }

            if (file.Buffered > 0)
            {
                await UploadPendingAsync(file, cancellationToken).ConfigureAwait(false);
            }
            var generation = file.Generation!.Value;
            var key = new SnapshotKey(BackupLayout.Snapshot(file.Prefix, at, generation, file.Position), at, generation, file.Position);
            var drill = at - file.DrilledAt >= DrillInterval;
            if (drill)
            {
                file.DrilledAt = at;
            }

            StartSide(file, key, snapshot, live: null, drill, claim);
            handedOff = true;
        }
        finally
        {
            if (file.Pending.Length == 0)
            {
                file.ReleasePendingClaims();
            }

            if (!handedOff)
            {
                try
                {
                    DeleteSnapshot(snapshot);
                    claim?.Dispose();
                }
                finally
                {
                    _snapshotSlots.Release();
                }
            }
        }
    }
    private void StartSide(ShippedFile file, SnapshotKey key, string? snapshot, byte[]? live, bool drill, IDisposable? claim = null)
    {
        var side = new SideWork(key, CancellationTokenSource.CreateLinkedTokenSource(
            file.Job?.Caller ?? CancellationToken.None, _jobCancellation.Token, file.Retirement.Token))
        {
            Stored = snapshot is null ? null : new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        file.Side = side;
        side.Completion = Task.Run(() => SideAsync(file, side, snapshot, live, drill, claim), CancellationToken.None);
        if (file.Job is { } job)
        {
            job.Side = side.Completion;
        }
    }

    private async Task SideAsync(ShippedFile file, SideWork side, string? snapshot, byte[]? live, bool drill, IDisposable? claim)
    {
        var token = side.Cancellation.Token;
        if (snapshot is not null)
        {
            try
            {
                live = drill ? await _engine.ChecksumAsync(snapshot, token).ConfigureAwait(false) : null;
                await StoreAsync(file, side.Snapshot.Key, snapshot, "snapshot", token).ConfigureAwait(false);
                side.Stored!.TrySetResult();
            }
            catch (Exception error)
            {
                side.Stored!.TrySetException(error);
                return;
            }
            finally
            {
                try
                {
                    DeleteSnapshot(snapshot);
                    claim?.Dispose();
                }
                finally
                {
                    _snapshotSlots.Release();
                }
            }
        }

        if (live is null)
        {
            return;
        }

        var slot = file.Target.Revision == BackupDestinationNames.Platform ? _drillSlot : _customerDrillSlots;
        await slot.WaitAsync(token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                Task? retained;
                lock (file.RetainGate)
                {
                    retained = file.Retaining?.Task;
                    if (retained is null)
                    {
                        side.Drilling = true;
                        break;
                    }
                }
                await retained.WaitAsync(token).ConfigureAwait(false);
            }
            await DrillAsync(file, side.Snapshot, live, token).ConfigureAwait(false);
        }
        finally
        {
            slot.Release();
        }
    }
}
