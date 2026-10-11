using TerraScale.DotNet;

namespace ApiBench;

/// <summary>Exchanges a project key for a JWT through the public SDK's OIDC client and caches the JWT.</summary>
internal sealed class Tokens(TerraScaleAuthOidcClient oidc, string apiKey)
{
    private const int RefreshMarginSeconds = 60;

    private readonly string clientId = ClientId(apiKey);
    private readonly SemaphoreSlim gate = new(1, 1);
    private Cached? cached;

    /// <summary>The cached JWT, exchanged again within 60 s of expiry.</summary>
    internal async ValueTask<string> GetAsync(CancellationToken cancellationToken)
    {
        if (cached is { } current && DateTimeOffset.UtcNow < current.Refresh) return current.Token;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cached is null || DateTimeOffset.UtcNow >= cached.Refresh)
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

    /// <summary>The ULID in tsk_&lt;ULID&gt;_&lt;secret&gt; is the OAuth client id; the whole key is the client secret.</summary>
    internal static string ClientId(string apiKey)
    {
        if (apiKey.Length <= 31 || !apiKey.StartsWith("tsk_", StringComparison.Ordinal) || apiKey[30] != '_' || !TerraScaleIds.IsValid(apiKey[4..30]))
        {
            throw new FormatException("api_key must be a tsk_<ULID>_<secret> project key.");
        }

        return apiKey[4..30];
    }

    private sealed record Cached(string Token, DateTimeOffset Refresh);
}
