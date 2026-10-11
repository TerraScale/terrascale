namespace TerraBase.E2E;

/// <summary>
/// Pins every request to one Fly region with <c>fly-prefer-region</c> and refuses an answer served from elsewhere.
/// Fly falls back to another region when the preferred one is unavailable; without this check a write meant for sjc
/// could land in gru and the test would pass without exercising sjc. Fly reports the serving region in the
/// <c>fly-region</c> response header.
/// </summary>
internal sealed class RegionPin(string region) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Add("fly-prefer-region", region);
        var response = await base.SendAsync(request, cancellationToken);
        var served = response.Headers.TryGetValues("fly-region", out var values) ? string.Join(',', values) : "(no fly-region header)";
        if (served == region)
        {
            return response;
        }

        response.Dispose();
        throw new RegionMismatchException(region, served, request.Method, request.RequestUri);
    }
}

/// <summary>A request pinned to one region that Fly served from another, or that reported no region.</summary>
internal sealed class RegionMismatchException(string pinned, string served, HttpMethod method, Uri? uri)
    : Exception($"{method} {uri} is pinned to {pinned} but was served by {served}");
