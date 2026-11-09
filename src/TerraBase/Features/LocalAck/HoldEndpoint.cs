using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.HomePush;
using static TerraBase.Features.HomePush.HomePush;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;

namespace TerraBase.Features.LocalAck;

internal sealed class HoldEndpoint(HoldStore holds, IMemoryBudget memory) : EndpointWithoutRequest<IResult>
{
    internal const long MaxBodyBytes = 16 * 1024 * 1024;

    public override void Configure()
    {
        Post("/v1/sync/holds");
        Name("holdRegionTransaction");
        Summary("Holds a local transaction for the in-region partner that committed it");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. Stores the transaction durably on this node, so a "
            + "local write is acknowledged on two nodes of the region; 204 once it is synced to disk. 503 when this node holds its limit.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(new RequestBodyMetadata(typeof(HeldTransaction), "application/json"), ListenerMetadata.Internal);
        Produces(StatusCodes.Status204NoContent);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (HttpContext.Request.ContentLength is not (> 0 and <= MaxBodyBytes))
        {
            return ErrorResponse.From(new Validation("the body is one held transaction of at most 16 MiB"));
        }

        using var reservation = memory.TryReserve(MemoryLane.Foreground, checked(HttpContext.Request.ContentLength.Value * 4), tenant: null);
        if (reservation is null)
        {
            return ErrorResponse.From(new Unavailable("the node's memory budget is exhausted; retry"));
        }

        HeldTransaction? held;
        try
        {
            held = await HttpContext.Request.ReadFromJsonAsync(TerraBaseJsonContext.Default.HeldTransaction, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            held = null;
        }

        if (Invalid(held) is { } invalid)
        {
            return ErrorResponse.From(new Validation(invalid));
        }

        holds.Touch(held!.Origin!);
        HttpContext.Response.Headers.CacheControl = "no-store";
        return await holds.PutAsync(held, cancellationToken).ConfigureAwait(false)
            ? TypedResults.NoContent()
            : ErrorResponse.From(new Unavailable("this node holds its limit of transactions; the write waits for the home"));
    }

    internal static string? Invalid(HeldTransaction? held) => held switch
    {
        null => "the body is a held transaction",
        { Version: not 1 } => "version must be 1",
        _ when !TerraBaseOptions.IsMachineId(held.Origin) => "origin must be a node name",
        _ when !TerraBaseOptions.IsRegion(held.Home) => "home must be a region id",
        _ when !EntityIds.IsValid(held.Organization) || !EntityIds.IsValid(held.Project) || !EntityIds.IsValid(held.Database) =>
            "organization, project and database must be ULIDs",
        { Sequence: 0 } => "sequence must be positive",
        { Payload: not { Length: >= TransactionCrypto.Overhead and <= MaxPayloadBytes } } => "payload must be a sealed TBP1 transaction",
        _ when !held.Payload.AsSpan(0, 4).SequenceEqual("TBP1"u8) => "payload must be a sealed TBP1 transaction",
        _ => null,
    };
}
