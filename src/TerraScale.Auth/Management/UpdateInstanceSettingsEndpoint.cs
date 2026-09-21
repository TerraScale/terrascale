namespace TerraScale.Auth.Management;

using TerraScale.Auth.Api;
using TerraScale.Auth.Store;
using TerraScale.DotNet;
using TerraScale.Endpoints;

internal sealed class UpdateInstanceSettingsEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPatch("/v1/{slug}/instance", HandleAsync);

    private static async Task<IResult> HandleAsync(
            string slug, HttpRequest request, InstanceResolver resolver, AuthStore store, CancellationToken ct)
    {
        if (await resolver.ResolveAsync(request, ct) is not { } context || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var body = await request.ReadFromJsonAsync<InstanceSettingsPatch>(ct);
        if (body is null)
            return ApiResults.BadRequest("invalid_request", "body required");

        // Branding lands in every hosted page's HTML: only absolute http(s) URLs.
        // Scheme matters — Uri accepts javascript:, data:, vbscript:, and even bare
        // paths (as file:) as "absolute", so an allowlist is the gate.
        if (body.LogoUrl is { } logoUrl
            && (!Uri.TryCreate(logoUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")))
            return ApiResults.BadRequest("invalid_logo_url", "logo_url must be an absolute http(s) URL");
        if (body.ThemeColor is { } themeColor && !IsValidHexColor(themeColor))
            return ApiResults.BadRequest("invalid_theme_color", "theme_color must look like #6c5ce7");

        var updated = context.Instance with
        {
            Name = string.IsNullOrWhiteSpace(body.Name) ? context.Instance.Name : body.Name.Trim(),
            LogoUrl = body.LogoUrl ?? context.Instance.LogoUrl,
            ThemeColor = body.ThemeColor ?? context.Instance.ThemeColor,
        };
        await store.UpsertInstanceAsync(updated, ct);
        await store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + Crypto.SecureTokens.RandomToken(20),
            InstanceId = context.Instance.Id,
            Type = "instance.updated",
            ActorId = context.ApiKeyId,
            Data = new Dictionary<string, object>(),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, ct);

        return Results.Ok(new { id = updated.Id, slug = updated.Slug, name = updated.Name, logo_url = updated.LogoUrl, theme_color = updated.ThemeColor });
    }

    private static bool IsValidHexColor(string value) =>
        value.Length == 7 && value[0] == '#' && value[1..].All(c => char.IsAsciiHexDigit(c));
}
