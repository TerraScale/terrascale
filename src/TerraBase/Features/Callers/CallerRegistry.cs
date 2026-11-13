using System.Collections.Frozen;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraBase.Primitives;

namespace TerraBase.Features.Callers;

internal sealed record CallerKey(string Kid, string Caller, string Kind, JsonElement Jwk, IReadOnlyList<string> Grants, DateTimeOffset NotAfter);
internal sealed record CallerList(int Version, IReadOnlyList<CallerKey> Callers);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CallerList))]
internal sealed partial class CallersJsonContext : JsonSerializerContext;

internal sealed class CallerRegistry(FrozenDictionary<string, CallerKey> keys, int expired)
{
    internal static readonly FrozenSet<string> Grants = FrozenSet.Create(StringComparer.Ordinal,
        "closure", "plans", "usage.read", "catalog.read", "rollout", "platform.read", "platform.write", "act-as", "admin", "audit.read");

    internal FrozenDictionary<string, CallerKey> Keys { get; } = keys;

    internal void Observe(IMeterFactory meters, TimeProvider time)
    {
        var meter = meters.Create(Telemetry.MeterName);
        meter.CreateObservableGauge("terrabase.caller.keys.expired",
            () => expired + Keys.Values.Count(key => key.NotAfter <= time.GetUtcNow()));
        meter.CreateObservableGauge("terrabase.caller.keys.soonest_expiry",
            () => Keys.Values.Where(key => key.NotAfter > time.GetUtcNow())
                .Select(key => key.NotAfter.ToUnixTimeSeconds()).DefaultIfEmpty().Min(), "s");
    }

    internal static bool IsGrant(string grant) => Grants.Contains(grant)
        || grant.StartsWith("data:", StringComparison.Ordinal) && EntityIds.IsValid(grant["data:".Length..]);

    internal static CallerRegistry FromProcess()
    {
        var path = Environment.GetEnvironmentVariable("TERRABASE_CALLERS_FILE");
        if (string.IsNullOrEmpty(path))
        {
            return new(FrozenDictionary<string, CallerKey>.Empty, 0);
        }

        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new(FrozenDictionary<string, CallerKey>.Empty, 0);
            }
            var list = JsonSerializer.Deserialize(json, CallersJsonContext.Default.CallerList);
            if (list is not { Version: 1, Callers: not null })
            {
                throw new JsonException();
            }
            Dictionary<string, CallerKey> entries = new(StringComparer.Ordinal);
            var expired = 0;
            var now = TimeProvider.System.GetUtcNow();
            HashSet<string> ids = new(StringComparer.Ordinal);
            foreach (var key in list.Callers)
            {
                if (key is null || string.IsNullOrWhiteSpace(key.Kid) || key.Kid.Length > 128
                    || string.IsNullOrWhiteSpace(key.Caller) || key.Caller.Length > 128 || key.Kind is not ("service" or "operator")
                    || key.NotAfter == default || key.Grants is null || key.Grants.Any(grant => grant is null || !IsGrant(grant))
                    || key.Jwk.ValueKind != JsonValueKind.Object || key.Jwk.TryGetProperty("d", out _))
                {
                    throw new JsonException();
                }
                var jwk = new JsonWebKey(key.Jwk.GetRawText());
                if (jwk.Kty != "EC" || jwk.Crv != "P-256" || Base64UrlEncoder.DecodeBytes(jwk.X).Length != 32
                    || Base64UrlEncoder.DecodeBytes(jwk.Y).Length != 32 || !ids.Add(key.Kid))
                {
                    throw new JsonException();
                }
                using var curve = ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = Base64UrlEncoder.DecodeBytes(jwk.X), Y = Base64UrlEncoder.DecodeBytes(jwk.Y) },
                });
                if (key.NotAfter <= now)
                {
                    expired++;
                }
                else
                {
                    entries.Add(key.Kid, key);
                }
            }
            return new(entries.ToFrozenDictionary(StringComparer.Ordinal), expired);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException
            or CryptographicException or FormatException)
        {
            throw new TerraBaseConfigurationException("TERRABASE_CALLERS_FILE must contain a valid version 1 caller registry with public keys");
        }
    }
}
