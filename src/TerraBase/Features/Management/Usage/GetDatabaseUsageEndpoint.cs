using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Usage;

internal sealed class GetDatabaseUsageEndpoint(IManagementService management, IUsageService usage, TimeProvider time) : EndpointWithoutRequest<IResult>
{
    internal const string CountsDescription =
        "Each SQL read statement is one read, however many rows it returns; each committed write statement is one write. "
        + "period sums the series; reads and writes are totals since each database was created. "
        + "Reads and writes are best-effort dashboard usage, not billing data: they are saved every 10 seconds and at a clean shutdown "
        + "(a catalog follower sends its counts to its primary as often), a failed save is retried, and a crash loses the operations "
        + "counted since the last successful save. " + UsageWindow.Description;

    public override void Configure()
    {
        Get(ProjectApiRoute.Database + "/usage");
        Name("getDatabaseUsage");
        Summary("Reads the usage of a database");
        Description("Bytes on disk, and reads and writes, with one point per hour or day of the period. " + CountsDescription + " " + ProjectApiRoute.IdentityDescription);
        Tags("Usage");
        Produces<DatabaseUsage>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        UsageWindow.Parse(HttpContext.Request.Query, time.GetUtcNow()) switch
        {
            Validation error => ErrorResponse.From(error),
            UsageWindow window => await ReadAsync(window, cancellationToken),
        };

    private async ValueTask<IResult> ReadAsync(UsageWindow window, CancellationToken cancellationToken) =>
        await ManagementRoute.DatabaseAsync(HttpContext, management, cancellationToken) switch
        {
            TenantContext database => await usage.GetDatabaseUsageAsync(database, window, cancellationToken) switch
            {
                DatabaseUsage result => TypedResults.Ok(result),
                NotFound error => ErrorResponse.From(error),
                Validation error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
}
