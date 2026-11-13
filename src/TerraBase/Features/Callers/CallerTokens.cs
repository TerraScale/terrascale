using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Features.Memory;

namespace TerraBase.Features.Callers;

internal sealed record CallerIdentity(CallerKey Key, FrozenSet<string> Grants, DateTimeOffset Until, string? Reason);
internal sealed record Requires(params IReadOnlyList<string> Grants);
internal sealed class CallerTransport;

internal sealed class CallerTokens(CallerRegistry registry, IMemoryBudget budget, TimeProvider time) : IDisposable
{
    internal const string Header = "X-TerraScale-Caller";
    private static readonly JsonWebTokenHandler Tokens = new() { MaximumTokenSizeInBytes = 8192 };
    private static readonly string[] Algorithms = ["ES256"];
    private static readonly string[] Types = ["ts-caller+jwt"];
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (CallerIdentity Identity, IMemoryReservation Memory)> _cache = new(StringComparer.Ordinal);

    internal async Task<CallerIdentity?> ReadAsync(HttpRequest request)
    {
        if (request.Headers[Header] is not [{ Length: > 0 and <= 8192 } token])
        {
            return null;
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        lock (_gate)
        {
            if (_cache.TryGetValue(hash, out var cached) && cached.Identity.Until > time.GetUtcNow())
            {
                return cached.Identity;
            }
        }
        using var memory = budget.TryReserve(64 * 1024);
        if (memory is null || !Tokens.CanReadToken(token))
        {
            return null;
        }
        JsonWebToken jwt;
        try
        {
            jwt = new JsonWebToken(token);
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (jwt.Alg != "ES256" || jwt.Typ != "ts-caller+jwt" || string.IsNullOrEmpty(jwt.Kid) || !registry.Keys.TryGetValue(jwt.Kid, out var key))
        {
            return null;
        }
        var now = time.GetUtcNow();
        if (key.NotAfter <= now || !jwt.TryGetPayloadValue<long>("iat", out var issued)
            || !jwt.TryGetPayloadValue<long>("exp", out var expires) || string.IsNullOrWhiteSpace(jwt.Id)
            || issued > now.ToUnixTimeSeconds() + 30 || issued < now.ToUnixTimeSeconds() - 930
            || expires <= now.ToUnixTimeSeconds() - 30
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
            LifetimeValidator = (notBefore, expiration, _, _) => expiration is not null
                && expiration.Value >= now.UtcDateTime.AddSeconds(-30)
                && (notBefore is null || notBefore.Value <= now.UtcDateTime.AddSeconds(30)),
        }).ConfigureAwait(false);
        if (!validated.IsValid)
        {
            return null;
        }
        IReadOnlyList<string> grants = key.Grants;
        string? reason = null;
        if (key.Kind == "operator")
        {
            if (!jwt.TryGetPayloadValue<string[]>("cap", out var caps) || caps is null || caps.Any(cap => !key.Grants.Contains(cap, StringComparer.Ordinal))
                || !jwt.TryGetPayloadValue<string>("reason", out reason) || string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            {
                return null;
            }
            grants = caps;
        }
        var identity = new CallerIdentity(key, grants.ToFrozenSet(StringComparer.Ordinal),
            DateTimeOffset.FromUnixTimeSeconds(expires) < key.NotAfter ? DateTimeOffset.FromUnixTimeSeconds(expires) : key.NotAfter, reason);
        lock (_gate)
        {
            foreach (var stale in _cache.Where(entry => entry.Value.Identity.Until <= now).Select(entry => entry.Key).ToArray())
            {
                _cache.Remove(stale, out var removed);
                removed.Memory.Dispose();
            }
            if (_cache.Count < 4096 && !_cache.ContainsKey(hash) && budget.TryReserve(16 * 1024) is { } reservation)
            {
                _cache.Add(hash, (identity, reservation));
            }
        }
        return identity;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _cache.Values)
            {
                entry.Memory.Dispose();
            }
            _cache.Clear();
        }
    }
}
