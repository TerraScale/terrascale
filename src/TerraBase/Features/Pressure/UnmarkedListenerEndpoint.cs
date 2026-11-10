using TerraScale.Endpoints;

namespace TerraBase.Features.Pressure;

internal sealed class UnmarkedListenerEndpoint : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/v1/admin/unmarked");
        AllowAnonymous();
    }

    public override ValueTask HandleAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
