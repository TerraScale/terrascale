namespace TerraScale.Auth.Templates;

using System.Net;
using System.Text.RegularExpressions;
using TerraScale.Auth.Api;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.DotNet;

/// <summary>JWT template endpoints: management CRUD over named claim templates,
/// plus on-demand minting of signed tokens with <c>{{user.*}}</c>/<c>{{org.*}}</c> substitution.</summary>
public static class JwtTemplateEndpoints
{
    private static readonly Regex Placeholder = new(@"\{\{\s*([a-z_.]+)\s*\}\}", RegexOptions.Compiled);

    /// <summary>Maps JWT template endpoints (and the org-domain module that rides in this slice's directory).</summary>
    public static void Map(WebApplication app)
    {
        MapTemplates(app);
        OrgDomainEndpoints.Map(app);
    }

    private static void MapTemplates(WebApplication app)
    {
        app.MapPost("/v1/{slug}/jwt-templates", (string slug, HttpRequest request, AuthStore store, InstanceResolver resolver, CancellationToken ct) =>
            WithScopeAsync(resolver, request, slug, async context =>
            {
                var body = await request.ReadFromJsonAsync<CreateJwtTemplateRequest>(ct);
                if (body?.Name is null || !IsValidName(body.Name))
                    return ApiResults.BadRequest("invalid_request", "name must be 1-48 url-safe characters (a-z, 0-9, -, _)");
                if (body.Claims is null || body.Claims.Count == 0)
                    return ApiResults.BadRequest("invalid_request", "claims must be a non-empty object");
                if (body.LifetimeSeconds is <= 0)
                    return ApiResults.BadRequest("invalid_request", "lifetime_seconds must be positive");

                var name = NormalizeName(body.Name);
                if ((await store.ListJwtTemplatesAsync(context.Instance.Id, ct)).Any(t => t.Name == name))
                    return ApiResults.Conflict("template_exists", "a template with this name already exists");

                var template = new JwtTemplate
                {
                    Id = "jtp_" + SecureTokens.RandomToken(20),
                    InstanceId = context.Instance.Id,
                    Name = name,
                    Claims = new Dictionary<string, string>(body.Claims, StringComparer.Ordinal),
                    LifetimeSeconds = body.LifetimeSeconds ?? 300,
                    CreatedAt = Now(),
                };
                await store.CreateJwtTemplateAsync(template, ct);
                return Results.Ok(Render(template));
            }, ct));

        app.MapGet("/v1/{slug}/jwt-templates", (string slug, HttpRequest request, AuthStore store, InstanceResolver resolver, CancellationToken ct) =>
            WithScopeAsync(resolver, request, slug, async context =>
            {
                var templates = await store.ListJwtTemplatesAsync(context.Instance.Id, ct);
                return Results.Ok(templates.Select(Render).ToList());
            }, ct));

        app.MapPatch("/v1/{slug}/jwt-templates/{name}", (string slug, string name, HttpRequest request, AuthStore store, InstanceResolver resolver, CancellationToken ct) =>
            WithScopeAsync(resolver, request, slug, async context =>
            {
                var template = await FindByNameAsync(store, context.Instance.Id, name, ct);
                if (template is null)
                    return ApiResults.NotFound("template_not_found", "no such jwt template");

                var body = await request.ReadFromJsonAsync<UpdateJwtTemplateRequest>(ct);
                if (body is null)
                    return ApiResults.BadRequest("invalid_request", "a json body is required");
                if (body.Name is not null && !IsValidName(body.Name))
                    return ApiResults.BadRequest("invalid_request", "name must be 1-48 url-safe characters (a-z, 0-9, -, _)");
                if (body.LifetimeSeconds is <= 0)
                    return ApiResults.BadRequest("invalid_request", "lifetime_seconds must be positive");

                var newName = body.Name is null ? null : NormalizeName(body.Name);
                if (newName is not null && newName != template.Name
                    && (await store.ListJwtTemplatesAsync(context.Instance.Id, ct)).Any(t => t.Name == newName && t.Id != template.Id))
                    return ApiResults.Conflict("template_exists", "a template with this name already exists");

                template = template with
                {
                    Name = newName ?? template.Name,
                    Claims = body.Claims is null ? template.Claims : new Dictionary<string, string>(body.Claims, StringComparer.Ordinal),
                    LifetimeSeconds = body.LifetimeSeconds ?? template.LifetimeSeconds,
                };
                await store.UpsertJwtTemplateAsync(template, ct);
                return Results.Ok(Render(template));
            }, ct));

        app.MapDelete("/v1/{slug}/jwt-templates/{name}", (string slug, string name, HttpRequest request, AuthStore store, InstanceResolver resolver, CancellationToken ct) =>
            WithScopeAsync(resolver, request, slug, async context =>
            {
                var template = await FindByNameAsync(store, context.Instance.Id, name, ct);
                if (template is null)
                    return ApiResults.NotFound("template_not_found", "no such jwt template");

                await store.DeleteJwtTemplateAsync(template.Id, ct);
                return Results.NoContent();
            }, ct));

        app.MapPost("/v1/{slug}/jwt-templates/{name}/tokens", (string slug, string name, HttpRequest request, AuthStore store, InstanceResolver resolver, KeyRing keys, CancellationToken ct) =>
            WithScopeAsync(resolver, request, slug, async context =>
            {
                var template = await FindByNameAsync(store, context.Instance.Id, name, ct);
                if (template is null)
                    return ApiResults.NotFound("template_not_found", "no such jwt template");

                var body = await request.ReadFromJsonAsync<MintTokenRequest>(ct);
                if (body is null || string.IsNullOrWhiteSpace(body.UserId))
                    return ApiResults.BadRequest("invalid_request", "user_id is required");
                var user = await store.GetUserAsync(body.UserId.Trim(), ct);
                if (user is null || user.InstanceId != context.Instance.Id)
                    return ApiResults.NotFound("user_not_found", "no such user in this instance");

                var jwt = await MintAsync(store, keys, template, user, ct);
                return Results.Ok(new { jwt });
            }, ct));
    }

    // ---- Minting --------------------------------------------------------------------

    /// <summary>Renders the template's claims for the user and signs them with the instance's current ES256 key.</summary>
    private static async Task<string> MintAsync(AuthStore store, KeyRing keys, JwtTemplate template, User user, CancellationToken ct)
    {
        OrgMembership? membership = null;
        if (template.Claims.Values.Any(value => value.Contains("{{org.", StringComparison.Ordinal)))
            membership = (await store.ListMembershipsByUserAsync(user.Id, ct))
                .Where(m => m.InstanceId == template.InstanceId)
                .OrderBy(m => m.CreatedAt)
                .FirstOrDefault();

        var claims = template.Claims.ToDictionary(
            pair => pair.Key,
            pair => RenderClaim(pair.Value, user, membership),
            StringComparer.Ordinal);
        var key = await keys.GetCurrentAsync(template.InstanceId, ct);
        return JsonWebToken.Sign(claims, DateTimeOffset.UtcNow.AddSeconds(template.LifetimeSeconds), key);
    }

    /// <summary>Substitutes <c>{{user.FIELD}}</c> and <c>{{org.FIELD}}</c> tokens; unknown placeholders render as empty strings.</summary>
    private static string RenderClaim(string value, User user, OrgMembership? membership) =>
        Placeholder.Replace(value, match => match.Groups[1].Value switch
        {
            "user.id" => user.Id,
            "user.email" => user.Email ?? "",
            "user.username" => user.Username ?? "",
            "user.name" => user.Name ?? "",
            "user.email_verified" => user.EmailVerified ? "true" : "false",
            "org.id" => membership?.OrgId ?? "",
            "org.role" => membership?.Role ?? "",
            _ => "",
        });

    // ---- Shared helpers (also used by OrgDomainEndpoints) -----------------------------

    /// <summary>Resolves the sk_ key (401 on a bad key), checks the slug matches the
    /// key's instance (401 on mismatch — a foreign-instance key must look exactly like
    /// an invalid one), then runs the handler with the instance context.</summary>
    internal static async Task<IResult> WithScopeAsync(
        InstanceResolver resolver, HttpRequest request, string slug,
        Func<InstanceContext, Task<IResult>> handler, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(request, ct);
        if (context is null || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();
        return await handler(context);
    }

    internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    internal static Task AuditAsync(AuthStore store, string instanceId, string type, string? actorId, Dictionary<string, object> data, CancellationToken ct) =>
        store.CreateAuditEventAsync(new AuditEvent
        {
            Id = "evt_" + SecureTokens.RandomToken(20),
            InstanceId = instanceId,
            Type = type,
            ActorId = actorId,
            Data = data,
            CreatedAt = Now(),
        }, ct);

    private static bool IsValidName(string? name)
    {
        var value = name?.Trim().ToLowerInvariant();
        return value is { Length: >= 1 and <= 48 }
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    }

    private static string NormalizeName(string name) => name.Trim().ToLowerInvariant();

    private static async Task<JwtTemplate?> FindByNameAsync(AuthStore store, string instanceId, string name, CancellationToken ct) =>
        (await store.ListJwtTemplatesAsync(instanceId, ct)).FirstOrDefault(t => t.Name == name);

    private static object Render(JwtTemplate template) =>
        new { id = template.Id, name = template.Name, claims = template.Claims, lifetime_seconds = template.LifetimeSeconds, created_at = template.CreatedAt };
}

/// <summary>Create JWT template request body.</summary>
internal sealed record CreateJwtTemplateRequest(
    string? Name,
    Dictionary<string, string>? Claims,
    [property: System.Text.Json.Serialization.JsonPropertyName("lifetime_seconds")] int? LifetimeSeconds);

/// <summary>Update JWT template request body; null fields are left unchanged.</summary>
internal sealed record UpdateJwtTemplateRequest(
    string? Name,
    Dictionary<string, string>? Claims,
    [property: System.Text.Json.Serialization.JsonPropertyName("lifetime_seconds")] int? LifetimeSeconds);

/// <summary>Mint JWT template token request body.</summary>
internal sealed record MintTokenRequest([property: System.Text.Json.Serialization.JsonPropertyName("user_id")] string? UserId);
