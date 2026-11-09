using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;

namespace TerraBase.Features.LocalAck;

internal sealed class ReleaseHoldsEndpoint(HoldStore holds) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/v1/sync/holds/release");
        Name("releaseRegionTransactions");
        Summary("Drops the held transactions the home already has");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. Drops the origin's held transactions of the "
            + "database up to the given push stream sequence; 204.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(new RequestBodyMetadata(typeof(HoldRelease), "application/json"), ListenerMetadata.Internal);
        Produces(StatusCodes.Status204NoContent);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (HttpContext.Request.ContentLength is not (> 0 and <= 4096))
        {
            return ErrorResponse.From(new Validation("the body is one release of at most 4 KiB"));
        }

        HoldRelease? release;
        try
        {
            release = await HttpContext.Request.ReadFromJsonAsync(TerraBaseJsonContext.Default.HoldRelease, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            release = null;
        }

        if (release is null || !TerraBaseOptions.IsMachineId(release.Origin)
            || !EntityIds.IsValid(release.Organization) || !EntityIds.IsValid(release.Project) || !EntityIds.IsValid(release.Database))
        {
            return ErrorResponse.From(new Validation("the body names an origin node, an organization, a project and a database"));
        }

        holds.Touch(release.Origin!);
        await holds.ReleaseAsync(release.Origin!, release.Organization!, release.Project!, release.Database!, release.Through, cancellationToken).ConfigureAwait(false);
        HttpContext.Response.Headers.CacheControl = "no-store";
        return TypedResults.NoContent();
    }
}
