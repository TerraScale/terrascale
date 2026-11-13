using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Backup;

public sealed record BackupHole(long From, long To, DateTimeOffset? FromAt, DateTimeOffset? ToAt, bool Open);

internal sealed record BackupStored(CdcCursor Cursor, Ulid Generation, string GenerationKey, long Position,
    DateTimeOffset SnapshotAt, DateTimeOffset ShippedAt, DateTimeOffset? PlatformAt);

internal sealed record BackupUpload(BackupStored Stored, string Key, string Hash);

internal sealed record BackupAccount(string Incarnation, string Destination, BackupStored? Stored, BackupUpload? Upload, long Holes, BackupHole? LastHole);

internal sealed partial class ContinuousBackup
{
    private const string AccountKind = "backup_continuity";

    private ICdcBackupRetention Accounting => (ICdcBackupRetention)_files[BackupLayout.File(null, _node)].Tap;

    public async Task<BackupHole?> HoleAsync(TenantContext database, CancellationToken cancellationToken) =>
        (await ReadAccountAsync(BackupLayout.File(database, _node), cancellationToken).ConfigureAwait(false))?.LastHole;

    private async Task<BackupAccount?> ReadAccountAsync(string prefix, CancellationToken cancellationToken)
    {
        var store = await _storage.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        var saved = await transaction.GetAsync(AccountKind, prefix, cancellationToken).ConfigureAwait(false);
        return saved is null ? null
            : JsonSerializer.Deserialize(saved.Json.Span, BackupJsonContext.Default.BackupAccount) ?? throw new InvalidDataException("the backup account record is empty");
    }

    private async Task SaveAsync(ShippedFile file, BackupAccount account, CancellationToken cancellationToken)
    {
        if (!file.Loaded)
        {
            throw new InvalidOperationException("a backup account is written only after it was loaded");
        }

        await Accounting.SaveAccountingAsync(file.Prefix,
            JsonSerializer.SerializeToUtf8Bytes(account, BackupJsonContext.Default.BackupAccount), cancellationToken).ConfigureAwait(false);
        file.Account = account;
    }

    private async Task RecoverAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        file.Account = await ReadAccountAsync(file.Prefix, cancellationToken).ConfigureAwait(false);
        file.Loaded = true;
        if (file.Account is { Upload: { } upload } account && account.Destination == file.Target.Revision)
        {
            var found = await file.Target.Store.ReadAsync(upload.Key, async (stream, token) =>
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                if (hash != upload.Hash)
                {
                    throw new InvalidDataException("the interrupted backup upload differs from its durable intent");
                }
            }, cancellationToken).ConfigureAwait(false);
            await SaveAsync(file, found ? Stored(account, upload.Stored) : account with { Upload = null }, cancellationToken).ConfigureAwait(false);
        }

        file.Recovered = true;
    }

    private async Task<bool> ContinueAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        var retention = (ICdcBackupRetention)file.Tap;
        var account = file.Account;
        var retained = await retention.RetainedAsync(account?.Stored?.Cursor.Position ?? 0, cancellationToken).ConfigureAwait(false);
        file.BacklogCommittedAt = retained.PendingAt;
        if (account is null)
        {
            await RestartAsync(file, new BackupAccount(retained.Incarnation, file.Target.Revision, null, null, 0, null), null, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (account.Incarnation != retained.Incarnation || account.Destination != file.Target.Revision)
        {
            await RestartAsync(file, account with { Incarnation = retained.Incarnation, Destination = file.Target.Revision, Stored = null, Upload = null },
                "lineage", cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (account.Stored is not { } stored)
        {
            file.Rebase = null;
            return false;
        }

        if (retained.Anchor > stored.Cursor.Position)
        {
            var hole = new BackupHole(stored.Cursor.Position, retained.Anchor, Millisecond(stored.PlatformAt ?? stored.ShippedAt), retained.AnchorAt, Open: true);
            await RestartAsync(file, account with { Stored = null, Upload = null, Holes = account.Holes + 1, LastHole = hole }, null, cancellationToken).ConfigureAwait(false);
            _holesRecorded.Add(1);
            _logger.ZLogWarning($"Continuous backup of {file.Prefix:@File} lost its history from position {hole.From} to {hole.To} before it was stored; hole {account.Holes + 1} is recorded and a new generation starts");
            return false;
        }

        if (file.Rebase is { } reason)
        {
            await RestartAsync(file, account with { Stored = null, Upload = null }, reason, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (!file.Resumed)
        {
            await retention.ResumeAsync(stored.Cursor, cancellationToken).ConfigureAwait(false);
            (file.Generation, file.GenerationKey, file.Position, file.PendingStart, file.LastSnapshot, file.LastShipped) =
                (stored.Generation, stored.GenerationKey, stored.Position, stored.Position, stored.SnapshotAt, stored.ShippedAt);
            if (stored.PlatformAt is { } at)
            {
                Prove(file, at);
            }

            await retention.AcknowledgeAsync(stored.Cursor.Position, cancellationToken).ConfigureAwait(false);
            file.Resumed = true;
        }

        return true;
    }

    private async Task RestartAsync(ShippedFile file, BackupAccount account, string? reason, CancellationToken cancellationToken)
    {
        await SaveAsync(file, account, cancellationToken).ConfigureAwait(false);
        file.Rebase = null;
        file.Resumed = false;
        ResetGeneration(file);
        if (reason is not null)
        {
            _restarts.Add(1, new KeyValuePair<string, object?>("reason", reason));
        }
    }

    private static BackupAccount Stored(BackupAccount account, BackupStored stored) => account with
    {
        Stored = stored,
        Upload = null,
        LastHole = account.LastHole is { Open: true } hole && account.Stored is null
            ? hole with { To = stored.Cursor.Position, ToAt = Millisecond(stored.SnapshotAt), Open = false }
            : account.LastHole,
    };

    private static DateTimeOffset Millisecond(DateTimeOffset at) => DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds());

    private async Task StorePositionAsync(ShippedFile file, BackupStored stored, string key, FileStream content, string kind,
        CancellationToken cancellationToken)
    {
        content.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false));
        var account = file.Account ?? throw new InvalidOperationException("the backup account was not decided");
        await SaveAsync(file, account with { Upload = new(stored, key, hash) }, cancellationToken).ConfigureAwait(false);
        await PutAsync(file, key, content, kind, cancellationToken).ConfigureAwait(false);
        await SaveAsync(file, Stored(account, stored), cancellationToken).ConfigureAwait(false);
        await ((ICdcBackupRetention)file.Tap).AcknowledgeAsync(stored.Cursor.Position, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnforceCapAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        var retention = (ICdcBackupRetention)file.Tap;
        var bytes = await retention.RetainedBytesAsync(cancellationToken).ConfigureAwait(false);
        var limit = MaxPendingBytes;
        try
        {
            using var claim = _disk.Reserve(bytes);
        }
        catch (DiskCapacityException)
        {
            limit = 0;
        }

        if (bytes > limit)
        {
            await retention.DiscardAsync(cancellationToken).ConfigureAwait(false);
            _logger.ZLogWarning($"Continuous backup of {file.Prefix:@File} discarded {bytes} bytes of change log not yet stored; the next pass compares positions");
        }
    }

    private async Task ObserveLagAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(ObservePeriod, _time);
        do
        {
            foreach (var (_, file) in _files)
            {
                if (file.Removed || file.Tap.Ended)
                {
                    continue;
                }

                try
                {
                    Observe(file, await ((ICdcBackupRetention)file.Tap).CommittedAsync(cancellationToken).ConfigureAwait(false));
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.ZLogDebug(error, $"The backup lag of {file.Prefix:@File} could not be observed; its backlog keeps aging");
                }
            }

            Volatile.Write(ref _observedAt, _time.GetTimestamp());
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private void Observe(ShippedFile file, long committed)
    {
        var stored = file.Account?.Stored?.Cursor.Position ?? 0;
        file.Backlog = committed <= stored ? null
            : file.Backlog is { } mark && stored < mark.Position ? mark
            : new BacklogMark(committed, _time.GetTimestamp());
    }

    private double Lag(ShippedFile file) => file.Backlog is { } mark ? _time.GetElapsedTime(mark.ObservedAt).TotalSeconds : 0;

    public bool Healthy
    {
        get
        {
            var observed = Volatile.Read(ref _observedAt);
            if (_responsible is not { } responsible || observed == 0 || _time.GetElapsedTime(observed) > ObservePeriod * 3)
            {
                return false;
            }

            foreach (var prefix in responsible)
            {
                if (!_files.TryGetValue(prefix, out var file))
                {
                    return false;
                }

                if (file.LeaseKnown && !file.Shipping)
                {
                    continue;
                }

                if (!file.Recovered || file.Generation is null || file.LastShipped is null || Lag(file) >= MaxLagSeconds)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
