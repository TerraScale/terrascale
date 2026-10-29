using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

/// <summary><c>GET …/databases/{database}/export-schedule</c>: reads when a database is exported to its project's customer bucket.</summary>
internal sealed class GetExportScheduleEndpoint(IManagementService management, IScheduledExports exports) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ProjectApiRoute.Database + "/export-schedule");
        Name("getExportSchedule");
        Summary("Reads the export schedule of a database");
        Description("cron is null until a schedule is saved. lastRun is the last attempt: status succeeded with the object key written, or failed with a fixed error." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Backups");
        Produces<ExportSchedule>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) switch
        {
            TenantContext database => await exports.GetAsync(database.DatabaseId!, cancellationToken) switch
            {
                ExportSchedule schedule => TypedResults.Ok(schedule),
                Unavailable error => ErrorResponse.From(error),
            },
            NotFound => ErrorResponse.From(new NotFound()),
            Unavailable error => ErrorResponse.From(error),
        };
}
