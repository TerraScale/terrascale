using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Features.Backup;
using TerraBase.Hosting;

namespace TerraBase.Features.Backup;

public sealed record SealedCredentials(string WrappedKey, string Ciphertext);

internal sealed record CustomerKeyPair(string AccessKeyId, string SecretKey);

internal static class CustomerCredentials
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    internal static ProjectBackupDestinationRecord ReadRecord(ReadOnlyMemory<byte> json)
    {
        var record = JsonSerializer.Deserialize(json.Span, BackupJsonContext.Default.ProjectBackupDestinationRecord);
        if (record is not
            {
                Version: 1, Revision.Length: > 0, Destination: not null,
                Credentials: { WrappedKey.Length: > 0, Ciphertext.Length: > 0 }
            })
        {
            throw new InvalidDataException("sealed credential record is malformed");
        }
        var ciphertext = record.Credentials.Ciphertext;
        var buffer = new byte[ciphertext.Length * 3 / 4];
        if (!Convert.TryFromBase64String(ciphertext, buffer, out var written) || written < NonceBytes + TagBytes)
        {
            throw new InvalidDataException("sealed credential envelope is malformed");
        }
        return record;
    }

    public static async Task<SealedCredentials> SealAsync(IKeyring master, string accessKeyId, string secretKey, CancellationToken cancellationToken)
    {
        var (key, wrapped) = await master.GenerateDataKeyAsync(cancellationToken).ConfigureAwait(false);
        byte[]? plain = null;
        try
        {
            plain = JsonSerializer.SerializeToUtf8Bytes(new CustomerKeyPair(accessKeyId, secretKey), BackupJsonContext.Default.CustomerKeyPair);
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
            if (plain is not null)
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
    }

    public static async Task<(string AccessKeyId, string SecretKey)> OpenAsync(IKeyring master, SealedCredentials credentials, CancellationToken cancellationToken)
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

            var pair = JsonSerializer.Deserialize(plain, BackupJsonContext.Default.CustomerKeyPair)
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
