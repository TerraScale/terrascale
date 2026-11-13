using System.Globalization;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed class RetainedPageEndpoint(RegionalHandoff handoff) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/rollout/retained");
        Name("pullRetainedHandoffPage");
        Summary("Reads a bounded retained page and certified positions for a reserved handoff");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<RetainedPage>();
        Produces(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (!long.TryParse(HttpContext.Request.Query["epoch"], NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
        {
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        var page = await handoff.RetainedPageAsync(HttpContext.Request.Query["operation"].ToString(), epoch,
            HttpContext.Request.Query["after"].FirstOrDefault(), HttpContext.Connection.RemoteIpAddress?.MapToIPv4(), cancellationToken).ConfigureAwait(false);
        return page is null ? TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable)
            : TypedResults.Json(page, ReplicasJsonContext.Default.RetainedPage);
    }
}
