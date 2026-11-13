using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Backup;

public sealed record BackupHole(string Incarnation, long From, long To, DateTimeOffset? FromAt, DateTimeOffset? ToAt, bool Open);

public sealed record BackupContinuity(
    string Incarnation, long? HistoryStart, long? OldestRetained, long? StoredPosition, string? StoredIncarnation, bool Replacing, IReadOnlyList<BackupHole> Holes);

internal sealed record BackupStored(CdcCursor Cursor, Ulid Generation, string GenerationKey, long Position,
    DateTimeOffset SnapshotAt, DateTimeOffset ShippedAt, DateTimeOffset? PlatformAt);

internal sealed record BackupUpload(BackupStored Stored, string Key, string Hash);

internal sealed record BackupAccount(
    string Incarnation, string Destination, BackupStored? Stored, bool Replacing, BackupUpload? Upload, long Holes, IReadOnlyList<BackupHole> Lost);

internal sealed partial class ContinuousBackup
{
    private const string AccountKind = "backup_continuity";

    private const int KeptHoles = 32;

    private ICdcBackupRetention Accounting => (ICdcBackupRetention)_files[BackupLayout.File(null, _node)].Tap;

    public async Task<IReadOnlyList<BackupHole>> HolesAsync(TenantContext database, CancellationToken cancellationToken) =>
        (await ReadAccountAsync(BackupLayout.File(database, _node), cancellationToken).ConfigureAwait(false))?.Lost ?? [];

    private static BackupContinuity? Continuity(ShippedFile file) => file.Account is not { } account ? null
        : new(account.Incarnation,
            file.History is { } history && history.Incarnation == account.Incarnation ? history.Start : null,
            file.History is { } retained && retained.Incarnation == account.Incarnation ? retained.Oldest : null,
            account.Stored?.Cursor.Position, account.Stored?.Cursor.Incarnation, account.Replacing, account.Lost);

    private static long StoredPosition(BackupAccount? account) =>
        account is { Stored: { } stored } && stored.Cursor.Incarnation == account.Incarnation ? stored.Cursor.Position : 0;

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
        var retained = await retention.RetainedAsync(account?.Stored?.Cursor, cancellationToken).ConfigureAwait(false);
        file.History = retained;
        file.BacklogCommittedAt = retained.PendingAt;
        if (retained.Oldest is { } oldest && oldest != retained.Start)
        {
            _logger.ZLogError($"Continuous backup of {file.Prefix:@File} found its change log's recorded start {retained.Start:@Start} differs from its oldest retained change {oldest:@Oldest}; the later of the two decides holes");
        }

        if (account is null)
        {
            await RestartAsync(file, new BackupAccount(retained.Incarnation, file.Target.Revision, null, true, null, 0, []), null, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (account.Incarnation != retained.Incarnation)
        {
            await RestartAsync(file, account with
            {
                Incarnation = retained.Incarnation, Destination = file.Target.Revision, Replacing = true, Upload = null,
                Lost = [.. account.Lost.Select(hole => hole.Open && hole.Incarnation == account.Incarnation ? hole with { Open = false } : hole)],
            }, "lineage", cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (account.Destination != file.Target.Revision)
        {
            await RestartAsync(file, account with { Destination = file.Target.Revision, Stored = null, Replacing = true, Upload = null }, "lineage", cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (account.Stored is { } kept && kept.Cursor.Incarnation == retained.Incarnation && retained.Anchor > kept.Cursor.Position)
        {
            var open = account.Lost.FirstOrDefault(hole => hole.Open && hole.Incarnation == retained.Incarnation && hole.From == kept.Cursor.Position);
            if (open is null)
            {
                var hole = new BackupHole(retained.Incarnation, kept.Cursor.Position, retained.Anchor, Millisecond(kept.PlatformAt ?? kept.ShippedAt), retained.AnchorAt, Open: true);
                await RestartAsync(file, account with { Replacing = true, Upload = null, Holes = account.Holes + 1, Lost = [.. account.Lost.TakeLast(KeptHoles - 1), hole] },
                    null, cancellationToken).ConfigureAwait(false);
                _holesRecorded.Add(1);
                _logger.ZLogWarning($"Continuous backup of {file.Prefix:@File} lost its history from position {hole.From:@From} to {hole.To:@To} of incarnation {hole.Incarnation:@Incarnation} before it was stored; hole {account.Holes + 1:@Holes} is recorded and a new generation starts");
                return false;
            }

            if (retained.Anchor > open.To)
            {
                await SaveAsync(file, account with
                {
                    Lost = [.. account.Lost.Select(hole => ReferenceEquals(hole, open) ? hole with { To = retained.Anchor, ToAt = retained.AnchorAt ?? hole.ToAt } : hole)],
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        if (account.Replacing)
        {
            file.Rebase = null;
            return false;
        }

        var stored = account.Stored ?? throw new InvalidDataException("a backup account that is not replacing its generation has no stored position");
        if (file.Rebase is { } reason)
        {
            await RestartAsync(file, account with { Replacing = true, Upload = null }, reason, cancellationToken).ConfigureAwait(false);
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
        Replacing = false,
        Upload = null,
        Lost = [.. account.Lost.Select(hole => hole.Open && hole.Incarnation == stored.Cursor.Incarnation
            ? hole with { To = stored.Cursor.Position, ToAt = Millisecond(stored.SnapshotAt), Open = false }
            : hole)],
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
            var history = await retention.DiscardAsync(cancellationToken).ConfigureAwait(false);
            _logger.ZLogWarning($"Continuous backup of {file.Prefix:@File} discarded change log not yet stored ({bytes} bytes over the cap); its history now starts at position {history.Start:@Start}, oldest retained change {history.Oldest:@Oldest}");
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
        var stored = StoredPosition(file.Account);
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
