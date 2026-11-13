using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed class RetainedCertificationEndpoint(RetainedReconciler retained) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/rollout/retained/certification");
        Name("certifyRetainedCopies");
        Summary("Certifies local retained copies without transferring files");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<RetainedCertification>();
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        TypedResults.Json(await retained.CertifyAsync(cancellationToken).ConfigureAwait(false), ReplicasJsonContext.Default.RetainedCertification);
}
