using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.Management.Backups;

internal interface IBackupSecretLifecycle
{
    ValueTask<BackupSecretWork?> BeginCreateAsync(CancellationToken cancellationToken);
    ValueTask<bool> CanPublishAsync(IStorageTransaction transaction, string intentId, CancellationToken cancellationToken);
    ValueTask CompleteCreateAsync(IStorageTransaction transaction, string intentId, CancellationToken cancellationToken);
    ValueTask<BackupSecretWork?> QueueRetirementAsync(IStorageTransaction transaction, ProjectBackupDestinationRecord record, CancellationToken cancellationToken);
    Task AbandonCreateAsync(string intentId, string? secretId, bool confirmedNotCreated);
    Task RetireCommittedAsync(BackupSecretWork? work);
}

internal sealed class BackupSecretLifecycle(
    SystemCatalog catalog, TerraBaseOptions options, TimeProvider time, IOciVaultSecrets? secrets = null) : BackgroundService, IBackupSecretLifecycle
{
    // Outside ProjectState: deleting a detached project must not discard its retirement work.
    internal const string Collection = "backup_secret_lifecycle";
    internal const int MaxCreates = 1024;
    internal const int MaxRetirements = 1024;
    internal const int MaxPending = MaxCreates + MaxRetirements;
    internal const int MaxRecordBytes = 2048;
    private const int BatchSize = 8;
    private static readonly DocumentFilter RetirementFilter = new CompareFilter(new(["secretName"]), Comparison.Equal, new(ScalarKind.Null, null));
    private static readonly DocumentFilter CreateFilter = new NotFilter(RetirementFilter);
    private static readonly DocumentFilter UnknownIdFilter = new CompareFilter(new(["secretId"]), Comparison.Equal, new(ScalarKind.Null, null));
    private static readonly DocumentFilter KnownIdFilter = new NotFilter(UnknownIdFilter);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);
    private readonly Lock _operations = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private bool _closed;
    private QueryPosition? _knownAfter;
    private QueryPosition? _unknownAfter;

    public ValueTask<BackupSecretWork?> BeginCreateAsync(CancellationToken cancellationToken) =>
        catalog.WriteDurableAsync<BackupSecretWork?>(async transaction =>
        {
            if (!await HasRoomAsync(transaction, creating: true, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var id = Ulid.NewUlid().ToString();
            var work = new BackupSecretWork(1, id, "terrabase-backup-" + id, null, false, time.GetUtcNow().AddMinutes(2));
            await PutWorkAsync(transaction, work, cancellationToken).ConfigureAwait(false);
            return work;
        }, cancellationToken);

    public async ValueTask<bool> CanPublishAsync(IStorageTransaction transaction, string intentId, CancellationToken cancellationToken) =>
        await GetAsync(transaction, Collection, intentId, TerraBaseJsonContext.Default.BackupSecretWork, cancellationToken).ConfigureAwait(false)
            is { Version: 1, Retiring: false };

    public async ValueTask CompleteCreateAsync(IStorageTransaction transaction, string intentId, CancellationToken cancellationToken) =>
        _ = await transaction.DeleteAsync(Collection, intentId, cancellationToken).ConfigureAwait(false);

    public async ValueTask<BackupSecretWork?> QueueRetirementAsync(
        IStorageTransaction transaction, ProjectBackupDestinationRecord record, CancellationToken cancellationToken)
    {
        if (!await HasRoomAsync(transaction, creating: false, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var work = new BackupSecretWork(1, Ulid.NewUlid().ToString(), null, record.SecretId, true, time.GetUtcNow().Add(RetryInterval));
        await PutWorkAsync(transaction, work, cancellationToken).ConfigureAwait(false);
        return work;
    }

    public async Task AbandonCreateAsync(string intentId, string? secretId, bool confirmedNotCreated)
    {
        if (!TryEnter(confirmedNotCreated))
        {
            return;
        }

        try
        {
            using var timeout = OperationTimeout();
            BackupSecretWork? work;
            while (true)
            {
                try
                {
                    work = await catalog.WriteDurableAsync(async transaction =>
                    {
                        var intent = await GetAsync(transaction, Collection, intentId, TerraBaseJsonContext.Default.BackupSecretWork, timeout.Token).ConfigureAwait(false);
                        if (intent is null)
                        {
                            // Publication removed it atomically, or the worker already retired the fenced create.
                            return null;
                        }

                        if (confirmedNotCreated && secretId is null && intent.SecretId is null)
                        {
                            await CompleteCreateAsync(transaction, intentId, timeout.Token).ConfigureAwait(false);
                            return null;
                        }

                        // A known ID keeps this create's reserved slot even when predecessor retirement is full.
                        var abandoned = intent with { Retiring = true, SecretId = secretId ?? intent.SecretId, DueAt = time.GetUtcNow().Add(RetryInterval) };
                        await PutWorkAsync(transaction, abandoned, timeout.Token).ConfigureAwait(false);
                        return abandoned;
                    }, timeout.Token).ConfigureAwait(false);
                    break;
                }
                catch (Exception error) when (confirmedNotCreated && IsAvailabilityFailure(error) && !timeout.IsCancellationRequested)
                {
                    // Retry local failure within the independent cleanup deadline, without admitting another save.
                    await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token).ConfigureAwait(false);
                }
            }
            if (work?.SecretId is not null)
            {
                await ProcessAsync(work, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (IsAvailabilityFailure(error))
        {
            // The original create intent remains durable even if this cleanup update cannot commit.
        }
        finally
        {
            ExitOperation();
        }
    }

    public async Task RetireCommittedAsync(BackupSecretWork? work)
    {
        if (work is null || secrets is null || !TryEnter())
        {
            return;
        }

        try
        {
            using var timeout = OperationTimeout();
            await ProcessAsync(work, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (IsAvailabilityFailure(error))
        {
            // The committed HTTP result survives; the durable row is retried on a later pass.
        }
        finally
        {
            ExitOperation();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.CatalogPrimary is not null || secrets is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(RetryInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await RunBatchAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception error) when (IsAvailabilityFailure(error) && !stoppingToken.IsCancellationRequested)
                {
                    // A failed catalog/Vault pass retains its rows for the next tick.
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown, including an idle timer wait.
        }
    }

    private async Task RunBatchAsync(CancellationToken cancellationToken)
    {
        if (!TryEnter())
        {
            return;
        }

        try
        {
            // Independent cursors keep unresolved names from delaying known retirements.
            await RunClassAsync(known: true, cancellationToken).ConfigureAwait(false);
            await RunClassAsync(known: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExitOperation();
        }
    }

    private async Task RunClassAsync(bool known, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken, deadline.Token);
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            await ProcessBatchAsync(known, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (IsAvailabilityFailure(error) && !cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            // One class can exhaust its budget without consuming the other class's independent deadline.
        }
    }

    private async Task ProcessBatchAsync(bool known, CancellationToken cancellationToken)
    {
        var page = await catalog.ReadAsync(transaction =>
            transaction.QueryAsync(new(Collection, known ? KnownIdFilter : UnknownIdFilter, [], known ? _knownAfter : _unknownAfter,
                BatchSize, BatchSize * MaxRecordBytes), cancellationToken), cancellationToken).ConfigureAwait(false);
        foreach (var row in page.Rows)
        {
            var document = row.Document;
            var after = new QueryPosition(row.Keys, document.Id);
            if (known)
            {
                _knownAfter = after;
            }
            else
            {
                _unknownAfter = after;
            }

            if (document.Json.Length > MaxRecordBytes)
            {
                continue;
            }

            var candidate = JsonSerializer.Deserialize(document.Json.Span, TerraBaseJsonContext.Default.BackupSecretWork);
            if (candidate is not { Version: 1 } || candidate.DueAt > time.GetUtcNow())
            {
                continue;
            }

            try
            {
                // Publication and claiming serialize in this transaction. Time only selects candidates.
                var claimed = await catalog.WriteDurableAsync(async transaction =>
                {
                    var current = await GetAsync(transaction, Collection, candidate.Id, TerraBaseJsonContext.Default.BackupSecretWork, cancellationToken).ConfigureAwait(false);
                    if (current is not { Version: 1 } || current.DueAt > time.GetUtcNow())
                    {
                        return null;
                    }

                    var work = current with { Retiring = true, DueAt = time.GetUtcNow().Add(RetryInterval) };
                    await PutWorkAsync(transaction, work, cancellationToken).ConfigureAwait(false);
                    return work;
                }, cancellationToken).ConfigureAwait(false);
                if (claimed is not null)
                {
                    await ProcessAsync(claimed, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (IsAvailabilityFailure(error) && !cancellationToken.IsCancellationRequested)
            {
                // One unavailable secret must not starve the other rows of the batch.
            }
        }

        if (!page.HasMore)
        {
            if (known)
            {
                _knownAfter = null;
            }
            else
            {
                _unknownAfter = null;
            }
        }
    }

    private async Task ProcessAsync(BackupSecretWork work, CancellationToken cancellationToken)
    {
        var id = work.SecretId ?? (work.SecretName is { } name
            ? await secrets!.FindAsync(name, cancellationToken).ConfigureAwait(false) : null);
        if (id is null)
        {
            // An empty listing does not prove a timed-out create will never appear. Never discard it.
            return;
        }

        if (work.SecretId is null)
        {
            await catalog.WriteDurableAsync(async transaction =>
            {
                var current = await GetAsync(transaction, Collection, work.Id, TerraBaseJsonContext.Default.BackupSecretWork, cancellationToken).ConfigureAwait(false);
                if (current is { Retiring: true })
                {
                    await PutWorkAsync(transaction, current with { SecretId = id }, cancellationToken).ConfigureAwait(false);
                }

                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        await secrets!.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        await catalog.WriteDurableAsync(transaction => transaction.DeleteAsync(Collection, work.Id, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> HasRoomAsync(IStorageTransaction transaction, bool creating, CancellationToken cancellationToken)
    {
        // Named intents retain their admission through reconciliation, independently of predecessor retirement.
        var (count, complete) = await transaction.CountAsync(Collection, creating ? CreateFilter : RetirementFilter, MaxPending, cancellationToken).ConfigureAwait(false);
        return complete && count < (creating ? MaxCreates : MaxRetirements);
    }

    private static async ValueTask PutWorkAsync(IStorageTransaction transaction, BackupSecretWork work, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(work, TerraBaseJsonContext.Default.BackupSecretWork);
        if (json.Length > MaxRecordBytes)
        {
            throw new InvalidDataException("backup secret lifecycle record exceeds its byte limit");
        }

        await transaction.PutAsync(Collection, work.Id, json, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsAvailabilityFailure(Exception error) => error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or BadHttpRequestException
        or StorageBusyException or StorageWriteLimitException or ChangeGroupCapacityException;

    private bool TryEnter(bool confirmedNotCreated = false)
    {
        lock (_operations)
        {
            // Confirmed discards keep their save admission, so at most four can bypass the remote-work limit.
            if (_closed || (!confirmedNotCreated && _active >= 4))
            {
                return false;
            }

            _active++;
            return true;
        }
    }

    private void ExitOperation()
    {
        lock (_operations)
        {
            _active--;
            if (_closed && _active == 0)
            {
                _drained.TrySetResult();
            }
        }
    }

    private CancellationTokenSource OperationTimeout(CancellationToken cancellationToken = default)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return timeout;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_operations)
        {
            _closed = true;
            if (_active == 0)
            {
                _drained.TrySetResult();
            }
        }

        var errors = new List<Exception>();
        try
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        // Host cancellation must not dispose Vault/catalog dependencies while an admitted cleanup uses them.
        await _drained.Task.ConfigureAwait(false);
        if (ExecuteTask is { } execute)
        {
            try
            {
                await execute.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        if (errors.Count != 0)
        {
            throw new AggregateException(errors);
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _shutdown.Dispose();
    }
}
