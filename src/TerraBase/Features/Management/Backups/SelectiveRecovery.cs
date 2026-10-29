using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Backup;
using TerraBase.Features.Documents;
using TerraBase.Features.Memory;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Platform;
using TerraBase.Features.Qos;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

public interface ISelectiveRecovery
{
    ValueTask<IResult> PreviewAsync(HttpContext context, CancellationToken cancellationToken);

    ValueTask<IResult> RecoverAsync(HttpContext context, CancellationToken cancellationToken);
}

internal static class SelectiveRecoveryRoute
{
    public const string Description = "Selects retained historical live records, previews their actual historical/current contents, then explicitly restores only approved records into the original database. selections contains 1 to 16 distinct existing collection names; omitted ids selects the whole historical collection, explicit ids selects 1 to 100 records. At most 100 records and 1 MiB combined historical/current JSON per preview; split larger selections into explicit id batches. Collections must already exist: explicitly recreate a dropped collection before previewing. Historical schema, ACLs, indexes and TTL settings are never restored. Approval expires after 10 minutes and is bound to the acting user, source and issuing process; pin the same server for apply or preview again. recover accepts only approval and a nonempty subset of its record keys. Every approved current version (including tombstones and never-written state) must still match the current local state, like If-Match; otherwise 412 and no writes. Platform databases are refused with 409 recovery_erasure_fenced. Retention, writable access, current schema and ordinary write/storage quotas are rechecked. Preview also requires documents:read; apply also requires documents:write. At most two recovery operations per node and one per source; excess work answers 503 capacity with Retry-After. Scratch never becomes a customer database and is cleaned on completion/error/cancellation. Apply commits locally; ambiguous outcomes must not be retried automatically. " + ProjectApiRoute.IdentityDescription;

    public static void Configure(EndpointDefinition endpoint, string documentScope)
    {
        endpoint.WithTags("Backups").RequireAuthorization(ApiKeyScopes.DatabasesWrite, documentScope)
            .WithMetadata(new ProjectManagementRoute(), ListenerMetadata.Management);
        foreach (var status in new[] { 400, 401, 403, 404, 409, 412, 413, 415, 429, 503 })
        {
            endpoint.Produces<ErrorBody>(status);
        }
    }
}

internal sealed class SelectiveRecovery(IManagementService management, IPointInTimeRestore restore, IStorageEngine engine,
    IDocumentService documents, IUsageMeter usage, IMemoryBudget memory, TimeProvider time) : ISelectiveRecovery, IDisposable
{
    private const int MaxRecords = 100;
    private const int MaxBytes = 1024 * 1024;
    private readonly byte[] _approvalKey = RandomNumberGenerator.GetBytes(32);
    private readonly SemaphoreSlim _scratchSlots = new(2, 2);
    private readonly ConcurrentDictionary<Guid, byte> _activeSources = new();

    public ValueTask<IResult> PreviewAsync(HttpContext context, CancellationToken cancellationToken) => RunAsync(context, apply: false, cancellationToken);
    public ValueTask<IResult> RecoverAsync(HttpContext context, CancellationToken cancellationToken) => RunAsync(context, apply: true, cancellationToken);
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_approvalKey);
        _scratchSlots.Dispose();
    }

    private async ValueTask<IResult> RunAsync(HttpContext context, bool apply, CancellationToken cancellationToken)
    {
        var access = await ManagementRoute.CheckWritableDatabaseAsync(context, management, cancellationToken).ConfigureAwait(false);
        if (access is not TenantContext database)
        {
            return AccessError(access);
        }
        if (PlatformDatabases.IsPlatform(database.OrganizationId))
        {
            return ErasureFenced();
        }

        using var admitted = memory.TryReserve(MemoryLane.Bulk, 2L * MaxBytes, database);
        if (admitted is null)
        {
            return ErrorResponse.From(new Unavailable("recovery memory capacity is exhausted", "capacity"));
        }

        var body = await DocumentRoute.CheckBodyAsync(context, 128 * 1024, "invalid_request", cancellationToken, DocumentRoute.JsonMediaType).ConfigureAwait(false);
        if (body is not byte[] json)
        {
            return body switch
            {
                Validation error => ErrorResponse.From(error),
                PayloadTooLarge error => ErrorResponse.From(error),
                UnsupportedMediaType error => ErrorResponse.From(error),
                byte[] => throw new System.Diagnostics.UnreachableException(),
            };
        }

        using var slot = await EnterAsync(database.VersionScope(), cancellationToken).ConfigureAwait(false);
        if (slot is null)
        {
            context.Response.Headers.RetryAfter = "1";
            return ErrorResponse.From(new Unavailable("recovery scratch capacity is exhausted", "capacity"));
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2), time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            return apply ? await ApplyAsync(context, database, json, deadline.Token).ConfigureAwait(false)
                : await PreviewCoreAsync(context, database, json, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (BackupDestinationRoute.IsUnavailable(error, cancellationToken))
        {
            return ErrorResponse.From(new Unavailable("recovery scratch or backup is unavailable", "recovery_unavailable"));
        }
    }

    private async ValueTask<IResult> PreviewCoreAsync(HttpContext context, TenantContext database, byte[] json, CancellationToken cancellationToken)
    {
        var parsed = RequestJson.Read<RecoveryPreviewRequest>(json, "invalid_request");
        if (parsed is not RecoveryPreviewRequest request)
        {
            return parsed switch { Validation error => ErrorResponse.From(error), RecoveryPreviewRequest => throw new System.Diagnostics.UnreachableException() };
        }
        if (request.PointInTime is not { } at || request.Selections is not { Count: >= 1 and <= 16 } selections
            || selections.Select(selection => selection.Collection).Distinct(StringComparer.Ordinal).Count() != selections.Count
            || selections.Any(selection => !DatabaseName.TryParse(selection.Collection, out _) || CollectionName.IsReserved(selection.Collection)
                || selection.Ids is { Count: < 1 or > MaxRecords } || selection.Ids?.Any(id => !EntityIds.IsValid(id)) == true))
        {
            return Invalid("pointInTime and 1 to 16 valid distinct collection selections are required; ids must be canonical ULIDs");
        }
        var selected = BackupDestinationRoute.Source(request.Destination);
        if (selected is not string destination)
        {
            return selected switch { Validation error => ErrorResponse.From(error), string => throw new System.Diagnostics.UnreachableException() };
        }

        var found = await restore.ReadSnapshotAsync(database, at, cancellationToken, destination).ConfigureAwait(false);
        if (found is not HistoricalSnapshot snapshot)
        {
            return SnapshotError(found);
        }
        await using var historical = snapshot;
        await using var current = await engine.BeginTransactionAsync(database, write: false, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return ErrorResponse.From(new NotFound());
        }
        var records = new List<RecoveryRecord>();
        var approved = new List<RecoveryApprovedRecord>();
        var bytes = 0L;
        foreach (var selection in selections)
        {
            if (await current.GetCollectionAsync(selection.Collection, cancellationToken).ConfigureAwait(false) is null
                || await historical.Transaction.GetCollectionAsync(selection.Collection, cancellationToken).ConfigureAwait(false) is null)
            {
                return ErrorResponse.From(new NotFound("the historical and current collection must both exist"));
            }
            var page = selection.Ids is null
                ? await historical.Transaction.ListAsync(selection.Collection, null, MaxRecords + 1, MaxBytes, cancellationToken).ConfigureAwait(false) : null;
            if (page is not null)
            {
                usage.RecordRead(database, page.Documents.Sum(document => OperationUnits.Of(document.Json.Length)));
            }
            if (page is { HasMore: true })
            {
                return Invalid("selection exceeds recovery bounds; select explicit ids in smaller batches");
            }
            foreach (var id in selection.Ids ?? [.. page!.Documents.Select(document => document.Id)])
            {
                var past = page is not null ? page.Documents.Single(document => document.Id == id)
                    : await historical.Transaction.GetAsync(selection.Collection, id, cancellationToken).ConfigureAwait(false);
                if (page is null)
                {
                    usage.RecordRead(database, OperationUnits.Of(past?.Json.Length ?? 0));
                }
                if (past is null)
                {
                    return ErrorResponse.From(new NotFound("a selected historical live document does not exist"));
                }
                var now = await current.GetAsync(selection.Collection, id, cancellationToken).ConfigureAwait(false);
                usage.RecordRead(database, OperationUnits.Of(now?.Json.Length ?? 0));
                var version = await current.GetVersionAsync(selection.Collection, id, cancellationToken).ConfigureAwait(false);
                if (version is { Deleted: false } && now is null)
                {
                    return ErrorResponse.From(new Conflict("a selected current record has an unresolved uniqueness conflict", "unique_conflict"));
                }
                bytes += past.Json.Length + (now?.Json.Length ?? 0);
                if (records.Count == MaxRecords || bytes > MaxBytes || approved.Any(record => record.Collection == selection.Collection && record.Id == id))
                {
                    return Invalid("selection exceeds recovery bounds or contains duplicate ids; use smaller distinct batches");
                }
                var tag = version is { } state ? ETags.Version(current.Scope, selection.Collection, id, state.Version,
                    now?.Json ?? ReadOnlyMemory<byte>.Empty, state.Deleted) : null;
                QosContext.ReserveResult(4L * (past.Json.Length + (now?.Json.Length ?? 0)) + 1024, database);
                records.Add(new(selection.Collection, id, DocumentRoute.Json(past.Json)!.Value, DocumentRoute.Json(now?.Json), tag));
                approved.Add(new(selection.Collection, id, Convert.ToHexString(SHA256.HashData(past.Json.Span)), tag));
            }
        }
        var expires = time.GetUtcNow().AddMinutes(10);
        var approval = new RecoveryApproval(1, ManagementRoute.UserId(context), database.VersionScope(), at, destination, expires, approved);
        var access = await ManagementRoute.CheckWritableDatabaseAsync(context, management, cancellationToken).ConfigureAwait(false);
        if (access is not TenantContext refreshed || refreshed.VersionScope() != database.VersionScope())
        {
            return access is TenantContext ? ErrorResponse.From(new NotFound()) : AccessError(access);
        }
        if (PlatformDatabases.IsPlatform(refreshed.OrganizationId))
        {
            return ErasureFenced();
        }
        QosContext.ReserveResult(128 * 1024, database);
        return TypedResults.Ok(new RecoveryPreview(Seal(approval), expires, records));
    }

    private async ValueTask<IResult> ApplyAsync(HttpContext context, TenantContext database, byte[] json, CancellationToken cancellationToken)
    {
        var parsed = RequestJson.Read<RecoveryRequest>(json, "invalid_request");
        if (parsed is not RecoveryRequest request)
        {
            return parsed switch { Validation error => ErrorResponse.From(error), RecoveryRequest => throw new System.Diagnostics.UnreachableException() };
        }
        var approval = Open(request.Approval);
        if (approval is null || approval.Version != 1 || approval.ExpiresAt <= time.GetUtcNow()
            || approval.Actor != ManagementRoute.UserId(context) || approval.Source != database.VersionScope())
        {
            return ErrorResponse.From(new Conflict("preview approval is invalid, expired or belongs to another server; preview again", "recovery_preview_invalid"));
        }
        if (request.Records is not { Count: >= 1 and <= MaxRecords } records || records.Distinct().Count() != records.Count
            || records.Any(key => !approval.Records.Any(record => record.Collection == key.Collection && record.Id == key.Id)))
        {
            return Invalid("records must be a nonempty distinct subset of the preview");
        }
        if (DocumentRoute.CheckDurability(context) is Validation durabilityError)
        {
            return ErrorResponse.From(durabilityError);
        }
        var found = await restore.ReadSnapshotAsync(database, approval.PointInTime, cancellationToken, approval.Destination).ConfigureAwait(false);
        if (found is not HistoricalSnapshot snapshot)
        {
            return SnapshotError(found);
        }
        await using var historical = snapshot;
        var operations = new List<DocumentOperation>(records.Count);
        foreach (var key in records)
        {
            var approved = approval.Records.Single(record => record.Collection == key.Collection && record.Id == key.Id);
            var past = await historical.Transaction.GetAsync(key.Collection, key.Id, cancellationToken).ConfigureAwait(false);
            usage.RecordRead(database, OperationUnits.Of(past?.Json.Length ?? 0));
            if (past is null || Convert.ToHexString(SHA256.HashData(past.Json.Span)) != approved.Hash)
            {
                return ErrorResponse.From(new Conflict("the approved historical record is no longer available; preview again", "recovery_history_changed"));
            }
            operations.Add(new PutOperation(key.Collection, key.Id, past.Json, approved.CurrentVersion is { } tag
                ? new Precondition([tag], false, false) : new Precondition(null, false, false, IfNeverWritten: true)));
        }
        var access = await ManagementRoute.CheckWritableDatabaseAsync(context, management, cancellationToken).ConfigureAwait(false);
        if (access is not TenantContext refreshed || refreshed.VersionScope() != database.VersionScope())
        {
            return access is TenantContext ? ErrorResponse.From(new NotFound()) : AccessError(access);
        }
        if (PlatformDatabases.IsPlatform(refreshed.OrganizationId))
        {
            return ErasureFenced();
        }
        if (approval.ExpiresAt <= time.GetUtcNow())
        {
            return ErrorResponse.From(new Conflict("preview expired; preview again", "recovery_preview_invalid"));
        }
        return await documents.WriteConfirmedAsync(refreshed, operations, indexErrors: true, cancellationToken).ConfigureAwait(false) switch
        {
            DocumentRejected rejected => ErrorResponse.From(rejected.Error, rejected.Op),
            WriteResult written => TypedResults.Ok(new TransactionBody(DocumentRoute.Decimal(written.Offset), written.Durability,
                [.. written.Results.Select(result => new TransactionResultBody(result.Id, result.ETag, null, null))])),
        };
    }

    private string Seal(RecoveryApproval approval)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(approval, TerraBaseJsonContext.Default.RecoveryApproval);
        return Convert.ToBase64String(payload) + "." + Convert.ToBase64String(HMACSHA256.HashData(_approvalKey, payload));
    }

    private async ValueTask<IDisposable?> EnterAsync(Guid source, CancellationToken cancellationToken)
    {
        if (!await _scratchSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        if (_activeSources.TryAdd(source, 0))
        {
            return new ScratchSlot(this, source);
        }
        _scratchSlots.Release();
        return null;
    }

    private sealed class ScratchSlot(SelectiveRecovery owner, Guid source) : IDisposable
    {
        public void Dispose()
        {
            owner._activeSources.TryRemove(source, out _);
            owner._scratchSlots.Release();
        }
    }

    private static IResult ErasureFenced() => ErrorResponse.From(new Conflict("platform database historical recovery is fenced by erasure requirements", "recovery_erasure_fenced"));

    private RecoveryApproval? Open(string? token)
    {
        if (token is null || token.Length > 64 * 1024 || token.Split('.') is not [var encoded, var signature])
        {
            return null;
        }
        try
        {
            var payload = Convert.FromBase64String(encoded);
            return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(signature), HMACSHA256.HashData(_approvalKey, payload))
                ? JsonSerializer.Deserialize(payload, TerraBaseJsonContext.Default.RecoveryApproval) : null;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return null;
        }
    }

    private static IResult Invalid(string message) => ErrorResponse.From(new Validation(message));
    private static IResult AccessError(WritableDatabase access) => access switch
    {
        NotFound error => ErrorResponse.From(error),
        Forbidden error => ErrorResponse.From(error),
        Unavailable error => ErrorResponse.From(error),
        TenantContext => throw new System.Diagnostics.UnreachableException(),
    };
    private static IResult SnapshotError(HistoricalSnapshotResult found) => found switch
    {
        NotFound error => ErrorResponse.From(error),
        Conflict error => ErrorResponse.From(error),
        Validation error => ErrorResponse.From(error),
        Unavailable error => ErrorResponse.From(error),
        HistoricalSnapshot => throw new System.Diagnostics.UnreachableException(),
    };
}
