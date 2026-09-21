namespace TerraScale.Auth.Webhooks;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Endpoints;

internal sealed class ListAuditEventsEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/v1/{slug}/audit-events", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, CancellationToken ct, AuthStore store, InstanceResolver resolver)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var raw = request.Query["limit"].ToString();
        var limit = 50;
        if (raw.Length > 0 && (!int.TryParse(raw, out limit) || limit is < 1 or > 100))
            return ApiResults.BadRequest("invalid_request", "limit must be an integer between 1 and 100");

        var events = await store.ListAuditEventsAsync(context.Instance.Id, limit, ct);
        return Results.Ok(events.OrderByDescending(e => e.CreatedAt).Select(ToAuditJson).ToArray());
    }

    private static object ToAuditJson(AuditEvent auditEvent)
    {
        if (auditEvent.ActorId is { } actor)
            return new
            {
                auditEvent.Id,
                instance_id = auditEvent.InstanceId,
                auditEvent.Type,
                actor_id = actor,
                auditEvent.Data,
                created_at = auditEvent.CreatedAt,
                integrity_version = auditEvent.IntegrityVersion,
                integrity_hash = auditEvent.IntegrityHash,
                integrity_signature = auditEvent.IntegritySignature,
                integrity_key_fingerprint = auditEvent.IntegrityKeyFingerprint,
            };
        return new
        {
            auditEvent.Id,
            instance_id = auditEvent.InstanceId,
            auditEvent.Type,
            auditEvent.Data,
            created_at = auditEvent.CreatedAt,
            integrity_version = auditEvent.IntegrityVersion,
            integrity_hash = auditEvent.IntegrityHash,
            integrity_signature = auditEvent.IntegritySignature,
            integrity_key_fingerprint = auditEvent.IntegrityKeyFingerprint,
        };
    }
}
