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

internal sealed class UpdateJwtTemplateEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapPatch("/v1/{slug}/jwt-templates/{name}", HandleAsync);

    private static Task<IResult> HandleAsync(string slug, string name, HttpRequest request, AuthStore store, InstanceResolver resolver, CancellationToken ct)
        => WithScopeAsync(resolver, request, slug, async context =>
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
            }, ct);
}
