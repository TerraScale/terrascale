namespace TerraScale.Auth.Webhooks;

using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.Endpoints;

internal sealed class CreateWebhookEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/webhooks", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest request, CancellationToken ct, AuthStore store, InstanceResolver resolver, AuthOptions options, WebhookDispatcher dispatcher)
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
    }

    /// <summary>https everywhere; plain http only for loopback hosts, so local
    /// e2e sinks and dev-time testing can receive deliveries.</summary>
    private static bool IsAllowedWebhookUrl(Uri uri) =>
        uri.Scheme == "https"
        || (uri.Scheme == "http" && uri.Host is "127.0.0.1" or "localhost" or "::1");
}
