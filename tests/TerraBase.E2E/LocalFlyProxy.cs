namespace TerraBase.E2E;

/// <summary>
/// Fly's proxy for the local nodes: a request goes to its own region's node, and an answer carrying
/// <c>fly-replay: region=&lt;r&gt;</c> is sent again to <c>r</c>'s node, as Fly replays it. A database is served only in its
/// placement's regions, and the others answer with that header (docs/terrabase/multi-region-replication.md, section 16).
/// </summary>
internal sealed class LocalFlyProxy(string region, Func<string, HttpClient> nodes) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var response = await SendToAsync(region, request, body, cancellationToken);
        if (!response.Headers.TryGetValues("fly-replay", out var replay) || Target(replay) is not { } target || target == region)
        {
            return response;
        }

        response.Dispose();
        return await SendToAsync(target, request, body, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendToAsync(string target, HttpRequestMessage request, byte[]? body, CancellationToken cancellationToken)
    {
        using var copy = new HttpRequestMessage(request.Method, new Uri(request.RequestUri!.PathAndQuery, UriKind.Relative));
        foreach (var header in request.Headers)
        {
            _ = copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body is not null)
        {
            copy.Content = new ByteArrayContent(body);
            foreach (var header in request.Content!.Headers)
            {
                _ = copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        // Headers first, so a change feed streams through the proxy.
        return await nodes(target).SendAsync(copy, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    // "region=sjc", possibly with other fields: "region=sjc;state=x".
    private static string? Target(IEnumerable<string> values) =>
        values.SelectMany(value => value.Split([';', ','], StringSplitOptions.TrimEntries))
            .FirstOrDefault(field => field.StartsWith("region=", StringComparison.Ordinal))?["region=".Length..];
}
