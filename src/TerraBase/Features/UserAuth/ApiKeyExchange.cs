using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using TerraBase.Configuration;
using TerraBase.Hosting;

namespace TerraBase.Features.UserAuth;

internal readonly union ApiKeyExchangeOutcome(string, CredentialRejected, BackendUnavailable);

internal readonly union TokenIssueOutcome(ApiKeyExchange.IssuedToken, CredentialRejected, BackendUnavailable);

internal sealed record ApiKeyTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("expires_in")] int? ExpiresIn);

[JsonSerializable(typeof(ApiKeyTokenResponse))]
internal sealed partial class ApiKeyExchangeJsonContext : JsonSerializerContext;

internal sealed class ApiKeyExchange(TerraBaseOptions options, IHttpClientFactory http, TimeProvider time) : IDisposable
{
    private const string KeyPrefix = "sk_";

    private const int SecretLength = 52;

    private const int MaxResponseBytes = 64 * 1024;

    private const int MaxCachedTokens = 4096;

    private const int MaxRefusedKeys = 4096;

    private static readonly TimeSpan RenewBefore = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan RefusalLifetime = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan TrustLifetime = TimeSpan.FromDays(1);

    private const string UnknownKeys = "unknown";

    private static readonly SearchValues<char> SecretCharacters = SearchValues.Create("0123456789abcdefghjkmnpqrstvwxyz");

    private static readonly CredentialRejected Refused = new("the API key was refused for this project", "invalid_key");

    private static readonly BackendUnavailable Busy = new("ts-auth could not exchange the API key; retry", "key_exchange_unavailable");

    private readonly MemoryCache _tokens = new(new MemoryCacheOptions { SizeLimit = MaxCachedTokens });

    private readonly MemoryCache _refused = new(new MemoryCacheOptions { SizeLimit = MaxRefusedKeys });

    private readonly MemoryCache _trusted = new(new MemoryCacheOptions { SizeLimit = MaxCachedTokens });

    private readonly PartitionedRateLimiter<string> _exchanges = PartitionedRateLimiter.Create<string, string>(static partition =>
        RateLimitPartition.GetTokenBucketLimiter(partition, static key => key == UnknownKeys
            ? new TokenBucketRateLimiterOptions
            {
                TokenLimit = 200,
                TokensPerPeriod = 50,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }
            : new TokenBucketRateLimiterOptions
            {
                TokenLimit = 20,
                TokensPerPeriod = 5,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    public static bool IsKey(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var body = secret.StartsWith(KeyPrefix, StringComparison.Ordinal) ? secret.AsSpan(KeyPrefix.Length) : secret.AsSpan();
        return body.Length == 27 + SecretLength && body[26] == '_' && Ulid.TryParse(body[..26], out _)
            && !body[27..].ContainsAnyExcept(SecretCharacters);
    }

    public async ValueTask<ApiKeyExchangeOutcome> ExchangeAsync(string secret, string project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(project);
        if (options.AuthIssuer is not { } issuer)
        {
            return new CredentialRejected("API keys need TERRABASE_AUTH_ISSUER", "invalid_key");
        }

        if (!IsKey(secret) || project.Length != 26 || !Ulid.TryParse(project, out _))
        {
            return new CredentialRejected("send an API key and connect to <project id>/<database>", "invalid_key");
        }

        var cacheKey = string.Concat(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))), "/", project);
        if (_tokens.TryGetValue(cacheKey, out string? cached) && cached is { } token)
        {
            return token;
        }

        if (_refused.TryGetValue(cacheKey, out _))
        {
            return Refused;
        }

        var keyId = KeyId(secret);
        using var permit = _exchanges.AttemptAcquire(_trusted.TryGetValue(keyId, out _) ? keyId : UnknownKeys);
        if (!permit.IsAcquired)
        {
            return Busy;
        }

        var outcome = await RequestAsync(issuer, secret, project, cancellationToken);
        return outcome switch
        {
            IssuedToken issued => Remember(cacheKey, keyId, issued),
            CredentialRejected refused => Forget(cacheKey, refused),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public void Dispose()
    {
        _tokens.Dispose();
        _refused.Dispose();
        _trusted.Dispose();
        _exchanges.Dispose();
    }

    private CredentialRejected Forget(string cacheKey, CredentialRejected refused)
    {
        _refused.Set(cacheKey, true, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpiration = time.GetUtcNow() + RefusalLifetime });
        return refused;
    }

    private static string KeyId(string secret) => secret.StartsWith(KeyPrefix, StringComparison.Ordinal) ? secret[KeyPrefix.Length..(KeyPrefix.Length + 26)] : secret[..26];

    private string Remember(string cacheKey, string keyId, IssuedToken issued)
    {
        _trusted.Set(keyId, true, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TrustLifetime });
        if (issued.Lifetime > RenewBefore)
        {
            _tokens.Set(cacheKey, issued.Token, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpiration = time.GetUtcNow() + issued.Lifetime - RenewBefore });
        }

        return issued.Token;
    }

    private async ValueTask<TokenIssueOutcome> RequestAsync(string issuer, string secret, string project, CancellationToken cancellationToken)
    {
        var body = secret.StartsWith(KeyPrefix, StringComparison.Ordinal) ? secret[KeyPrefix.Length..] : secret;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(string.Concat(issuer.TrimEnd('/'), "/oauth/token")))
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials"), new("project", project)]),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Concat(body[..26], ":", secret))));
        try
        {
            using var client = http.CreateClient(TokenVerifier.HttpClientName);
            client.MaxResponseContentBufferSize = MaxResponseBytes;
            using var response = await client.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return Busy;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return Refused;
            }

            var answer = await response.Content.ReadFromJsonAsync(ApiKeyExchangeJsonContext.Default.ApiKeyTokenResponse, cancellationToken);
            return answer is { AccessToken: { Length: > 0 } token, ExpiresIn: var expiresIn }
                ? new IssuedToken(token, TimeSpan.FromSeconds(expiresIn ?? 0))
                : Refused;
        }
        catch (JsonException)
        {
            return Refused;
        }
        catch (HttpRequestException)
        {
            return Busy;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Busy;
        }
    }

    internal sealed record IssuedToken(string Token, TimeSpan Lifetime);
}
