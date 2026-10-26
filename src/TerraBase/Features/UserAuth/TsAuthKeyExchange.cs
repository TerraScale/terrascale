using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Primitives;

namespace TerraBase.Features.UserAuth;

internal sealed record TsAuthTokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);

/// <summary>What a ts-auth project API key may reach, read from the access token ts-auth issued for it.</summary>
/// <param name="Subject">The key's id (the token's <c>sub</c>).</param>
/// <param name="ProjectId">The key's project.</param>
/// <param name="Database">The key's database restriction, when it has one.</param>
/// <param name="Scopes">Granted scopes.</param>
/// <param name="ValidTo">When the access token expires.</param>
/// <param name="Allowlists">The key's and project's IP allowlists (<see cref="IpAllowlists"/>); empty for none.</param>
/// <param name="Key">The token as <see cref="IKeyAuthority"/> checks it before every statement.</param>
internal sealed record TsAuthKeyGrant(string Subject, string ProjectId, string? Database, IReadOnlySet<string> Scopes, DateTimeOffset ValidTo, IReadOnlyList<IPNetwork[]> Allowlists, KeyToken Key);

/// <summary>A key's grant, a refused key, or ts-auth is unavailable.</summary>
internal readonly union TsAuthKeyResult(TsAuthKeyGrant, NotFound, Unavailable);

/// <summary>
/// Accepts a dashboard API key (<c>&lt;key id&gt;_&lt;secret&gt;</c>, a ts-auth client credential) where a client can only send
/// a password, as on the Postgres wire: exchanges it at ts-auth's token endpoint with the client credentials grant and
/// validates the access token exactly like a bearer token. A grant is reused until shortly before its token expires and
/// then exchanged again; whether its key still acts is <see cref="IKeyAuthority"/>'s answer, asked before every statement.
/// </summary>
internal sealed class TsAuthKeyExchange(TerraBaseOptions options, IHttpClientFactory http, IIssuerKeys issuerKeys, IMemoryBudget budget, TimeProvider time)
{
    private const int MaxInFlight = 16;
    private const int MaxResponseBytes = 64 * 1024;
    private const int MaxKeyLength = 256;
    private const int MaxGrants = 4096;

    // Exchanged again this long before the token expires, so a statement never runs on a token about to lapse. ts-auth
    // issues 60-second project tokens: a key exchanges about every 50 seconds.
    private static readonly TimeSpan RenewBefore = TimeSpan.FromSeconds(10);
    private static readonly JsonWebTokenHandler Tokens = new();
    private static readonly SearchValues<char> SecretCharacters = SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_");

    // Keyed by the SHA-256 of the key, never the key itself.
    private readonly ConcurrentDictionary<string, TsAuthKeyGrant> _grants = new(StringComparer.Ordinal);
    private int _inFlight;

    /// <summary>Whether <paramref name="secret"/> has the shape of a ts-auth project API key.</summary>
    public static bool IsKey(string secret) =>
        secret.Length is > 27 and <= MaxKeyLength && secret[26] == '_' && EntityIds.IsValid(secret[..26])
        && !secret.AsSpan(27).ContainsAnyExcept(SecretCharacters);

    public async ValueTask<TsAuthKeyResult> ExchangeAsync(string secret, CancellationToken cancellationToken)
    {
        if (options.AuthIssuer is not { } issuer || !IsKey(secret))
        {
            return new NotFound();
        }

        var cacheKey = ValidatedTokens.KeyOf(secret);
        if (_grants.TryGetValue(cacheKey, out var cached) && time.GetUtcNow() < cached.ValidTo - RenewBefore)
        {
            return cached;
        }

        var exchanged = await RequestAsync(issuer, secret, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask<TsAuthKeyResult> RequestAsync(string issuer, string secret, CancellationToken cancellationToken)
    {
        // RFC 6749 §2.3.1: the client id and secret are form-encoded before they are joined; both are URL-safe already.
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(secret[..26] + ":" + secret));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(issuer.TrimEnd('/') + "/oauth/token"))
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials")]),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        string? token;
        try
        {
            if (Interlocked.Increment(ref _inFlight) > MaxInFlight)
            {
                return Unavailable();
            }

            using var reservation = budget.TryReserve(MemoryLane.Foreground, 4L * MaxResponseBytes);
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
                return new NotFound();
            }

            token = (await response.Content.ReadFromJsonAsync(TerraBaseJsonContext.Default.TsAuthTokenResponse, cancellationToken).ConfigureAwait(false))?.AccessToken;
        }
        catch (JsonException)
        {
            return new NotFound();
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

        return token is null ? new NotFound() : await ValidateAsync(token, cancellationToken).ConfigureAwait(false);
    }

    // The same checks as a bearer token on the client listener: signature, issuer, audience, type and lifetime.
    private async ValueTask<TsAuthKeyResult> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if ((issuerKeys.Keys ?? await issuerKeys.JoinAsync(cancellationToken).ConfigureAwait(false)) is not { } keys)
        {
            return Unavailable();
        }

        var result = await Tokens.ValidateTokenAsync(token, UserAuthenticationHandler.Parameters(options, keys, time)).ConfigureAwait(false);
        if (result.Exception is SecurityTokenSignatureKeyNotFoundException
            && await issuerKeys.RefreshAsync(cancellationToken).ConfigureAwait(false) is IReadOnlyList<SecurityKey> refreshed)
        {
            result = await Tokens.ValidateTokenAsync(token, UserAuthenticationHandler.Parameters(options, refreshed, time)).ConfigureAwait(false);
        }

        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt
            || !jwt.TryGetPayloadValue<string>(JwtRegisteredClaimNames.Sub, out var subject) || !EntityIds.IsValid(subject)
            || !jwt.TryGetPayloadValue<string>("project_id", out var project) || !EntityIds.IsValid(project)
            || jwt.Alg != SecurityAlgorithms.EcdsaSha256 || string.IsNullOrEmpty(jwt.Kid)
            || !jwt.TryGetPayloadValue<string>("key_id", out var keyId) || keyId != subject
            || !jwt.TryGetPayloadValue<string>("key_gen", out var generation) || !KeyToken.IsGeneration(generation))
        {
            return new NotFound();
        }

        var database = jwt.TryGetPayloadValue<string>("database", out var name) ? name : null;
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
                    return new NotFound();
                }

                allowlists.Add(networks);
            }
        }

        return new TsAuthKeyGrant(subject, project, database, scopes, new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero), allowlists,
            new KeyToken(subject, generation, project, token));
    }

    private static Unavailable Unavailable() => new("ts-auth key exchange is unavailable");
}
