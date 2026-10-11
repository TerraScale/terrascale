using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Hosting;

namespace TerraBase.Features.Backup;

/// <summary>A customer bucket's key pair, sealed for the catalog: <see cref="WrappedKey"/> is its data key wrapped by the master key.</summary>
/// <param name="WrappedKey">The data key, wrapped by the master key.</param>
/// <param name="Ciphertext">base64(nonce | ciphertext | tag) of the key pair, sealed with the data key.</param>
public sealed record SealedCredentials(string WrappedKey, string Ciphertext);

/// <summary>The JSON form of a customer key pair, only ever sealed.</summary>
internal sealed record CustomerKeyPair(string AccessKeyId, string SecretKey);

/// <summary>Seals and opens a customer key pair with AES-256-GCM under a per-record data key (envelope encryption).</summary>
internal static class CustomerCredentials
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public static async Task<SealedCredentials> SealAsync(IMasterKey master, string accessKeyId, string secretKey, CancellationToken cancellationToken)
    {
        var (key, wrapped) = await master.GenerateDataKeyAsync(cancellationToken).ConfigureAwait(false);
        var plain = JsonSerializer.SerializeToUtf8Bytes(new CustomerKeyPair(accessKeyId, secretKey), TerraBaseJsonContext.Default.CustomerKeyPair);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var sealedBytes = new byte[NonceBytes + plain.Length + TagBytes];
            nonce.CopyTo(sealedBytes, 0);
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, plain, sealedBytes.AsSpan(NonceBytes, plain.Length), sealedBytes.AsSpan(NonceBytes + plain.Length));
            return new(wrapped, Convert.ToBase64String(sealedBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static async Task<(string AccessKeyId, string SecretKey)> OpenAsync(IMasterKey master, SealedCredentials credentials, CancellationToken cancellationToken)
    {
        var key = await master.UnwrapAsync(credentials.WrappedKey, cancellationToken).ConfigureAwait(false);
        var sealedBytes = new byte[credentials.Ciphertext.Length * 3 / 4];
        byte[]? plain = null;
        try
        {
            if (!Convert.TryFromBase64String(credentials.Ciphertext, sealedBytes, out var written) || written < NonceBytes + TagBytes)
            {
                throw new InvalidDataException("customer credentials are not sealed");
            }

            plain = new byte[written - NonceBytes - TagBytes];
            using var aes = new AesGcm(key, TagBytes);
            try
            {
                aes.Decrypt(sealedBytes.AsSpan(0, NonceBytes), sealedBytes.AsSpan(NonceBytes, plain.Length), sealedBytes.AsSpan(written - TagBytes, TagBytes), plain);
            }
            catch (CryptographicException)
            {
                throw new InvalidDataException("customer credentials are not authentic under their data key");
            }

            var pair = JsonSerializer.Deserialize(plain, TerraBaseJsonContext.Default.CustomerKeyPair)
                ?? throw new InvalidDataException("customer credentials are empty");
            return (pair.AccessKeyId, pair.SecretKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(sealedBytes);
            if (plain is not null)
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
    }
}
