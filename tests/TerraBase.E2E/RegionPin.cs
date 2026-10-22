namespace TerraBase.E2E;

/// <summary>
/// Pins every request to one Fly region with <c>fly-prefer-region</c> and refuses an answer served from elsewhere.
/// Fly falls back to another region when the preferred one is unavailable; without this check a write meant for sjc
/// could land in gru and the test would pass without exercising sjc. The serving node names its region in the
/// <c>X-TerraBase-Region</c> response header; Fly's proxy sends no <c>fly-region</c> header on responses (checked against
/// ts-terrabase on 2026-10-12). <c>served</c> names another region only where TerraBase itself replays the
/// request there (a changes-feed resume goes to its cursor's issuing machine): the answer must then come from exactly it.
/// </summary>
internal sealed class RegionPin(string region, string? served = null) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Add("fly-prefer-region", region);
        var response = await base.SendAsync(request, cancellationToken);
        var answered = response.Headers.TryGetValues("X-TerraBase-Region", out var values) ? string.Join(',', values) : "(no X-TerraBase-Region header)";
        if (answered == (served ?? region))
        {
            return response;
        }

        response.Dispose();
        throw new RegionMismatchException(served is null ? region : $"{region}, answered by {served}", answered, request.Method, request.RequestUri);
    }
}

/// <summary>A request pinned to one region that Fly served from another, or that reported no region.</summary>
internal sealed class RegionMismatchException(string pinned, string served, HttpMethod method, Uri? uri)
    : Exception($"{method} {uri} is pinned to {pinned} but was served by {served}");
