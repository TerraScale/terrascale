using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

/// <summary><c>GET …/databases/{database}/point-in-time</c>: the times a database can be restored to.</summary>
internal sealed class GetRestoreWindowEndpoint(IManagementService management, IPointInTimeRestore restore) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ProjectApiRoute.Database + "/point-in-time");
        Name("getRestoreWindow");
        Summary("Reads the point-in-time restore window of a database");
        Description("Query destination selects platform (default) or customer. earliest is the oldest continuous backup snapshot of the database limited by its plan's retention window, latest the newest time the backup proves restorable: read from the bucket, the second at or before the newest read mark in the newest generation's last three segments (at most three object reads; an unreadable segment counts as holding none), or its newest snapshot's time; while this node ships the database to the platform, the shipper gives it from the objects it has stored, without reading the bucket: the later of its generation's newest stored snapshot's time and, for the newest stored segment holding a read mark, the second at or before that mark, or the mark's own time to the millisecond when the mark ends a segment read in that millisecond. 404 when continuous backup is off or holds no data of the database within its plan's retention window." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Backups");
        Produces<RestoreWindow>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Metadata(new OpenApiParameter
        {
            Name = "destination",
            In = ParameterLocation.Query,
            Description = "The backup bucket to inspect; defaults to platform.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Enum = [JsonValue.Create(BackupDestinationNames.Platform), JsonValue.Create(BackupDestinationNames.Customer)],
                Default = JsonValue.Create(BackupDestinationNames.Platform),
            },
        });
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var resolved = await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken);
        if (resolved is not TenantContext database)
        {
            return resolved switch
            {
                NotFound error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
                TenantContext => throw new System.Diagnostics.UnreachableException(),
            };
        }

        try
        {
            return BackupDestinationRoute.Source(HttpContext.Request.Query["destination"]) switch
            {
                Validation error => ErrorResponse.From(error),
                string source => await restore.GetWindowAsync(database, cancellationToken, source) switch
                {
                    RestoreWindow window => TypedResults.Ok(window),
                    NotFound error => ErrorResponse.From(error),
                    Unavailable error => ErrorResponse.From(error),
                },
            };
        }
        catch (InvalidDataException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ErrorResponse.From(BackupDestinationRoute.Unavailable());
        }
        catch (Exception error) when (BackupDestinationRoute.IsUnavailable(error, cancellationToken))
        {
            return ErrorResponse.From(BackupDestinationRoute.Unavailable());
        }
    }
}
