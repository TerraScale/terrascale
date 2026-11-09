using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed class RegionalPrepareEndpoint(RegionalHandoff handoff, IMemoryBudget memory) : EndpointWithoutRequest<IResult>
{
    public const long MaxBodyBytes = 4 * 1024 * 1024;

    public override void Configure()
    {
        Post("/v1/rollout/prepare");
        Name("RegionalPrepareEndpoint");
        Summary("Coordinates a caught-up regional writer handoff");
        Description("Internal listener only, behind the existing peer credential. A readiness response alone never authorizes a replacement.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces(StatusCodes.Status204NoContent);
        Produces(StatusCodes.Status503ServiceUnavailable);
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        AnswerAsync(HttpContext, handoff, memory, handoff.PrepareAsync, cancellationToken);

    internal static async ValueTask<IResult> AnswerAsync(HttpContext context, RegionalHandoff handoff, IMemoryBudget memory,
        Func<RegionalPrepare, CancellationToken, Task<bool>> act, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is not (> 0 and <= MaxBodyBytes))
        {
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        using var reservation = memory.TryReserve(context.Request.ContentLength.Value * 4, tenant: null);
        if (reservation is null)
        {
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var request = await context.Request.ReadFromJsonAsync(TerraBaseJsonContext.Default.RegionalPrepare, cancellationToken).ConfigureAwait(false);
        return request is not null && await act(request, cancellationToken).ConfigureAwait(false)
            ? TypedResults.NoContent() : TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}
