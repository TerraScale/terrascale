using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Backup;

public sealed record BackupHole(string Incarnation, long From, long To, DateTimeOffset? FromAt, DateTimeOffset? ToAt, bool Open);

public sealed record BackupHoles(IReadOnlyList<BackupHole> Lost, DateTimeOffset? UnavailableBefore);

public static class BackupHoleExtensions
{
    extension(BackupHole hole)
    {
        public bool Covers(DateTimeOffset at) => (hole.FromAt is not { } from || at > from) && (hole.Open || hole.ToAt is not { } to || at < to);
    }

    extension(IEnumerable<BackupHole> states)
    {
        public IReadOnlyList<BackupHole> Merged() =>
        [
            .. states.GroupBy(state => (state.Incarnation, state.From)).Select(group =>
            {
                var open = group.All(state => state.Open);
                var unknownEnd = group.Any(state => state.ToAt is null && (open || !state.Open));
                return new BackupHole(group.Key.Incarnation, group.Key.From, group.Max(state => state.To),
                    group.Any(state => state.FromAt is null) ? null : group.Min(state => state.FromAt),
                    unknownEnd ? null : group.Max(state => state.ToAt), open);
            }),
        ];
    }
}

public sealed record BackupContinuity(
    string Incarnation, long? HistoryStart, long? OldestRetained, long? StoredPosition, string? StoredIncarnation, bool Replacing, IReadOnlyList<BackupHole> Holes);

internal sealed record BackupStored(CdcCursor Cursor, Ulid Generation, string GenerationKey, long Position,
    DateTimeOffset SnapshotAt, DateTimeOffset ShippedAt, DateTimeOffset? PlatformAt);

internal sealed record BackupUpload(BackupStored Stored, string Key, string Hash);

internal sealed record BackupAccount(
    string Incarnation, string Destination, BackupStored? Stored, bool Replacing, BackupUpload? Upload, long Holes, IReadOnlyList<BackupHole> Lost,
    IReadOnlyList<string> Recorded, DateTimeOffset? UnavailableBefore);

internal sealed partial class ContinuousBackup
{
    private const string AccountKind = "backup_continuity";

    private const int KeptHoles = 32;

    private ICdcBackupRetention Accounting => (ICdcBackupRetention)_files[BackupLayout.File(null, _node)].Tap;

    public async Task<BackupHoles> HolesAsync(TenantContext database, CancellationToken cancellationToken) =>
        await ReadAccountAsync(BackupLayout.File(database, _node), cancellationToken).ConfigureAwait(false) is { } account
            ? new(account.Lost, account.UnavailableBefore)
            : new([], null);

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
            await RestartAsync(file, new BackupAccount(retained.Incarnation, file.Target.Revision, null, true, null, 0, [], [], null), null, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (account.Incarnation != retained.Incarnation)
        {
            await RestartAsync(file, account with
            {
                Incarnation = retained.Incarnation,
                Destination = file.Target.Revision,
                Replacing = true,
                Upload = null,
            }, "lineage", cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (account.Destination != file.Target.Revision)
        {
            await RestartAsync(file, account with { Destination = file.Target.Revision, Stored = null, Replacing = true, Upload = null, Recorded = [] }, "lineage", cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (account.Stored is { } kept && kept.Cursor.Incarnation == retained.Incarnation && retained.Anchor > kept.Cursor.Position)
        {
            var open = account.Lost.FirstOrDefault(hole => hole.Open && hole.Incarnation == retained.Incarnation && hole.From == kept.Cursor.Position);
            if (open is null)
            {
                var hole = new BackupHole(retained.Incarnation, kept.Cursor.Position, retained.Anchor, Millisecond(kept.PlatformAt ?? kept.ShippedAt), retained.AnchorAt, Open: true);
                await RestartAsync(file, Lose(file.Prefix, account with { Replacing = true, Upload = null }, hole), null, cancellationToken).ConfigureAwait(false);
                _holesRecorded.Add(1);
                _logger.ZLogWarning($"Continuous backup of {file.Prefix:@File} lost its history from position {hole.From:@From} to {hole.To:@To} of incarnation {hole.Incarnation:@Incarnation} before it was stored; hole {account.Holes + 1:@Holes} is recorded and a new generation starts");
                return false;
            }

            if (retained.Anchor > open.To)
            {
                await SaveAsync(file, account with
                {
                    Lost = [.. account.Lost.Select(hole => ReferenceEquals(hole, open) ? hole with { To = retained.Anchor, ToAt = retained.AnchorAt is { } at ? Later(hole.ToAt, Millisecond(at)) : null } : hole)],
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
        Lost = [.. account.Lost.Select(hole => !hole.Open ? hole
            : hole.Incarnation == stored.Cursor.Incarnation
                ? hole with { To = stored.Cursor.Position, ToAt = Later(hole.ToAt, Millisecond(stored.SnapshotAt)), Open = false }
                : hole with { ToAt = Later(hole.ToAt, Millisecond(stored.SnapshotAt)), Open = false })],
    };

    private static BackupAccount Lose(string prefix, BackupAccount account, BackupHole hole)
    {
        List<BackupHole> lost = [.. account.Lost, hole];
        var floor = account.UnavailableBefore;
        while (lost.Count > KeptHoles
            && lost.FindIndex(kept => kept is { Open: false, ToAt: not null } && account.Recorded.Contains(BackupLayout.Hole(prefix, kept))) is var index and >= 0)
        {
            floor = Later(floor, lost[index].ToAt);
            lost.RemoveAt(index);
        }

        return account with { Holes = account.Holes + 1, Lost = lost, UnavailableBefore = floor };
    }

    private static DateTimeOffset? Later(DateTimeOffset? first, DateTimeOffset? second) =>
        first is not { } one ? second : second is not { } other ? one : one > other ? one : other;

    private async Task RecordHolesAsync(ShippedFile file, CancellationToken cancellationToken)
    {
        var account = file.Account ?? throw new InvalidOperationException("the backup account was not decided");
        List<string> keys = [.. account.Lost.Select(hole => BackupLayout.Hole(file.Prefix, hole))];
        if (keys.SequenceEqual(account.Recorded))
        {
            return;
        }

        for (var index = 0; index < keys.Count; index++)
        {
            if (!account.Recorded.Contains(keys[index]))
            {
                using var body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(account.Lost[index], BackupJsonContext.Default.BackupHole));
                await file.Target.Store.PutAsync(keys[index], body, cancellationToken).ConfigureAwait(false);
                _uploaded.Add(body.Length, new KeyValuePair<string, object?>("kind", "hole"));
            }
        }

        await SaveAsync(file, account with { Recorded = keys }, cancellationToken).ConfigureAwait(false);
    }

    private static bool Unrecorded(ShippedFile file) => file.Account is { } account
        && account.Lost.Any(hole => !account.Recorded.Any(key => key.StartsWith(BackupLayout.HoleStates(file.Prefix, hole), StringComparison.Ordinal)));

    private static DateTimeOffset Millisecond(DateTimeOffset at) => DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds());

    private async Task StorePositionAsync(ShippedFile file, BackupStored stored, string key, FileStream content, string kind,
        CancellationToken cancellationToken)
    {
        await RecordHolesAsync(file, cancellationToken).ConfigureAwait(false);
        content.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false));
        var account = file.Account ?? throw new InvalidOperationException("the backup account was not decided");
        await SaveAsync(file, account with { Upload = new(stored, key, hash) }, cancellationToken).ConfigureAwait(false);
        await PutAsync(file, key, content, kind, cancellationToken).ConfigureAwait(false);
        await SaveAsync(file, Stored(account, stored), cancellationToken).ConfigureAwait(false);
        await ((ICdcBackupRetention)file.Tap).AcknowledgeAsync(stored.Cursor.Position, cancellationToken).ConfigureAwait(false);
        await RecordHolesAsync(file, cancellationToken).ConfigureAwait(false);
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
