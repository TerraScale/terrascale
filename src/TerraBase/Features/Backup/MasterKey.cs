using System.Security.Cryptography;

namespace TerraBase.Features.Backup;

/// <summary>The master key that wraps backup data keys (envelope encryption).</summary>
internal interface IMasterKey
{
    /// <summary>A new 256-bit data key, in the clear and wrapped by the master key.</summary>
    Task<(byte[] Key, string Wrapped)> GenerateDataKeyAsync(CancellationToken cancellationToken);

    /// <summary>Unwraps a data key from <see cref="GenerateDataKeyAsync"/>.</summary>
    Task<byte[]> UnwrapAsync(string wrapped, CancellationToken cancellationToken);
}

/// <summary>
/// The TerraBase master key (32 bytes base64), delivered from OCI Vault via <c>TERRABASE_MASTER_KEY_FILE</c>.
/// It wraps each data key with AES-256-GCM as base64(nonce | ciphertext | tag); the key never leaves the process.
/// </summary>
internal sealed class MasterKey(byte[] masterKey) : IMasterKey
{
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public Task<(byte[] Key, string Wrapped)> GenerateDataKeyAsync(CancellationToken cancellationToken)
    {
        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        return Task.FromResult((key, Wrap(key)));
    }

    public Task<byte[]> UnwrapAsync(string wrapped, CancellationToken cancellationToken) => Task.FromResult(Unwrap(wrapped));

    /// <summary>Wraps a 256-bit data key.</summary>
    public string Wrap(ReadOnlySpan<byte> key)
    {
        var wrapped = new byte[NonceBytes + KeyBytes + TagBytes];
        RandomNumberGenerator.Fill(wrapped.AsSpan(0, NonceBytes));
        using var aes = new AesGcm(masterKey, TagBytes);
        aes.Encrypt(wrapped.AsSpan(0, NonceBytes), key, wrapped.AsSpan(NonceBytes, KeyBytes), wrapped.AsSpan(NonceBytes + KeyBytes));
        return Convert.ToBase64String(wrapped);
    }

    /// <summary>Unwraps a data key from <see cref="Wrap"/>.</summary>
    /// <exception cref="InvalidDataException">It is malformed or was not wrapped by this master key.</exception>
    public byte[] Unwrap(string wrapped)
    {
        var bytes = new byte[NonceBytes + KeyBytes + TagBytes];
        if (!Convert.TryFromBase64String(wrapped, bytes, out var written) || written != bytes.Length)
        {
            throw new InvalidDataException("wrapped data key is malformed");
        }

        var key = new byte[KeyBytes];
        using var aes = new AesGcm(masterKey, TagBytes);
        try
        {
            aes.Decrypt(bytes.AsSpan(0, NonceBytes), bytes.AsSpan(NonceBytes, KeyBytes), bytes.AsSpan(NonceBytes + KeyBytes), key);
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("wrapped data key is not authentic under the master key");
        }

        return key;
    }
}
