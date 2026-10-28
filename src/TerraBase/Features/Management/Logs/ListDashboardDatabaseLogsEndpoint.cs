using TerraBase.Errors;
using TerraBase.Features.Management.Usage;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Logs;

/// <summary>
/// <c>GET /v1/usage/projects/{project}/databases/{databaseId}/logs</c>: the public database logs for the dashboard. Served only on
/// the internal listener, which requires the shared peer credential, as <c>GET /v1/usage/allowances</c> is.
/// </summary>
internal sealed class ListDashboardDatabaseLogsEndpoint(IProjectCatalog projects, SystemCatalog catalog, IRequestLogs logs) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/v1/usage/projects/{project}/databases/{databaseId}/logs");
        Name("listDashboardDatabaseLogs");
        Summary("Lists a database's recent requests for the dashboard");
        Description("The listDatabaseLogs response, unfiltered: newest 100 recorded requests within 24 hours. databaseId is the catalog id; the organization comes from the catalog. " +
            ListDatabaseLogsEndpoint.CursorDescription);
        Tags("Usage");
        Produces<DatabaseRequestLogs>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Metadata(ListDatabaseLogsEndpoint.CursorParameter());
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var (project, organization, error) = await GetDashboardProjectAnalyticsEndpoint.OrganizationAsync(HttpContext, projects, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (!ListDatabaseLogsEndpoint.TryCursor(HttpContext, out var after))
        {
            return ErrorResponse.From(new Validation("after must be one canonical uppercase ULID", "invalid_cursor"));
        }

        var database = HttpContext.Request.RouteValues["databaseId"] as string;
        if (!EntityIds.IsValid(database))
        {
            return ErrorResponse.From(new Validation("databaseId must be a canonical uppercase ULID"));
        }

        var tenant = await catalog.ReadAsync<object?>(
            async transaction => await EngineManagementService.DatabaseTenantUncheckedAsync(transaction, organization!, project, database, cancellationToken).ConfigureAwait(false),
            static unavailable => unavailable, cancellationToken).ConfigureAwait(false);
        return tenant switch
        {
            TenantContext found => await logs.ReadAsync(found, statusClass: null, after, cancellationToken).ConfigureAwait(false) switch
            {
                RequestLogRead read => Retain(read),
                Unavailable unavailable => ErrorResponse.From(unavailable),
            },
            Unavailable unavailable => ErrorResponse.From(unavailable),
            _ => ErrorResponse.From(new NotFound("the database does not exist", "unknown_database")),
        };
    }

    private RequestLogRead Retain(RequestLogRead read)
    {
        HttpContext.Response.RegisterForDispose(read);
        return read;
    }
}
