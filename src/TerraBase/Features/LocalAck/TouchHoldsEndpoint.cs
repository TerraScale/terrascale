using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.LocalAck;

internal sealed class TouchHoldsEndpoint(HoldStore holds, IStorageEngine storage) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/v1/sync/holds/{origin}/contact");
        Name("touchRegionHolds");
        Summary("Tells the in-region partner that the origin node is alive");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. 204 when this node can hold the origin's "
            + "transactions; 503 when its storage is not ready. A partner the origin has not reached for 10 s finishes the origin's push to the home.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces(StatusCodes.Status204NoContent);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var origin = HttpContext.Request.RouteValues["origin"] as string;
        if (!TerraBaseOptions.IsMachineId(origin))
        {
            return ErrorResponse.From(new Validation("origin must be a node name"));
        }

        holds.Touch(origin!);
        HttpContext.Response.Headers.CacheControl = "no-store";
        return await storage.IsReadyAsync(cancellationToken).ConfigureAwait(false)
            ? TypedResults.NoContent()
            : ErrorResponse.From(new Unavailable("this node's storage is not ready"));
    }
}
