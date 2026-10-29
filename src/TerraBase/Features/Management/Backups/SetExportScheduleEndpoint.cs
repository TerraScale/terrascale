using Cronos;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Backups;

/// <summary><c>PUT …/databases/{database}/export-schedule</c>: saves when a database is exported to its project's customer bucket.</summary>
internal sealed class SetExportScheduleEndpoint(IManagementService management, IScheduledExports exports) : Endpoint<ExportScheduleRequest, IResult>
{
    public override void Configure()
    {
        Put(ProjectApiRoute.Database + "/export-schedule");
        Metadata(AnchoredRevocation.Instance);
        Name("setExportSchedule");
        Summary("Saves the export schedule of a database");
        Description("cron is a standard 5-field cron expression evaluated in UTC, or null or empty to stop scheduled exports. Each run writes every live document as NDJSON (the export endpoint's lines, which NDJSON import reads) to terrabase-exports/{project}/{database}/{yyyyMMddTHHmmssZ}.ndjson in the project's backup destination bucket; a schedule needs that destination (409 otherwise)." + " " + ProjectApiRoute.IdentityDescription);
        Tags("Backups");
        Produces<ExportSchedule>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(ExportScheduleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cron = string.IsNullOrWhiteSpace(request.Cron) ? null : request.Cron.Trim();
        if (cron is not null && !CronExpression.TryParse(cron, CronFormat.Standard, out _))
        {
            return ErrorResponse.From(new Validation("cron must be a standard 5-field cron expression"));
        }

        return await ManagementRoute.CheckWritableDatabaseAsync(HttpContext, management, cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => await exports.SetAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), database, cron, cancellationToken) switch
            {
                ExportSchedule schedule => TypedResults.Ok(schedule),
                NotFound error => ErrorResponse.From(error),
                Conflict error => ErrorResponse.From(error),
                Forbidden error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
    }
}
