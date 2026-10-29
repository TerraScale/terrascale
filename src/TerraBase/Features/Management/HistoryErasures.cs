using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Features.Platform;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Management;

internal readonly union HistoryErasureResult(HistoryErasureStatus, Conflict, Unavailable);

internal sealed partial class HistoryErasures(
    IStorageEngine engine, IHealthyRewrite rewrites, SystemCatalog catalog, LocalReplica self, TerraBaseOptions options,
    TimeProvider time, IMeterFactory meters, IServiceProvider services, ILogger<HistoryErasures> logger) : BackgroundService
{
    private const int Page = 16;
    private const int MaxBackupObjects = 100_000;

    internal static readonly TenantContext AuthDatabase =
        new(PlatformDatabases.OrganizationId, PlanTier.Enterprise, PlatformDatabases.AuthProjectId, PlatformDatabases.AuthId);

    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly Counter<long> _errors = meters.Create(Telemetry.MeterName)
        .CreateCounter<long>("terrabase.history_erasure.errors", description: "History erasure steps that remain pending, by fixed reason.");
    private string? _after;
    private long _pending;
    private long _activeOverdue;
    private long _backupOverdue;
    private long _passPending;
    private long _passActiveOverdue;
    private long _passBackupOverdue;
    private long _oldestTicks;
    private long _passOldestTicks;
    private bool _metrics;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan MinimumRewriteInterval = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, long> _rewriteStarted = [];
    private RewritePhase _nextRewritePhase;

    private enum RewritePhase { Final, Residue, Retirement }

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);
    private readonly Dictionary<string, (DateTimeOffset Next, TimeSpan Delay)> _backoff = [];

    private string Machine => options.MachineId;

    private ulong Self => self.Id.Value;

    public async Task<HistoryErasureResult> RequestAsync(string erasureId, DateTimeOffset requestedAt, string? token, CancellationToken cancellationToken)
    {
        if (await catalog.CheckAsync(cancellationToken).ConfigureAwait(false) is Unavailable refused)
        {
            return refused;
        }

        requestedAt = requestedAt.ToUniversalTime();
        var record = await catalog.WriteDurableAsync<HistoryErasureRecord?>(async transaction =>
        {
            var existing = await SystemCatalog.GetAsync(transaction, HistoryErasure.Collection, erasureId, TerraBaseJsonContext.Default.HistoryErasureRecord, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return existing;
            }

            var holders = EngineManagementService.CatalogHolders(Machine);
            var created = new HistoryErasureRecord(erasureId, requestedAt, requestedAt.AddDays(30),
                requestedAt.AddDays((options.Backup?.RetentionDays.Values.Max() ?? 0) + 30), [.. holders.Order(StringComparer.Ordinal)], Requester: Machine);
            await SystemCatalog.PutAsync(transaction, HistoryErasure.Collection, erasureId, created, TerraBaseJsonContext.Default.HistoryErasureRecord, cancellationToken).ConfigureAwait(false);
            return created;
        }, static _ => null, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return new Unavailable("the catalog is unavailable", "catalog_unavailable");
        }

        if (record.FormatVersion != 1 || record.RequestedAt != requestedAt)
        {
            return new Conflict("this history erasure exists with another request time", "erasure_conflict");
        }

        if (token is not null)
        {
            return record.Requester != Machine || record.Certified
                ? new Conflict("only the requester's current settlement token finalizes an uncertified erasure", "settlement_stale")
                : await FinalizeAsync(record, token, cancellationToken).ConfigureAwait(false);
        }

        var (status, receipts) = await StatusAsync(record, cancellationToken).ConfigureAwait(false);
        if (status.Status == HistoryErasure.Reverify && status.SettlementToken is null && record.Requester == Machine)
        {
            return await SettleAsync(record, receipts, cancellationToken).ConfigureAwait(false) ?? status with { Status = HistoryErasure.Settling };
        }

        return status;
    }

    private async Task<HistoryErasureStatus?> SettleAsync(HistoryErasureRecord record, IReadOnlyList<HistoryErasureReceipt> receipts, CancellationToken cancellationToken)
    {
        if (!await WithAuthAsync(write: false, (transaction, token) => CoversMarksAsync(transaction, record.ErasureId, receipts, token), cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var marks = MarksDigest(receipts);
        var settled = await catalog.WriteDurableAsync<HistoryErasureRecord?>(async transaction =>
        {
            var current = await GetRecordAsync(transaction, record.ErasureId, cancellationToken).ConfigureAwait(false);
            var now = await ReceiptsAsync(transaction, current, cancellationToken).ConfigureAwait(false);
            if (current is null || MarksDigest(now) != marks || !await CoversSystemAsync(transaction, record.ErasureId, now, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var next = current with { SettlementToken = EntityIds.New(time).ToString(), SettledMarks = Machine + "/" + marks };
            await SystemCatalog.PutAsync(transaction, HistoryErasure.Collection, record.ErasureId, next, TerraBaseJsonContext.Default.HistoryErasureRecord, cancellationToken).ConfigureAwait(false);
            return next;
        }, static _ => null, cancellationToken).ConfigureAwait(false);
        return settled is null ? null : (await StatusAsync(settled, cancellationToken).ConfigureAwait(false)).Status;
    }

    private async Task<HistoryErasureResult> FinalizeAsync(HistoryErasureRecord record, string token, CancellationToken cancellationToken)
    {
        var receipts = await catalog.ReadUncheckedAsync(transaction => ReceiptsAsync(transaction, record, cancellationToken), cancellationToken).ConfigureAwait(false);
        var marks = MarksDigest(receipts);
        if (record.SettlementToken != token || record.SettledMarks != Machine + "/" + marks)
        {
            return new Conflict("the settlement token is not this machine's current one", "settlement_stale");
        }

        var finalId = EntityIds.New(time).ToString();
        var markId = MarkId(record.ErasureId, "final", Machine);
        var auth = await engine.OpenDatabaseAsync(AuthDatabase, cancellationToken).ConfigureAwait(false);
        if (auth is null)
        {
            return new Unavailable("the auth database is not open here", "auth_database_missing");
        }

        ulong cut;
        var transaction = await auth.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            if (!await CoversMarksAsync(transaction, record.ErasureId, receipts, cancellationToken).ConfigureAwait(false))
            {
                return new Conflict("a fence marker is no longer covered here", "settlement_stale");
            }

            var stamp = await PutMarkAsync(transaction, record.ErasureId, "final", markId, finalId, cancellationToken).ConfigureAwait(false);
            cut = stamp;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!await MarkCommittedAsync(auth, markId, cut, cancellationToken).ConfigureAwait(false))
        {
            return new Unavailable("the final marker is not readable after its commit", "final_unproven");
        }

        var finalized = await catalog.WriteDurableAsync<HistoryErasureRecord?>(async system =>
        {
            var current = await GetRecordAsync(system, record.ErasureId, cancellationToken).ConfigureAwait(false);
            var now = await ReceiptsAsync(system, current, cancellationToken).ConfigureAwait(false);
            if (current is null || current.SettlementToken != token || MarksDigest(now) != marks || !await CoversSystemAsync(system, record.ErasureId, now, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            await SystemCatalog.PutAsync(system, HistoryErasure.Finals(record.ErasureId), Machine, new FinalBody(finalId), FinalJson, cancellationToken).ConfigureAwait(false);
            var next = current with
            {
                SettlementToken = null,
                FinalId = finalId,
                FinalMachine = Machine,
                FinalReplica = Self,
                FinalMarks = marks,
                FinalAuthMark = cut,
                BackupCleanAt = null,
                BackupFinalId = null,
            };
            await SystemCatalog.PutAsync(system, HistoryErasure.Collection, record.ErasureId, next, TerraBaseJsonContext.Default.HistoryErasureRecord, cancellationToken).ConfigureAwait(false);
            return next;
        }, static _ => null, cancellationToken).ConfigureAwait(false);
        if (finalized is null)
        {
            return new Conflict("the settlement changed while finalizing", "settlement_stale");
        }

        return (await StatusAsync(finalized, cancellationToken).ConfigureAwait(false)).Status;
    }

    internal async Task<(HistoryErasureStatus Status, IReadOnlyList<HistoryErasureReceipt> Receipts)> StatusAsync(HistoryErasureRecord record, CancellationToken cancellationToken) =>
        await catalog.ReadUncheckedAsync(async transaction =>
        {
            var receipts = await ReceiptsAsync(transaction, record, cancellationToken).ConfigureAwait(false);
            var holders = Holders(record);
            return (Compute(record, holders, receipts), (IReadOnlyList<HistoryErasureReceipt>)receipts);
        }, cancellationToken).ConfigureAwait(false);

    private HistoryErasureStatus Compute(HistoryErasureRecord record, IReadOnlyCollection<string> holders, IReadOnlyList<HistoryErasureReceipt> receipts)
    {
        var byHolder = receipts.ToDictionary(receipt => receipt.MachineId, StringComparer.Ordinal);
        var now = time.GetUtcNow();
        string status;
        int pending;
        string? token = null;
        if (record.Certified)
        {
            pending = record.CertifiedHolders!.Count(holder => byHolder.GetValueOrDefault(holder) is not { Phase: HistoryErasure.RetiredPhase });
            status = pending > 0 ? HistoryErasure.Retire : HistoryErasure.Complete;
        }

        else
        {
            var marks = MarksDigest(receipts);
            var finalCurrent = record.FinalId is not null && record.FinalMarks == marks;
            (status, pending) = holders.Count(holder => !byHolder.ContainsKey(holder)) is var unfenced and > 0 ? (HistoryErasure.Fencing, unfenced)
                : !finalCurrent ? (HistoryErasure.Reverify, 0)
                : holders.Count(holder => byHolder[holder] is not { Phase: HistoryErasure.Rewritten or HistoryErasure.ResidueCleared } receipt || receipt.FinalId != record.FinalId) is var rewriting and > 0
                    ? (HistoryErasure.Rewriting, rewriting)
                : holders.Count(holder => byHolder[holder].Phase != HistoryErasure.ResidueCleared) is var residue and > 0 ? (HistoryErasure.Residue, residue)
                : (HistoryErasure.Backup, 0);
            token = status == HistoryErasure.Reverify && record.Requester == Machine && record.SettledMarks == Machine + "/" + marks ? record.SettlementToken : null;
        }

        return new HistoryErasureStatus(status, record.ErasureId, pending, record.ActiveDueAt, record.BackupDueAt,
            status is HistoryErasure.Fencing or HistoryErasure.Reverify or HistoryErasure.Rewriting && now >= record.ActiveDueAt,
            status is not (HistoryErasure.Complete or HistoryErasure.Retire) && now >= record.BackupDueAt, token);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (stoppingToken.IsCancellationRequested
                && error is ObjectDisposedException)
            {
                break;
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                _errors.Add(1, new KeyValuePair<string, object?>("reason", "pass"));
                logger.ZLogWarning(error, $"A history erasure pass failed; durable work remains pending");
            }
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await _pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RegisterMetrics();
            var page = await catalog.ReadUncheckedAsync(transaction => SystemCatalog.ListAsync(transaction, HistoryErasure.Collection, _after, Page,
                TerraBaseJsonContext.Default.HistoryErasureRecord, static record => record.ErasureId, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (page.Items.Count == 0 && _after is null)
            {
                Interlocked.Exchange(ref _pending, 0);
                Interlocked.Exchange(ref _activeOverdue, 0);
                Interlocked.Exchange(ref _backupOverdue, 0);
                Interlocked.Exchange(ref _oldestTicks, 0);
                return;
            }

            if (await catalog.CheckAsync(cancellationToken).ConfigureAwait(false) is not Done)
            {
                return;
            }

            var records = page.Items.Where(record => record.FormatVersion == 1 && EntityIds.IsValid(record.ErasureId)).ToList();
            var replaced = await LineageChangedAsync(cancellationToken).ConfigureAwait(false);
            List<HistoryErasureRecord> rewrite = [];
            List<HistoryErasureRecord> residue = [];
            List<HistoryErasureRecord> retire = [];
            foreach (var record in records)
            {
                try
                {
                    var (status, receipts) = await StatusAsync(record, cancellationToken).ConfigureAwait(false);
                    var own = receipts.FirstOrDefault(receipt => receipt.MachineId == Machine);
                    if (record.Certified)
                    {
                        if (record.CertifiedHolders!.Contains(Machine, StringComparer.Ordinal) && own is not { Phase: HistoryErasure.RetiredPhase })
                        {
                            retire.Add(record);
                        }
                    }
                    else if (own is null || replaced || own.Replica != Self)
                    {
                        await MarkAsync(record, cancellationToken).ConfigureAwait(false);
                    }
                    else if (record.FinalId is not null && record.FinalMarks == MarksDigest(receipts) && own.FinalId != record.FinalId)
                    {
                        rewrite.Add(record);
                    }
                    else if (record.FinalId is not null && own is { Phase: HistoryErasure.Rewritten } && own.FinalId == record.FinalId)
                    {
                        residue.Add(record);
                    }

                    Count(record, status);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    Count(record, null);
                    _errors.Add(1, new KeyValuePair<string, object?>("reason", "record"));
                    logger.ZLogWarning(error, $"A history erasure step failed; the next pass tries again");
                }
            }

            var first = (int)_nextRewritePhase;
            for (var step = 0; step < 3; step++)
            {
                await ((RewritePhase)((first + step) % 3) switch
                {
                    RewritePhase.Final => RewriteAsync(rewrite, cancellationToken),
                    RewritePhase.Residue => RewriteResidueAsync(residue, cancellationToken),
                    _ => RetireAsync(retire, cancellationToken),
                }).ConfigureAwait(false);
            }
            await VerifyBackupsAsync(records, cancellationToken).ConfigureAwait(false);
            if (page.Next is not null)
            {
                _after = page.Next;
                return;
            }

            Interlocked.Exchange(ref _pending, _passPending);
            Interlocked.Exchange(ref _activeOverdue, _passActiveOverdue);
            Interlocked.Exchange(ref _backupOverdue, _passBackupOverdue);
            Interlocked.Exchange(ref _oldestTicks, _passOldestTicks);
            _after = null;
            _passPending = _passActiveOverdue = _passBackupOverdue = _passOldestTicks = 0;
        }
        finally
        {
            _pass.Release();
        }
    }

    private void Count(HistoryErasureRecord record, HistoryErasureStatus? status)
    {
        if (status?.Status == HistoryErasure.Complete)
        {
            return;
        }

        _passPending++;
        _passOldestTicks = _passOldestTicks == 0 ? record.RequestedAt.UtcTicks : Math.Min(_passOldestTicks, record.RequestedAt.UtcTicks);
        _passActiveOverdue += status?.ActiveOverdue == true ? 1 : 0;
        _passBackupOverdue += status?.BackupOverdue == true ? 1 : 0;
    }

    internal static readonly TimeSpan AlertAge = TimeSpan.FromDays(20);

    private void RegisterMetrics()
    {
        if (_metrics)
        {
            return;
        }

        _metrics = true;
        var meter = meters.Create(Telemetry.MeterName);
        meter.CreateObservableGauge("terrabase.history_erasure.pending", () => Interlocked.Read(ref _pending));
        meter.CreateObservableGauge("terrabase.history_erasure.active_overdue", () => Interlocked.Read(ref _activeOverdue));
        meter.CreateObservableGauge("terrabase.history_erasure.backup_overdue", () => Interlocked.Read(ref _backupOverdue));
        meter.CreateObservableGauge("terrabase.history_erasure.oldest_age", () =>
        {
            var ticks = Interlocked.Read(ref _oldestTicks);
            return ticks == 0 ? 0 : Math.Max(0, (time.GetUtcNow() - new DateTimeOffset(ticks, TimeSpan.Zero)).TotalSeconds);
        }, unit: "s");
    }

    private async Task MarkAsync(HistoryErasureRecord record, CancellationToken cancellationToken)
    {
        var auth = await engine.OpenDatabaseAsync(AuthDatabase, cancellationToken).ConfigureAwait(false);
        if (auth is null || !await auth.HasCollectionAsync(HistoryErasure.MarkCollection, cancellationToken).ConfigureAwait(false))
        {
            _errors.Add(1, new KeyValuePair<string, object?>("reason", "auth_database_missing"));
            return;
        }

        var markId = MarkId(record.ErasureId, HistoryErasure.Fenced, Machine);
        ulong stamp;
        Ulid incarnation;
        var transaction = await auth.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            if (await FenceOfAsync(transaction, record.ErasureId, cancellationToken).ConfigureAwait(false) is not { } fence
                || await transaction.GetVersionAsync(AuthDeletionJobs, fence, cancellationToken).ConfigureAwait(false) is not { Deleted: false })
            {
                return;
            }

            incarnation = await transaction.GetIncarnationAsync(cancellationToken).ConfigureAwait(false);
            stamp = await PutMarkAsync(transaction, record.ErasureId, HistoryErasure.Fenced, markId, null, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!await MarkCommittedAsync(auth, markId, stamp, cancellationToken).ConfigureAwait(false))
        {
            _errors.Add(1, new KeyValuePair<string, object?>("reason", "marker_unproven"));
            return;
        }

        var system = await SystemIncarnationAsync(cancellationToken).ConfigureAwait(false);
        _ = await catalog.WriteDurableAsync(async system_ =>
        {
            await SystemCatalog.PutAsync(system_, HistoryErasure.Receipts(record.ErasureId), Machine,
                new HistoryErasureReceipt(record.ErasureId, Machine, Self, HistoryErasure.Fenced, stamp, incarnation.ToString(), time.GetUtcNow()),
                TerraBaseJsonContext.Default.HistoryErasureReceipt, cancellationToken).ConfigureAwait(false);
            await PutLineageAsync(system_, incarnation, system, cancellationToken).ConfigureAwait(false);
            return true;
        }, static _ => false, cancellationToken).ConfigureAwait(false);
    }

    private async Task RewriteAsync(List<HistoryErasureRecord> ready, CancellationToken cancellationToken)
    {
        if (ready.Count == 0 || Backing(Auth) || Backing(System))
        {
            return;
        }

        var covered = await WithAuthAsync(write: true, async (transaction, token) =>
        {
            List<HistoryErasureRecord> result = [];
            foreach (var record in ready)
            {
                if (await CoversFinalAuthAsync(transaction, record, token).ConfigureAwait(false))
                {
                    result.Add(record);
                }
            }

            return (result, Offset: await transaction.GetOffsetAsync(token).ConfigureAwait(false), Incarnation: await transaction.GetIncarnationAsync(token).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
        if (covered.result.Count == 0)
        {
            return;
        }

        var system = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        long systemOffset;
        Ulid systemIncarnation;
        List<HistoryErasureRecord> both = [];
        var reader = await system.BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            foreach (var record in covered.result)
            {
                if (await CoversFinalSystemAsync(reader, record, cancellationToken).ConfigureAwait(false))
                {
                    both.Add(record);
                }
            }

            systemOffset = await reader.GetOffsetAsync(cancellationToken).ConfigureAwait(false);
            systemIncarnation = await reader.GetIncarnationAsync(cancellationToken).ConfigureAwait(false);
        }

        if (both.Count == 0)
        {
            return;
        }

        if (await TryRewriteAsync(AuthDatabase, Auth, covered.Offset, covered.Incarnation, cancellationToken).ConfigureAwait(false) is not { } a)
        {
            return;
        }

        _nextRewritePhase = RewritePhase.Residue;
        await RecordLineageAsync(auth: a.TargetIncarnation, system: null, cancellationToken).ConfigureAwait(false);
        if (await TryRewriteAsync(null, System, systemOffset, systemIncarnation, cancellationToken).ConfigureAwait(false) is not { } s)
        {
            return;
        }

        _ = await catalog.WriteDurableAsync(async transaction =>
        {
            var now = time.GetUtcNow();
            foreach (var record in both)
            {
                var own = await SystemCatalog.GetAsync(transaction, HistoryErasure.Receipts(record.ErasureId), Machine, TerraBaseJsonContext.Default.HistoryErasureReceipt, cancellationToken).ConfigureAwait(false);
                if (own is null || own.Replica != Self)
                {
                    continue;
                }

                await SystemCatalog.PutAsync(transaction, HistoryErasure.Receipts(record.ErasureId), Machine, own with
                {
                    Phase = HistoryErasure.Rewritten,
                    FinalId = record.FinalId,
                    Auth = Bind(a),
                    System = Bind(s),
                    RewrittenAt = now,
                    Residue = null,
                    ResidueAt = null,
                }, TerraBaseJsonContext.Default.HistoryErasureReceipt, cancellationToken).ConfigureAwait(false);
            }

            await PutLineageAsync(transaction, a.TargetIncarnation, s.TargetIncarnation, cancellationToken).ConfigureAwait(false);
            return true;
        }, static _ => false, cancellationToken).ConfigureAwait(false);
    }

    private async Task RewriteResidueAsync(List<HistoryErasureRecord> ready, CancellationToken cancellationToken)
    {
        if (ready.Count == 0 || Backing(Auth))
        {
            return;
        }

        var clear = await WithAuthAsync(write: true, async (transaction, token) =>
        {
            List<HistoryErasureRecord> result = [];
            foreach (var record in ready)
            {
                if (!await transaction.HoldsHistoryAtOrBelowAsync(new Hlc(record.FinalAuthMark), token).ConfigureAwait(false))
                {
                    result.Add(record);
                }
            }

            return (result, Offset: await transaction.GetOffsetAsync(token).ConfigureAwait(false), Incarnation: await transaction.GetIncarnationAsync(token).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
        if (clear.result.Count == 0 || await TryRewriteAsync(AuthDatabase, Auth, clear.Offset, clear.Incarnation, cancellationToken).ConfigureAwait(false) is not { } auth)
        {
            return;
        }

        _nextRewritePhase = RewritePhase.Retirement;
        await RecordLineageAsync(auth: auth.TargetIncarnation, system: null, cancellationToken).ConfigureAwait(false);
        _ = await catalog.WriteDurableAsync(async transaction =>
        {
            foreach (var record in clear.result)
            {
                var own = await SystemCatalog.GetAsync(transaction, HistoryErasure.Receipts(record.ErasureId), Machine, TerraBaseJsonContext.Default.HistoryErasureReceipt, cancellationToken).ConfigureAwait(false);
                if (own is { Phase: HistoryErasure.Rewritten } && own.FinalId == record.FinalId && own.Replica == Self)
                {
                    await SystemCatalog.PutAsync(transaction, HistoryErasure.Receipts(record.ErasureId), Machine,
                        own with { Phase = HistoryErasure.ResidueCleared, Residue = Bind(auth), ResidueAt = time.GetUtcNow() },
                        TerraBaseJsonContext.Default.HistoryErasureReceipt, cancellationToken).ConfigureAwait(false);
                }
            }

            return true;
        }, static _ => false, cancellationToken).ConfigureAwait(false);
    }

    private async Task RetireAsync(List<HistoryErasureRecord> ready, CancellationToken cancellationToken)
    {
        if (ready.Count == 0 || Backing(Auth))
        {
            return;
        }

        var owns = await catalog.ReadUncheckedAsync(async transaction =>
        {
            var found = new Dictionary<string, HistoryErasureReceipt?>(StringComparer.Ordinal);
            foreach (var record in ready)
            {
                found[record.ErasureId] = await SystemCatalog.GetAsync(transaction, HistoryErasure.Receipts(record.ErasureId), Machine,
                    TerraBaseJsonContext.Default.HistoryErasureReceipt, cancellationToken).ConfigureAwait(false);
            }

            return found;
        }, cancellationToken).ConfigureAwait(false);
        var clear = await WithAuthAsync(write: true, async (transaction, token) =>
        {
            List<(HistoryErasureRecord Record, ulong Cut)> cuts = [];
            List<HistoryErasureRecord> result = [];
            foreach (var record in ready)
            {
                var own = owns[record.ErasureId];
                var cut = own?.RetireCut ?? 0;
                if (cut == 0)
                {
                    var request = await transaction.GetVersionAsync(HistoryErasure.MarkCollection, record.ErasureId, token).ConfigureAwait(false);
                    if (request is { Deleted: false })
                    {
                        continue;
                    }

                    cut = request is { } tombstone ? unchecked((ulong)tombstone.Version) : self.Clock.Tick().Value;
                    cuts.Add((record, cut));
                }

                if (!await transaction.HoldsHistoryAtOrBelowAsync(new Hlc(cut), token).ConfigureAwait(false))
                {
                    result.Add(record);
                }
            }

            return (cuts, result, Offset: await transaction.GetOffsetAsync(token).ConfigureAwait(false), Incarnation: await transaction.GetIncarnationAsync(token).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
        if (clear.cuts.Count > 0)
        {
            _ = await catalog.WriteDurableAsync(async transaction =>
            {
                foreach (var (record, cut) in clear.cuts)
                {
                    var own = owns[record.ErasureId];
                    await SystemCatalog.PutAsync(transaction, HistoryErasure.Receipts(record.ErasureId), Machine,
                        (own ?? new HistoryErasureReceipt(record.ErasureId, Machine, Self, HistoryErasure.ResidueCleared, 0, "", time.GetUtcNow())) with { RetireCut = cut },
                        TerraBaseJsonContext.Default.HistoryErasureReceipt, cancellationToken).ConfigureAwait(false);
                }

                return true;
            }, static _ => false, cancellationToken).ConfigureAwait(false);
        }

        if (clear.result.Count == 0 || await TryRewriteAsync(AuthDatabase, Auth, clear.Offset, clear.Incarnation, cancellationToken).ConfigureAwait(false) is not { } auth)
        {
            return;
        }

        _nextRewritePhase = RewritePhase.Final;
        await RecordLineageAsync(auth: auth.TargetIncarnation, system: null, cancellationToken).ConfigureAwait(false);
        _ = await catalog.WriteDurableAsync(async transaction =>
        {
            foreach (var record in clear.result)
            {
                if (await SystemCatalog.GetAsync(transaction, HistoryErasure.Receipts(record.ErasureId), Machine, TerraBaseJsonContext.Default.HistoryErasureReceipt, cancellationToken).ConfigureAwait(false) is { RetireCut: > 0 } own)
                {
                    await SystemCatalog.PutAsync(transaction, HistoryErasure.Receipts(record.ErasureId), Machine,
                        own with { Phase = HistoryErasure.RetiredPhase, Retired = Bind(auth), RetiredAt = time.GetUtcNow() },
                        TerraBaseJsonContext.Default.HistoryErasureReceipt, cancellationToken).ConfigureAwait(false);
                }
            }

            return true;
        }, static _ => false, cancellationToken).ConfigureAwait(false);
    }

    private const string Auth = "auth";
    private const string System = "system";

    private bool Backing(string file) =>
        _rewriteStarted.TryGetValue(file, out var started) && time.GetElapsedTime(started) < MinimumRewriteInterval
        || _backoff.TryGetValue(file, out var backoff) && time.GetUtcNow() < backoff.Next;

    private async Task<HealthyRewrite?> TryRewriteAsync(TenantContext? database, string file, long offset, Ulid covered, CancellationToken cancellationToken)
    {
        if (Backing(file))
        {
            return null;
        }

        _rewriteStarted[file] = time.GetTimestamp();
        try
        {
            var rewrite = await rewrites.RewriteAsync(database, offset, cancellationToken).ConfigureAwait(false);
            _backoff.Remove(file);
            if (rewrite.SourceIncarnation != covered)
            {
                _errors.Add(1, new KeyValuePair<string, object?>("reason", "rewrite_source_changed"));
                return null;
            }

            return rewrite;
        }
        catch (RewriteRefusedException error)
        {
            var delay = _backoff.TryGetValue(file, out var backoff) ? TimeSpan.FromTicks(Math.Min(backoff.Delay.Ticks * 2, MaxBackoff.Ticks)) : Interval;
            var wait = delay + TimeSpan.FromTicks((long)(delay.Ticks * 0.25 * Random.Shared.NextDouble()));
            _backoff[file] = (time.GetUtcNow() + wait, delay);
            _errors.Add(1, new KeyValuePair<string, object?>("reason", "rewrite_refused"));
            logger.ZLogWarning(error, $"A history erasure rewrite of {file} was refused; it is tried again after {wait}");
            return null;
        }
        catch (RewriteUncertainException error)
        {
            _errors.Add(1, new KeyValuePair<string, object?>("reason", "rewrite_uncertain"));
            logger.ZLogError(error, $"A history erasure rewrite may not be durable; storage failed closed");
            return null;
        }
    }

    private async Task VerifyBackupsAsync(List<HistoryErasureRecord> records, CancellationToken cancellationToken)
    {
        if (services.GetService(typeof(S3ObjectStore)) is not S3ObjectStore store)
        {
            return;
        }

        foreach (var record in records.Where(record => record.Requester == Machine && !record.Certified))
        {
            var (status, receipts) = await StatusAsync(record, cancellationToken).ConfigureAwait(false);
            if (status.Status != HistoryErasure.Backup)
            {
                continue;
            }

            var cut = receipts.Max(receipt => receipt.ResidueAt!.Value);
            var systemCut = receipts.Max(receipt => receipt.RewrittenAt!.Value);
            bool clean;
            bool snapshots;
            try
            {
                clean = await OlderAbsentAsync(store, BackupLayout.File(AuthDatabase), cut, cancellationToken, [.. receipts.Select(receipt => receipt.Residue!)], services.GetRequiredService<DataKeys>(), BackupLayout.KeyScope(AuthDatabase)).ConfigureAwait(false)
                    && await OlderAbsentAsync(store, BackupLayout.File(null), systemCut, cancellationToken, [.. receipts.Select(receipt => receipt.System!)], services.GetRequiredService<DataKeys>(), BackupLayout.KeyScope(null)).ConfigureAwait(false)
                    && !await store.HoldsHiddenVersionsAsync(BackupLayout.File(AuthDatabase) + "/", cut, MaxBackupObjects, cancellationToken).ConfigureAwait(false)
                    && !await store.HoldsHiddenVersionsAsync(BackupLayout.File(null) + "/", systemCut, MaxBackupObjects, cancellationToken).ConfigureAwait(false);
                snapshots = clean && record.BackupCleanAt is { } since && record.BackupFinalId == record.FinalId
                    && await store.SnapshotsEnabledAsync(cancellationToken).ConfigureAwait(false)
                    && (await store.ListSnapshotsAsync(cancellationToken).ConfigureAwait(false)).Any(snapshot => snapshot.At <= since);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                _errors.Add(1, new KeyValuePair<string, object?>("reason", "backup_inventory"));
                continue;
            }

            if (snapshots)
            {
                continue;
            }

            _ = await catalog.WriteDurableAsync(async transaction =>
            {
                var current = await GetRecordAsync(transaction, record.ErasureId, cancellationToken).ConfigureAwait(false);
                if (current is null || current.FinalId != record.FinalId)
                {
                    return false;
                }

                var receipts = await ReceiptsAsync(transaction, current, cancellationToken).ConfigureAwait(false);
                if (Compute(current, Holders(current), receipts).Status != HistoryErasure.Backup)
                {
                    return false;
                }

                var next = !clean ? current with { BackupCleanAt = null, BackupFinalId = null }
                    : current.BackupCleanAt is null || current.BackupFinalId != current.FinalId ? current with { BackupCleanAt = time.GetUtcNow(), BackupFinalId = current.FinalId }
                    : current with
                    {
                        BackupVerifiedAt = time.GetUtcNow(),
                        CertifiedHolders = [.. receipts.Select(receipt => receipt.MachineId).Order(StringComparer.Ordinal)],
                        SettlementToken = null,
                        SettledMarks = null,
                        FinalMarks = null,
                    };
                if (next == current)
                {
                    return false;
                }

                await SystemCatalog.PutAsync(transaction, HistoryErasure.Collection, record.ErasureId, next, TerraBaseJsonContext.Default.HistoryErasureRecord, cancellationToken).ConfigureAwait(false);
                return true;
            }, static _ => false, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<bool> OlderAbsentAsync(IObjectStore store, string file, DateTimeOffset cut, CancellationToken cancellationToken,
        IReadOnlyList<HistoryRewrite>? rewrites = null, DataKeys? keys = null, string? scope = null)
    {
        var seen = 0;
        Dictionary<Ulid, bool> proved = [];
        async Task<bool> CleanGenerationAsync(Ulid generation, bool requireProof = false)
        {
            if (!requireProof && generation.Time >= cut)
            {
                return true;
            }

            if (!proved.TryGetValue(generation, out var clean))
            {
                clean = rewrites is not null && keys is not null && scope is not null
                    && await GenerationCleanAsync(store, file, generation, rewrites, keys, scope, cancellationToken).ConfigureAwait(false);
                proved[generation] = clean;
            }

            return clean;
        }

        await foreach (var item in store.ListAsync(BackupLayout.Snapshots(file), null, cancellationToken).ConfigureAwait(false))
        {
            if (++seen > MaxBackupObjects || BackupLayout.ParseSnapshot(item.Key) is not { } snapshot || !await CleanGenerationAsync(snapshot.Generation, requireProof: snapshot.At < cut).ConfigureAwait(false))
            {
                return false;
            }
        }

        await foreach (var item in store.ListAsync(file + "/wal/", null, cancellationToken).ConfigureAwait(false))
        {
            var generation = item.Key.Length > file.Length + 5 ? item.Key[(file.Length + 5)..].Split('/')[0] : "";
            if (++seen > MaxBackupObjects || !Ulid.TryParse(generation, out var id) || !await CleanGenerationAsync(id).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> LineageChangedAsync(CancellationToken cancellationToken)
    {
        var lineage = await catalog.ReadUncheckedAsync(transaction => SystemCatalog.GetAsync(transaction, HistoryErasure.Lineage, Machine,
            TerraBaseJsonContext.Default.HistoryErasureLineage, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (lineage is null)
        {
            return false;
        }

        var auth = await WithAuthAsync(write: false, (transaction, token) => transaction.GetIncarnationAsync(token), cancellationToken).ConfigureAwait(false);
        return lineage.Auth != auth.ToString() || lineage.System != (await SystemIncarnationAsync(cancellationToken).ConfigureAwait(false)).ToString();
    }

    private async Task RecordLineageAsync(Ulid? auth, Ulid? system, CancellationToken cancellationToken) =>
        _ = await catalog.WriteDurableAsync(async transaction =>
        {
            var lineage = await SystemCatalog.GetAsync(transaction, HistoryErasure.Lineage, Machine, TerraBaseJsonContext.Default.HistoryErasureLineage, cancellationToken).ConfigureAwait(false);
            await PutLineageAsync(transaction, auth ?? Ulid.Parse(lineage?.Auth ?? Ulid.Empty.ToString()),
                system ?? Ulid.Parse(lineage?.System ?? Ulid.Empty.ToString()), cancellationToken).ConfigureAwait(false);
            return true;
        }, static _ => false, cancellationToken).ConfigureAwait(false);

    private async ValueTask PutLineageAsync(IStorageTransaction transaction, Ulid auth, Ulid system, CancellationToken cancellationToken) =>
        await SystemCatalog.PutAsync(transaction, HistoryErasure.Lineage, Machine, new HistoryErasureLineage(Machine, auth.ToString(), system.ToString(), time.GetUtcNow()),
            TerraBaseJsonContext.Default.HistoryErasureLineage, cancellationToken).ConfigureAwait(false);

    private async Task<Ulid> SystemIncarnationAsync(CancellationToken cancellationToken) =>
        await catalog.ReadUncheckedAsync(transaction => transaction.GetIncarnationAsync(cancellationToken), cancellationToken).ConfigureAwait(false);

    private async ValueTask<bool> CoversMarksAsync(IStorageTransaction transaction, string erasureId, IReadOnlyList<HistoryErasureReceipt> receipts, CancellationToken cancellationToken)
    {
        var incarnation = await transaction.GetIncarnationAsync(cancellationToken).ConfigureAwait(false);
        foreach (var receipt in receipts)
        {
            if (receipt.MachineId != Machine || receipt.AuthIncarnation != incarnation.ToString()
                || await transaction.GetVersionAsync(HistoryErasure.MarkCollection, MarkId(erasureId, HistoryErasure.Fenced, Machine), cancellationToken).ConfigureAwait(false)
                    is not { Deleted: false } stored || unchecked((ulong)stored.Version) != receipt.AuthMark)
            {
                return false;
            }
        }
        return receipts.Count == 1;
    }

    private async ValueTask<bool> CoversSystemAsync(IStorageTransaction transaction, string erasureId, List<HistoryErasureReceipt> receipts, CancellationToken cancellationToken)
    {
        if (receipts.Count != 1 || receipts[0].MachineId != Machine)
        {
            return false;
        }
        var lineage = await SystemCatalog.GetAsync(transaction, HistoryErasure.Lineage, Machine,
            TerraBaseJsonContext.Default.HistoryErasureLineage, cancellationToken).ConfigureAwait(false);
        return lineage?.System == (await transaction.GetIncarnationAsync(cancellationToken).ConfigureAwait(false)).ToString()
            && await transaction.GetVersionAsync(HistoryErasure.Receipts(erasureId), Machine, cancellationToken).ConfigureAwait(false) is { Deleted: false };
    }

    private async ValueTask<bool> CoversFinalAuthAsync(IStorageTransaction transaction, HistoryErasureRecord record, CancellationToken cancellationToken)
    {
        if (record.FinalAuthMark == 0 || record.FinalMachine != Machine)
        {
            return false;
        }
        return await transaction.GetVersionAsync(HistoryErasure.MarkCollection, MarkId(record.ErasureId, "final", Machine), cancellationToken).ConfigureAwait(false)
            is { Deleted: false } marker && unchecked((ulong)marker.Version) == record.FinalAuthMark && record.FinalReplica == Self;
    }

    private async ValueTask<bool> CoversFinalSystemAsync(IStorageTransaction transaction, HistoryErasureRecord record, CancellationToken cancellationToken)
    {
        if (record.FinalMachine != Machine)
        {
            return false;
        }
        var marker = await SystemCatalog.GetAsync(transaction, HistoryErasure.Finals(record.ErasureId), Machine, FinalJson, cancellationToken).ConfigureAwait(false);
        return marker is not null && marker.FinalId == record.FinalId;
    }

    private async Task<T> WithAuthAsync<T>(bool write, Func<IStorageTransaction, CancellationToken, ValueTask<T>> read, CancellationToken cancellationToken)
    {
        var auth = await engine.OpenDatabaseAsync(AuthDatabase, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("the auth database is not open on this machine");
        var transaction = await auth.BeginTransactionAsync(write, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return await read(transaction, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<string?> FenceOfAsync(IStorageTransaction transaction, string erasureId, CancellationToken cancellationToken)
    {
        if (await transaction.GetAsync(HistoryErasure.MarkCollection, erasureId, cancellationToken).ConfigureAwait(false) is not { Deleted: false } request)
        {
            return null;
        }

        using var body = JsonDocument.Parse(request.Json);
        return body.RootElement.TryGetProperty("kind", out var kind) && kind.ValueEquals("request")
            && body.RootElement.TryGetProperty("erasureId", out var id) && id.ValueEquals(erasureId)
            && body.RootElement.TryGetProperty("fenceId", out var fence) && fence.GetString() is { } value && EntityIds.IsValid(value)
            ? value : null;
    }

    private static async Task<bool> MarkCommittedAsync(IDatabaseStore auth, string markId, ulong stamp, CancellationToken cancellationToken)
    {
        var transaction = await auth.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return await transaction.GetVersionAsync(HistoryErasure.MarkCollection, markId, cancellationToken).ConfigureAwait(false) is { Deleted: false } version
                && unchecked((ulong)version.Version) == stamp;
        }
    }

    private async ValueTask<ulong> PutMarkAsync(IStorageTransaction transaction, string erasureId, string kind, string markId, string? finalId, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new MarkBody(erasureId, kind, Machine, EntityIds.New(time).ToString(), finalId), MarkJson);
        return unchecked((ulong)await transaction.PutAsync(HistoryErasure.MarkCollection, markId, body, cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask<HistoryErasureRecord?> GetRecordAsync(IStorageTransaction transaction, string erasureId, CancellationToken cancellationToken) =>
        await SystemCatalog.GetAsync(transaction, HistoryErasure.Collection, erasureId, TerraBaseJsonContext.Default.HistoryErasureRecord, cancellationToken).ConfigureAwait(false)
            is { FormatVersion: 1 } record && record.ErasureId == erasureId ? record : null;

    private async ValueTask<List<HistoryErasureReceipt>> ReceiptsAsync(IStorageTransaction transaction, HistoryErasureRecord? record, CancellationToken cancellationToken)
    {
        List<HistoryErasureReceipt> receipts = [];
        if (record is null)
        {
            return receipts;
        }

        foreach (var holder in Holders(record))
        {
            if (await SystemCatalog.GetAsync(transaction, HistoryErasure.Receipts(record.ErasureId), holder, TerraBaseJsonContext.Default.HistoryErasureReceipt, cancellationToken).ConfigureAwait(false)
                is { FormatVersion: 1 } receipt && receipt.MachineId == holder && receipt.ErasureId == record.ErasureId
                && (receipt.Phase == HistoryErasure.Fenced
                    || receipt.Phase is HistoryErasure.Rewritten or HistoryErasure.ResidueCleared && receipt.FinalId is not null && receipt.Auth is not null && receipt.System is not null
                    || receipt.Phase == HistoryErasure.RetiredPhase && receipt.Retired is not null && receipt.RetireCut > 0)
                && (receipt.Phase != HistoryErasure.ResidueCleared || receipt.ResidueAt is not null))
            {
                receipts.Add(receipt);
            }
        }

        return receipts;
    }

    private HashSet<string> Holders(HistoryErasureRecord record)
    {
        var holders = EngineManagementService.CatalogHolders(Machine);
        holders.UnionWith(record.ExpectedHolders);
        return holders;
    }

    internal static string MarksDigest(IEnumerable<HistoryErasureReceipt> receipts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
            receipts.OrderBy(receipt => receipt.MachineId, StringComparer.Ordinal).Select(receipt => $"{receipt.MachineId}|{receipt.Replica}|{receipt.AuthMark}")))));

    internal static string MarkId(string erasureId, string kind, string machine) =>
        new Ulid(SHA256.HashData(Encoding.UTF8.GetBytes($"ts873/{erasureId}/{kind}/{machine}")).AsSpan(0, 16)).ToString();

    private static HistoryRewrite Bind(HealthyRewrite rewrite) => new(rewrite.SourceIncarnation.ToString(), rewrite.TargetIncarnation.ToString(), rewrite.Offset);

    private const string AuthDeletionJobs = "deletion_jobs";

    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<MarkBody> MarkJson = HistoryErasureJson.Default.MarkBody;

    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<FinalBody> FinalJson = HistoryErasureJson.Default.FinalBody;

    public override void Dispose()
    {
        _pass.Dispose();
        base.Dispose();
    }
}

internal sealed record MarkBody(string ErasureId, string Kind, string Machine, string Nonce, string? FinalId);

internal sealed record FinalBody(string FinalId);

[System.Text.Json.Serialization.JsonSerializable(typeof(MarkBody))]
[System.Text.Json.Serialization.JsonSerializable(typeof(FinalBody))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
internal sealed partial class HistoryErasureJson : System.Text.Json.Serialization.JsonSerializerContext;
