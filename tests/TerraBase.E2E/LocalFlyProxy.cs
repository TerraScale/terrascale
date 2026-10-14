using System.Net;

namespace TerraBase.E2E;

/// <summary>
/// Fly's proxy for the local nodes: a request goes to its own region's node, and an answer carrying
/// <c>fly-replay: region=&lt;r&gt;</c> is sent again to <c>r</c>'s node, as Fly replays it. A database is served only in its
/// placement's regions, and the others answer with that header (docs/terrabase/multi-region-replication.md, section 16).
/// <c>fly-replay: instance=&lt;machine&gt;</c> (a changes-feed resume on another machine than its cursor's issuer) goes to
/// that machine's node. When the target is down and the replay names <c>fallback=force_self</c> or <c>prefer_self</c>, the
/// request comes back to the first node with <c>fly-replay-failed</c>, Fly's documented fallback. Every replayed request
/// carries <c>Fly-Replay-Src</c>, as on Fly, and every replay is reported to <paramref name="replayed"/>.
/// </summary>
internal sealed class LocalFlyProxy(string region, Func<string, HttpClient> nodes, Func<string, string?> regionOf, Action<LocalFlyProxy.Replay>? replayed = null)
    : HttpMessageHandler
{
    /// <summary>A replay the proxy followed: from <c>Edge</c> to <c>Target</c>, and whether it fell back to the edge.</summary>
    internal sealed record Replay(string Edge, string Target, string PathAndQuery, bool FellBack);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var response = await SendToAsync(region, request, body, extra: null, cancellationToken);
        if (!response.Headers.TryGetValues("fly-replay", out var values))
        {
            return response;
        }

        var fields = values.SelectMany(value => value.Split([';', ','], StringSplitOptions.TrimEntries)).ToArray();
        var target = Field(fields, "region") ?? (Field(fields, "instance") is { } machine ? regionOf(machine) : null);
        if (target is null || target == region)
        {
            return response;
        }

        response.Dispose();
        var path = request.RequestUri!.PathAndQuery;
        try
        {
            var replayedResponse = await SendToAsync(target, request, body, ("Fly-Replay-Src", $"region={region}"), cancellationToken);
            replayed?.Invoke(new Replay(region, target, path, FellBack: false));
            return replayedResponse;
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.ServiceUnavailable && Field(fields, "fallback") is "force_self" or "prefer_self")
        {
            replayed?.Invoke(new Replay(region, target, path, FellBack: true));
            return await SendToAsync(region, request, body, ("fly-replay-failed", "1"), cancellationToken);
        }
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

    // "region=sjc", possibly with other fields: "region=sjc;state=x".
    private static string? Field(string[] fields, string name) =>
        fields.FirstOrDefault(field => field.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
}
