namespace TerraScale.Auth.Webhooks;

using TerraScale.Auth.Api;
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
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<WebhookDispatcher>();

        // App.cs is shared, so there is no AddHostedService: the delivery engine starts
        // here instead — one loop per application, stopped by ApplicationStopping.
        var dispatcher = new WebhookDispatcher(store, logger);
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
                // Stored plaintext on purpose: unlike sk_ keys (hashed, because ts-auth only
                // verifies them), the signing secret must be recoverable — ts-auth itself uses
                // it to sign outbound deliveries. It is revealed exactly once, in this response.
                SigningSecret = Crypto.SecureTokens.RandomToken(32),
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
            dispatcher.EnsureInstance(context.Instance.Id);
            return Results.Ok(new { endpoint.Id, endpoint.Url, endpoint.Events, signing_secret = endpoint.SigningSecret });
        });

        app.MapGet("/v1/{slug}/webhooks", async (string slug, HttpRequest request, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
                return ApiResults.Unauthorized();

            var endpoints = await store.ListWebhookEndpointsAsync(context.Instance.Id, ct);
            return Results.Ok(endpoints.Select(ToListItem).ToArray());
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
            dispatcher.EnsureInstance(context.Instance.Id);
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
            return new { auditEvent.Id, auditEvent.Type, actor_id = actor, auditEvent.Data, created_at = auditEvent.CreatedAt };
        return new { auditEvent.Id, auditEvent.Type, auditEvent.Data, created_at = auditEvent.CreatedAt };
    }
}

/// <summary>Request body for webhook endpoint creation.</summary>
internal sealed record CreateWebhookRequest(string? Url, string[]? Events);
