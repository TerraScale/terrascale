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

internal sealed class ListUserOrganizationsEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/v1/{slug}/users/{userId}/organizations", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string userId, HttpRequest httpRequest, AuthStore store, InstanceResolver resolver, CancellationToken ct)
    {
        var context = await resolver.ResolveAsync(httpRequest, ct);
        if (context is null || context.Instance.Slug != slug)
            return ApiResults.Unauthorized();

        var user = await store.GetUserAsync(userId, ct);
        if (user is null || user.InstanceId != context.Instance.Id)
            return ApiResults.NotFound("user_not_found", "no such user in this instance");

        var items = new List<object>();
        foreach (var membership in (await store.ListMembershipsByUserAsync(user.Id, ct)).Where(m => m.InstanceId == context.Instance.Id))
        {
            var organization = await store.GetOrganizationAsync(membership.OrgId, ct);
            object? summary = organization is null ? null : Render(organization, context.Instance.Id);
            items.Add(new { id = membership.Id, role = membership.Role, created_at = membership.CreatedAt, organization = summary });
        }
        return Results.Ok(items);
    }
}
