namespace TerraScale.Auth.Organizations;

using System.Net;
using TerraScale.Auth.Api;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.DotNet;
using TerraScale.Auth.Templates;
using TerraScale.Endpoints;
using static TerraScale.Auth.Organizations.OrganizationDomainSupport;

internal sealed class DeleteDomainEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/v1/{slug}/organizations/{orgId}/domains/{id}", HandleAsync);

    private static Task<IResult> HandleAsync(string slug, string orgId, string id, HttpRequest request, AuthStore store, InstanceResolver resolver, CancellationToken ct)
        => JwtTemplateSupport.WithScopeAsync(resolver, request, slug, async context =>
            {
                var organization = await FindOrganizationAsync(store, context.Instance.Id, orgId, ct);
                if (organization is null)
                    return ApiResults.NotFound("organization_not_found", "no such organization");
                var record = await store.GetOrgDomainAsync(id, ct);
                if (record is null || record.InstanceId != context.Instance.Id || record.OrgId != organization.Id)
                    return ApiResults.NotFound("domain_not_found", "no such domain for this organization");

                await store.DeleteOrgDomainAsync(record.Id, ct);
                return Results.NoContent();
            }, ct);
}
