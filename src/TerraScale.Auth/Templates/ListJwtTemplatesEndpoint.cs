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

internal sealed class ListJwtTemplatesEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/v1/{slug}/jwt-templates", HandleAsync);

    private static Task<IResult> HandleAsync(string slug, HttpRequest request, AuthStore store, InstanceResolver resolver, CancellationToken ct)
        => WithScopeAsync(resolver, request, slug, async context =>
            {
                var templates = await store.ListJwtTemplatesAsync(context.Instance.Id, ct);
                return Results.Ok(templates.Select(Render).ToList());
            }, ct);
}
