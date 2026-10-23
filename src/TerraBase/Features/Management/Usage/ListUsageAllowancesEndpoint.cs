using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Usage;

/// <summary>
/// <c>GET /v1/usage/allowances</c>: every organization's billed operation units this UTC month, for the dashboard's allowance
/// notices. Served only on the internal listener, which requires the shared peer credential, as
/// <c>PUT /admin/projects/{project}/plan</c> is.
/// </summary>
public sealed class ListUsageAllowancesEndpoint(IUsageService usage) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/v1/usage/allowances");
        Name("listUsageAllowances");
        Summary("Lists every organization's billed usage this month");
        Description("Up to 25 organizations after the optional after (an organization id), in id order, with the 4 KiB operation units of every project, region and node this UTC month: what the plan allowance counts. next names the last organization when more follow. logicalBytes is null and storageContract none while storage is not reported. periodStart and observedAt are taken before the scan; a scan that crosses into another UTC month, or runs past 30 seconds, answers 503 and should be retried. Responses are never cached.");
        Tags("Usage");
        Produces<AllowanceUsagePage>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var after = HttpContext.Request.Query["after"].ToString();
        if (after.Length > 0 && !EntityIds.IsValid(after))
        {
            return ErrorResponse.From(new Validation("after must be a canonical uppercase ULID"));
        }

        // A cached page could report a month's totals after they changed, or in the next month.
        HttpContext.Response.Headers.CacheControl = "no-store";
        return await usage.ListAllowanceUsageAsync(after.Length > 0 ? after : null, cancellationToken) switch
        {
            AllowanceUsagePage page => TypedResults.Ok(page),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
