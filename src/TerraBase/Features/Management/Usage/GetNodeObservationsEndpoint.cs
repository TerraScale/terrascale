using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Usage;

internal sealed class GetNodeObservationsEndpoint(UsageMeter meter, TimeProvider time, IMemoryBudget memory, TerraBaseOptions options) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get(PeerUsage.ObservationsPath);
        Name("getNodeUsageObservations");
        Summary("Reads regional usage observations for a database");
        Description("Internal peer listener only. organization, project and database are canonical identifiers; region selects the observation owner and source identifies the requesting region. range selects the bounded analytics window.");
        Tags("Administration");
        Produces<UsageCounts>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var query = HttpContext.Request.Query;
        if (query["organization"] is not [var organization] || !EntityIds.IsValid(organization)
            || query["project"] is not [var project] || !EntityIds.IsValid(project)
            || query["database"] is not [var database] || !EntityIds.IsValid(database)
            || query["region"] is not [var region] || region is null || !UsageMeter.ValidRegion(region)
            || query["source"] is not [var source] || source is null || !UsageMeter.ValidRegion(source))
        {
            return ErrorResponse.From(new Validation("organization, project and database must be canonical identifiers and region and source serving regions"));
        }
        if (AnalyticsWindow.Parse(query, time.GetUtcNow()) is not AnalyticsWindow window)
        {
            return ErrorResponse.From(new Validation("range must be 1h, 24h, 7d or 30d"));
        }
        if (source == options.Region)
        {
            return TypedResults.Ok(new UsageCounts(OpCounts.Zero, [.. Enumerable.Repeat(OpCounts.Zero, window.Usage.Count)]));
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(UsageService.Deadline);
        try
        {
            var reservation = memory.TryReserve(16L * 1024 * 1024 + window.Usage.Count * 2048L)
                ?? throw new QueryCapacityException("analytics memory admission exhausted") { Deterministic = true };
            using var loaded = new UsageAnalytics.Loaded(memory, window.Usage.Count, reservation);
            return await meter.GetObservationsAsync(new TenantContext(organization, PlanTier.Hobby, project, database),
                window.Usage, deadline.Token, filter: loaded.Filter(region)).ConfigureAwait(false) switch
            {
                UsageCounts counts => loaded.HandOff(HttpContext.Response.RegisterForDispose, TypedResults.Ok(counts)),
                Unavailable error => ErrorResponse.From(error),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ErrorResponse.From(new Unavailable("analytics exceeded its execution budget", "capacity"));
        }
        catch (QueryCapacityException)
        {
            return ErrorResponse.From(new Unavailable("analytics memory admission exhausted", "capacity"));
        }
    }
}
