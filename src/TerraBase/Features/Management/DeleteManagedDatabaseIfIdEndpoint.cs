using Microsoft.OpenApi;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

internal sealed class DeleteManagedDatabaseIfIdEndpoint(IManagementService management, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Delete(ProjectApiRoute.Database + "/if-id/{expectedId}");
        Metadata(AnchoredRevocation.Instance);
        Name("deleteDatabaseIfId");
        Summary("Deletes a database only if its immutable identity matches");
        Description("expectedId must be a canonical uppercase ULID matching the database ID resolved from the name for this request. A mismatch answers 409 database_identity_mismatch before deletion. A matching deletion enters the seven-day deleted state. Data, backups and keys are retained until purgeAt and storage counts against the organization quota until purge. Restore or permanently purge by immutable ID through deleted-databases. Outside placement returns 421 wrong_region; a move in progress returns 409 move_in_progress and deletion outside the home region returns 409 wrong_region. Other regions drop their copies at deletion and are re-seeded after restore. Branches remain independent. " + ProjectApiRoute.IdentityDescription);
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

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var expected = HttpContext.RouteValue("expectedId");
        if (!EntityIds.IsValid(expected))
        {
            return ValueTask.FromResult(ErrorResponse.From(new Validation("expectedId must be a canonical uppercase ULID")));
        }

        if (!string.Equals(expected, HttpContext.RouteValue("database"), StringComparison.Ordinal))
        {
            return ValueTask.FromResult(ErrorResponse.From(new Conflict("the database identity does not match expectedId", "database_identity_mismatch")));
        }

        return DeleteManagedDatabaseEndpoint.DeleteAsync(HttpContext, management, options, cancellationToken);
    }
}
