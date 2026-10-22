using System.Net;

namespace TerraBase.E2E;

/// <summary>
/// The ts-orch edge for the local nodes: a request goes to its own region's node, and an answer carrying
/// <c>ts-replay: region=&lt;r1&gt;,&lt;r2&gt;</c> is sent again to the first listed region's node other than its own (the
/// stand-in for the nearest), falling to the next when a node is down. A database is served only in its placement's
/// regions, and the others answer with that header (docs/terrabase/multi-region-replication.md, section 16). When no listed
/// node is up and the replay names <c>fallback=self</c>, the request comes back to the first node with
/// <c>ts-replay-failed: 1</c>; without it the original 421 is returned. Every replayed request carries <c>ts-replay-src</c>,
/// and every replay is reported to <paramref name="replayed"/>.
/// </summary>
internal sealed class LocalEdge(string region, Func<string, HttpClient> nodes, Action<LocalEdge.Replay>? replayed = null)
    : HttpMessageHandler
{
    /// <summary>A replay the edge followed: from <c>Edge</c> to <c>Target</c>, and whether it fell back to the edge.</summary>
    internal sealed record Replay(string Edge, string Target, string PathAndQuery, bool FellBack);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var response = await SendToAsync(region, request, body, extra: null, cancellationToken);
        if (!response.Headers.TryGetValues("ts-replay", out var values))
        {
            return response;
        }

        var fields = values.Single().Split(';', StringSplitOptions.TrimEntries);
        var targets = (Field(fields, "region") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).Where(target => target != region).ToArray();
        if (targets.Length == 0)
        {
            return response;
        }

        var path = request.RequestUri!.PathAndQuery;
        foreach (var target in targets)
        {
            try
            {
                var replayedResponse = await SendToAsync(target, request, body, ("ts-replay-src", $"region={region}"), cancellationToken);
                response.Dispose();
                replayed?.Invoke(new Replay(region, target, path, FellBack: false));
                return replayedResponse;
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
            }
        }

        if (Field(fields, "fallback") != "self")
        {
            return response;
        }

        response.Dispose();
        replayed?.Invoke(new Replay(region, targets[^1], path, FellBack: true));
        return await SendToAsync(region, request, body, ("ts-replay-failed", "1"), cancellationToken);
    }

    private async Task<HttpResponseMessage> SendToAsync(
        string target, HttpRequestMessage request, byte[]? body, (string Name, string Value)? extra, CancellationToken cancellationToken)
    {
        using var copy = new HttpRequestMessage(request.Method, new Uri(request.RequestUri!.PathAndQuery, UriKind.Relative));
        foreach (var header in request.Headers)
        {
            _ = copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (extra is { } added)
        {
            _ = copy.Headers.TryAddWithoutValidation(added.Name, added.Value);
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

    // "region=sjc,fra", possibly with other fields: "region=sjc;instance=m;fallback=self".
    private static string? Field(string[] fields, string name) =>
        fields.FirstOrDefault(field => field.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
}
