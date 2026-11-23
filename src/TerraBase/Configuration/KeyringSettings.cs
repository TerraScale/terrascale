using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraBase.Configuration;

public sealed class KeyringSettings
{
    public const string SingleKeyId = "k0";

    private KeyringSettings(string active, FrozenDictionary<string, byte[]> keys)
    {
        Active = active;
        Keys = keys;
    }

    public string Active { get; }

    internal FrozenDictionary<string, byte[]> Keys { get; }

    internal static KeyringSettings Single(byte[] key) =>
        new(SingleKeyId, new Dictionary<string, byte[]>(StringComparer.Ordinal) { [SingleKeyId] = key }.ToFrozenDictionary(StringComparer.Ordinal));

    internal static bool ValidId(string id) => id.Length is >= 1 and <= 63
        && id.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    internal static KeyringSettings? Parse(SettingValues values)
    {
        if (values.Get("master-keys") is not { } json)
        {
            return values.Get("master-key") is { } single && MasterKey(values, single) is { } key ? Single(key) : null;
        }

        if (values.Has("master-key"))
        {
            values.Fail("master-key and master-keys cannot be set together");
            return null;
        }

        if (Deserialize(json) is not { Active: { } active, Keys: { Count: >= 1 and <= 32 } entries }
            || !ValidId(active) || !entries.ContainsKey(active) || entries.Any(static pair => !ValidId(pair.Key) || pair.Value is null))
        {
            values.Fail("master-keys must be JSON with 1 to 32 keys with valid ids and a configured active key");
            return null;
        }

        Dictionary<string, byte[]> keys = new(StringComparer.Ordinal);
        foreach (var (id, encoded) in entries)
        {
            if (MasterKey(values, encoded) is not { } material)
            {
                return null;
            }

            if (keys.Values.Any(existing => CryptographicOperations.FixedTimeEquals(existing, material)))
            {
                values.Fail(string.Concat("master key id '", id, "' duplicates another configured id's material"));
                return null;
            }

            keys[id] = material;
        }

        return new KeyringSettings(active, keys.ToFrozenDictionary(StringComparer.Ordinal));
    }

    internal static byte[]? MasterKey(SettingValues values, string encoded)
    {
        var key = new byte[33];
        if (!Convert.TryFromBase64String(encoded, key, out var written) || written != 32)
        {
            values.Fail("master-key must be exactly 32 bytes, base64");
            return null;
        }

        return key[..32];
    }

    private static KeyringConfiguration? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, KeyringJsonContext.Default.KeyringConfiguration);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed record KeyringConfiguration(string? Active, Dictionary<string, string>? Keys);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(KeyringConfiguration))]
internal sealed partial class KeyringJsonContext : JsonSerializerContext;
