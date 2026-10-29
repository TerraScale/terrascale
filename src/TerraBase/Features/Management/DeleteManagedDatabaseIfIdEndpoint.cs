using Microsoft.OpenApi;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary>Deletes only the immutable database identity the caller inspected.</summary>
internal sealed class DeleteManagedDatabaseIfIdEndpoint(IManagementService management, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(ProjectApiRoute.Database + "/if-id/{expectedId}");
        Metadata(AnchoredRevocation.Instance);
        Name("deleteDatabaseIfId");
        Summary("Deletes a database only if its immutable identity matches");
        Description("expectedId must be a canonical uppercase ULID matching the database ID resolved from the name for this request. A mismatch answers 409 database_identity_mismatch before placement or deletion. Never fall back to unconditional DELETE on 404: older servers do not support this route. Matching deletion has the same home-region requirement, independent branches and durable replica-erasure behavior as deleteDatabase, including allRegions, without a placement PATCH. 204 records logical deletion and local cleanup; remote erasure is retried durably. This is not an atomic reset or replacement operation. " + ProjectApiRoute.IdentityDescription);
        Tags("Databases");
        Metadata(new OpenApiParameter
        {
            Name = "expectedId",
            In = ParameterLocation.Path,
            Required = true,
            Description = "The immutable database ID inspected by the caller; a canonical uppercase ULID.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[0-7][0-9A-HJKMNP-TV-Z]{25}$" },
        });
        foreach (var status in new[] { 400, 403, 404, 409, 421, 503 })
        {
            Produces<ErrorBody>(status);
        }

        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.PlacementAware(), new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var expected = ManagementRoute.Value(HttpContext, "expectedId");
        if (!EntityIds.IsValid(expected))
        {
            return ValueTask.FromResult(ErrorResponse.From(new Validation("expectedId must be a canonical uppercase ULID")));
        }

        // Catalog middleware already replaced the name with its immutable ID. Never resolve the name again.
        if (!string.Equals(expected, ManagementRoute.Value(HttpContext, "database"), StringComparison.Ordinal))
        {
            return ValueTask.FromResult(ErrorResponse.From(new Conflict("the database identity does not match expectedId", "database_identity_mismatch")));
        }

        return DeleteManagedDatabaseEndpoint.DeleteAsync(HttpContext, management, options, cancellationToken);
    }
}
