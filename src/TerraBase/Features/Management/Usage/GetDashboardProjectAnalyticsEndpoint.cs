using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Usage;

/// <summary>
/// <c>GET /v1/usage/projects/{project}/analytics</c>: the public project analytics for the dashboard. Served only on the internal
/// listener, which requires the shared peer credential, as <c>GET /v1/usage/allowances</c> is.
/// </summary>
internal sealed class GetDashboardProjectAnalyticsEndpoint(IProjectCatalog catalog, IUsageAnalytics analytics, TimeProvider time) : EndpointWithoutRequest<IResult>
{
    /// <summary>The acting service of a synthesized project grant; reads only, never recorded as a member.</summary>
    internal const string Actor = "ts-dashboard";

    /// <inheritdoc />
    public override void Configure()
    {
        Get("/v1/usage/projects/{project}/analytics");
        Name("getDashboardProjectAnalytics");
        Summary("Reads a project's analytics for the dashboard");
        Description("The getProjectAnalytics response for every database of the project; the organization comes from the catalog. " + AnalyticsWindow.Description);
        Tags("Usage");
        Produces<ProjectAnalytics>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var (project, organization, error) = await OrganizationAsync(HttpContext, catalog, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var access = new ProjectAccess(Actor, project, organization!, DatabaseName: null, DatabaseId: null);
        HttpContext.Features.Set(access);
        IResult? result = null;
        await ProjectAccess.RunAsync(access, async () =>
            result = await AnalyticsRoute.ReadAsync(HttpContext, analytics, time, replication: false, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        return result!;
    }

    /// <summary>The route's project and the organization the catalog verified for it, or the error response.</summary>
    internal static async ValueTask<(string Project, string? Organization, IResult? Error)> OrganizationAsync(
        HttpContext context, IProjectCatalog catalog, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        var project = context.Request.RouteValues["project"] as string ?? "";
        if (!EntityIds.IsValid(project))
        {
            return (project, null, ErrorResponse.From(new Validation("project must be a canonical uppercase ULID")));
        }

        return await catalog.ResolveProjectAsync(project, Actor, createIn: null, cancellationToken).ConfigureAwait(false) switch
        {
            string organization => (project, organization, null),
            NotFound error => (project, null, ErrorResponse.From(error)),
            Unavailable error => (project, null, ErrorResponse.From(error)),
        };
    }
}
