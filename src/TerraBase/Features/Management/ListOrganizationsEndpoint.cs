using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal readonly union ListOrganizationsResponse(PageBody<OrganizationBody>, InvalidInput, CredentialRejected, BackendUnavailable);

internal sealed class ListOrganizationsEndpoint(CatalogStore catalog)
    : Endpoint<PageInput, ListOrganizationsResponse>
{
    public override void Configure()
    {
        Get(ManagementRoute.Organizations);
        Name("listOrganizations");
        Summary("Lists the organizations the calling user created");
        Description("Query: after (exclusive cursor), limit (1 to 100, default 20).");
        Tags(ManagementRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<ListOrganizationsResponse> HandleAsync(
        PageInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (HttpContext.Caller, ManagementService.Limit(request.Limit)) switch
        {
            (_, InvalidInput invalid) => invalid,
            (UserCaller user, int limit) => await catalog.ListOrganizationsAsync(user.UserId, request.After, limit, cancellationToken) switch
            {
                CatalogPage<OrganizationRecord> page => new PageBody<OrganizationBody>([.. page.Items.Select(OrganizationBody.From)], page.Next),
                BackendUnavailable unavailable => unavailable,
            },
            (AnonymousCaller or PeerCaller or ServiceCaller or ApiKeyCaller or ProjectCaller or EndUserCaller, int) =>
                new CredentialRejected("listing organizations needs a ts-auth access token for a user", "unauthorized"),
        };
    }
}
