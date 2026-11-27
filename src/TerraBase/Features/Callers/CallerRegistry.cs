using System.Collections.Frozen;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Configuration;
using TerraBase.Hosting;

namespace TerraBase.Features.Callers;

internal readonly union CallerRegistryOutcome(CallerRegistry, ConfigurationError);

internal sealed record CallerKey(string Kid, string Caller, string Kind, JsonElement Jwk, IReadOnlyList<string> Grants, DateTimeOffset NotAfter);

internal sealed record CallerList(int Version, IReadOnlyList<CallerKey>? Callers);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CallerList))]
internal sealed partial class CallersJsonContext : JsonSerializerContext;

internal sealed class CallerRegistry(FrozenDictionary<string, CallerKey> keys, int expired)
{
    public const string FileVariable = "TERRABASE_CALLERS_FILE";

    public static readonly FrozenSet<string> Grants = FrozenSet.Create(StringComparer.Ordinal,
        "closure", "plans", "usage.read", "catalog.read", "rollout", "platform.read", "platform.write", "act-as", "admin", "audit.read");

    public FrozenDictionary<string, CallerKey> Keys { get; } = keys;

    public FrozenDictionary<string, JsonWebKey> SigningKeys { get; } = BuildSigningKeys(keys);

    private static FrozenDictionary<string, JsonWebKey> BuildSigningKeys(FrozenDictionary<string, CallerKey> keys)
    {
        if (keys.Count == 0)
        {
            return FrozenDictionary<string, JsonWebKey>.Empty;
        }

        Dictionary<string, JsonWebKey> signing = new(keys.Count, StringComparer.Ordinal);
        foreach (var (kid, entry) in keys)
        {
            signing.Add(kid, new JsonWebKey(entry.Jwk.GetRawText()) { Kid = entry.Kid });
        }

        return signing.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public static bool IsGrant(string grant) => Grants.Contains(grant)
        || (grant.StartsWith("data:", StringComparison.Ordinal) && Ulid.TryParse(grant["data:".Length..], out _));

    public static CallerRegistryOutcome FromProcess(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var path = Environment.GetEnvironmentVariable(FileVariable);
        if (string.IsNullOrEmpty(path))
        {
            return new CallerRegistry(FrozenDictionary<string, CallerKey>.Empty, 0);
        }

        var invalid = new ConfigurationError(FileVariable + " must contain a valid version 1 caller registry");
        if (FileText.Read(path) is not { } json || Deserialize(json) is not { Version: 1, Callers: { } callers })
        {
            return invalid;
        }

        Dictionary<string, CallerKey> entries = new(StringComparer.Ordinal);
        var expiredCount = 0;
        var now = time.GetUtcNow();
        foreach (var key in callers)
        {
            if (!Valid(key) || entries.ContainsKey(key.Kid))
            {
                return invalid;
            }

            if (key.NotAfter <= now)
            {
                expiredCount++;
            }
            else
            {
                entries.Add(key.Kid, key);
            }
        }

        return new CallerRegistry(entries.ToFrozenDictionary(StringComparer.Ordinal), expiredCount);
    }

    public void Observe(IMeterFactory meters, TimeProvider time)
    {
        var meter = meters.Create(Telemetry.MeterName);
        meter.CreateObservableGauge("terrabase.caller.keys.expired", () => expired + Keys.Values.Count(key => key.NotAfter <= time.GetUtcNow()));
        meter.CreateObservableGauge("terrabase.caller.keys.soonest_expiry",
            () => Keys.Values.Where(key => key.NotAfter > time.GetUtcNow()).Select(static key => key.NotAfter.ToUnixTimeSeconds()).DefaultIfEmpty().Min(), "s");
    }

    private static CallerList? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, CallersJsonContext.Default.CallerList);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Valid(CallerKey? key)
    {
        if (key is null || string.IsNullOrWhiteSpace(key.Kid) || key.Kid.Length > 128 || string.IsNullOrWhiteSpace(key.Caller) || key.Caller.Length > 128
            || key.Kind is not ("service" or "operator") || key.NotAfter == default || key.Grants is null || key.Grants.Any(static grant => grant is null || !IsGrant(grant))
            || key.Jwk.ValueKind != JsonValueKind.Object || key.Jwk.TryGetProperty("d", out _))
        {
            return false;
        }

        try
        {
            var jwk = new JsonWebKey(key.Jwk.GetRawText());
            return jwk.Kty == "EC" && jwk.Crv == "P-256" && Base64UrlEncoder.DecodeBytes(jwk.X).Length == 32 && Base64UrlEncoder.DecodeBytes(jwk.Y).Length == 32;
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
            return false;
        }
    }
}
