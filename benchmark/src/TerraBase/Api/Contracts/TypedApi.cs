// <copyright file="TypedApi.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TerraBase.Protocol;

/// <summary>
/// The typed public API of the C# host (SE-08): every HTTP operation is
/// a typed TBEP operation on the engine through the SE-14 client, every
/// stream is a TBEP backbone subscription, and every sync session is a
/// forwarded terrascale.sync.v2 serving session. The boundary is local
/// and backend-only (PD-2026-08-26-11 D1): there is no authentication
/// or authorization surface here — the local gate checks that the
/// backend-supplied context (principal, tenant) is present and valid
/// before any engine frame is sent, the engine enforces tenant
/// isolation from that context, and customer auth/PB-3042 lives
/// exclusively at the TerraScale Platform (no claim is made here).
/// </summary>
public sealed class TypedApi
{
    private readonly EngineSupervisor supervisor;
    private readonly BackboneHub hub;
    private readonly AuditLog audit;

    internal TypedApi(EngineSupervisor supervisor, BackboneHub hub, AuditLog audit)
    {
        this.supervisor = supervisor;
        this.hub = hub;
        this.audit = audit;
    }

    // ------------------------------------------------------------------
    // Health, metrics, audit
    // ------------------------------------------------------------------

    internal async Task<IResult> HealthAsync(CancellationToken cancellationToken)
    {
        var health = await this.supervisor.HealthAsync(cancellationToken).ConfigureAwait(false);
        var status = health.Status == EngineStatus.Serving && health.EngineState is not ("Stopped" or "Draining")
            ? "serving"
            : "unavailable";
        return Results.Json(
            new HealthResponse(
                Status: status,
                Engine: new EngineHealth(
                    health.EngineState,
                    health.Sessions,
                    health.Subscriptions,
                    health.FramesServed,
                    health.Generation,
                    health.Restarts,
                    health.Detail)),
            statusCode: status == "serving" ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }

    internal IResult Metrics() => Results.Json(new MetricsResponse(
        EngineGeneration: this.supervisor.Generation,
        EngineRestarts: this.supervisor.Restarts,
        EngineStatus: this.supervisor.Status.ToString().ToLowerInvariant(),
        OpenStreams: this.hub.OpenCount,
        DroppedFrames: this.hub.DroppedTotal,
        TrackedSessions: this.supervisor.Sessions.Count,
        AuditRecords: this.audit.Count));

    internal IResult Audit(int limit = 100) => Results.Json(new AuditResponse(this.audit.Recent(Math.Clamp(limit, 1, AuditLog.Capacity))));

    internal async Task<IResult> EngineHealthAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        var health = await this.supervisor.HealthAsync(cancellationToken).ConfigureAwait(false);
        return Results.Json(health);
    }

    internal async Task<IResult> EngineStatusAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        try
        {
            var status = await this.supervisor
                .CallAsync(
                    context.Request.Headers["X-TB-Tenant"].ToString(),
                    context.Request.Headers["X-TB-Principal"].ToString(),
                    "admin.status",
                    channel => channel.AdminStatusAsync(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(status);
        }
        catch (Exception failure)
        {
            return this.Failure(failure);
        }
    }

    internal async Task<IResult> EngineTopologyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        try
        {
            var topology = await this.supervisor
                .CallAsync(
                    context.Request.Headers["X-TB-Tenant"].ToString(),
                    context.Request.Headers["X-TB-Principal"].ToString(),
                    "topology.read",
                    channel => channel.TopologyReadAsync(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(topology);
        }
        catch (Exception failure)
        {
            return this.Failure(failure);
        }
    }

    internal async Task<IResult> EngineShutdownAsync(HttpContext context, ShutdownRequestDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        var caller = ResolveCaller(context);
        try
        {
            var reason = request.Reason == "supervisor" ? ShutdownReason.Supervisor() : ShutdownReason.Operator();
            var answer = await this.supervisor.ShutdownAsync(reason, cancellationToken).ConfigureAwait(false);
            return Results.Json(new ShutdownResponseDto(answer.State.Variant, answer.InFlight));
        }
        catch (EngineUnavailableException failure)
        {
            return Unavailable(failure);
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    // ------------------------------------------------------------------
    // Documents, queries, families (forwarded typed operations)
    // ------------------------------------------------------------------

    internal async Task<IResult> DocumentReadAsync(HttpContext context, string collection, string document, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (this.Scope(context, collection) is { } invalid)
        {
            return invalid;
        }

        if (this.Scope(context, document) is { } invalidDocument)
        {
            return invalidDocument;
        }

        var caller = ResolveCaller(context);
        try
        {
            var answer = await this.supervisor
                .CallAsync(caller.Tenant, caller.Principal, "document.read", channel => channel.DocumentReadAsync(collection, document, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return answer.Found
                ? Results.Json(new DocumentReadDto(true, DocumentBodyDto.Of(answer.Document!)))
                : Results.Json(new DocumentReadDto(false, null));
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> DocumentWriteAsync(HttpContext context, string collection, string document, JsonValue? body, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (this.Scope(context, collection) is { } invalid)
        {
            return invalid;
        }

        if (this.Scope(context, document) is { } invalidDocument)
        {
            return invalidDocument;
        }

        if (Idempotency(context) is { } badKey)
        {
            return badKey;
        }

        var caller = ResolveCaller(context);
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        try
        {
            var put = new DocumentPut { Id = document, Collection = collection, Body = body?.ToValue() ?? TbepValue.Null };
            // The operation id of a mutation is its idempotency key —
            // the retry identity — never the per-request trace id, so a
            // retried write names the same journal operation.
            var answer = await this.supervisor
                .CallAsync(
                    caller.Tenant,
                    caller.Principal,
                    "document.write",
                    channel => channel.DocumentWriteAsync(put, Expected(context), idempotencyKey, idempotencyKey, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(WriteOutcomeDto.Of(answer.Outcome));
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> DocumentDeleteAsync(HttpContext context, string collection, string document, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (this.Scope(context, collection) is { } invalid)
        {
            return invalid;
        }

        if (this.Scope(context, document) is { } invalidDocument)
        {
            return invalidDocument;
        }

        if (Idempotency(context) is { } badKey)
        {
            return badKey;
        }

        var caller = ResolveCaller(context);
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        try
        {
            var answer = await this.supervisor
                .CallAsync(
                    caller.Tenant,
                    caller.Principal,
                    "document.delete",
                    channel => channel.DocumentDeleteAsync(collection, document, Expected(context), idempotencyKey, idempotencyKey, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(WriteOutcomeDto.Of(answer.Outcome));
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> QueryExecuteAsync(HttpContext context, QueryRequestDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        var caller = ResolveCaller(context);
        try
        {
            var answer = await this.supervisor
                .CallAsync(
                    caller.Tenant,
                    caller.Principal,
                    "query.execute",
                    channel => channel.QueryExecuteAsync(NextQueryId(), request.Text, request.MaxItems ?? 256, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new QueryResponseDto(
                answer.Documents.Select(DocumentBodyDto.Of).ToArray(),
                answer.Count,
                answer.Terminal.Variant.ToLowerInvariant()));
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> QueryCancelAsync(HttpContext context, QueryCancelDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        var caller = ResolveCaller(context);
        try
        {
            var answer = await this.supervisor
                .CallAsync(caller.Tenant, caller.Principal, "query.cancel", channel => channel.QueryCancelAsync(request.QueryId, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new QueryCancelResponseDto(answer.Outcome.Variant.ToSnakeCase()));
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> FamilyReadAsync(HttpContext context, string family, string name, FamilyRequestDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        var caller = ResolveCaller(context);
        try
        {
            var answer = await this.supervisor
                .CallAsync(
                    caller.Tenant,
                    caller.Principal,
                    "family.read",
                    channel => channel.FamilyReadAsync(
                        new FamilyReadRequest { Family = family, Name = name, RequestCodec = request.RequestCodec, Request = request.RequestBytes },
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new FamilyResponseDto(answer.ResponseCodec, answer.Response));
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> FamilyWriteAsync(HttpContext context, string family, string name, FamilyRequestDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (Idempotency(context) is { } badKey)
        {
            return badKey;
        }

        var caller = ResolveCaller(context);
        try
        {
            var answer = await this.supervisor
                .CallAsync(
                    caller.Tenant,
                    caller.Principal,
                    "family.write",
                    channel => channel.FamilyWriteAsync(
                        new FamilyWriteRequest
                        {
                            Family = family,
                            Name = name,
                            RequestCodec = request.RequestCodec,
                            Request = request.RequestBytes,
                            OperationId = context.Request.Headers["Idempotency-Key"].ToString(),
                        },
                        context.Request.Headers["Idempotency-Key"].ToString(),
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new FamilyResponseDto(answer.ResponseCodec, answer.Response));
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    // ------------------------------------------------------------------
    // The schema surface (SM-04): the typed schema lifecycle
    // ------------------------------------------------------------------

    internal async Task<IResult> SchemaStatusAsync(HttpContext context, string collection, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (this.Scope(context, collection) is { } invalid)
        {
            return invalid;
        }

        var caller = ResolveCaller(context);
        try
        {
            var status = await this.supervisor
                .CallAsync(caller.Tenant, caller.Principal, "schema.status", channel => channel.SchemaStatusAsync(collection, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new SchemaStatusDto(status.EnforcedVersion, status.RuleCount));
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> SchemaDeclareAsync(HttpContext context, string collection, SchemaDeclareDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (this.Scope(context, collection) is { } invalid)
        {
            return invalid;
        }

        if (Idempotency(context) is { } badKey)
        {
            return badKey;
        }

        var caller = ResolveCaller(context);
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        try
        {
            var schema = request.Schema is { } document ? JsonValues.ToValue(document) : null;
            var answer = await this.supervisor
                .CallAsync(
                    caller.Tenant,
                    caller.Principal,
                    "schema.declare",
                    channel => channel.SchemaDeclareAsync(collection, schema, idempotencyKey, idempotencyKey, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            this.audit.Record(
                AuditKind.SchemaLifecycle,
                caller.Tenant,
                caller.Principal,
                operation: "schema.declare",
                outcome: AuditOutcome.Accepted,
                detail: $"collection={collection}");
            return Results.Json(new SchemaStatusDto(answer.EnforcedVersion, answer.RuleCount));
        }
        catch (TbepException failure)
        {
            return this.SchemaFailure(failure, caller, collection, "schema.declare");
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> SchemaValidateAsync(HttpContext context, string collection, SchemaValidateDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (this.Scope(context, collection) is { } invalid)
        {
            return invalid;
        }

        if (request.Document is not { } document)
        {
            var missing = ResolveCaller(context);
            this.audit.Record(
                AuditKind.ScopeRefused,
                missing.Tenant,
                missing.Principal,
                operation: "schema.validate",
                outcome: AuditOutcome.Refused,
                detail: "the document to validate is missing");
            return Results.Json(
                new ErrorDto("document-missing", "schema validation names the document value it validates"),
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var caller = ResolveCaller(context);
        try
        {
            var answer = await this.supervisor
                .CallAsync(
                    caller.Tenant,
                    caller.Principal,
                    "schema.validate",
                    channel => channel.SchemaValidateAsync(collection, JsonValues.ToValue(document), cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new SchemaValidationDto(
                answer.EnforcedVersion,
                answer.Valid,
                [.. answer.Violations.Select(violation => new ViolationDto(violation.Field, violation.Kind.Variant.ToSnakeCase()))]));
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> MigrationStatusAsync(HttpContext context, string collection, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (this.Scope(context, collection) is { } invalid)
        {
            return invalid;
        }

        var caller = ResolveCaller(context);
        try
        {
            var status = await this.supervisor
                .CallAsync(caller.Tenant, caller.Principal, "migration.status", channel => channel.MigrationStatusAsync(collection, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new MigrationStatusDto(MigrationStateDto.Of(status.State)));
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> MigrationDryRunAsync(HttpContext context, string collection, MigrationDryRunDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (this.Scope(context, collection) is { } invalid)
        {
            return invalid;
        }

        if (request.Contract is not { } dryRunContract)
        {
            return this.MigrationContractMissing(context, "migration.dry-run");
        }

        var caller = ResolveCaller(context);
        try
        {
            var contract = dryRunContract.ToContract();
            var answer = await this.supervisor
                .CallAsync(caller.Tenant, caller.Principal, "migration.dry_run", channel => channel.MigrationDryRunAsync(contract, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new { plan = MigrationPlanDto.Of(answer.Plan) });
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> MigrationApplyAsync(HttpContext context, string collection, MigrationApplyDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        if (this.Scope(context, collection) is { } invalid)
        {
            return invalid;
        }

        if (Idempotency(context) is { } badKey)
        {
            return badKey;
        }

        if (request.Contract is not { } applyContract)
        {
            return this.MigrationContractMissing(context, "migration.apply");
        }

        var caller = ResolveCaller(context);
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        try
        {
            var contract = applyContract.ToContract();
            var answer = await this.supervisor
                .CallAsync(
                    caller.Tenant,
                    caller.Principal,
                    "migration.apply",
                    channel => channel.MigrationApplyAsync(contract, request.AcknowledgeNotRollbackable, idempotencyKey, idempotencyKey, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            this.audit.Record(
                AuditKind.SchemaLifecycle,
                caller.Tenant,
                caller.Principal,
                operation: "migration.apply",
                outcome: AuditOutcome.Accepted,
                detail: $"collection={collection}");
            return Results.Json(new { receipt = MigrationReceiptDto.Of(answer.Receipt) });
        }
        catch (TbepException failure)
        {
            return this.SchemaFailure(failure, caller, collection, "migration.apply");
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    /// <summary>Refuses a migration request whose contract is absent, after the local gate has run.</summary>
    /// <param name="context">The request context.</param>
    /// <param name="operation">The operation name.</param>
    /// <returns>The refusal answer.</returns>
    private IResult MigrationContractMissing(HttpContext context, string operation)
    {
        var caller = ResolveCaller(context);
        this.audit.Record(
            AuditKind.ScopeRefused,
            caller.Tenant,
            caller.Principal,
            operation,
            AuditOutcome.Refused,
            "the migration contract is missing");
        return Results.Json(
            new ErrorDto("contract-missing", "a migration names the contract it runs"),
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    /// <summary>Maps a schema-surface engine refusal: the engine-enforced tenant isolation is surfaced typed and audited.</summary>
    /// <param name="failure">The typed engine failure.</param>
    /// <param name="caller">The caller.</param>
    /// <param name="collection">The collection.</param>
    /// <param name="operation">The operation name.</param>
    /// <returns>The refusal answer.</returns>
    private IResult SchemaFailure(TbepException failure, Caller caller, string collection, string operation)
    {
        if (failure.Detail.Contains("another channel", StringComparison.Ordinal))
        {
            this.audit.Record(
                AuditKind.TenantRefused,
                caller.Tenant,
                caller.Principal,
                operation,
                AuditOutcome.Refused,
                failure.Detail);
            return Results.Json(
                new ErrorDto("tenant-refused", $"the schema surface of {collection} belongs to another tenant's channel"),
                statusCode: StatusCodes.Status403Forbidden);
        }

        return this.Failure(failure, caller);
    }

    // ------------------------------------------------------------------
    // Subscription endpoints (SSE and WebSocket over the backbone)
    // ------------------------------------------------------------------

    internal async Task StreamSseAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            await refused.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var (collections, invalid) = this.Collections(context);
        if (invalid is not null)
        {
            await invalid.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var caller = ResolveCaller(context);
        var from = FromSequence(context, context.Request.Headers["Last-Event-ID"].ToString());
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        await using var writer = new StreamWriter(context.Response.Body, new UTF8Encoding(false)) { AutoFlush = false };
        await this.PumpAsync(
            context,
            caller,
            collections,
            from,
            async (name, id, payload) =>
            {
                await writer.WriteLineAsync($"id: {id}").ConfigureAwait(false);
                await writer.WriteLineAsync($"event: {name}").ConfigureAwait(false);
                await writer.WriteLineAsync($"data: {JsonSerializer.Serialize(payload, Json.Options)}").ConfigureAwait(false);
                await writer.WriteLineAsync().ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task StreamWebSocketAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            await refused.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var (collections, invalid) = this.Collections(context);
        if (invalid is not null)
        {
            await invalid.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var caller = ResolveCaller(context);
        var from = FromSequence(context, context.Request.Query["from"].ToString());
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var send = new SemaphoreSlim(1, 1);
        try
        {
            await this.PumpAsync(
                context,
                caller,
                collections,
                from,
                async (name, id, payload) =>
                {
                    await send.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        payload["kind"] = name;
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json.Options);
                        await socket.SendAsync(bytes, System.Net.WebSockets.WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        send.Release();
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (System.Net.WebSockets.WebSocketException)
        {
            // The consumer went away; the stream closes cleanly below.
        }
    }

    private async Task PumpAsync(
        HttpContext context,
        Caller caller,
        string[]? collections,
        ulong? from,
        Func<string, ulong, Dictionary<string, object?>, Task> writer,
        CancellationToken cancellationToken)
    {
        BackboneStream stream;
        try
        {
            stream = await this.hub.OpenAsync(caller.Tenant, caller.Principal, collections, from, cancellationToken).ConfigureAwait(false);
            stream.Principal = caller.Principal;
        }
        catch (Exception failure)
        {
            context.Response.StatusCode = failure switch
            {
                EngineUnavailableException => StatusCodes.Status503ServiceUnavailable,
                TbepException => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status500InternalServerError,
            };
            if (context.Response.ContentType?.StartsWith("text/event-stream", StringComparison.Ordinal) != true)
            {
                await context.Response.WriteAsJsonAsync(new ErrorDto("stream-refused", failure.Message), cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        try
        {
            await writer("opened", stream.StartSequence, new Dictionary<string, object?>
            {
                ["subscriptionId"] = stream.SubscriptionId,
                ["startSequence"] = stream.StartSequence,
                ["collections"] = stream.Collections,
            }).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested
                && await stream.ReadAsync(cancellationToken).ConfigureAwait(false) is { } frame)
            {
                switch (frame)
                {
                    case StreamEvent.Batch batch:
                        await writer(
                            "batch",
                            batch.Body.NextSequence > 0 ? batch.Body.NextSequence - 1 : 0,
                            new Dictionary<string, object?>
                            {
                                ["subscriptionId"] = batch.Body.SubscriptionId,
                                ["fromSequence"] = batch.Body.FromSequence,
                                ["events"] = batch.Body.Events.Select(e => new Dictionary<string, object?>
                                {
                                    ["sequence"] = e.Sequence,
                                    ["operationId"] = e.OperationId,
                                    ["kind"] = e.Kind.Variant.ToSnakeCase(),
                                    ["collection"] = e.Collection,
                                    ["document"] = e.Document,
                                }).ToArray(),
                                ["nextSequence"] = batch.Body.NextSequence,
                            }).ConfigureAwait(false);
                        break;
                    case StreamEvent.Trigger trigger:
                        await writer(
                            "trigger",
                            trigger.Body.Sequence,
                            new Dictionary<string, object?>
                            {
                                ["subscriptionId"] = trigger.Body.SubscriptionId,
                                ["collection"] = trigger.Body.Collection,
                                ["sequence"] = trigger.Body.Sequence,
                                ["operationId"] = trigger.Body.OperationId,
                            }).ConfigureAwait(false);
                        break;
                    case StreamEvent.Closed closed:
                        await writer(
                            "closed",
                            stream.LastSequence,
                            new Dictionary<string, object?>
                            {
                                ["subscriptionId"] = closed.Body.SubscriptionId,
                                ["reason"] = closed.Body.Reason.Variant.ToSnakeCase(),
                                ["dropped"] = stream.Dropped,
                            }).ConfigureAwait(false);
                        break;
                    case StreamEvent.Dropped drop:
                        // The event id is the consumer's safe checkpoint:
                        // the last sequence still serveable without a
                        // hole (one before the dropped region's boundary,
                        // which the payload carries) — a reconnect with
                        // that id re-delivers the missed range.
                        await writer(
                            "drop",
                            drop.ResumeFrom > 0 ? drop.ResumeFrom - 1 : 0,
                            new Dictionary<string, object?>
                            {
                                ["subscriptionId"] = stream.SubscriptionId,
                                ["resumeFrom"] = drop.ResumeFrom,
                                ["dropped"] = stream.Dropped,
                            }).ConfigureAwait(false);
                        break;
                    case StreamEvent.GenerationLost:
                        await writer(
                            "closed",
                            stream.LastSequence,
                            new Dictionary<string, object?>
                            {
                                ["subscriptionId"] = stream.SubscriptionId,
                                ["reason"] = "engine-generation-lost",
                                ["dropped"] = stream.Dropped,
                                ["resumeFrom"] = stream.LastSequence,
                            }).ConfigureAwait(false);
                        break;
                }
            }
        }
        finally
        {
            await this.hub.CloseAsync(stream, reason: "consumer-disconnected").ConfigureAwait(false);
        }
    }

    internal async Task<IResult> StreamAcknowledgeAsync(HttpContext context, ulong id, StreamAckDto request, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        var caller = ResolveCaller(context);
        try
        {
            var answer = await this.supervisor
                .CallAsync(
                    caller.Tenant,
                    caller.Principal,
                    "backbone.acknowledge",
                    channel => channel.BackboneAcknowledgeAsync(id, request.ThroughSequence, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(new StreamAckResponseDto(answer.SubscriptionId, answer.AcknowledgedThrough));
        }
        catch (TbepException failure) when (failure.Code?.Variant == "SubscriptionRefused")
        {
            return this.TenantOrStale(failure, caller, id, "subscription");
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    internal async Task<IResult> StreamCloseAsync(HttpContext context, ulong id, CancellationToken cancellationToken)
    {
        if (this.Gate(context) is { } refused)
        {
            return refused;
        }

        var caller = ResolveCaller(context);
        try
        {
            var answer = await this.supervisor
                .CallAsync(
                    caller.Tenant,
                    caller.Principal,
                    "backbone.unsubscribe",
                    channel => channel.BackboneUnsubscribeAsync(id, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            this.audit.Record(
                AuditKind.SubscriptionClosed,
                caller.Tenant,
                caller.Principal,
                operation: "backbone.unsubscribe",
                outcome: AuditOutcome.Accepted,
                detail: $"subscription {id}");
            return Results.Json(new StreamAckResponseDto(answer.SubscriptionId, 0));
        }
        catch (TbepException failure) when (failure.Code?.Variant == "SubscriptionRefused")
        {
            return this.TenantOrStale(failure, caller, id, "subscription");
        }
        catch (Exception failure)
        {
            return this.Failure(failure, caller);
        }
    }

    // ------------------------------------------------------------------
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
                new ErrorDto("checkpoint-refused", failure.Detail) { ResyncRequired = true },
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
                new ErrorDto("checkpoint-refused", failure.Detail) { ResyncRequired = true },
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
            return Results.Json(new ErrorDto("checkpoint-refused", failure.Detail), statusCode: StatusCodes.Status409Conflict);
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
    // The local gate, scope validation, and error mapping
    // ------------------------------------------------------------------

    private IResult? Gate(HttpContext context)
    {
        var principal = context.Request.Headers["X-TB-Principal"].ToString();
        var tenant = context.Request.Headers["X-TB-Tenant"].ToString();
        if (IsPresent(principal) && IsPresent(tenant))
        {
            return null;
        }

        // The local gate: no backend-supplied context, no engine frame —
        // the refusal happens here, before anything is sent (the caller
        // is the trusted local backend; customer auth lives exclusively
        // at the Platform, PD-2026-08-26-11 D1).
        this.audit.Record(
            AuditKind.GateRefused,
            tenant: IsPresent(tenant) ? tenant : null,
            principal: IsPresent(principal) ? principal : null,
            operation: "gate",
            outcome: AuditOutcome.Refused,
            detail: "backend-supplied context missing");
        return Results.Json(
            new ErrorDto("unauthorized", "backend-supplied context (X-TB-Principal, X-TB-Tenant) is required"),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    private IResult? Scope(HttpContext context, string candidate)
    {
        if (candidate.Length is > 0 and <= 128 && candidate.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
        {
            return null;
        }

        var caller = ResolveCaller(context);
        this.audit.Record(
            AuditKind.ScopeRefused,
            caller.Tenant,
            caller.Principal,
            operation: "scope-validation",
            outcome: AuditOutcome.Refused,
            detail: "the named scope does not validate");
        return Results.Json(
            new ErrorDto("scope-invalid", "collections, documents, and family names are bounded identifiers"),
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    private static IResult? Idempotency(HttpContext context)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        try
        {
            TbepWire.ValidateIdempotency(key);
            return null;
        }
        catch (TbepWire.WireException)
        {
            return Results.Json(
                new ErrorDto("idempotency-key-invalid", "mutations require a non-empty, bounded, control-free Idempotency-Key"),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private (string[]? Collections, IResult? Invalid) Collections(HttpContext context)
    {
        var raw = context.Request.Query["collections"].ToString();
        if (raw.Length == 0)
        {
            return (null, null);
        }

        var collections = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var collection in collections)
        {
            if (this.Scope(context, collection) is { } invalid)
            {
                return (null, invalid);
            }
        }

        return (collections, null);
    }

    private static ulong? FromSequence(HttpContext context, string lastEventId)
    {
        // An explicit <c>from</c> query is an inclusive replay start; the
        // SSE <c>Last-Event-ID</c> header names the last event the
        // consumer saw, so the resume point is strictly after it — the
        // reconnect replays exactly the missed events, no duplicates.
        if (context.Request.Query["from"].ToString() is { Length: > 0 } fromQuery && ulong.TryParse(fromQuery, out var parsed))
        {
            return parsed;
        }

        return lastEventId.Length > 0 && ulong.TryParse(lastEventId, out var lastSeen) ? lastSeen + 1 : null;
    }

    private static ExpectedVersion Expected(HttpContext context)
    {
        var header = context.Request.Headers["X-TB-Expected"].ToString();
        return header switch
        {
            "any" => ExpectedVersion.Any(),
            { Length: > 0 } when ulong.TryParse(header, out var version) => ExpectedVersion.Exact(version),
            _ => ExpectedVersion.Absent(),
        };
    }

    private static bool IsPresent(string value) =>
        value.Length is > 0 and <= 128 && !value.Any(char.IsControl) && !string.IsNullOrWhiteSpace(value);

    private static long queryId;

    private static ulong NextQueryId() => (ulong)Interlocked.Increment(ref queryId);

    private static Caller ResolveCaller(HttpContext context) => new(
        context.Request.Headers["X-TB-Principal"].ToString(),
        context.Request.Headers["X-TB-Tenant"].ToString());

    private IResult? StaleSession(ulong id, Caller caller)
    {
        if (this.supervisor.Sessions.ContainsKey(id))
        {
            return null;
        }

        // The host never orphans session state: a session id this host
        // does not track (ended with an engine generation, or closed) is
        // answered as ended — truthfully, without touching the engine.
        this.audit.Record(
            AuditKind.SessionEndedByRestart,
            caller.Tenant,
            caller.Principal,
            operation: "sync.session",
            outcome: AuditOutcome.Refused,
            detail: $"session {id} is not live on this host");
        return Results.Json(
            new ErrorDto("session-ended", $"session {id} is not live on this host; open a new session"),
            statusCode: StatusCodes.Status410Gone);
    }

    private IResult TenantOrStale(TbepException failure, Caller caller, ulong id, string kind)
    {
        if (failure.Detail.Contains("another channel", StringComparison.Ordinal))
        {
            // The engine enforced the isolation (PD-2026-08-26-11 D1):
            // cross-tenant lifecycle frames are refused and audited here.
            this.audit.Record(
                AuditKind.TenantRefused,
                caller.Tenant,
                caller.Principal,
                operation: $"{kind}.lifecycle",
                outcome: AuditOutcome.Refused,
                detail: failure.Detail);
            return Results.Json(
                new ErrorDto("tenant-refused", $"the {kind} belongs to another tenant's channel"),
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (kind == "session" && this.supervisor.Sessions.TryRemove(id, out var ended))
        {
            // The engine refused a lifecycle frame naming this session
            // without the cross-tenant detail: the session is provably
            // not live on the engine (ended there, without a generation
            // loss the host would hear) — the host stops tracking it, so
            // the registry never lingers state the engine already ended.
            this.audit.Record(
                AuditKind.SessionEndedByRestart,
                caller.Tenant,
                caller.Principal,
                operation: "sync.session",
                outcome: AuditOutcome.Noted,
                detail: $"session {ended.SessionId} is not live on the engine");
        }

        return Results.Json(
            new ErrorDto($"{kind}-ended", failure.Detail),
            statusCode: StatusCodes.Status410Gone);
    }

    private IResult Failure(Exception failure, Caller? caller = null)
    {
        switch (failure)
        {
            case EngineUnavailableException:
                return Results.Json(
                    new ErrorDto("engine-unavailable", failure.Message),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            case TbepException typed when typed.Code is not null:
                var status = typed.Code.Variant switch
                {
                    // The caller's frame was rejected by the codec or the
                    // operation contract: fix the request.
                    "InvalidRequest" or "IdempotencyPairing" or "MalformedFrame" or "UnknownFrameKind"
                        or "DigestMismatch" or "UnsupportedVersion" or "UnboundChannel" or "AlreadyBound"
                        => StatusCodes.Status400BadRequest,
                    "OperationRefused" => StatusCodes.Status501NotImplemented,
                    "SessionRefused" or "SubscriptionRefused" => StatusCodes.Status410Gone,
                    "CheckpointRefused" => StatusCodes.Status409Conflict,
                    "EngineFailure" => StatusCodes.Status500InternalServerError,
                    "ShuttingDown" => StatusCodes.Status503ServiceUnavailable,
                    _ => StatusCodes.Status502BadGateway,
                };
                if (typed.Code.Variant is "SessionRefused" or "SubscriptionRefused")
                {
                    return this.TenantOrStale(typed, caller ?? new Caller("unknown", "unknown"), 0, typed.Code.Variant == "SessionRefused" ? "session" : "subscription");
                }

                return Results.Json(new ErrorDto(typed.Code.Variant.ToSnakeCase(), typed.Detail), statusCode: status);
            default:
                return Results.Json(
                    new ErrorDto("host-failure", failure.Message),
                    statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult Unavailable(EngineUnavailableException failure) => Results.Json(
        new ErrorDto("engine-unavailable", failure.Message),
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private sealed record Caller(string Principal, string Tenant);

    private static class Json
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    }
}
