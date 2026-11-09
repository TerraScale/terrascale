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
        ValueTask.FromResult<IResult>(TypedResults.Redirect("scalar/"));
}
