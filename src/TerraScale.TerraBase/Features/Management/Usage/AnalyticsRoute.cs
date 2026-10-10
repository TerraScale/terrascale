using TerraBase.Errors;
using TerraBase.Features.UserAuth;

namespace TerraBase.Features.Management.Usage;

internal static class AnalyticsRoute
{
    public const string Project = ProjectApiRoute.Project;
    public const string Database = Project + "/databases/{database}";

    public static async ValueTask<IResult> ReadAsync(
        HttpContext context, IUsageAnalytics analytics, TimeProvider time, bool replication, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Features.Get<ProjectAccess>() is not { } access || access.DatabaseName is not null && access.DatabaseId is null)
        {
            return ErrorResponse.From(new NotFound());
        }

        return AnalyticsWindow.Parse(context.Request.Query, time.GetUtcNow()) switch
        {
            Validation error => ErrorResponse.From(error),
            AnalyticsWindow window => await ReadWindowAsync(context, analytics, access, window, replication, cancellationToken).ConfigureAwait(false),
        };
    }

    private static async ValueTask<IResult> ReadWindowAsync(
        HttpContext context, IUsageAnalytics analytics, ProjectAccess access, AnalyticsWindow window, bool replication, CancellationToken cancellationToken)
    {
        var database = context.Request.RouteValues["database"] as string ?? context.Request.Query["database"].ToString();
        database = database is "" ? null : database;
        var region = context.Request.Query["region"].ToString();
        region = region is "" ? null : region;
        return replication
            ? await analytics.ReplicationAsync(access, window, database, region, context.Response.RegisterForDispose, cancellationToken).ConfigureAwait(false) switch
            {
                ProjectReplication result => TypedResults.Ok(result),
                NotFound error => ErrorResponse.From(error),
                Validation error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            }
            : await analytics.AnalyticsAsync(access, window, database, region, context.Response.RegisterForDispose, cancellationToken).ConfigureAwait(false) switch
            {
                ProjectAnalytics series => TypedResults.Ok(series),
                NotFound error => ErrorResponse.From(error),
                Validation error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            };
    }
}
