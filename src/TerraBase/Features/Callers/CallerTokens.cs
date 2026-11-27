using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TerraBase.Features.Callers;

internal readonly struct TokenDigest : IEquatable<TokenDigest>
{
    private const int StackByteLimit = 1024;

    private readonly ulong _p0;
    private readonly ulong _p1;
    private readonly ulong _p2;
    private readonly ulong _p3;

    private TokenDigest(ulong p0, ulong p1, ulong p2, ulong p3)
    {
        _p0 = p0;
        _p1 = p1;
        _p2 = p2;
        _p3 = p3;
    }

    public static TokenDigest FromHash(ReadOnlySpan<byte> sha256) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(sha256),
        BinaryPrimitives.ReadUInt64LittleEndian(sha256[8..]),
        BinaryPrimitives.ReadUInt64LittleEndian(sha256[16..]),
        BinaryPrimitives.ReadUInt64LittleEndian(sha256[24..]));

    public static TokenDigest HashUtf8(ReadOnlySpan<char> value)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        HashUtf8(value, digest);
        return FromHash(digest);
    }

    public static void HashUtf8(ReadOnlySpan<char> value, Span<byte> destination)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount <= StackByteLimit)
        {
            Span<byte> buffer = stackalloc byte[byteCount];
            var written = Encoding.UTF8.GetBytes(value, buffer);
            _ = SHA256.HashData(buffer[..written], destination);
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        var writtenCount = 0;
        try
        {
            writtenCount = Encoding.UTF8.GetBytes(value, rented);
            _ = SHA256.HashData(rented.AsSpan(0, writtenCount), destination);
        }
        finally
        {
            if (writtenCount > 0)
            {
                CryptographicOperations.ZeroMemory(rented.AsSpan(0, writtenCount));
            }

            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public bool Equals(TokenDigest other) => _p0 == other._p0 && _p1 == other._p1 && _p2 == other._p2 && _p3 == other._p3;

    public override bool Equals(object? obj) => obj is TokenDigest other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_p0, _p1, _p2, _p3);
}

internal sealed record CallerIdentity(CallerKey Key, FrozenSet<string> Grants, DateTimeOffset Until, string? Reason);

internal sealed class CallerTokens(CallerRegistry registry, TimeProvider time)
{
    public const string Header = "X-TerraScale-Caller";

    private const int MaxCached = 4096;

    private static readonly JsonWebTokenHandler Tokens = new() { MaximumTokenSizeInBytes = 8192 };
    private static readonly string[] Algorithms = [SecurityAlgorithms.EcdsaSha256];
    private static readonly string[] Types = ["ts-caller+jwt"];

    private readonly ConcurrentDictionary<TokenDigest, CachedCallerIdentity> _cache = new();
    private readonly Lock _trimGate = new();
    private int _cachedCount;

    public ValueTask<CallerIdentity?> ReadAsync(HttpRequest request)
    {
        if (request.Headers[Header] is not [{ Length: > 0 and <= 8192 } token])
        {
            return ValueTask.FromResult<CallerIdentity?>(null);
        }

        var now = time.GetUtcNow();
        var digest = TokenDigest.HashUtf8(token);
        if (_cache.TryGetValue(digest, out var cached))
        {
            if (now < cached.Identity.Until && (cached.NotBefore is not { } notBefore || now >= notBefore))
            {
                return ValueTask.FromResult<CallerIdentity?>(cached.Identity);
            }

            if (now >= cached.Identity.Until && _cache.TryRemove(digest, out _))
            {
                _ = Interlocked.Decrement(ref _cachedCount);
            }
        }

        return ReadSlowAsync(token, digest, now);
    }

    private async ValueTask<CallerIdentity?> ReadSlowAsync(string token, TokenDigest digest, DateTimeOffset now)
    {
        if (Parse(token) is not { } jwt || jwt.Alg != SecurityAlgorithms.EcdsaSha256 || jwt.Typ != Types[0] || string.IsNullOrEmpty(jwt.Kid)
            || !registry.Keys.TryGetValue(jwt.Kid, out var key) || !registry.SigningKeys.TryGetValue(jwt.Kid, out var signingKey) || key.NotAfter <= now
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
            IssuerSigningKey = signingKey,
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
        var validUntil = until < key.NotAfter ? until : key.NotAfter;
        var identity = new CallerIdentity(key, grants.ToFrozenSet(StringComparer.Ordinal), validUntil, reason);
        DateTimeOffset? tokenNotBefore = jwt.ValidFrom == DateTime.MinValue
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(jwt.ValidFrom, DateTimeKind.Utc));
        var checkNow = time.GetUtcNow();
        if (checkNow < validUntil && (tokenNotBefore is not { } nbf || checkNow >= nbf))
        {
            Cache(digest, new CachedCallerIdentity(identity, tokenNotBefore), checkNow);
        }

        return identity;
    }

    private void Cache(TokenDigest digest, CachedCallerIdentity entry, DateTimeOffset now)
    {
        if (Volatile.Read(ref _cachedCount) >= MaxCached)
        {
            Trim(now);
        }

        if (Volatile.Read(ref _cachedCount) < MaxCached && _cache.TryAdd(digest, entry))
        {
            _ = Interlocked.Increment(ref _cachedCount);
        }
    }

    private void Trim(DateTimeOffset now)
    {
        if (!_trimGate.TryEnter())
        {
            return;
        }

        try
        {
            if (Volatile.Read(ref _cachedCount) < MaxCached)
            {
                return;
            }

            var removed = 0;
            foreach (var (key, value) in _cache)
            {
                if (now >= value.Identity.Until && _cache.TryRemove(key, out _))
                {
                    _ = Interlocked.Decrement(ref _cachedCount);
                    removed++;
                }
            }

            if (Volatile.Read(ref _cachedCount) >= MaxCached)
            {
                var target = MaxCached / 4;
                foreach (var (key, _) in _cache)
                {
                    if (removed >= target)
                    {
                        break;
                    }

                    if (_cache.TryRemove(key, out _))
                    {
                        _ = Interlocked.Decrement(ref _cachedCount);
                        removed++;
                    }
                }
            }
        }
        finally
        {
            _trimGate.Exit();
        }
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

    private sealed record CachedCallerIdentity(CallerIdentity Identity, DateTimeOffset? NotBefore);
}
