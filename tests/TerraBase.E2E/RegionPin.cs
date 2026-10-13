namespace TerraBase.E2E;

/// <summary>
/// Pins every request to one Fly region with <c>fly-prefer-region</c> and refuses an answer served from elsewhere.
/// Fly falls back to another region when the preferred one is unavailable; without this check a write meant for sjc
/// could land in gru and the test would pass without exercising sjc. The serving node names its region in the
/// <c>X-TerraBase-Region</c> response header; Fly's proxy sends no <c>fly-region</c> header on responses (checked against
/// ts-terrabase on 2026-10-12).
/// </summary>
internal sealed class RegionPin(string region) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Add("fly-prefer-region", region);
        var response = await base.SendAsync(request, cancellationToken);
        var served = response.Headers.TryGetValues("X-TerraBase-Region", out var values) ? string.Join(',', values) : "(no X-TerraBase-Region header)";
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

/// <summary>
/// Pins every request to one Fly machine with <c>fly-force-instance-id</c>, which Fly routes to that machine or fails (it
/// never answers from another), and refuses an answer that does not name that machine and its region
/// (<c>X-TerraBase-Machine</c>, <c>X-TerraBase-Region</c>): the identity of every edge a disposable-app run records.
/// </summary>
internal sealed class MachinePin(string region, string machine) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Add("fly-force-instance-id", machine);
        var response = await base.SendAsync(request, cancellationToken);
        // Fly's own errors carry no TerraBase headers: the status says what answered instead.
        var served = $"{Header(response, "X-TerraBase-Region")}/{Header(response, "X-TerraBase-Machine")} (HTTP {(int)response.StatusCode})";
        if (Header(response, "X-TerraBase-Region") == region && Header(response, "X-TerraBase-Machine") == machine)
        {
            return response;
        }

        response.Dispose();
        throw new RegionMismatchException($"{region}/{machine}", served, request.Method, request.RequestUri);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(',', values) : $"(no {name})";
}
