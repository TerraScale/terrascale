namespace TerraScale.Auth.Organizations;

using System.Net;
using TerraScale.Auth.Api;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.DotNet;
using TerraScale.Auth.Templates;
using TerraScale.Endpoints;
using static TerraScale.Auth.Organizations.OrganizationDomainSupport;

internal sealed class ListDomainsEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/v1/{slug}/organizations/{orgId}/domains", HandleAsync);

    private static Task<IResult> HandleAsync(string slug, string orgId, HttpRequest request, AuthStore store, InstanceResolver resolver, CancellationToken ct)
        => JwtTemplateSupport.WithScopeAsync(resolver, request, slug, async context =>
            {
                var organization = await FindOrganizationAsync(store, context.Instance.Id, orgId, ct);
                if (organization is null)
                    return ApiResults.NotFound("organization_not_found", "no such organization");

                var domains = (await store.ListOrgDomainsAsync(context.Instance.Id, ct)).Where(d => d.OrgId == organization.Id);
                return Results.Ok(domains.Select(Render).ToList());
            }, ct);
}
