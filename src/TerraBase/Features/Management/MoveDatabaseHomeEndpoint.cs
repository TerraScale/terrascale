using System.Diagnostics;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Catalog;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

public sealed record MoveHomeRequest(string? HomeRegion);

internal sealed class MoveDatabaseHomeEndpoint(IManagementService management, HomeMoves moves, TerraBaseOptions options, IRegionCatalog regions)
    : Endpoint<MoveHomeRequest, IResult>
{
    public override void Configure()
    {
        Post(ProjectApiRoute.Database + "/move-home");
        Metadata(AnchoredRevocation.Instance);
        Name("moveDatabaseHome");
        Summary("Moves a database's home to one of its replica regions");
        Description("The replica in homeRegion catches up, writes pause briefly, the home switches and the old home becomes a replica region. "
            + "No acknowledged write is lost. Answers when the move is done. " + ProjectApiRoute.IdentityDescription);
        Tags("Databases");
        Produces<ManagedDatabaseBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware());
    }

    public override async ValueTask<IResult> HandleAsync(MoveHomeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (user, organization, project) = (ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"));
        if (!TerraBaseOptions.IsRegion(request.HomeRegion))
        {
            return ErrorResponse.From(new Validation("homeRegion must name a region"));
        }

        if (DatabaseRegions.Unserved("homeRegion", [request.HomeRegion], await regions.ServingAsync(cancellationToken).ConfigureAwait(false)) is { } unserved)
        {
            return ErrorResponse.From(unserved);
        }

        switch (await management.GetDatabaseAsync(user, organization, project, ManagementRoute.Value(HttpContext, "database"), cancellationToken).ConfigureAwait(false))
        {
            case NotFound error:
                return ManagementRoute.DatabaseNotFound(error);
            case Unavailable error:
                return ErrorResponse.From(error);
            case ManagedDatabase database when database.Home(options.Region) != options.Region:
                return DatabaseRegions.HomeRegion(database.Home(options.Region), database.Regions(options.Region));
            case ManagedDatabase database:
                return await moves.MoveAsync(new TenantContext(organization, PlanTier.Hobby, database.ProjectId, database.Id), request.HomeRegion, cancellationToken)
                    .ConfigureAwait(false) switch
                {
                    ManagedDatabase moved => TypedResults.Ok(ManagedDatabaseBody.From(moved, options.Region)),
                    NotFound error => ManagementRoute.DatabaseNotFound(error),
                    Validation error => ErrorResponse.From(error),
                    Conflict error when error == ManagementErrors.WrongRegion => DatabaseRegions.HomeRegion(database.Home(options.Region), database.Regions(options.Region)),
                    Conflict error => ErrorResponse.From(error),
                    Unavailable error => ErrorResponse.From(error),
                };
            default:
                throw new UnreachableException();
        }
    }
}
