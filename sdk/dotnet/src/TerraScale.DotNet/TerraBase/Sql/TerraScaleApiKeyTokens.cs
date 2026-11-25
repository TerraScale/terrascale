using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraScale.DotNet.TerraBase;

internal sealed partial class TerraScaleApiKeyTokens
{
    internal static readonly Uri ProductionOrigin = new("https://auth.terrascale.tech/");
    private static readonly TimeSpan ExchangeDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);
    private const int MaxResponseBytes = 64 * 1024;

    private readonly HttpClient http;
    private readonly Uri tokenEndpoint;
    private readonly Uri scopedKeysEndpoint;
    private readonly AuthenticationHeaderValue basic;
    private readonly TimeProvider time;

    internal TerraScaleApiKeyTokens(HttpClient http, Uri authOrigin, string key, string keyName, TimeProvider time)
    {
        if (!KeyPattern().IsMatch(key))
        {
            throw new ArgumentException("ApiKey must be a TerraScale API key from the dashboard: optional sk_, a ULID, an underscore and 52 secret characters.", keyName);
        }

        var id = key.StartsWith("sk_", StringComparison.Ordinal) ? key[3..29] : key[..26];
        this.http = http;
        this.time = time;
        tokenEndpoint = new Uri(authOrigin, "terrascale/oauth/token");
        scopedKeysEndpoint = new Uri(authOrigin, "v1/terrascale/api-keys/self/scoped-keys");
        basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{id}:{key}")));
    }

    internal ProjectToken For(string project) => new(this, project);

    internal async Task<TerraBaseScopedKey> CreateScopedKeyAsync(TerraBaseScopedKeyRequest body, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ExchangeDeadline);
        using var request = new HttpRequestMessage(HttpMethod.Post, scopedKeysEndpoint)
        {
            Content = TerraBaseConnection.Json(body, TerraBaseJsonContext.Wire.TerraBaseScopedKeyRequest),
        };
        request.Headers.Authorization = basic;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = TerraScaleOrigin.Sent(await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false), scopedKeysEndpoint);
            if (!response.IsSuccessStatusCode)
                throw await TerraBaseException.FromResponseAsync(response, null, deadline.Token).ConfigureAwait(false);
            using var answer = new BoundedHttpBodyStream(MaxResponseBytes, () => new InvalidDataException("ts-auth's scoped key answer exceeds 64 KiB."), deadline.Token);
            await response.Content.CopyToAsync(answer, deadline.Token).ConfigureAwait(false);
            return TerraBaseConnection.ReadResponse(answer.Written.Span, TerraBaseJsonContext.Wire.TerraBaseScopedKey, response.StatusCode);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("ts-auth did not answer the scoped key request within the request deadline.", error);
        }
        finally
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    [GeneratedRegex("^(?:sk_)?[0-7][0-9A-HJKMNP-TV-Z]{25}_[0-9a-hjkmnp-tv-z]{52}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    private async Task<(string Token, TimeSpan Lifetime)> ExchangeAsync(string project, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ExchangeDeadline);
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials"), new("project", project)]),
        };
        request.Headers.Authorization = basic;
        using var response = TerraScaleOrigin.Sent(await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false), tokenEndpoint);
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
            || !root.TryGetProperty("expires_in", out var expires) || !expires.TryGetInt64(out var seconds) || seconds <= 0 || token.GetString() is not { Length: > 0 } accessToken)
        {
            throw new InvalidDataException("ts-auth's token answer lacks access_token or a positive expires_in.");
        }

        return (accessToken, TimeSpan.FromSeconds(seconds));
    }

    internal sealed class ProjectToken(TerraScaleApiKeyTokens owner, string project)
    {
        private readonly Lock gate = new();
        private Cached? cached;
        private Task<string>? flight;
        private Exception? refusal;

        public async ValueTask<string> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<string> pending;
            lock (gate)
            {
                if (refusal is not null) throw refusal;
                var now = owner.time.GetUtcNow();
                var valid = cached is { } entry && now < entry.ExpiresAt ? entry : null;
                if (valid is not null && now < valid.RefreshAt) return valid.Token;
                if (flight is null)
                {
                    var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    flight = completion.Task;
                    _ = Task.Run(() => RefreshAsync(completion));
                }

                if (valid is not null) return valid.Token;
                pending = flight;
            }

            return await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Reject(string token)
        {
            lock (gate)
            {
                if (cached is { } entry && entry.Token == token) cached = null;
            }
        }

        private async Task RefreshAsync(TaskCompletionSource<string> completion)
        {
            try
            {
                var issued = owner.time.GetUtcNow();
                var (token, lifetime) = await owner.ExchangeAsync(project, CancellationToken.None).ConfigureAwait(false);
                var expires = issued + lifetime;
                if (owner.time.GetUtcNow() >= expires) throw new InvalidDataException("ts-auth's token expired before it arrived.");
                lock (gate)
                {
                    cached = new Cached(token, issued + lifetime - TimeSpan.FromTicks(Math.Min(RefreshMargin.Ticks, lifetime.Ticks / 2)), expires);
                    completion.SetResult(token);
                    flight = null;
                }
            }
            catch (Exception error)
            {
                lock (gate)
                {
                    if (error is TerraBaseAuthenticationException { ErrorCode: "invalid_client" })
                    {
                        refusal = error;
                        cached = null;
                    }
                    else if (cached is { } entry) cached = entry with { RefreshAt = owner.time.GetUtcNow() + TimeSpan.FromSeconds(1) };
                    completion.SetException(error);
                    _ = completion.Task.Exception;
                    flight = null;
                }
            }
        }

        private sealed record Cached(string Token, DateTimeOffset RefreshAt, DateTimeOffset ExpiresAt)
        {
            public override string ToString() => $"Cached token [REDACTED] until {ExpiresAt:O}";
        }
    }
}
