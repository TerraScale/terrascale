using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed record RetentionExpiryRequest(DateTimeOffset DeletedAt);
internal sealed record RetentionExpiryAnswer(bool Confirmed);

internal sealed class RetentionExpiryEndpoint(TimeProvider time) : Endpoint<RetentionExpiryRequest, IResult>
{
    public override void Configure()
    {
        Post("/v1/sync/retention/expiry");
        Name("confirmRetentionExpiry");
        Summary("Confirms that this node's clock passed the retention deadline and safety margin");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<RetentionExpiryAnswer>();
    }

    public override ValueTask<IResult> HandleAsync(RetentionExpiryRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IResult>(TypedResults.Json(new RetentionExpiryAnswer(
            request.DeletedAt <= time.GetUtcNow().AddDays(-7).AddHours(-1)), ReplicasJsonContext.Default.RetentionExpiryAnswer));
}
