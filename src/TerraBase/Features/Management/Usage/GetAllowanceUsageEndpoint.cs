using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Usage;

/// <summary>A bounded page of this node's allowance observations, from the enforcement meter.</summary>
/// <param name="PeriodStart">The UTC billing month.</param>
/// <param name="ObservedAt">When the observations started.</param>
/// <param name="Items">Organizations with databases on this node.</param>
/// <param name="Next">The next organization cursor, or null.</param>
/// <param name="OperationsContract">The operation unit and aggregation scope contract.</param>
/// <param name="StorageContract">The storage unit and aggregation scope contract.</param>
public sealed record AllowanceUsagePage(DateTimeOffset PeriodStart, DateTimeOffset ObservedAt, IReadOnlyList<AllowanceUsage> Items, string? Next, string OperationsContract, string StorageContract);

/// <summary>Node-local allowance usage; null readings are unavailable, never zero.</summary>
/// <param name="OrganizationId">The organization.</param>
/// <param name="Operations">The same month counter used by operation allowance enforcement.</param>
/// <param name="LogicalBytes">Live document JSON bytes of this node's organization databases.</param>
public sealed record AllowanceUsage(string OrganizationId, long? Operations, long? LogicalBytes);

/// <summary>Reads allowance observations using the existing internal listener's peer authority.</summary>
public sealed class GetAllowanceUsageEndpoint(IAllowanceUsageFeed feed) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/v1/usage/allowances");
        Name("getAllowanceUsage");
        Summary("This node's monthly operation and storage allowance observations");
        Description("Internal peer-authenticated listener only. Uses the operation allowance enforcement meter, including its documented per-node and per-document-unit limits. Storage covers databases on this node. At most 25 organizations per page and 100 databases per organization; unavailable readings are null. Supply the returned next organization ULID as after. This is not a globally settled billing feed.");
        Tags("Usage");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<AllowanceUsagePage>();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var after = HttpContext.Request.Query["after"].ToString();
        return after.Length > 0 && !TerraBase.Primitives.EntityIds.IsValid(after)
            ? TypedResults.BadRequest()
            : TypedResults.Ok(await feed.ReadAsync(after, cancellationToken).ConfigureAwait(false));
    }
}
