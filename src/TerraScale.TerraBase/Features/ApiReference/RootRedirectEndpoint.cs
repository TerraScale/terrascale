using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.ApiReference;

/// <summary>
/// <c>GET /</c>: redirects to the Scalar API reference. The <c>Location</c> is relative, so it holds on every regional host
/// and behind proxies. Unauthenticated, on the client listener only.
/// </summary>
public sealed class RootRedirectEndpoint : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/");
        Name("apiReference");
        AllowAnonymous();
        Metadata(ListenerMetadata.Client, new ExcludeFromDescriptionAttribute());
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        // Scalar's own page lives at scalar/ (it redirects /scalar there); go straight to it.
        ValueTask.FromResult<IResult>(TypedResults.Redirect("scalar/"));
}
