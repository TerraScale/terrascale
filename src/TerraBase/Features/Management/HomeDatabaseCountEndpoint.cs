using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

public sealed record HomeCountBody(long Databases);

internal sealed class HomeDatabaseCountEndpoint(IManagementService management) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/sync/organizations/{organization}/databases/count");
        Name("countOrganizationDatabasesHomed");
        Summary("Counts the databases homed here for an organization");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. The live databases "
            + "this region homes for the organization, the authoritative count a region creating a database adds to its "
            + "own for the organization's allowance.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<HomeCountBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var organization = HttpContext.Request.RouteValues["organization"] as string ?? string.Empty;
        if (!EntityIds.IsValid(organization))
        {
            return ErrorResponse.From(new NotFound("the organization does not exist"));
        }

        return await management.CountOrganizationDatabasesHomedAsync(organization, cancellationToken).ConfigureAwait(false) switch
        {
            long count => TypedResults.Json(new HomeCountBody(count), TerraBaseJsonContext.Default.HomeCountBody),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
