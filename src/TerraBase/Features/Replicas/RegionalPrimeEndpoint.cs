using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraScale.Endpoints;
using TerraBase.Features.Callers;

namespace TerraBase.Features.Replicas;

internal sealed class RegionalPrimeEndpoint(RegionalHandoff handoff) : EndpointWithoutRequest<IResult>
{
    internal const string PrimedWithoutHeader = "TerraBase-Primed-Without";
    internal const string PrimedWithoutCountHeader = "TerraBase-Primed-Without-Count";
    private const int MaxHeaderDatabases = 1_000;

    public override void Configure()
    {
        Metadata(new Requires("rollout"));
        Post("/v1/rollout/prime");
        Name("RegionalPrimeEndpoint");
        Summary("Coordinates a caught-up regional writer handoff");
        Description("Internal listener only, behind the existing peer credential. A readiness response alone never authorizes a replacement.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces(StatusCodes.Status204NoContent);
        Produces<DetailedErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var source = HttpContext.Request.Query["source"].ToString();
        return await handoff.PrimeAsync(source, cancellationToken).ConfigureAwait(false) switch
        {
            PrimedWithout primed => Primed(primed.Databases),
            Unavailable error => ErrorResponse.From(error)
        };
    }

    private NoContent Primed(IReadOnlyList<string> skipped)
    {
        if (skipped is [_, ..])
        {
            HttpContext.Response.Headers[PrimedWithoutCountHeader] = skipped.Count.ToString(CultureInfo.InvariantCulture);
            HttpContext.Response.Headers[PrimedWithoutHeader] = string.Join(',', skipped.Take(MaxHeaderDatabases));
        }
        return TypedResults.NoContent();
    }
}
