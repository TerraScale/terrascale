// <copyright file="TypedApi.Sync.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using TerraBase.Protocol;

public sealed partial class TypedApi
{
    // terrascale.sync.v2 sessions, forwarded to the engine
    // ------------------------------------------------------------------

    internal async Task<IResult> SyncSessionSetupAsync(HttpContext context, SyncSessionSetupDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (this.Scope(context, request.Collection) is { } invalid)
        {
            return invalid;
        }

        var caller = ResolveCaller(context);
        try
        {
            var answer = await this.supervisor
                .CallAsync(
                    caller.Tenant,
                    caller.Principal,
                    "sync.session_setup",
                    channel => channel.SyncSessionSetupAsync(request.Collection, request.ResumeBytes, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            this.supervisor.Sessions[answer.SessionId] = new TrackedSession(answer.SessionId, caller.Tenant, request.Collection, this.supervisor.Generation);
            this.audit.Record(
                AuditKind.SessionOpened,
                caller.Tenant,
                caller.Principal,
                operation: "sync.session_setup",
                outcome: AuditOutcome.Accepted,
                detail: $"session {answer.SessionId}, collection {request.Collection}, generation {this.supervisor.Generation}");
            return Results.Json(new SyncSessionDto(answer.SessionId, answer.Checkpoint, answer.RetainedFloor));
        }
        catch (TbepException failure) when (failure.Code?.Variant == "CheckpointRefused")
        {
            // A resume checkpoint that does not decode: the consumer
            // resyncs by opening a fresh session (or pulling a snapshot
            // on one it owns).
            return Results.Json(
                new ErrorDto("checkpoint-refused", PublicEngineFailureDetail("CheckpointRefused")) { ResyncRequired = true },
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal IResult SyncSessionsAsync(HttpContext context)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        var caller = ResolveCaller(context);
        var sessions = this.supervisor.Sessions.Values
            .Where(session => session.Tenant == caller.Tenant)
            .Select(session => new SyncSessionSummaryDto(session.SessionId, session.Collection, session.Generation))
            .OrderBy(session => session.SessionId)
            .ToArray();
        return Results.Json(sessions);
    }

    internal async Task<IResult> SyncDeltaPullAsync(HttpContext context, ulong id, SyncDeltaDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        var caller = ResolveCaller(context);
        if (this.StaleSession(id, caller) is { } stale)
        {
            return stale;
        }

        try
        {
            var answer = await this.supervisor
                .CallAsync(caller.Tenant, caller.Principal, "sync.delta_pull", channel => channel.SyncDeltaPullAsync(id, request.FromBytes, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new SyncDeltaResponseDto(answer.SessionId, answer.PayloadCodec, answer.Payload, answer.Next, answer.Commits, answer.SingleCommitOversize));
        }
        catch (TbepException failure) when (failure.Code?.Variant == "SessionRefused")
        {
            return this.TenantOrStale(failure, caller, id, "session");
        }
        catch (TbepException failure) when (failure.Code?.Variant == "CheckpointRefused")
        {
            return Results.Json(
                new ErrorDto("checkpoint-refused", PublicEngineFailureDetail("CheckpointRefused")) { ResyncRequired = true },
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> SyncSnapshotPullAsync(HttpContext context, ulong id, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        var caller = ResolveCaller(context);
        if (this.StaleSession(id, caller) is { } stale)
        {
            return stale;
        }

        try
        {
            var answer = await this.supervisor
                .CallAsync(caller.Tenant, caller.Principal, "sync.snapshot_pull", channel => channel.SyncSnapshotPullAsync(id, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new SyncSnapshotResponseDto(answer.SessionId, answer.PayloadCodec, answer.Payload, answer.Checkpoint));
        }
        catch (TbepException failure) when (failure.Code?.Variant == "SessionRefused")
        {
            return this.TenantOrStale(failure, caller, id, "session");
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> SyncCheckpointAckAsync(HttpContext context, ulong id, SyncAckDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        var caller = ResolveCaller(context);
        if (this.StaleSession(id, caller) is { } stale)
        {
            return stale;
        }

        try
        {
            await this.supervisor
                .CallAsync(caller.Tenant, caller.Principal, "sync.checkpoint_ack", channel => channel.SyncCheckpointAckAsync(id, request.CheckpointBytes, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new SyncAckedDto(id));
        }
        catch (TbepException failure) when (failure.Code?.Variant == "SessionRefused")
        {
            return this.TenantOrStale(failure, caller, id, "session");
        }
        catch (TbepException failure) when (failure.Code?.Variant == "CheckpointRefused")
        {
            return Results.Json(new ErrorDto("checkpoint-refused", PublicEngineFailureDetail("CheckpointRefused")), statusCode: StatusCodes.Status409Conflict);
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> SyncMutationSubmitAsync(HttpContext context, ulong id, SyncMutationDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (this.Scope(context, request.Document) is { } invalidDocument)
        {
            return invalidDocument;
        }

        if (Idempotency(context) is { } badKey)
        {
            return badKey;
        }

        var caller = ResolveCaller(context);
        if (this.StaleSession(id, caller) is { } stale)
        {
            return stale;
        }

        var key = context.Request.Headers["Idempotency-Key"].ToString();
        try
        {
            var change = request.Change.ToSyncChange();
            var answer = await this.supervisor
                .CallAsync(
                    caller.Tenant,
                    caller.Principal,
                    "sync.mutation_submit",
                    channel => channel.SyncMutationSubmitAsync(id, request.Document, change, key, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            var applied = answer.Outcome.Variant == "Applied";
            this.audit.Record(
                applied ? AuditKind.MutationApplied : AuditKind.MutationRejected,
                caller.Tenant,
                caller.Principal,
                operation: "sync.mutation_submit",
                outcome: applied ? AuditOutcome.Accepted : AuditOutcome.Refused,
                detail: applied
                    ? $"session {id}, receipt {((CommitReceipt)answer.Outcome.Payload!).Sequence}"
                    : $"session {id}, reason {((MutationReason)answer.Outcome.Payload!).Variant.ToSnakeCase()}");
            return Results.Json(new SyncMutationResponseDto(id, SyncMutationOutcomeDto.Of(answer.Outcome)));
        }
        catch (TbepException failure) when (failure.Code?.Variant == "SessionRefused")
        {
            return this.TenantOrStale(failure, caller, id, "session");
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> SyncSessionCloseAsync(HttpContext context, ulong id, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        var caller = ResolveCaller(context);
        if (this.StaleSession(id, caller) is { } stale)
        {
            return stale;
        }

        try
        {
            await this.supervisor
                .CallAsync(caller.Tenant, caller.Principal, "sync.session_close", channel => channel.SyncSessionCloseAsync(id, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            this.supervisor.Sessions.TryRemove(id, out _);
            this.audit.Record(
                AuditKind.SessionClosed,
                caller.Tenant,
                caller.Principal,
                operation: "sync.session_close",
                outcome: AuditOutcome.Accepted,
                detail: $"session {id}");
            return Results.Json(new SyncAckedDto(id));
        }
        catch (TbepException failure) when (failure.Code?.Variant == "SessionRefused")
        {
            return this.TenantOrStale(failure, caller, id, "session");
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    // ------------------------------------------------------------------

}
