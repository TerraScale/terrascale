using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Configuration;

namespace Journey.Keyring;

internal sealed class MasterKeys
{
    public const int RawKeyLength = 32;

    private static readonly byte[] Info = "terrabase/v2/database-key"u8.ToArray();

    private readonly Dictionary<string, byte[]> _keys;

    private MasterKeys(string active, Dictionary<string, byte[]> keys)
    {
        Active = active;
        _keys = keys;
    }

    public string Active { get; }

    public IReadOnlyList<string> Ids => [.. _keys.Keys.Order(StringComparer.Ordinal)];

    public static MasterKeys FromProcess()
    {
        if (Setting("TERRABASE_MASTER_KEYS") is { } json)
        {
            var parsed = JsonSerializer.Deserialize(json, ProofJsonContext.Default.KeyringFile)
                ?? throw new InvalidOperationException("TERRABASE_MASTER_KEYS is empty");
            return new MasterKeys(parsed.Active, parsed.Keys.ToDictionary(
                static pair => pair.Key, static pair => Convert.FromBase64String(pair.Value), StringComparer.Ordinal));
        }

        if (Setting("TERRABASE_MASTER_KEY") is { } single)
        {
            return new MasterKeys(KeyringSettings.SingleKeyId, new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [KeyringSettings.SingleKeyId] = Convert.FromBase64String(single),
            });
        }

        throw new InvalidOperationException("the keyring proof reads the same TERRABASE_MASTER_KEYS_FILE or TERRABASE_MASTER_KEY as the node");
    }

    public byte[] Derive(Ulid databaseId, string keyId) => _keys.TryGetValue(keyId, out var master)
        ? HKDF.DeriveKey(HashAlgorithmName.SHA256, master, RawKeyLength, databaseId.ToByteArray(), Info)
        : throw new InvalidOperationException(string.Concat("master key '", keyId, "' is not configured for the proof"));

    private static string? Setting(string name)
    {
        if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
        {
            return value;
        }

        return Environment.GetEnvironmentVariable(name + "_FILE") is { Length: > 0 } path ? File.ReadAllText(path).Trim() : null;
    }
}
