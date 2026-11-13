using System.Diagnostics;
using System.Globalization;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;
using TerraBase.Features.Callers;

namespace TerraBase.Features.Management.Usage;

internal sealed class ListUsageAllowancesEndpoint(IUsageService usage, PeerUsage peers, TimeProvider time) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Metadata(new Requires("usage.read"));
        Get("/v1/usage/allowances");
        Name("listUsageAllowances");
        Summary("Lists every organization's billed usage this month");
        Description("Up to 25 organizations after the optional after (an organization id), in id order, with the write units (operations, billed over the plan allowance) and SQL read units (sqlReads, fair use, never billed) of every project, region and node in the UTC month (each region answers its own share and the answering region sums them; 503 when a region cannot answer): this month, or the previous one when month is its yyyy-MM, so a month's last units are billed after it closes. next names the last organization when more follow. storageByteHours sums every node's hourly samples of its stored bytes (storageContract stored-byte-hours-global-v1); divide by the hours of the month for average stored bytes. logicalBytes is null. periodStart and observedAt are taken before the scan; a scan that crosses into another UTC month, or runs past 30 seconds, answers 503 and should be retried. Responses are never cached.");
        Tags("Usage");
        Produces<AllowanceUsagePage>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var after = HttpContext.Request.Query["after"].ToString();
        if (after.Length > 0 && !EntityIds.IsValid(after))
        {
            return ErrorResponse.From(new Validation("after must be a canonical uppercase ULID"));
        }

        var now = time.GetUtcNow();
        var current = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var month = HttpContext.Request.Query["month"].ToString();
        var previous = month.Length > 0;
        if (previous && month != current.AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture))
        {
            return ErrorResponse.From(new Validation("month must be the previous UTC month, yyyy-MM"));
        }

        HttpContext.Response.Headers.CacheControl = "no-store";
        var nodeOnly = PeerUsage.NodeOnly(HttpContext);
        var others = nodeOnly ? Task.FromResult<IReadOnlyList<AllowanceUsagePage>?>([]) : peers.AllowancesAsync(after.Length > 0 ? after : null, previous ? month : null, cancellationToken);
        var local = await usage.ListAllowanceUsageAsync(after.Length > 0 ? after : null, previous, cancellationToken);
        if (local is not AllowanceUsagePage page)
        {
            return local switch
            {
                Unavailable error => ErrorResponse.From(error),
                AllowanceUsagePage => throw new UnreachableException(),
            };
        }

        if (await others is not { } remote || remote.Any(other => other.PeriodStart != page.PeriodStart))
        {
            return ErrorResponse.From(new Unavailable("usage of another region is unavailable; retry"));
        }

        try
        {
            return TypedResults.Ok(nodeOnly ? page : PeerUsage.MergeAllowances(page, remote, UsageService.AllowancePageSize));
        }
        catch (OverflowException)
        {
            return ErrorResponse.From(new Unavailable("an organization's usage exceeds the countable range"));
        }
    }
}
