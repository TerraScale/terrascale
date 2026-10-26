using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary>Branches only the source identity the caller expected.</summary>
public sealed class BranchManagedDatabaseIfIdEndpoint(IManagementService management, TerraBaseOptions options) : Endpoint<NameRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ProjectApiRoute.Database + "/branches/if-id/{expectedId}");
        Name("branchDatabaseIfId");
        Summary("Branches a database if its source ID matches");
        Description("Creates an independent snapshot copy using the existing name body. expectedId must be a canonical uppercase ULID matching the source resolved by database name; otherwise 409 database_identity_changed before placement or copy work. The admitted source ID is retained throughout copying. Sent to the source's home region; another region answers 421 wrong_region with X-TerraBase-Database-Home. An unsupported server answers 404; never fall back to branching by name." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Databases");
        Produces<ManagedDatabaseBody>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status421MisdirectedRequest);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware());
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(NameRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // A database-restricted token cannot create a sibling database; preserve its opaque refusal before the guard.
        if (ProjectAccess.Current is { DatabaseName: not null })
        {
            return ValueTask.FromResult(ManagementRoute.DatabaseNotFound(new NotFound()));
        }

        // Project catalog admission resolved the name once; route validation already checked expectedId's ULID.
        var sourceId = ManagementRoute.Value(HttpContext, "database");
        return !string.Equals(sourceId, ManagementRoute.Value(HttpContext, "expectedId"), StringComparison.Ordinal)
            ? ValueTask.FromResult(ErrorResponse.From(new Conflict("the database identity changed", "database_identity_changed")))
            : BranchManagedDatabaseEndpoint.BranchAsync(HttpContext, request, management, options, sourceId, cancellationToken);
    }
}
