using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed class RegionalVerifyEndpoint(RegionalHandoff handoff, IMemoryBudget memory) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/v1/rollout/verify");
        Name("RegionalVerifyEndpoint");
        Summary("Coordinates a caught-up regional writer handoff");
        Description("Internal listener only, behind the existing peer credential. A readiness response alone never authorizes a replacement.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces(StatusCodes.Status204NoContent);
        Produces(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (HttpContext.Request.ContentLength is not (> 0 and <= 4 * 1024 * 1024))
        {
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        using var reservation = memory.TryReserve(MemoryLane.Bulk, HttpContext.Request.ContentLength.Value * 4, tenant: null);
        if (reservation is null)
        {
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var request = await HttpContext.Request.ReadFromJsonAsync(TerraBaseJsonContext.Default.RegionalPrepare, cancellationToken).ConfigureAwait(false);
        return request is not null && await handoff.VerifyAsync(request, cancellationToken).ConfigureAwait(false)
            ? TypedResults.NoContent() : TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}
