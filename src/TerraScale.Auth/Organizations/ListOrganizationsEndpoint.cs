namespace TerraScale.Auth.Organizations;

using System.Net;
using TerraScale.Auth.Api;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Email;
using TerraScale.Auth.Store;
using TerraScale.Auth.Ui;
using TerraScale.Auth.Users;
using TerraScale.DotNet;
using TerraScale.Endpoints;
using static TerraScale.Auth.Organizations.OrganizationSupport;

internal sealed class ListOrganizationsEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/v1/{slug}/organizations", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(httpRequest, ct);
        if (context is null || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var organizations = await store.ListOrganizationsAsync(context.Instance.Id, ct);
        return Results.Ok(organizations.Select(org => Render(org, context.Instance.Id)).ToList());
    }
}
