using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Import;

/// <summary>A job answer of an import route, or why there is none.</summary>
public readonly union ImportResult(ImportJobBody, NotFound, Conflict, Validation, PayloadTooLarge, Unavailable);

/// <summary>A job <c>createImport</c> attached (<c>201</c>).</summary>
/// <param name="Job">The new job.</param>
public sealed record ImportCreated(ImportJobBody Job);

/// <summary>A created job, the same job for an identical replay (<c>200</c>), or why there is none.</summary>
public readonly union CreateImportResult(ImportCreated, ImportJobBody, NotFound, Conflict, Validation, PayloadTooLarge, Unavailable);

/// <summary>Import jobs of destinations served by this node (docs/terrabase/import.md).</summary>
public interface IImportJobs
{
    /// <summary>Attaches an import to an empty destination, or answers an identical replay with the same job.</summary>
    ValueTask<CreateImportResult> CreateAsync(TenantContext database, string name, CreateImportRequest request, CancellationToken cancellationToken);

    /// <summary>Reads the destination's import.</summary>
    ValueTask<ImportResult> GetAsync(TenantContext database, string name, CancellationToken cancellationToken);

    /// <summary>Stages one part of the source at <paramref name="offset"/>.</summary>
    ValueTask<ImportResult> UploadAsync(TenantContext database, string name, long offset, long length, Stream body, CancellationToken cancellationToken);

    /// <summary>Sets new options and analyzes the source again.</summary>
    ValueTask<ImportResult> UpdateAsync(TenantContext database, string name, UpdateImportRequest request, CancellationToken cancellationToken);

    /// <summary>Confirms the current preview and queues the copy.</summary>
    ValueTask<ImportResult> StartAsync(TenantContext database, string name, StartImportRequest request, CancellationToken cancellationToken);

    /// <summary>Cancels the import.</summary>
    ValueTask<ImportResult> CancelAsync(TenantContext database, string name, CancellationToken cancellationToken);

    /// <summary>Resumes a failed or cancelled import.</summary>
    ValueTask<ImportResult> ResumeAsync(TenantContext database, string name, CancellationToken cancellationToken);
}

/// <summary>An import's two records and the revision its next write asserts.</summary>
internal sealed record ImportRecords(ImportJobRecord Job, ImportStateRecord State);

/// <summary>The result of reading an import's records.</summary>
internal readonly union ReadImportResult(ImportRecords, NotFound, Unavailable);

/// <summary>The result of writing an import's records.</summary>
internal readonly union WriteImportResult(ImportRecords, DocumentRejected);

internal sealed class ImportJobs(IDocumentService documents, ImportStaging staging, IImportQueue queue, TerraBaseOptions options, TimeProvider time) : IImportJobs
{
    public const int MaxPart = DocumentRoute.MaxRequestBytes;

    internal static readonly NotFound UnknownImport = new("the database has no import", "unknown_import");

    private static readonly Validation BadFormat = new("format must be postgres_plain, sqlite or ndjson");
    // One lock per destination that ever had an import on this node.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <summary>The per-destination lock every change of an import takes on this node.</summary>
    public SemaphoreSlim Lock(TenantContext database) => _locks.GetOrAdd(database.DatabaseId!, _ => new SemaphoreSlim(1, 1));

    public async ValueTask<CreateImportResult> CreateAsync(TenantContext database, string name, CreateImportRequest request, CancellationToken cancellationToken)
    {
        if (request.Format is not (ImportFormats.PostgresPlain or ImportFormats.Sqlite or ImportFormats.Ndjson))
        {
            return BadFormat;
        }

        if (request.SourceBytes is not { } bytes || bytes < 1)
        {
            return new Validation("sourceBytes must be a positive size");
        }

        if (bytes > ImportStaging.MaxSourceBytes)
        {
            return new PayloadTooLarge("a source is at most 1 GiB");
        }

        if (request.SourceSha256 is not { Length: 64 } sha || !sha.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            return new Validation("sourceSha256 must be 64 lowercase hex characters");
        }

        var checkedOptions = Normalize(request.Options);
        if (checkedOptions is Validation invalid)
        {
            return invalid;
        }

        var normalized = checkedOptions switch
        {
            ImportOptionsBody value => value,
            Validation => throw new UnreachableException(),
        };

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0',
            [database.ProjectId!, database.DatabaseId!, request.Format, bytes.ToString(System.Globalization.CultureInfo.InvariantCulture), sha,
            normalized.Collection ?? "", .. normalized.Exclude!]))));
        var gate = Lock(database);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            switch (await ReadAsync(database, cancellationToken).ConfigureAwait(false))
            {
                case ImportRecords existing:
                    return existing.Job.RequestDigest == digest ? Body(existing)
                        : new Conflict("the database already has a different import", "import_exists");
                case Unavailable error:
                    return error;
            }

            // Empty, or holding only the record collection of a create that failed before its first write.
            switch (await documents.ListCollectionsAsync(database, null, 2, cancellationToken).ConfigureAwait(false))
            {
                case NotFound:
                    return DocumentRoute.UnknownDatabase;
                case CollectionPage { Collections: [{ Name: ImportNames.Collection }] }:
                    break;
                case CollectionPage { Collections.Count: > 0 }:
                    return new Conflict("an import needs a new, empty database; create one with createDatabase", "destination_not_empty");
                default:
                    switch (await documents.CreateCollectionAsync(database, ImportNames.Collection, cancellationToken).ConfigureAwait(false))
                    {
                        case NotFound:
                            return DocumentRoute.UnknownDatabase;
                        case Unavailable error:
                            return error;
                    }

                    break;
            }

            var now = time.GetUtcNow();
            var job = new ImportJobRecord(1, EntityIds.New(time).ToString(), database.ProjectId!, database.DatabaseId!, name, options.Region, request.Format, bytes, sha,
                normalized, digest, now, null, null, null);
            var state = new ImportStateRecord(1, "", ImportStates.AwaitingSource, false, null, null, null, null, null, now, null, null, null);
            return await WriteAsync(database, null, job, state, [], cancellationToken).ConfigureAwait(false) switch
            {
                ImportRecords written => new ImportCreated(Body(written)),
                DocumentRejected rejected => Failure(rejected) switch
                {
                    NotFound error => error,
                    Conflict error => error,
                    Validation error => error,
                    PayloadTooLarge error => error,
                    Unavailable error => error,
                    ImportJobBody => throw new UnreachableException(),
                },
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<ImportResult> GetAsync(TenantContext database, string name, CancellationToken cancellationToken)
    {
        var gate = Lock(database);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // State and staged bytes form one response: a checksum retry persists awaiting_source
            // before discarding the source, under this same gate. Never expose that intermediate state.
            var read = await ReadAsync(database, cancellationToken).ConfigureAwait(false);
            if (read is ImportRecords records && records.State.State is ImportStates.Analyzing or ImportStates.Queued or ImportStates.Running or ImportStates.Validating
                && staging.Has(records.Job.DatabaseId))
            {
                // A job whose runner is gone (another machine, a lost volume) gets picked up again here.
                queue.Ensure(database, name, records.State.State == ImportStates.Analyzing);
            }

            return read switch
            {
                ImportRecords found => Body(found),
                NotFound error => error,
                Unavailable error => error,
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<ImportResult> UploadAsync(TenantContext database, string name, long offset, long length, Stream body, CancellationToken cancellationToken)
    {
        if (length is < 1 or > MaxPart)
        {
            return length > MaxPart ? new PayloadTooLarge("an upload part is at most 4 MiB") : new Validation("an upload part needs Content-Length of at least 1 byte");
        }

        var gate = Lock(database);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = await ReadAsync(database, cancellationToken).ConfigureAwait(false);
            if (read is not ImportRecords records)
            {
                return read switch
                {
                    NotFound error => error,
                    Unavailable error => error,
                    ImportRecords => throw new UnreachableException(),
                };
            }

            var (job, state) = (records.Job, records.State);
            var received = staging.Received(job.DatabaseId);
            var open = state.State == ImportStates.AwaitingSource
                || (state.State is ImportStates.Failed or ImportStates.Cancelled && received < job.SourceBytes);
            if (!open)
            {
                return InvalidState(state.State);
            }

            if (offset + length <= received)
            {
                // A replay of the completing part also completes the transition a failed write left undone.
                return received == job.SourceBytes && state.State == ImportStates.AwaitingSource
                    ? await CompleteAsync(database, name, records, cancellationToken).ConfigureAwait(false)
                    : Body(records);
            }

            if (offset != received)
            {
                return new Conflict($"the next part starts at offset {received}", "offset_mismatch");
            }

            if (offset + length > job.SourceBytes)
            {
                return new PayloadTooLarge("the part extends past sourceBytes");
            }

            if (!staging.Reserve(new ImportManifest(database.OrganizationId, database.ProjectId!, database.DatabaseId!, name, job.SourceBytes)))
            {
                return new Unavailable("no room to stage the source on this node now; retry later", "capacity");
            }

            _ = await staging.AppendAsync(job.DatabaseId, body, length, cancellationToken).ConfigureAwait(false);
            return staging.Received(job.DatabaseId) < job.SourceBytes || state.State != ImportStates.AwaitingSource
                ? Body(records)
                : await CompleteAsync(database, name, records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<ImportResult> CompleteAsync(TenantContext database, string name, ImportRecords records, CancellationToken cancellationToken) =>
        await WriteStateAsync(database, records, records.State with { State = ImportStates.Analyzing, Error = null }, cancellationToken).ConfigureAwait(false) switch
        {
            ImportRecords written => Queued(written, database, name, analyze: true),
            DocumentRejected rejected => Failure(rejected),
        };

    public async ValueTask<ImportResult> UpdateAsync(TenantContext database, string name, UpdateImportRequest request, CancellationToken cancellationToken)
    {
        var checkedOptions = Normalize(request.Options);
        if (checkedOptions is Validation invalid)
        {
            return invalid;
        }

        var normalized = checkedOptions switch
        {
            ImportOptionsBody value => value,
            Validation => throw new UnreachableException(),
        };
        return await ChangeAsync(database, name, records => records.State.State is ImportStates.Ready or ImportStates.Rejected
            ? new Change(records.Job with { Options = normalized, Preview = null, PlanDigest = null, Checksums = null },
                records.State with { State = ImportStates.Analyzing, Error = null }, Analyze: true)
            : InvalidState(records.State.State), cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ImportResult> StartAsync(TenantContext database, string name, StartImportRequest request, CancellationToken cancellationToken) =>
        ChangeAsync(database, name, records =>
        {
            var (job, state) = (records.Job, records.State);
            if (state.State is ImportStates.Queued or ImportStates.Running or ImportStates.Validating && state.ConfirmedPreviewId == request.PreviewId)
            {
                return Unchanged.Value;
            }

            if (state.State is not (ImportStates.Ready or ImportStates.Rejected))
            {
                return InvalidState(state.State);
            }

            if (request.PreviewId is null || job.Preview?.Id != request.PreviewId)
            {
                return new Conflict("previewId is not the current preview", "preview_changed");
            }

            if (state.State == ImportStates.Rejected || job.Preview.Blocking > 0)
            {
                return new Conflict("the preview has blocking issues", "preview_blocked");
            }

            return new Change(job, state with { State = ImportStates.Queued, ConfirmedPreviewId = request.PreviewId, StartedAt = state.StartedAt ?? time.GetUtcNow() }, Analyze: false);
        }, cancellationToken);

    public ValueTask<ImportResult> CancelAsync(TenantContext database, string name, CancellationToken cancellationToken) =>
        ChangeAsync(database, name, records =>
        {
            var state = records.State;
            var now = time.GetUtcNow();
            // Running work stops promptly; the runner records the cancellation.
            queue.Interrupt(database);
            return state.State switch
            {
                ImportStates.Cancelled => Unchanged.Value,
                ImportStates.Analyzing or ImportStates.Running or ImportStates.Validating when state.CancelRequested => Unchanged.Value,
                ImportStates.Analyzing or ImportStates.Running or ImportStates.Validating => new Change(records.Job, state with { CancelRequested = true }, Analyze: null),
                ImportStates.AwaitingSource or ImportStates.Ready or ImportStates.Rejected or ImportStates.Queued =>
                    new Change(records.Job, Cancelled(state, now), Analyze: null),
                _ => InvalidState(state.State),
            };
        }, cancellationToken);

    public ValueTask<ImportResult> ResumeAsync(TenantContext database, string name, CancellationToken cancellationToken) =>
        ChangeAsync(database, name, records =>
        {
            var (job, state) = (records.Job, records.State);
            if (state.State is ImportStates.Queued or ImportStates.Running or ImportStates.Validating or ImportStates.Analyzing)
            {
                return Unchanged.Value;
            }

            if (state.State is not (ImportStates.Failed or ImportStates.Cancelled))
            {
                return InvalidState(state.State);
            }

            if (staging.Received(job.DatabaseId) != job.SourceBytes)
            {
                return new Conflict("the staged source is gone; upload the same file again first", "source_required");
            }

            var resumed = state with { CancelRequested = false, Error = null, FinishedAt = null, RetainedUntil = null };
            return state.ConfirmedPreviewId is not null && job.Preview?.Id == state.ConfirmedPreviewId
                ? new Change(job, resumed with { State = ImportStates.Queued }, Analyze: false)
                : new Change(job with { Preview = null, PlanDigest = null, Checksums = null }, resumed with { State = ImportStates.Analyzing, ConfirmedPreviewId = null }, Analyze: true);
        }, cancellationToken);

    /// <summary>Reads both records of the destination's import in one query, so they come from one snapshot.</summary>
    internal async ValueTask<ReadImportResult> ReadAsync(TenantContext database, CancellationToken cancellationToken)
    {
        var both = new InFilter(DocumentPath.Id, [new ScalarValue(ScalarKind.Text, ImportNames.JobId), new ScalarValue(ScalarKind.Text, ImportNames.StateId)]);
        var page = await documents.QueryAsync(database, new DocumentQuery(ImportNames.Collection, both, [], null, 2, DocumentRoute.MaxPageBytes), null, cancellationToken)
            .ConfigureAwait(false);
        if (page is not QueryOutcome outcome)
        {
            return page switch
            {
                Validation error => new Unavailable(error.Message),
                _ => UnknownImport,
            };
        }

        var jobDocument = outcome.Page.Rows.FirstOrDefault(row => row.Document.Id == ImportNames.JobId)?.Document;
        var stateDocument = outcome.Page.Rows.FirstOrDefault(row => row.Document.Id == ImportNames.StateId)?.Document;
        if (jobDocument is null || stateDocument is null)
        {
            return UnknownImport;
        }

        try
        {
            var jobStored = JsonSerializer.Deserialize(jobDocument.Json.Span, TerraBaseJsonContext.Default.ImportStored);
            var stateStored = JsonSerializer.Deserialize(stateDocument.Json.Span, TerraBaseJsonContext.Default.ImportStored);
            var jobRecord = jobStored is { V: 1 } ? JsonSerializer.Deserialize(jobStored.Record, TerraBaseJsonContext.Default.ImportJobRecord) : null;
            var stateRecord = stateStored is { V: 1, Rev: { } rev } && JsonSerializer.Deserialize(stateStored.Record, TerraBaseJsonContext.Default.ImportStateRecord) is { } state
                ? state with { Rev = rev }
                : null;
            return jobRecord is { V: 1 } && stateRecord is { V: 1 } && jobRecord.DatabaseId == database.DatabaseId
                ? new ImportRecords(jobRecord, stateRecord)
                : Modified;
        }
        catch (JsonException)
        {
            return Modified;
        }
    }

    /// <summary>Writes a new state (and job, when given) together with <paramref name="operations"/>, asserting the revision read.</summary>
    internal async ValueTask<WriteImportResult> WriteAsync(
        TenantContext database, ImportRecords? previous, ImportJobRecord? job, ImportStateRecord state, IReadOnlyList<DocumentOperation> operations,
        CancellationToken cancellationToken)
    {
        var next = state with { Rev = EntityIds.New(time).ToString(), UpdatedAt = time.GetUtcNow() };
        var all = new List<DocumentOperation>(operations.Count + 3);
        all.AddRange(operations);
        all.Add(previous is null
            ? new AssertOperation(ImportNames.Collection, ImportNames.StateId, null)
            : new AssertOperation(ImportNames.Collection, ImportNames.StateId, new CompareFilter(new DocumentPath(["rev"]), Comparison.Equal, new ScalarValue(ScalarKind.Text, previous.State.Rev))));
        all.Add(new PutOperation(ImportNames.Collection, ImportNames.StateId, Stored(next.Rev, JsonSerializer.Serialize(next, TerraBaseJsonContext.Default.ImportStateRecord)), Unconditional));
        if (job is not null && !ReferenceEquals(job, previous?.Job))
        {
            all.Add(new PutOperation(ImportNames.Collection, ImportNames.JobId, Stored(null, JsonSerializer.Serialize(job, TerraBaseJsonContext.Default.ImportJobRecord)), Unconditional));
        }

        return await documents.WriteAsync(database, all, indexErrors: true, cancellationToken).ConfigureAwait(false) switch
        {
            WriteResult => new ImportRecords(job ?? previous!.Job, next),
            DocumentRejected rejected => rejected,
        };
    }

    private static readonly Unavailable Modified = new("the import's record was changed outside the import and cannot be read", "destination_modified");

    private static byte[] Stored(string? rev, string record) =>
        JsonSerializer.SerializeToUtf8Bytes(new ImportStored(1, rev, record), TerraBaseJsonContext.Default.ImportStored);

    internal ValueTask<WriteImportResult> WriteStateAsync(TenantContext database, ImportRecords previous, ImportStateRecord state, CancellationToken cancellationToken) =>
        WriteAsync(database, previous, previous.Job, state, [], cancellationToken);

    internal ImportJobBody Body(ImportRecords records)
    {
        var (job, state) = (records.Job, records.State);
        var received = Math.Min(staging.Received(job.DatabaseId), job.SourceBytes);
        // A completed job has released its source: the runner deletes it right after persisting completed (the sweep retries).
        return new ImportJobBody(job.Id, job.ProjectId, job.Database, job.Region, state.State, state.CancelRequested, job.Options,
            new ImportSourceBody(job.Format, job.SourceBytes, job.SourceSha256, received, received == job.SourceBytes && state.State is not (ImportStates.AwaitingSource or ImportStates.Completed), state.RetainedUntil),
            job.Preview, state.ConfirmedPreviewId, state.Progress, state.Validation, state.Error, job.CreatedAt, state.UpdatedAt, state.StartedAt, state.FinishedAt);
    }

    internal static ImportStateRecord Cancelled(ImportStateRecord state, DateTimeOffset now) =>
        state with { State = ImportStates.Cancelled, CancelRequested = false, FinishedAt = now, RetainedUntil = now + ImportStaging.Retention };

    private static readonly Precondition Unconditional = new(null, IfMatchAny: false, IfNoneMatch: false);

    private static Conflict InvalidState(string state) => new($"the import is {state}", "invalid_state");

    private static ImportResult Failure(DocumentRejected rejected) => rejected.Error switch
    {
        NotFound error => error,
        Unavailable error => error,
        Validation error => error,
        PayloadTooLarge error => error,
        _ => new Conflict("the import's record changed concurrently; retry", "conflict"),
    };

    private ImportResult Queued(ImportRecords written, TenantContext database, string name, bool analyze)
    {
        queue.Ensure(database, name, analyze);
        return Body(written);
    }

    // Applies one state transition under the destination's lock: a new job and state, the current job unchanged, or a refusal.
    private async ValueTask<ImportResult> ChangeAsync(
        TenantContext database, string name, Func<ImportRecords, Transition> decide, CancellationToken cancellationToken)
    {
        var gate = Lock(database);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = await ReadAsync(database, cancellationToken).ConfigureAwait(false);
            if (read is not ImportRecords records)
            {
                return read switch
                {
                    NotFound error => error,
                    Unavailable error => error,
                    ImportRecords => throw new UnreachableException(),
                };
            }

            switch (decide(records))
            {
                case Unchanged:
                    return Body(records);
                case Conflict error:
                    return error;
                case Change change:
                    return await WriteAsync(database, records, change.Job, change.State, [], cancellationToken).ConfigureAwait(false) switch
                    {
                        ImportRecords written when change.Analyze is { } kind => Queued(written, database, name, kind),
                        ImportRecords written => Body(written),
                        DocumentRejected rejected => Failure(rejected),
                    };
                default:
                    throw new InvalidOperationException();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static Valid<ImportOptionsBody> Normalize(ImportOptionsBody? requested)
    {
        var exclude = requested?.Exclude ?? [];
        if (exclude.Count > 1000 || exclude.Sum(entry => entry?.Length ?? 0) > 64 * 1024)
        {
            return new Validation("options.exclude holds at most 1000 entries and 64 KiB");
        }

        var canonical = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in exclude)
        {
            if (entry is not { Length: > 0 and <= 512 } || ImportNames.Parse(entry) is not { Count: 2 or 3 } parts)
            {
                return new Validation("options.exclude entries are schema.table or schema.table.column, double-quoted where needed");
            }

            canonical.Add(string.Join('.', parts.Select(ImportNames.Quote)));
        }

        var collection = requested?.Collection;
        if (collection is not null && (DocumentRoute.CheckCollectionName(collection) is Validation || ImportNames.Reserved(collection)))
        {
            return new Validation("options.collection must be a lowercase, unreserved collection name of at most 63 characters");
        }

        return new ImportOptionsBody([.. canonical], collection);
    }

    /// <summary>A decision of <see cref="ChangeAsync"/>.</summary>
    private readonly union Transition(Unchanged, Conflict, Change);

    /// <summary>A new job and state; <c>Analyze</c> queues analysis (true), a copy (false) or nothing (null).</summary>
    private sealed record Change(ImportJobRecord Job, ImportStateRecord State, bool? Analyze);

    private sealed record Unchanged
    {
        public static Unchanged Value { get; } = new();
    }
}
