using TerraBase.Errors;
using TerraBase.Hosting;
using TerraScale.Endpoints;
using TerraBase.Features.Callers;

namespace TerraBase.Features.Replicas;

internal sealed class RegionalAdmitEndpoint(RegionalHandoff handoff) : EndpointWithoutRequest<IResult>
{
    private const int MaxSpecLength = 256;

    public override void Configure()
    {
        Metadata(new Requires("rollout"));
        Post("/v1/rollout/drain/admit");
        Name("RegionalAdmitEndpoint");
        Summary("Reserves a regional writer handoff before the caller withdraws ingress");
        Description("Internal listener only, behind the existing peer credential. Admission fences nothing and holds no ingress; an admission that no drain claims expires.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<RegionalAdmission>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<DetailedErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var target = HttpContext.Request.Query["target"].ToString();
        var spec = HttpContext.Request.Query["spec"].ToString();
        if (spec.Length is 0 or > MaxSpecLength)
        {
            return ErrorResponse.From(new Validation("spec names the stop request and is 1 to 256 characters"));
        }
        return await handoff.AdmitAsync(target, spec, cancellationToken).ConfigureAwait(false) switch
        {
            RegionalAdmission admission => TypedResults.Json(admission, ReplicasJsonContext.Default.RegionalAdmission),
            Unavailable error => ErrorResponse.From(error)
        };
    }
}
