// <copyright file="TypedApi.Infrastructure.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TerraBase.Protocol;

public sealed partial class TypedApi
{
    // The local gate, scope validation, and error mapping
    // ------------------------------------------------------------------

    private static EngineHealthSnapshot PublicHealth(EngineHealthSnapshot health) => health with
    {
        Detail = PublicHealthDetail(health),
    };

    private static string? PublicHealthDetail(EngineHealthSnapshot health) => health.Detail is null
        ? null
        : health.Status == EngineStatus.Unavailable
            ? "engine unavailable"
            : "engine health unavailable";

    private static AuditRecord PublicAudit(AuditRecord record) => record with
    {
        Detail = record.Kind switch
        {
            AuditKind.EngineLost => "engine unavailable",
            AuditKind.EngineStarted => "engine started",
            AuditKind.EngineShutdown => "engine shutdown accepted",
            AuditKind.TenantRefused => "tenant lifecycle refused",
            AuditKind.SessionEndedByRestart => "session ended with engine generation",
            _ => record.Detail,
        },
    };

    private IResult? Gate(HttpContext context, params string[] roles)
    {
        var principal = context.Request.Headers["X-TB-Principal"].ToString();
        var tenant = context.Request.Headers["X-TB-Tenant"].ToString();
        if (!IsPresent(principal) || !IsPresent(tenant))
        {
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

        if (roles.Length == 0)
        {
            return null;
        }

        var role = context.Request.Headers["X-TB-Role"].ToString();
        if (roles.Contains(role, StringComparer.Ordinal))
        {
            return null;
        }

        this.audit.Record(
            AuditKind.GateRefused,
            tenant,
            principal,
            operation: "role-gate",
            outcome: AuditOutcome.Refused,
            detail: "the backend role is not permitted for this operation");
        return Results.Json(
            new ErrorDto("forbidden", "the backend role is not permitted for this operation"),
            statusCode: StatusCodes.Status403Forbidden);
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

        if (lastEventId.Length == 0 || !ulong.TryParse(lastEventId, out var lastSeen))
        {
            return null;
        }

        // A client can legally reconnect with the maximum representable
        // event id. Saturate rather than wrapping to zero and replaying the
        // entire journal.
        return lastSeen == ulong.MaxValue ? ulong.MaxValue : lastSeen + 1;
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
                detail: "tenant lifecycle refused");
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
            new ErrorDto($"{kind}-ended", $"the {kind} is no longer live on the engine"),
            statusCode: StatusCodes.Status410Gone);
    }

    private IResult Failure(Exception failure, Caller? caller = null)
    {
        switch (failure)
        {
            case FormatException:
                return Results.Json(
                    new ErrorDto("input-invalid", "request bytes must be valid base64"),
                    statusCode: StatusCodes.Status400BadRequest);
            case EngineUnavailableException:
                return Results.Json(
                    new ErrorDto("engine-unavailable", "the engine is unavailable"),
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

                return Results.Json(new ErrorDto(typed.Code.Variant.ToSnakeCase(), PublicEngineFailureDetail(typed.Code.Variant)), statusCode: status);
            default:
                return Results.Json(
                    new ErrorDto("host-failure", "the host could not complete the request"),
                    statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static string PublicEngineFailureDetail(string code) => code switch
    {
        "CheckpointRefused" => "the checkpoint was refused; resync is required",
        "OperationRefused" => "the engine does not support this operation",
        "SessionRefused" or "SubscriptionRefused" => "the lifecycle is no longer live on the engine",
        "ShuttingDown" => "the engine is shutting down",
        _ => "the engine rejected the request",
    };

    private static IResult Unavailable(EngineUnavailableException _) => Results.Json(
        new ErrorDto("engine-unavailable", "the engine is unavailable"),
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private sealed record Caller(string Principal, string Tenant);

    private static class Json
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    }
}
