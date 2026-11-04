using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraScale.DotNet.TerraBase;

/// <summary>
/// Exchanges a TerraScale API key for project access tokens at ts-auth (platform contract 6.1: client credentials,
/// Basic keyId:key) and caches each project's token until shortly before it expires. Concurrent callers share one
/// exchange. The key goes only to ts-auth.
/// </summary>
internal sealed partial class TerraScaleApiKeyTokens
{
    private static readonly Uri TokenEndpoint = new("https://auth.terrascale.tech/terrascale/oauth/token");
    private static readonly TimeSpan ExchangeDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);
    private const int MaxResponseBytes = 64 * 1024;

    private readonly HttpClient http;
    private readonly AuthenticationHeaderValue basic;
    private readonly TimeProvider time;

    internal TerraScaleApiKeyTokens(HttpClient http, string key, string keyName, TimeProvider time)
    {
        if (!KeyPattern().IsMatch(key))
        {
            throw new ArgumentException("ApiKey must be a TerraScale API key from the dashboard: a ULID, an underscore and 52 secret characters.", keyName);
        }

        this.http = http;
        this.time = time;
        basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{key[..26]}:{key}")));
    }

    /// <summary>Returns the access-token provider for <paramref name="project"/>.</summary>
    internal Func<CancellationToken, ValueTask<string>> For(string project)
    {
        var cache = new ProjectToken(this, project);
        return cache.GetAsync;
    }

    [GeneratedRegex("^[0-7][0-9A-HJKMNP-TV-Z]{25}_[0-9a-hjkmnp-tv-z]{52}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    private async Task<(string Token, TimeSpan Lifetime)> ExchangeAsync(string project, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ExchangeDeadline);
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials"), new("project", project)]),
        };
        request.Headers.Authorization = basic;
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        using var body = new BoundedHttpBodyStream(MaxResponseBytes, () => new InvalidDataException("ts-auth's token answer exceeds 64 KiB."), deadline.Token);
        await response.Content.CopyToAsync(body, deadline.Token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(body.Length == 0 ? "{}"u8.ToArray() : body.ToArray());
        var root = json.RootElement;
        if (!response.IsSuccessStatusCode)
        {
            var code = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            var text = $"ts-auth refused the API key exchange: HTTP {(int)response.StatusCode}{(code is null ? null : $" ({code})")}.";
            throw response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                ? new TerraBaseAuthenticationException(response.StatusCode, text, code, null)
                : new TerraBaseException(response.StatusCode, text, code, null);
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("access_token", out var token) || token.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("expires_in", out var expires) || !expires.TryGetInt64(out var seconds) || seconds <= 0 || string.IsNullOrEmpty(token.GetString()))
        {
            throw new InvalidDataException("ts-auth's token answer lacks access_token or a positive expires_in.");
        }

        return (token.GetString()!, TimeSpan.FromSeconds(seconds));
    }

    private sealed class ProjectToken(TerraScaleApiKeyTokens owner, string project)
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private Cached? cached;

        public async ValueTask<string> GetAsync(CancellationToken cancellationToken)
        {
            if (Fresh(Volatile.Read(ref cached)) is { } current) return current;
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Fresh(cached) is { } raced) return raced;
                var issued = owner.time.GetUtcNow();
                var (token, lifetime) = await owner.ExchangeAsync(project, cancellationToken).ConfigureAwait(false);
                // Refresh a minute early, or at half-life for short-lived tokens.
                Volatile.Write(ref cached, new Cached(token, issued + lifetime - TimeSpan.FromTicks(Math.Min(RefreshMargin.Ticks, lifetime.Ticks / 2))));
                return token;
            }
            finally
            {
                gate.Release();
            }
        }

        private string? Fresh(Cached? entry) => entry is not null && owner.time.GetUtcNow() < entry.RefreshAt ? entry.Token : null;

        private sealed record Cached(string Token, DateTimeOffset RefreshAt)
        {
            public override string ToString() => $"Cached token [REDACTED] until {RefreshAt:O}";
        }
    }
}
