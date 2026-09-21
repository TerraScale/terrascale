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

internal sealed class DeleteJwtTemplateEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/v1/{slug}/jwt-templates/{name}", HandleAsync);

    private static Task<IResult> HandleAsync(string slug, string name, HttpRequest request, AuthStore store, InstanceResolver resolver, CancellationToken ct)
        => WithScopeAsync(resolver, request, slug, async context =>
            {
                var template = await FindByNameAsync(store, context.Instance.Id, name, ct);
                if (template is null)
                    return ApiResults.NotFound("template_not_found", "no such jwt template");

                await store.DeleteJwtTemplateAsync(template.Id, ct);
                return Results.NoContent();
            }, ct);
}
