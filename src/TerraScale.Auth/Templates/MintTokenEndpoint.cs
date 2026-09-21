namespace TerraScale.Auth.Templates;

using System.Net;
using System.Text.RegularExpressions;
using TerraScale.Auth.Api;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Keys;
using TerraScale.Auth.Store;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.Templates.JwtTemplateSupport;

internal sealed class MintTokenEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/jwt-templates/{name}/tokens", HandleAsync);

    private static Task<IResult> HandleAsync(string slug, string name, HttpRequest request, AuthStore store, InstanceResolver resolver, KeyRing keys, CancellationToken ct)
        => WithScopeAsync(resolver, request, slug, async context =>
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
            }, ct);

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

    private static readonly Regex Placeholder = new(@"\{\{\s*([a-z_.]+)\s*\}\}", RegexOptions.Compiled);
}
