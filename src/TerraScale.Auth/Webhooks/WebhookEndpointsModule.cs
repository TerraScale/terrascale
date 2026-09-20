namespace TerraScale.Auth.Webhooks;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;

/// <summary>Webhooks feature: outbound endpoint management (sk_ auth), the
/// audit-event query API, and the delivery engine boot hook.</summary>
public static class WebhookEndpointsModule
{
    /// <summary>Maps Webhooks endpoints and starts the webhook dispatcher.</summary>
    public static void Map(WebApplication app)
    {
        var store = app.Services.GetRequiredService<AuthStore>();
        var resolver = app.Services.GetRequiredService<InstanceResolver>();
        var options = app.Services.GetRequiredService<AuthOptions>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<WebhookDispatcher>();

        // App.cs is shared, so there is no AddHostedService: the delivery engine starts
        // here instead — one loop per application, stopped by ApplicationStopping.
        var dispatcher = new WebhookDispatcher(store, logger, options.MasterKey);
        _ = Task.Run(() => dispatcher.RunAsync(app.Lifetime.ApplicationStopping));

        app.MapPost("/v1/{slug}/webhooks", async (string slug, HttpRequest request, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            var body = await request.ReadFromJsonAsync<CreateWebhookRequest>(ct);
            if (body is not { Url: not null, Events: { Length: > 0 } })
                return ApiResults.BadRequest("invalid_request", "url and a non-empty events list are required");
            if (!Uri.TryCreate(body.Url, UriKind.Absolute, out var uri) || !IsAllowedWebhookUrl(uri))
                return ApiResults.BadRequest("invalid_request", "url must be an absolute https URL");
            if (body.Events.Any(string.IsNullOrWhiteSpace))
                return ApiResults.BadRequest("invalid_request", "events must be non-empty event types");

            var endpoint = new WebhookEndpoint
            {
                Id = "wh_" + Crypto.SecureTokens.RandomToken(20),
                InstanceId = context.Instance.Id,
                Url = body.Url,
                Events = body.Events,
                SigningSecretBox = SecretBox.Encrypt(options.MasterKey, System.Text.Encoding.UTF8.GetBytes(Crypto.SecureTokens.RandomToken(32))),
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            await store.CreateWebhookEndpointAsync(endpoint, ct);
            await store.CreateAuditEventAsync(new AuditEvent
            {
                Id = "evt_" + Crypto.SecureTokens.RandomToken(20),
                InstanceId = context.Instance.Id,
                Type = "webhook.created",
                ActorId = context.ApiKeyId,
                Data = new Dictionary<string, object> { ["endpoint_id"] = endpoint.Id },
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, ct);
            await dispatcher.EnsureInstanceAsync(context.Instance.Id, ct);
            var signingSecret = System.Text.Encoding.UTF8.GetString(SecretBox.Decrypt(options.MasterKey, endpoint.SigningSecretBox));
            return Results.Ok(new { endpoint.Id, endpoint.Url, endpoint.Events, signing_secret = signingSecret });
        });

        app.MapGet("/v1/{slug}/webhooks", async (string slug, HttpRequest request, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            var endpoints = await store.ListWebhookEndpointsAsync(context.Instance.Id, ct);
            return Results.Ok(endpoints.Select(ToListItem).ToArray());
        });

        app.MapGet("/v1/{slug}/webhooks/dead-letter", async (string slug, HttpRequest request, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();
            var deliveries = await store.ListWebhookDeliveriesAsync(context.Instance.Id, ct);
            return Results.Ok(deliveries.Where(d => d.Status == "dead").OrderByDescending(d => d.UpdatedAt).Select(d => new
            {
                d.Id,
                endpoint_id = d.EndpointId,
                event_id = d.EventId,
                d.Attempts,
                last_error = d.LastError,
                updated_at = d.UpdatedAt,
            }).ToArray());
        });

        app.MapPost("/v1/{slug}/webhooks/dead-letter/{id}/replay", async (string slug, string id, HttpRequest request, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();
            var replayed = await dispatcher.ReplayAsync(context.Instance.Id, id, ct);
            return replayed ? Results.Ok(new { replayed = true }) : ApiResults.NotFound();
        });

        app.MapDelete("/v1/{slug}/webhooks/{id}", async (string slug, string id, HttpRequest request, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            if (await store.GetWebhookEndpointAsync(id, ct) is not { } endpoint || endpoint.InstanceId != context.Instance.Id)
                return ApiResults.NotFound();
            await store.DeleteWebhookEndpointAsync(id, ct);
            await store.CreateAuditEventAsync(new AuditEvent
            {
                Id = "evt_" + Crypto.SecureTokens.RandomToken(20),
                InstanceId = context.Instance.Id,
                Type = "webhook.deleted",
                ActorId = context.ApiKeyId,
                Data = new Dictionary<string, object> { ["endpoint_id"] = id },
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, ct);
            return Results.NoContent();
        });

        app.MapPost("/v1/{slug}/webhooks/{id}/test", async (string slug, string id, HttpRequest request, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            if (await store.GetWebhookEndpointAsync(id, ct) is not { } endpoint || endpoint.InstanceId != context.Instance.Id)
                return ApiResults.NotFound();

            await store.CreateAuditEventAsync(new AuditEvent
            {
                Id = "evt_" + Crypto.SecureTokens.RandomToken(20),
                InstanceId = context.Instance.Id,
                Type = "webhook.test",
                ActorId = context.ApiKeyId,
                Data = new Dictionary<string, object> { ["endpoint_id"] = id },
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, ct);
            await dispatcher.EnsureInstanceAsync(context.Instance.Id, ct);
            return Results.Ok(new { queued = true });
        });

        app.MapGet("/v1/{slug}/audit-events", async (string slug, HttpRequest request, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            var raw = request.Query["limit"].ToString();
            var limit = 50;
            if (raw.Length > 0 && (!int.TryParse(raw, out limit) || limit is < 1 or > 100))
                return ApiResults.BadRequest("invalid_request", "limit must be an integer between 1 and 100");

            var events = await store.ListAuditEventsAsync(context.Instance.Id, limit, ct);
            return Results.Ok(events.OrderByDescending(e => e.CreatedAt).Select(ToAuditJson).ToArray());
        });
    }

    /// <summary>https everywhere; plain http only for loopback hosts, so local
    /// e2e sinks and dev-time testing can receive deliveries.</summary>
    private static bool IsAllowedWebhookUrl(Uri uri) =>
        uri.Scheme == "https"
        || (uri.Scheme == "http" && uri.Host is "127.0.0.1" or "localhost" or "::1");

    private static object ToListItem(WebhookEndpoint endpoint) =>
        new { endpoint.Id, endpoint.Url, endpoint.Events, endpoint.Enabled, created_at = endpoint.CreatedAt };

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

/// <summary>Request body for webhook endpoint creation.</summary>
internal sealed record CreateWebhookRequest(string? Url, string[]? Events);
