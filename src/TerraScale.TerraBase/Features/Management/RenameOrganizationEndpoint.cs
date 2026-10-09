using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>PATCH /v1/management/organizations/{organization}</c>: renames an organization.</summary>
public sealed class RenameOrganizationEndpoint(IManagementService management) : Endpoint<NameRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Patch(ManagementRoute.Organization);
        Name("renameOrganization");
        Summary("Renames an organization");
        Description("Needs admin (403 otherwise). " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<Organization>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(NameRequest request, CancellationToken cancellationToken) =>
        ManagementRoute.CheckDisplayName(request) switch
        {
            Validation error => ErrorResponse.From(error),
            string name => await management.RenameOrganizationAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), name, cancellationToken) switch
            {
                Organization value => TypedResults.Ok(value),
                NotFound error => ErrorResponse.From(error),
                Forbidden error => ErrorResponse.From(error),
                Conflict error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
}
