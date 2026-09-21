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

internal sealed class CreateJwtTemplateEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPost("/v1/{slug}/jwt-templates", HandleAsync);

    private static Task<IResult> HandleAsync(string slug, HttpRequest request, AuthStore store, InstanceResolver resolver, CancellationToken ct)
        => WithScopeAsync(resolver, request, slug, async context =>
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
            }, ct);
}
