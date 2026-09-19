namespace TerraScale.Auth.Keys;

using System.Collections.Concurrent;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;

/// <summary>
/// Per-instance JWT signing keys, persisted in TerraBase. Generates an ES256
/// key on first use; rotation keeps the old key verifiable until retired.
/// </summary>
public sealed class KeyRing(AuthStore store)
{
    private readonly ConcurrentDictionary<string, List<EcdsaSigningKey>> cache = new(StringComparer.Ordinal);

    /// <summary>Returns the current signing key for the instance, generating one if needed.</summary>
    public async Task<EcdsaSigningKey> GetCurrentAsync(string instanceId, CancellationToken ct = default)
    {
        var keys = await LoadAsync(instanceId, ct);
        return keys[0];
    }

    /// <summary>All active keys (current first) for JWKS and token verification.</summary>
    public async Task<IReadOnlyList<EcdsaSigningKey>> GetActiveAsync(string instanceId, CancellationToken ct = default) =>
        await LoadAsync(instanceId, ct);

    private async Task<List<EcdsaSigningKey>> LoadAsync(string instanceId, CancellationToken ct)
    {
        if (cache.TryGetValue(instanceId, out var cached))
            return cached;

        var records = await store.ListSigningKeysAsync(instanceId, ct);
        var active = records.Where(r => r.Active).OrderByDescending(r => r.CreatedAt).ToList();

        List<EcdsaSigningKey> keys;
        if (active.Count == 0)
        {
            var generated = EcdsaSigningKey.Generate("skey_" + SecureTokens.RandomToken(20));
            await store.CreateSigningKeyAsync(new SigningKeyRecord
            {
                Id = generated.Id,
                InstanceId = instanceId,
                Algorithm = "ES256",
                SecretBase64 = Convert.ToBase64String(generated.ExportSecret()),
                Active = true,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, ct);
            keys = [generated];
        }
        else
        {
            keys = [];
            foreach (var record in active)
                keys.Add(EcdsaSigningKey.FromSecret(Convert.FromBase64String(record.SecretBase64), record.Id));
        }

        cache[instanceId] = keys;
        return keys;
    }
}

/// <summary>AES-GCM secret box for recoverable secrets (TOTP seeds) using the master key.</summary>
public static class SecretBox
{
    /// <summary>Encrypts to "v1:" + base64(nonce + ciphertext).</summary>
    public static string Encrypt(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(masterKey, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return "v1:" + Convert.ToBase64String([.. nonce, .. ciphertext, .. tag]);
    }

    /// <summary>Decrypts a value produced by <see cref="Encrypt"/>.</summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> masterKey, string boxed)
    {
        if (!boxed.StartsWith("v1:", StringComparison.Ordinal))
            throw new FormatException("unknown secret box version");
        var raw = Convert.FromBase64String(boxed["v1:".Length..]);
        var nonce = raw.AsSpan(0, 12);
        var ciphertext = raw.AsSpan(12, raw.Length - 12 - 16);
        var tag = raw.AsSpan(raw.Length - 16);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(masterKey, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
