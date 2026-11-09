using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class ListOrganizationsEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get(ManagementRoute.Organizations);
        Name("listOrganizations");
        Summary("Lists organizations");
        Description(ManagementRoute.PageDescription + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ManagementPage<Organization>>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        ManagementRoute.CheckPage(HttpContext) switch
        {
            Validation error => ErrorResponse.From(error),
            PageQuery page => await management.ListOrganizationsAsync(ManagementRoute.UserId(HttpContext), page.After, page.Limit, cancellationToken) switch
            {
                ManagementPage<Organization> value => TypedResults.Ok(value),
                Unavailable error => ErrorResponse.From(error),
            },
        };
}
