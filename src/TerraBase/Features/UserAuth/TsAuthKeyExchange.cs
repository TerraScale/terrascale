using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Primitives;

namespace TerraBase.Features.UserAuth;

internal sealed record TsAuthTokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);

internal sealed record TsAuthKeyGrant(string Subject, string OrganizationId, string ProjectId, string? Database, IReadOnlySet<string> Scopes, DateTimeOffset ValidTo, IReadOnlyList<IPNetwork[]> Allowlists, KeyToken? Key);

internal readonly union TsAuthKeyResult(TsAuthKeyGrant, NotFound, Unavailable);

internal sealed class TsAuthKeyExchange(TerraBaseOptions options, IHttpClientFactory http, IIssuerKeys issuerKeys, IMemoryBudget budget, TimeProvider time)
{
    private const int MaxInFlight = 16;
    private const int MaxResponseBytes = 64 * 1024;
    private const int MaxKeyLength = 256;
    private const int MaxGrants = 4096;

    private const int MaxExchanging = 64;

    private static readonly TimeSpan RenewBefore = TimeSpan.FromSeconds(10);
    private static readonly JsonWebTokenHandler Tokens = new();

    private static readonly NotFound Refused =
        new("the API key, project or database was not found, or the key may not use it; check the key, the project id and the database name");
    private static readonly SearchValues<char> SecretCharacters = SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_");

    private readonly ConcurrentDictionary<string, TsAuthKeyGrant> _grants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<TsAuthKeyResult>>> _exchanging = new(StringComparer.Ordinal);
    private int _inFlight;

    public static bool IsKey(string secret)
    {
        if (secret.StartsWith("sk_", StringComparison.Ordinal))
        {
            secret = secret[3..];
        }

        return secret.Length is > 27 and <= MaxKeyLength && secret[26] == '_' && EntityIds.IsValid(secret[..26])
            && !secret.AsSpan(27).ContainsAnyExcept(SecretCharacters);
    }

    public async ValueTask<TsAuthKeyResult> ExchangeAsync(string secret, string? project, CancellationToken cancellationToken)
    {
        if (options.AuthIssuer is not { } issuer || !IsKey(secret) || project is not null && !EntityIds.IsValid(project))
        {
            return Refused;
        }

        var cacheKey = ValidatedTokens.KeyOf(secret) + (project is null ? string.Empty : "/" + project);
        if (_grants.TryGetValue(cacheKey, out var cached) && time.GetUtcNow() < cached.ValidTo - RenewBefore)
        {
            return cached;
        }

        if (_exchanging.Count >= MaxExchanging && !_exchanging.ContainsKey(cacheKey))
        {
            return Unavailable();
        }

        return await _exchanging.GetOrAdd(cacheKey, _ => new Lazy<Task<TsAuthKeyResult>>(() => ExchangeOnceAsync(cacheKey, issuer, secret, project)))
            .Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TsAuthKeyResult> ExchangeOnceAsync(string cacheKey, string issuer, string secret, string? project)
    {
        try
        {
            var exchanged = await RequestAsync(issuer, secret, project, CancellationToken.None).ConfigureAwait(false);
            switch (exchanged)
            {
                case TsAuthKeyGrant grant:
                    if (_grants.Count >= MaxGrants)
                    {
                        var now = time.GetUtcNow();
                        foreach (var (key, entry) in _grants)
                        {
                            if (entry.ValidTo <= now)
                            {
                                _grants.TryRemove(key, out _);
                            }
                        }

                        if (_grants.Count >= MaxGrants)
                        {
                            _grants.Clear();
                        }
                    }

                    _grants[cacheKey] = grant;
                    break;
                case NotFound:
                    _grants.TryRemove(cacheKey, out _);
                    break;
            }

            return exchanged;
        }
        finally
        {
            _exchanging.TryRemove(cacheKey, out _);
        }
    }

    private async ValueTask<TsAuthKeyResult> RequestAsync(string issuer, string secret, string? project, CancellationToken cancellationToken)
    {
        var body = secret.StartsWith("sk_", StringComparison.Ordinal) ? secret[3..] : secret;
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(body[..26] + ":" + secret));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(issuer.TrimEnd('/') + "/oauth/token"))
        {
            Content = new FormUrlEncodedContent(project is null
                ? [new("grant_type", "client_credentials")]
                : [new("grant_type", "client_credentials"), new("project", project)]),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        string? token;
        try
        {
            if (Interlocked.Increment(ref _inFlight) > MaxInFlight)
            {
                return Unavailable();
            }

            using var reservation = budget.TryReserve(4L * MaxResponseBytes);
            if (reservation is null)
            {
                return Unavailable();
            }

            using var client = http.CreateClient(TsAuthKeys.HttpClientName);
            client.MaxResponseContentBufferSize = MaxResponseBytes;
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return Unavailable();
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return Refused;
            }

            token = (await response.Content.ReadFromJsonAsync(UserAuthJsonContext.Default.TsAuthTokenResponse, cancellationToken).ConfigureAwait(false))?.AccessToken;
        }
        catch (JsonException)
        {
            return Refused;
        }
        catch (HttpRequestException)
        {
            return Unavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable();
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }

        return token is null ? Refused : await ValidateAsync(token, project, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TsAuthKeyResult> ValidateAsync(string token, string? project, CancellationToken cancellationToken)
    {
        if (!UserAuthenticationHandler.CouldBeValid(token, options, time))
        {
            return Refused;
        }

        var candidate = Tokens.ReadJsonWebToken(token);
        if (!candidate.TryGetPayloadValue<string>("project_id", out var requested) || !EntityIds.IsValid(requested)
            || requested != (project ?? requested) || candidate.Alg != SecurityAlgorithms.EcdsaSha256 || string.IsNullOrEmpty(candidate.Kid))
        {
            return Refused;
        }

        if ((issuerKeys.Keys ?? await issuerKeys.JoinAsync(cancellationToken).ConfigureAwait(false)) is not { } keys)
        {
            return Unavailable();
        }

        var result = await Tokens.ValidateTokenAsync(token, UserAuthenticationHandler.Parameters(options, keys, time)).ConfigureAwait(false);
        if (result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            var refreshed = await issuerKeys.RefreshAsync(Tokens.ReadJsonWebToken(token).Kid, cancellationToken).ConfigureAwait(false);
            if (refreshed is not IReadOnlyList<SecurityKey> refreshedKeys)
            {
                return Unavailable();
            }

            result = await Tokens.ValidateTokenAsync(token, UserAuthenticationHandler.Parameters(options, refreshedKeys, time)).ConfigureAwait(false);
        }

        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt
            || !jwt.TryGetPayloadValue<string>(JwtRegisteredClaimNames.Sub, out var subject) || !EntityIds.IsValid(subject)
            || !jwt.TryGetPayloadValue<string>("org_id", out var organization) || !EntityIds.IsValid(organization)
            || !jwt.TryGetPayloadValue<string>("project_id", out var granted) || !EntityIds.IsValid(granted) || granted != (project ?? granted)
            || jwt.Alg != SecurityAlgorithms.EcdsaSha256 || string.IsNullOrEmpty(jwt.Kid))
        {
            return Refused;
        }

        if (jwt.TryGetPayloadValue<object>("key_id", out var rawId) && rawId is not string
            || jwt.TryGetPayloadValue<object>("key_gen", out var rawGeneration) && rawGeneration is not string)
        {
            return Refused;
        }

        var hasKey = jwt.TryGetPayloadValue<string>("key_id", out var keyId);
        if ((hasKey | jwt.TryGetPayloadValue<string>("key_gen", out var generation)) && (!hasKey || keyId != subject || !KeyToken.IsGeneration(generation)))
        {
            return Refused;
        }

        if (jwt.TryGetPayloadValue<object>("database", out var restriction) && restriction is not string)
        {
            return Refused;
        }
        var database = jwt.TryGetPayloadValue<string>("database", out var name) ? name : null;
        if (database is not null && !EntityIds.IsValid(database))
        {
            return Refused;
        }
        var scopes = jwt.TryGetPayloadValue<string>("scope", out var scope) && scope.Length <= 4096
            ? scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)
            : [];
        List<IPNetwork[]> allowlists = [];
        foreach (var claim in IpAllowlists.Claims)
        {
            if (jwt.TryGetPayloadValue<string>(claim, out var cidrs))
            {
                if (IpAllowlists.Parse(cidrs) is not { } networks)
                {
                    return Refused;
                }

                allowlists.Add(networks);
            }
        }

        return new TsAuthKeyGrant(subject, organization, granted, database, scopes, new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero), allowlists,
            hasKey ? new KeyToken(subject, generation!, granted, token) { ValidTo = new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero) } : null);
    }

    private static Unavailable Unavailable() => new("ts-auth key exchange is unavailable");
}
