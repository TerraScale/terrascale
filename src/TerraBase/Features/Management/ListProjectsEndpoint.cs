using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class ListProjectsEndpoint(ManagementService management, CatalogStore catalog)
    : Endpoint<PageInput, Result<PageBody<ProjectBody>, Validation, Unauthorized, NotFound, Unavailable>>
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

    public override async ValueTask<Result<PageBody<ProjectBody>, Validation, Unauthorized, NotFound, Unavailable>> HandleAsync(
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
                    Unavailable unavailable => unavailable,
                },
                Unauthorized unauthorized => unauthorized,
                NotFound missing => missing,
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
        };
    }
}
