using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Import;

/// <summary>Queues analysis and copy work for the runner.</summary>
internal interface IImportQueue
{
    /// <summary>Queues analysis (<paramref name="analyze"/>) or a copy of the destination's import, unless one is queued or running.</summary>
    void Ensure(TenantContext database, string name, bool analyze);

    /// <summary>Stops the destination's running analysis or copy at its next step.</summary>
    void Interrupt(TenantContext database);
}

/// <summary>One unit of runner work.</summary>
/// <param name="Database">The destination.</param>
/// <param name="Name">Its name.</param>
internal sealed record ImportWork(TenantContext Database, string Name);

/// <summary>Two queues, analyses and copies, each holding a destination at most once.</summary>
internal sealed class ImportQueue : IImportQueue
{
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);

    public Channel<ImportWork> Analyses { get; } = Channel.CreateUnbounded<ImportWork>();

    public Channel<ImportWork> Copies { get; } = Channel.CreateUnbounded<ImportWork>();

    public void Ensure(TenantContext database, string name, bool analyze)
    {
        if (_pending.TryAdd(Key(database, analyze), 0))
        {
            _ = (analyze ? Analyses : Copies).Writer.TryWrite(new ImportWork(database, name));
        }
    }

    public void Interrupt(TenantContext database)
    {
        foreach (var analyze in new[] { true, false })
        {
            if (_running.TryGetValue(Key(database, analyze), out var running))
            {
                running.Cancel();
            }
        }
    }

    /// <summary>The token of one unit of work: cancelled by <paramref name="stop"/> or by <see cref="Interrupt"/>.</summary>
    public CancellationTokenSource Begin(TenantContext database, bool analyze, CancellationToken stop)
    {
        var running = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _running[Key(database, analyze)] = running;
        return running;
    }

    public void Done(TenantContext database, bool analyze)
    {
        if (_running.TryRemove(Key(database, analyze), out var running))
        {
            running.Dispose();
        }

        _pending.TryRemove(Key(database, analyze), out _);
    }

    private static string Key(TenantContext database, bool analyze) => database.DatabaseId + (analyze ? "/analyze" : "/copy");
}

/// <summary>
/// Runs import work on this node, one analysis and one copy at a time (docs/terrabase/import.md section 7), and sweeps
/// staged sources: at start (which also resumes interrupted jobs) and every hour.
/// </summary>
internal sealed class ImportRunner : BackgroundService
{
    private const int MaxBatchDocuments = 500;

    // A transaction captures at most 2 MiB of names, ids, field states and bodies; each field's stored state costs about
    // its name plus 40 bytes, so a batch stops at 768 KiB by this estimate.
    private const int MaxBatchBytes = 768 * 1024;
    private const int MaxAttempts = 8;

    private readonly ImportQueue _queue;
    private readonly ImportJobs _jobs;
    private readonly ImportStaging _staging;
    private readonly IDocumentService _documents;
    private readonly ITenantDirectory _tenants;
    private readonly TimeProvider _time;
    private readonly ILogger<ImportRunner> _logger;
    private readonly Counter<long> _finished;
    private readonly Counter<long> _written;

    public ImportRunner(
        ImportQueue queue, ImportJobs jobs, ImportStaging staging, IDocumentService documents, ITenantDirectory tenants, TimeProvider time,
        ILogger<ImportRunner> logger, IMeterFactory meters)
    {
        (_queue, _jobs, _staging, _documents, _tenants, _time, _logger) = (queue, jobs, staging, documents, tenants, time, logger);
        var meter = meters.Create(Telemetry.MeterName);
        _finished = meter.CreateCounter<long>("terrabase.import.finished", description: "Import jobs that ended, by outcome: completed, failed or cancelled.");
        _written = meter.CreateCounter<long>("terrabase.import.documents", description: "Documents imported.");
        meter.CreateObservableGauge("terrabase.import.staged", () => _staging.Manifests().Sum(manifest => _staging.Received(manifest.DatabaseId)), "By",
            "Bytes of import sources staged on this node.");
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        Task.Run(() => LoopAsync(_queue.Analyses.Reader, analyze: true, stoppingToken), stoppingToken),
        Task.Run(() => LoopAsync(_queue.Copies.Reader, analyze: false, stoppingToken), stoppingToken),
        Task.Run(() => SweepLoopAsync(stoppingToken), stoppingToken));

    private async Task LoopAsync(ChannelReader<ImportWork> reader, bool analyze, CancellationToken stop)
    {
        try
        {
            await foreach (var work in reader.ReadAllAsync(stop).ConfigureAwait(false))
            {
                var running = _queue.Begin(work.Database, analyze, stop);
                try
                {
                    // Only the node that staged the source works on it.
                    if (_staging.Has(work.Database.DatabaseId!))
                    {
                        await (analyze ? AnalyzeAsync(work, running.Token) : CopyAsync(work, running.Token)).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error)
                {
                    // Nothing a source or a job record holds may stop the runner: the job fails (or is cancelled) and the loop goes on.
                    await EndAsync(work.Database, error, stop).ConfigureAwait(false);
                }
                finally
                {
                    _queue.Done(work.Database, analyze);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private async Task EndAsync(TenantContext database, Exception error, CancellationToken stop)
    {
        try
        {
            if (error is OperationCanceledException)
            {
                Finished(await UpdateAsync(database, current => current.State.CancelRequested
                    ? (current.Job, ImportJobs.Cancelled(current.State, _time.GetUtcNow()))
                    : null, null, stop).ConfigureAwait(false));
                return;
            }

            _logger.ZLogWarning(error, $"An import step stopped; the job is marked failed and can be resumed");
            await FailAsync(database, new ImportError("write_failed", "the import stopped on an unexpected error; resume it", Retryable: true), stop).ConfigureAwait(false);
        }
        catch (Exception recording) when (recording is not OperationCanceledException)
        {
            // The job stays in its state; GET or the next sweep queues it again.
            _logger.ZLogWarning(recording, $"An import's failure could not be recorded");
        }
    }

    private async Task AnalyzeAsync(ImportWork work, CancellationToken stop)
    {
        var database = work.Database;
        if (await ReadAsync(database, stop).ConfigureAwait(false) is not { State.State: ImportStates.Analyzing } records)
        {
            return;
        }

        var job = records.Job;
        if (!await _staging.VerifyAsync(job.DatabaseId, job.SourceBytes, job.SourceSha256, stop).ConfigureAwait(false))
        {
            await UpdateAsync(database, current => current.State.State != ImportStates.Analyzing ? null : (current.Job, current.State with
            {
                State = ImportStates.AwaitingSource,
                Error = new ImportError("source_checksum_mismatch", "the uploaded bytes do not match sourceSha256 and were discarded; upload the same file again", Retryable: true),
            }), () => _staging.Discard(job.DatabaseId), stop).ConfigureAwait(false);
            return;
        }

        var analysis = await Task.Run(() => ImportPlanner.Analyze(_staging.Source(job.DatabaseId), job.Format, job.Options, job.Id, job.CreatedAt, _time, stop), stop)
            .ConfigureAwait(false);
        (ImportJobRecord Job, ImportStateRecord State)? Analyzed(ImportRecords current, ImportPreview? preview, ImportError? error)
        {
            if (current.State.State != ImportStates.Analyzing || current.Job.Id != job.Id)
            {
                return null;
            }

            var analyzed = current.Job with { Preview = preview, PlanDigest = preview is null ? null : analysis.PlanDigest, Checksums = preview is null ? null : analysis.Checksums };
            return current.State.CancelRequested
                ? (analyzed, ImportJobs.Cancelled(current.State, _time.GetUtcNow()))
                : (analyzed, current.State with
                {
                    State = error is null && preview is { Blocking: 0 } ? ImportStates.Ready : ImportStates.Rejected,
                    Error = error,
                });
        }

        if (await UpdateAsync(database, current => Analyzed(current, analysis.Preview, analysis.Error), null, stop).ConfigureAwait(false) is null
            && await ReadAsync(database, stop).ConfigureAwait(false) is { State.State: ImportStates.Analyzing })
        {
            // TerraBase refused the record (too large for one document): the job is rejected instead of analyzing forever.
            _ = await UpdateAsync(database, current => Analyzed(current, null,
                new ImportError("preview_too_large", "the preview cannot be stored; exclude tables or import them separately", Retryable: false)), null, stop)
                .ConfigureAwait(false);
        }
    }

    private async Task CopyAsync(ImportWork work, CancellationToken stop)
    {
        var database = work.Database;
        var started = await UpdateAsync(database, current => current.State switch
        {
            { State: ImportStates.Queued or ImportStates.Running or ImportStates.Validating, CancelRequested: true } =>
                (current.Job, ImportJobs.Cancelled(current.State, _time.GetUtcNow())),
            { State: ImportStates.Queued } => (current.Job, current.State with { State = ImportStates.Running }),
            { State: ImportStates.Running or ImportStates.Validating } => (current.Job, current.State),
            _ => null,
        }, null, stop).ConfigureAwait(false);
        if (started is not { State.State: ImportStates.Running or ImportStates.Validating } records)
        {
            Finished(started);
            return;
        }

        var job = records.Job;
        // The records live in a collection the tenant can write: copy only under a confirmed, unblocked preview and a sane checkpoint.
        if (job.Preview is not { Blocking: 0 } preview || records.State.ConfirmedPreviewId != preview.Id || job.Checksums is null || job.PlanDigest is null
            || records.State.Checkpoint is { } mark && (mark.Position.Offset < 0 || mark.Position.Offset > job.SourceBytes || mark.Position.Table is < 0 or > ImportPlanner.MaxTables
                || mark.Position.Row < 0 || (mark.Position.CopyTable is not null && mark.Position.CopyColumns is null) || mark.Rows is null || mark.Written is null))
        {
            await FailAsync(database, new ImportError("destination_modified", "the import's record was changed outside the import", Retryable: false), stop).ConfigureAwait(false);
            return;
        }

        if (!await _staging.VerifyAsync(job.DatabaseId, job.SourceBytes, job.SourceSha256, stop).ConfigureAwait(false))
        {
            var lost = _staging.Received(job.DatabaseId) != job.SourceBytes;
            if (!lost)
            {
                _staging.Discard(job.DatabaseId);
            }

            await FailAsync(database, new ImportError(lost ? "source_lost" : "source_checksum_mismatch",
                "the staged source is gone or damaged; upload the same file again, then resume", Retryable: true), stop).ConfigureAwait(false);
            return;
        }

        using var source = ImportPlanner.Open(_staging.Source(job.DatabaseId), job.Format, job.Options, stop);
        var plans = ImportPlanner.Plan(source, job.Options, job.Format);
        if (ImportPlanner.Digest(plans, job.Format) != job.PlanDigest)
        {
            // TerraBase changed between preview and copy: analyze again, and the user confirms the new preview.
            var again = await UpdateAsync(database, current => (current.Job with { Preview = null, PlanDigest = null, Checksums = null }, current.State with
            {
                State = ImportStates.Analyzing,
                ConfirmedPreviewId = null,
                Error = new ImportError("preview_changed", "the plan differs from the confirmed preview; confirm the new preview", Retryable: true),
            }), null, stop).ConfigureAwait(false);
            if (again is not null)
            {
                _queue.Ensure(database, work.Name, analyze: true);
            }

            return;
        }

        var included = plans.Where(plan => !plan.Excluded).ToList();
        if (records.State.State == ImportStates.Running)
        {
            if (await CopyRowsAsync(database, records, source, plans, included, stop).ConfigureAwait(false) is not { } copied)
            {
                return;
            }

            records = copied;
        }

        await ValidateAsync(database, records, included, stop).ConfigureAwait(false);
    }

    // Writes every row after the checkpoint in batches committed with the checkpoint; null when the job stopped.
    private async Task<ImportRecords?> CopyRowsAsync(
        TenantContext database, ImportRecords records, IImportSource source, List<TablePlan> plans, List<TablePlan> included, CancellationToken stop)
    {
        foreach (var plan in included)
        {
            switch (await _documents.CreateCollectionAsync(database, plan.Collection, stop).ConfigureAwait(false))
            {
                case NotFound:
                    return null;
                case Unavailable error:
                    await FailAsync(database, new ImportError("write_failed", error.Message, Retryable: true), stop).ConfigureAwait(false);
                    return null;
            }
        }

        var job = records.Job;
        var byTable = plans.ToDictionary(plan => plan.Source);
        var checkpoint = records.State.Checkpoint;
        var rows = new Dictionary<string, long>(checkpoint?.Rows ?? new Dictionary<string, long>(), StringComparer.Ordinal);
        var written = new Dictionary<string, long>(checkpoint?.Written ?? new Dictionary<string, long>(), StringComparer.Ordinal);
        var totals = included.ToDictionary(plan => plan.Source.Display, plan => job.Checksums![plan.Source.Display].Rows, StringComparer.Ordinal);
        var batch = new List<DocumentOperation>(MaxBatchDocuments);
        var targets = new HashSet<(string, string)>();
        var bytes = 0;
        var buffer = new ArrayBufferWriter<byte>(4096);
        var position = checkpoint?.Position;
        string? current = null;
        foreach (var row in source.Rows(checkpoint?.Position, stop))
        {
            if (!byTable.TryGetValue(row.Table, out var plan))
            {
                continue;
            }

            var table = plan.Source.Display;
            var ordinal = rows.GetValueOrDefault(table) + 1;
            if (!plan.Excluded)
            {
                string id;
                byte[] json;
                try
                {
                    (id, json) = plan.Map(row, ordinal, job.Id, job.CreatedAt, buffer);
                }
                catch (ImportValueException error)
                {
                    await FailAsync(database, new ImportError(error.Code, error.Message, Retryable: false, table, ordinal, error.Column), stop).ConfigureAwait(false);
                    return null;
                }

                // Flush before a row that would overflow the batch: the checkpoint then ends exactly before it.
                var cost = json.Length + (id.Length * 2) + plan.Collection.Length + (row.Columns.Count * 64);
                if (batch.Count > 0 && (batch.Count >= MaxBatchDocuments || bytes + cost > MaxBatchBytes))
                {
                    if (await FlushAsync(database, records, batch, position, rows, written, totals, current, stop).ConfigureAwait(false) is not { } next)
                    {
                        return null;
                    }

                    (records, bytes) = (next, 0);
                    batch.Clear();
                    targets.Clear();
                }

                // NDJSON export lines keep their ids, so two lines can name one document.
                if (!targets.Add((plan.Collection, id)))
                {
                    await FailAsync(database, new ImportError("duplicate_document_id", $"two rows map to document id {id}", Retryable: false, table, ordinal), stop)
                        .ConfigureAwait(false);
                    return null;
                }

                batch.Add(new PutOperation(plan.Collection, id, json, Precondition.Absent));
                written[table] = written.GetValueOrDefault(table) + 1;
                bytes += cost;
                current = table;
            }

            rows[table] = ordinal;
            position = row.Resume;
        }

        return await FlushAsync(database, records, batch, position, rows, written, totals, null, stop, validating: true).ConfigureAwait(false);
    }

    // Commits one batch with the checkpoint after it, retrying storage pressure; null when the job stopped.
    private async Task<ImportRecords?> FlushAsync(
        TenantContext database, ImportRecords records, List<DocumentOperation> batch, ImportPosition? position, Dictionary<string, long> rows,
        Dictionary<string, long> written, Dictionary<string, long> totals, string? current, CancellationToken stop, bool validating = false)
    {
        var progress = new ImportProgress(totals.Count(total => written.GetValueOrDefault(total.Key) >= total.Value), totals.Count,
            written.Values.Sum(), totals.Values.Sum(), current);
        var state = records.State with
        {
            State = validating ? ImportStates.Validating : ImportStates.Running,
            Progress = progress,
            Checkpoint = new ImportCheckpoint(position ?? new ImportPosition(0, null, null, 0, 0), new Dictionary<string, long>(rows), new Dictionary<string, long>(written)),
        };
        for (var attempt = 0; ; attempt++)
        {
            WriteImportResult? result = null;
            var gate = _jobs.Lock(database);
            await gate.WaitAsync(stop).ConfigureAwait(false);
            try
            {
                result = await _jobs.WriteAsync(database, records, records.Job, state, batch, stop).ConfigureAwait(false);
            }
            catch (StorageBusyException) when (attempt + 1 < MaxAttempts)
            {
                // Storage pressure (a long writer wait, a full mailbox): retried like a capacity refusal.
            }
            finally
            {
                gate.Release();
            }

            if (result is not { } outcome)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << attempt)), _time, stop).ConfigureAwait(false);
                continue;
            }

            switch (outcome)
            {
                case ImportRecords next:
                    _written.Add(batch.Count);
                    return next;
                case DocumentRejected { Op: { } op } rejected when op < batch.Count && rejected.Error is PreconditionFailed:
                    var put = (PutOperation)batch[op];
                    await FailAsync(database, new ImportError("destination_modified",
                        $"document {put.Id} of collection {put.Collection} already exists: written outside the import, or two source lines name the same id",
                        Retryable: false), stop).ConfigureAwait(false);
                    return null;
                case DocumentRejected { Op: { } op } when op == batch.Count:
                    // The state changed under the copy: a cancel, or a write outside the import.
                    var changed = await UpdateAsync(database, now => now.State.CancelRequested
                        ? (now.Job, ImportJobs.Cancelled(now.State, _time.GetUtcNow()))
                        : (now.Job, Failed(now.State, new ImportError("destination_modified", "the import's record was changed outside the import", Retryable: false))), null, stop)
                        .ConfigureAwait(false);
                    Finished(changed);
                    return null;
                case DocumentRejected { Error: NotFound { Code: "unknown_database" } }:
                    return null;
                case DocumentRejected { Error: QuotaExceeded error }:
                    await FailAsync(database, new ImportError("quota_exceeded", error.Message, Retryable: true), stop).ConfigureAwait(false);
                    return null;
                case DocumentRejected { Error: Unavailable } when attempt + 1 < MaxAttempts:
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << attempt)), _time, stop).ConfigureAwait(false);
                    continue;
                case DocumentRejected { Error: Validation or PayloadTooLarge } rejected:
                    await FailAsync(database, new ImportError("invalid_value", Message(rejected.Error), Retryable: false), stop).ConfigureAwait(false);
                    return null;
                case DocumentRejected { Error: NotFound }:
                    await FailAsync(database, new ImportError("destination_modified", "a collection of the import was dropped during the copy", Retryable: false), stop)
                        .ConfigureAwait(false);
                    return null;
                case DocumentRejected rejected:
                    await FailAsync(database, new ImportError("write_failed", Message(rejected.Error), Retryable: true), stop).ConfigureAwait(false);
                    return null;
            }
        }
    }

    // Reads every imported collection back: its count and checksum must equal the source's.
    private async Task ValidateAsync(TenantContext database, ImportRecords records, List<TablePlan> included, CancellationToken stop)
    {
        var results = new List<ImportTableValidation>(included.Count);
        foreach (var plan in included)
        {
            var expected = records.Job.Checksums![plan.Source.Display];
            var sum = new ulong[4];
            long count = 0;
            QueryPosition? after = null;
            while (true)
            {
                var page = await _documents.QueryAsync(database, new DocumentQuery(plan.Collection, null, [], after, 1000, DocumentRoute.MaxPageBytes), null, stop)
                    .ConfigureAwait(false);
                if (page is not QueryOutcome outcome)
                {
                    if (page is Validation error)
                    {
                        await FailAsync(database, new ImportError("write_failed", error.Message, Retryable: true), stop).ConfigureAwait(false);
                        return;
                    }

                    break;
                }

                foreach (var row in outcome.Page.Rows)
                {
                    count++;
                    ImportMapping.Add(sum, row.Document.Id, row.Document.Json.Span);
                }

                if (!outcome.Page.HasMore || outcome.Page.Rows.Count == 0)
                {
                    break;
                }

                after = new QueryPosition(outcome.Page.Rows[^1].Keys, outcome.Page.Rows[^1].Document.Id);
            }

            var checksum = ImportMapping.Hex(sum);
            results.Add(new(plan.Source.Display, plan.Collection, expected.Rows, count, expected.Checksum, checksum,
                count == expected.Rows && checksum == expected.Checksum));
        }

        var validation = new ImportValidation(results.All(result => result.Match), results);
        var ended = await UpdateAsync(database, current => current.State.State != ImportStates.Validating ? null
            : current.State.CancelRequested ? (current.Job, ImportJobs.Cancelled(current.State, _time.GetUtcNow()) with { Validation = validation })
            : validation.Passed ? (current.Job, current.State with { State = ImportStates.Completed, Validation = validation, FinishedAt = _time.GetUtcNow(), Progress = current.State.Progress! with { CurrentTable = null } })
            : (current.Job, Failed(current.State, new ImportError("validation_failed", "the imported documents do not match the source; see validation", Retryable: false)) with { Validation = validation }), null, stop)
            .ConfigureAwait(false);
        if (ended is { State.State: ImportStates.Completed })
        {
            _staging.Delete(records.Job.DatabaseId);
        }

        Finished(ended);
    }

    private async Task SweepLoopAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), _time);
        do
        {
            try
            {
                await SweepAsync(stop).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _logger.ZLogWarning(error, $"The import staging sweep failed; it retries next hour");
            }
        }
        while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false));
    }

    // Resumes interrupted jobs and deletes staged sources nobody needs: completed jobs, deleted destinations and expired retention.
    private async Task SweepAsync(CancellationToken stop)
    {
        _staging.RemoveOrphans();
        foreach (var manifest in _staging.Manifests())
        {
            var found = await _tenants.FindDatabaseAsync(manifest.OrganizationId, manifest.ProjectId, manifest.Database, stop).ConfigureAwait(false);
            if (found is Unavailable)
            {
                continue;
            }

            if (found is not TenantContext database || database.DatabaseId != manifest.DatabaseId)
            {
                _staging.Delete(manifest.DatabaseId);
                continue;
            }

            var now = _time.GetUtcNow();
            var touched = _staging.LastWrite(manifest.DatabaseId);
            switch (await _jobs.ReadAsync(database, stop).ConfigureAwait(false))
            {
                // Staged bytes are kept only while a job can use them: an upload idle for a day, a preview or a failure
                // left alone for the retention period, and a finished or forgotten import release their room.
                case NotFound:
                case ImportRecords { State.State: ImportStates.Completed }:
                case ImportRecords { State.State: ImportStates.AwaitingSource } when touched is null || touched + ImportStaging.IdleUpload <= now:
                case ImportRecords { State: { State: ImportStates.Ready or ImportStates.Rejected, UpdatedAt: var updated } } when updated + ImportStaging.Retention <= now:
                case ImportRecords { State: { State: ImportStates.Failed or ImportStates.Cancelled, RetainedUntil: { } until } }
                    when until <= now && (touched is null || touched + ImportStaging.Retention <= now):
                    _staging.Delete(manifest.DatabaseId);
                    break;
                case ImportRecords { State.State: ImportStates.Analyzing }:
                    _queue.Ensure(database, manifest.Database, analyze: true);
                    break;
                case ImportRecords { State.State: ImportStates.Queued or ImportStates.Running or ImportStates.Validating }:
                    _queue.Ensure(database, manifest.Database, analyze: false);
                    break;
            }
        }
    }

    private async Task<ImportRecords?> ReadAsync(TenantContext database, CancellationToken stop) =>
        await _jobs.ReadAsync(database, stop).ConfigureAwait(false) switch
        {
            ImportRecords records => records,
            NotFound or Unavailable => null,
        };

    // Applies a change decided from the current records under the destination's lock; null when nothing was written.
    private async Task<ImportRecords?> UpdateAsync(
        TenantContext database, Func<ImportRecords, (ImportJobRecord Job, ImportStateRecord State)?> decide, Action? afterWrite, CancellationToken stop)
    {
        var gate = _jobs.Lock(database);
        await gate.WaitAsync(stop).ConfigureAwait(false);
        try
        {
            if (await ReadAsync(database, stop).ConfigureAwait(false) is not { } records || decide(records) is not { } change)
            {
                return null;
            }

            if (ReferenceEquals(change.State, records.State) && ReferenceEquals(change.Job, records.Job))
            {
                return records;
            }

            var written = await _jobs.WriteAsync(database, records, change.Job, change.State, [], stop).ConfigureAwait(false) switch
            {
                ImportRecords next => next,
                DocumentRejected => null,
            };
            if (written is not null)
            {
                afterWrite?.Invoke();
            }

            return written;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task FailAsync(TenantContext database, ImportError error, CancellationToken stop) =>
        Finished(await UpdateAsync(database, current => current.State.State is ImportStates.Completed or ImportStates.Failed or ImportStates.Cancelled
            ? null
            : current.State.CancelRequested
                ? (current.Job, ImportJobs.Cancelled(current.State, _time.GetUtcNow()))
                : (current.Job, Failed(current.State, error)), null, stop).ConfigureAwait(false));

    private ImportStateRecord Failed(ImportStateRecord state, ImportError error)
    {
        var now = _time.GetUtcNow();
        return state with { State = ImportStates.Failed, Error = error, CancelRequested = false, FinishedAt = now, RetainedUntil = now + ImportStaging.Retention };
    }

    private void Finished(ImportRecords? records)
    {
        if (records?.State.State is ImportStates.Completed or ImportStates.Failed or ImportStates.Cancelled)
        {
            _finished.Add(1, new KeyValuePair<string, object?>("outcome", records.State.State));
        }
    }

    private static string Message(Failure failure) => failure switch
    {
        NotFound error => error.Message ?? "not found",
        Conflict error => error.Message,
        Validation error => error.Message,
        PreconditionFailed error => error.Message,
        Unauthorized error => error.Message ?? "unauthorized",
        Forbidden error => error.Message,
        QuotaExceeded error => error.Message,
        Unavailable error => error.Message,
        PayloadTooLarge error => error.Message,
        UnsupportedMediaType error => error.Message,
        RequestTimeout error => error.Message,
        Gone error => error.Message,
        OutcomeUnknown error => error.Message,
    };
}
