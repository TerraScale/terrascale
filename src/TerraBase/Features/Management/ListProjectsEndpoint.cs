using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal readonly union ListProjectsResponse(PageBody<ProjectBody>, InvalidInput, CredentialRejected, ResourceNotFound, BackendUnavailable);

internal sealed class ListProjectsEndpoint(ManagementService management, CatalogStore catalog)
    : Endpoint<PageInput, ListProjectsResponse>
{
    public override void Configure()
    {
        Get(ManagementRoute.Projects);
        Name("listProjects");
        Summary("Lists an organization's projects");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<ListProjectsResponse> HandleAsync(
        PageInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ManagementService.Limit(request.Limit) switch
        {
            int limit => await management.OwnedOrganizationAsync(HttpContext.Caller, request.Organization, cancellationToken) switch
            {
                OrganizationRecord organization => await catalog.ListProjectsAsync(organization.Id, request.After, limit, cancellationToken) switch
                {
                    CatalogPage<ProjectRecord> page => new PageBody<ProjectBody>([.. page.Items.Select(ProjectBody.From)], page.Next),
                    BackendUnavailable unavailable => unavailable,
                },
                CredentialRejected unauthorized => unauthorized,
                ResourceNotFound missing => missing,
                BackendUnavailable unavailable => unavailable,
            },
            InvalidInput invalid => invalid,
        };
    }
}
