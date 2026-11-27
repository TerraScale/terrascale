using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TerraBase.Features.Callers;

internal sealed record CallerIdentity(CallerKey Key, FrozenSet<string> Grants, DateTimeOffset Until, string? Reason);

internal sealed class CallerTokens(CallerRegistry registry, TimeProvider time)
{
    public const string Header = "X-TerraScale-Caller";

    private const int MaxCached = 4096;

    private static readonly JsonWebTokenHandler Tokens = new() { MaximumTokenSizeInBytes = 8192 };
    private static readonly string[] Algorithms = [SecurityAlgorithms.EcdsaSha256];
    private static readonly string[] Types = ["ts-caller+jwt"];

    private readonly ConcurrentDictionary<string, CallerIdentity> _cache = new(StringComparer.Ordinal);

    public async Task<CallerIdentity?> ReadAsync(HttpRequest request)
    {
        if (request.Headers[Header] is not [{ Length: > 0 and <= 8192 } token])
        {
            return null;
        }

        var now = time.GetUtcNow();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        if (_cache.TryGetValue(hash, out var cached) && cached.Until > now)
        {
            return cached;
        }

        if (Parse(token) is not { } jwt || jwt.Alg != SecurityAlgorithms.EcdsaSha256 || jwt.Typ != Types[0] || string.IsNullOrEmpty(jwt.Kid)
            || !registry.Keys.TryGetValue(jwt.Kid, out var key) || key.NotAfter <= now
            || !jwt.TryGetPayloadValue<long>("iat", out var issued) || !jwt.TryGetPayloadValue<long>("exp", out var expires) || string.IsNullOrWhiteSpace(jwt.Id)
            || issued > now.ToUnixTimeSeconds() + 30 || issued < now.ToUnixTimeSeconds() - 930 || expires <= now.ToUnixTimeSeconds() - 30
            || expires <= issued || expires - issued > (key.Kind == "service" ? 300 : 900))
        {
            return null;
        }

        var validated = await Tokens.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidAlgorithms = Algorithms,
            ValidTypes = Types,
            ValidIssuer = key.Caller,
            ValidAudience = "terrabase",
            IssuerSigningKey = new JsonWebKey(key.Jwk.GetRawText()) { Kid = key.Kid },
            TryAllIssuerSigningKeys = false,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            LifetimeValidator = (notBefore, expiration, _, _) => expiration is { } until && until >= now.UtcDateTime.AddSeconds(-30)
                && (notBefore is null || notBefore.Value <= now.UtcDateTime.AddSeconds(30)),
        });
        if (!validated.IsValid)
        {
            return null;
        }

        IReadOnlyList<string> grants = key.Grants;
        string? reason = null;
        if (key.Kind == "operator")
        {
            if (!jwt.TryGetPayloadValue<string[]>("cap", out var caps) || caps is null || caps.Any(cap => !key.Grants.Contains(cap, StringComparer.Ordinal))
                || !jwt.TryGetPayloadValue("reason", out reason) || string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            {
                return null;
            }

            grants = caps;
        }

        var until = DateTimeOffset.FromUnixTimeSeconds(expires);
        var identity = new CallerIdentity(key, grants.ToFrozenSet(StringComparer.Ordinal), until < key.NotAfter ? until : key.NotAfter, reason);
        if (_cache.Count >= MaxCached)
        {
            foreach (var stale in _cache.Where(entry => entry.Value.Until <= now).Select(static entry => entry.Key).ToArray())
            {
                _cache.TryRemove(stale, out _);
            }
        }

        if (_cache.Count < MaxCached)
        {
            _cache.TryAdd(hash, identity);
        }

        return identity;
    }

    private static JsonWebToken? Parse(string token)
    {
        if (!Tokens.CanReadToken(token))
        {
            return null;
        }

        try
        {
            return new JsonWebToken(token);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
