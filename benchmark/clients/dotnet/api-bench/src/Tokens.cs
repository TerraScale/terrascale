using System.Buffers;
using TerraScale.DotNet;

namespace ApiBench;

/// <summary>Exchanges a project key for a JWT through the public SDK's OIDC client and caches the JWT.</summary>
internal sealed class Tokens(TerraScaleAuthOidcClient oidc, string apiKey)
{
    private const int RefreshMarginSeconds = 60;

    private static readonly SearchValues<char> SecretAlphabet = SearchValues.Create("0123456789abcdefghjkmnpqrstvwxyz");

    private readonly string clientId = ClientId(apiKey);
    private readonly SemaphoreSlim gate = new(1, 1);
    private Cached? cached;

    /// <summary>The cached JWT, exchanged again within 60 s of expiry.</summary>
    internal ValueTask<string> GetAsync(CancellationToken cancellationToken) => GetAsync(TimeSpan.Zero, cancellationToken);

    /// <summary>Exchanges now unless the cached JWT stays fresh for <paramref name="horizon"/>, so the next reads cannot refresh it.</summary>
    internal async ValueTask PrewarmAsync(TimeSpan horizon, CancellationToken cancellationToken) => await GetAsync(horizon, cancellationToken);

    private async ValueTask<string> GetAsync(TimeSpan horizon, CancellationToken cancellationToken)
    {
        if (cached is { } current && DateTimeOffset.UtcNow + horizon < current.Refresh) return current.Token;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cached is null || DateTimeOffset.UtcNow + horizon >= cached.Refresh)
            {
                var response = await ExchangeAsync(cancellationToken);
                cached = new Cached(response.AccessToken, DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn - RefreshMarginSeconds));
            }

            return cached.Token;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>One client-credentials exchange. No project form field: ts-auth §6.1 makes it optional for project API keys.</summary>
    internal Task<TerraScaleAuthTokenResponse> ExchangeAsync(CancellationToken cancellationToken) =>
        oidc.RequestClientCredentialsTokenAsync(clientId, apiKey, cancellationToken: cancellationToken);

    /// <summary>The ULID in &lt;ULID&gt;_&lt;52 base32&gt; is the OAuth client id; the whole key is the client secret.</summary>
    internal static string ClientId(string apiKey)
    {
        if (apiKey.Length != 79 || apiKey[26] != '_' || !TerraScaleIds.IsValid(apiKey[..26]) || apiKey.AsSpan(27).ContainsAnyExcept(SecretAlphabet))
        {
            throw new FormatException("api_key must be a <ULID>_<52 base32> project key.");
        }

        return apiKey[..26];
    }

    private sealed record Cached(string Token, DateTimeOffset Refresh);
}
