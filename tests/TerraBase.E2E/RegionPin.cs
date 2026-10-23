namespace TerraBase.E2E;

/// <summary>
/// Verifies the answering region after the edge follows neutral replay. The direct client address chooses the landing
/// region; <c>served</c> names the expected answering region when a cursor resume is replayed to its issuer's region.
/// No outgoing routing pin is added, because it would remain on the replay and reject the intended issuer.
/// </summary>
internal sealed class RegionPin(string region, string? served = null) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
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

/// <summary>A request answered by an unexpected region, or that reported no region.</summary>
internal sealed class RegionMismatchException(string pinned, string served, HttpMethod method, Uri? uri)
    : Exception($"{method} {uri} is pinned to {pinned} but was served by {served}");
