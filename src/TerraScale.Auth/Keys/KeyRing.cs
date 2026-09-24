namespace TerraScale.Auth.Keys;

using System.Collections.Concurrent;
using System.Net;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.DotNet;

/// <summary>
/// Per-instance JWT signing keys, persisted in TerraBase. Generates an ES256
/// key on first use; rotation keeps the old key verifiable until retired.
/// </summary>
public sealed class KeyRing(AuthStore store)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> creationGates = new(StringComparer.Ordinal);

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
        // Signing and verification must observe key creation, rotation, and
        // retirement performed by any other auth process.
        var active = await ListActiveAsync(instanceId, ct);
        if (active.Count > 0)
            return Restore(active);

        var gate = creationGates.GetOrAdd(instanceId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            active = await ListActiveAsync(instanceId, ct);
            if (active.Count > 0)
                return Restore(active);

            // CreateOnly on a stable id arbitrates first use across processes.
            // A loser loads the winner's secret by id, even if a query page is
            // momentarily stale after the winning write.
            var id = "skey_" + SecureTokens.Sha256Hex("initial:" + instanceId);
            var generated = EcdsaSigningKey.Generate(id);
            try
            {
                await store.CreateSigningKeyAsync(new SigningKeyRecord
                {
                    Id = id,
                    InstanceId = instanceId,
                    Algorithm = "ES256",
                    SecretBase64 = Convert.ToBase64String(generated.ExportSecret()),
                    Active = true,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                }, ct);
                return [generated];
            }
            catch (TerraScaleApiException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
            {
                var winner = await store.GetSigningKeyAsync(id, ct);
                if (winner is not { Active: true } || winner.InstanceId != instanceId)
                    throw new InvalidOperationException("No active signing key remains for the instance.", exception);
                return [Restore(winner)];
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<List<SigningKeyRecord>> ListActiveAsync(string instanceId, CancellationToken ct) =>
        (await store.ListSigningKeysAsync(instanceId, ct))
            .Where(static record => record.Active)
            .OrderByDescending(static record => record.CreatedAt)
            .ThenBy(static record => record.Id, StringComparer.Ordinal)
            .ToList();

    private static List<EcdsaSigningKey> Restore(IEnumerable<SigningKeyRecord> records) =>
        records.Select(Restore).ToList();

    private static EcdsaSigningKey Restore(SigningKeyRecord record) =>
        EcdsaSigningKey.FromSecret(Convert.FromBase64String(record.SecretBase64), record.Id);
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
        if (raw.Length < 12 + 16)
            throw new FormatException("truncated secret box");
        var nonce = raw.AsSpan(0, 12);
        var ciphertext = raw.AsSpan(12, raw.Length - 12 - 16);
        var tag = raw.AsSpan(raw.Length - 16);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(masterKey, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
