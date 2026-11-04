using System.Globalization;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Usage;

/// <summary>
/// <c>GET /v1/internal/usage/months</c>: the units this node served for each named organization in a month, for a peer
/// region's sum. Internal listener only, behind the shared peer credential.
/// </summary>
internal sealed class ListNodeUsageMonthsEndpoint(UsageMeter meter) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(PeerUsage.MonthsPath);
        Name("listNodeUsageMonths");
        Summary("Lists the units this node served for organizations in a month");
        Description("Internal peer listener only, behind the shared peer credential; 404 elsewhere. month is a UTC month, yyyy-MM; "
            + "organization, repeated up to 100 times, names each organization. Answers this node's own SQL read units, write units "
            + "and stored byte hours of each, unflushed units included; the asking region adds every region's answer to its own.");
        Tags("Administration");
        Produces<NodeUsageMonths>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var organizations = HttpContext.Request.Query["organization"];
        if (!DateTimeOffset.TryParseExact(HttpContext.Request.Query["month"].ToString(), "yyyy-MM", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var month)
            || organizations.Count is 0 or > PeerUsage.MaxOrganizations
            || organizations.Any(organization => !EntityIds.IsValid(organization)))
        {
            return ErrorResponse.From(new Validation("month must be yyyy-MM and organization 1 to 100 canonical uppercase ULIDs"));
        }

        var items = new List<NodeUsageMonth>(organizations.Count);
        foreach (var organization in organizations.Distinct(StringComparer.Ordinal))
        {
            if (await meter.GetMonthAsync(organization!, projectId: null, cancellationToken, month).ConfigureAwait(false) is not UsageTotals units)
            {
                return ErrorResponse.From(new Unavailable("usage accounting is unavailable; retry"));
            }

            items.Add(new NodeUsageMonth(organization!, units.Reads, units.Writes, units.StorageByteHours));
        }

        HttpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new NodeUsageMonths(items));
    }
}
