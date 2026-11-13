using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraBase.Configuration;

public sealed class KeyringSettings
{
    internal string Active { get; }

    internal FrozenDictionary<string, byte[]> Keys { get; }

    private KeyringSettings(string active, FrozenDictionary<string, byte[]> keys)
    {
        Active = active;
        Keys = keys;
    }

    internal static bool ValidId(string id) => id.Length is >= 1 and <= 63
        && id.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');

    internal static KeyringSettings? Parse(IReadOnlyDictionary<string, string> values)
    {
        if (!values.TryGetValue("master-keys", out var json))
        {
            return values.TryGetValue("master-key", out var single)
                ? new("k0", new Dictionary<string, byte[]> { ["k0"] = BackupSettings.ParseMasterKey(single) }.ToFrozenDictionary(StringComparer.Ordinal))
                : null;
        }

        if (values.ContainsKey("master-key"))
        {
            throw new TerraBaseConfigurationException("master-key and master-keys cannot be set together");
        }

        try
        {
            var document = JsonSerializer.Deserialize(json, KeyringJsonContext.Default.KeyringDocument);
            if (document is null || document.Active is null || document.Keys is null || !ValidId(document.Active) || document.Keys.Count is < 1 or > 32
                || !document.Keys.ContainsKey(document.Active) || document.Keys.Any(pair => !ValidId(pair.Key) || pair.Value is null))
            {
                throw new TerraBaseConfigurationException("master-keys requires 1 to 32 keys with valid ids and a configured active key");
            }

            return new(document.Active, document.Keys.ToFrozenDictionary(pair => pair.Key, pair => BackupSettings.ParseMasterKey(pair.Value), StringComparer.Ordinal));
        }
        catch (JsonException)
        {
            throw new TerraBaseConfigurationException("master-keys must be JSON with active and keys");
        }
    }
}

internal sealed record KeyringDocument(string Active, Dictionary<string, string> Keys);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(KeyringDocument))]
internal sealed partial class KeyringJsonContext : JsonSerializerContext;
