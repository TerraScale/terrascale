using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.ApiReference;

public sealed class RootRedirectEndpoint : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/");
        Name("apiReference");
        AllowAnonymous();
        Metadata(new ListenerMetadata(TerraBaseListener.Client), new ExcludeFromDescriptionAttribute());
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        // Scalar's own page lives at scalar/ (it redirects /scalar there); the Location stays relative so it
        // holds on every regional host and behind proxies.
        ValueTask.FromResult<IResult>(TypedResults.Redirect("scalar/"));
}
