using System.Net;
using System.Text.Json;

namespace TerraScale.DotNet;

/// <summary>Validates and briefly caches Atlas' ranked auth endpoints.</summary>
internal sealed class TerraScaleAuthEndpointDiscovery(HttpClient httpClient, Uri atlasOrigin)
{
    private const int MaxEndpoints = 20;
    private const int MaxPayloadBytes = 65_536;
    private readonly Uri discoveryUri = new(atlasOrigin, "/api/v1/auth/discovery");
    private readonly object gate = new();
    private Snapshot? cached;

    private sealed record Snapshot(IReadOnlyList<Uri> Endpoints, DateTimeOffset Expires);

    internal async Task<IReadOnlyList<Uri>> GetEndpointsAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (cached is { } snapshot && snapshot.Expires > DateTimeOffset.UtcNow)
            {
                return snapshot.Endpoints;
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, discoveryUri);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException("Atlas auth endpoint discovery is unavailable.", null, response.StatusCode);
        }

        if (response.Content.Headers.ContentLength > MaxPayloadBytes)
        {
            throw new HttpRequestException("Atlas auth endpoint discovery response is too large.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaxPayloadBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        if (length > MaxPayloadBytes) throw new HttpRequestException("Atlas auth endpoint discovery response is too large.");

        IReadOnlyList<Uri> endpoints;
        int refreshSeconds;
        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            var root = document.RootElement;
            if (root.GetProperty("v").GetInt32() != 1)
            {
                throw new JsonException();
            }

            refreshSeconds = root.GetProperty("refreshSeconds").GetInt32();
            var entries = root.GetProperty("endpoints");
            if (refreshSeconds is < 1 or > 3600 || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() is < 1 or > MaxEndpoints)
            {
                throw new JsonException();
            }

            var parsed = new List<Uri>(entries.GetArrayLength());
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                var region = entry.GetProperty("region").GetString();
                var rawUrl = entry.GetProperty("url").GetString();
                if (region is null || !TerraScaleRegionCodes.IsKnown(region) || !seen.Add(region) ||
                    !Uri.TryCreate(rawUrl, UriKind.Absolute, out var url) ||
                    url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort || url.UserInfo.Length != 0 ||
                    url.AbsolutePath != "/" || url.Query.Length != 0 || url.Fragment.Length != 0 ||
                    !url.Host.Equals($"{region.ToLowerInvariant()}.auth.terrascale.tech", StringComparison.OrdinalIgnoreCase))
                {
                    throw new JsonException();
                }

                parsed.Add(url);
            }

            endpoints = parsed;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new HttpRequestException("Atlas returned an invalid auth endpoint discovery response.", exception);
        }

        lock (gate)
        {
            cached = new Snapshot(endpoints, DateTimeOffset.UtcNow.AddSeconds(Math.Min(refreshSeconds, 60)));
        }

        return endpoints;
    }
}
