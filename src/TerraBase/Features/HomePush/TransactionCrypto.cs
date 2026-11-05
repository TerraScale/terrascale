using System.Security.Cryptography;
using System.Text;
using TerraBase.Features.Backup;
using TerraBase.Tenancy;

namespace TerraBase.Features.HomePush;

internal interface ITransactionCrypto
{
    Task<byte[]> SealAsync(TenantContext database, byte[] packed, CancellationToken cancellationToken);
    Task<byte[]?> OpenAsync(TenantContext database, byte[] sealedBody, CancellationToken cancellationToken);
}

// TBP1: magic, 26-byte data-key ULID, 32-byte random salt, ciphertext, 16-byte GCM tag.
// One envelope is reused by JetStream and the regional partner, including its system-database backups.
internal sealed class TransactionCrypto(DataKeys? keys) : ITransactionCrypto
{
    internal const int Overhead = 78;
    private const int Header = 62;

    public async Task<byte[]> SealAsync(TenantContext database, byte[] packed, CancellationToken cancellationToken)
    {
        var store = keys ?? throw new LocalWriteRefusedException("durable local writes require the database key store");
        var (id, dataKey) = await store.CurrentAsync(BackupLayout.KeyScope(database), cancellationToken).ConfigureAwait(false);
        var result = new byte[packed.Length + Overhead];
        "TBP1"u8.CopyTo(result);
        Encoding.ASCII.GetBytes(id, result.AsSpan(4, 26));
        RandomNumberGenerator.Fill(result.AsSpan(30, 32));
        var key = Derive(database, dataKey, result);
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(new byte[12], packed, result.AsSpan(Header, packed.Length), result.AsSpan(Header + packed.Length, 16), result.AsSpan(0, Header));
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task<byte[]?> OpenAsync(TenantContext database, byte[] sealedBody, CancellationToken cancellationToken)
    {
        if (sealedBody.Length < Overhead || sealedBody.Length > HomePush.MaxPayloadBytes || !sealedBody.AsSpan(0, 4).SequenceEqual("TBP1"u8))
        {
            return null;
        }

        var store = keys ?? throw new IOException("durable local writes require the database key store");
        byte[] dataKey;
        try
        {
            dataKey = await store.GetAsync(BackupLayout.KeyScope(database), Encoding.ASCII.GetString(sealedBody, 4, 26), cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseShreddedException)
        {
            return null;
        }

        var key = Derive(database, dataKey, sealedBody);
        var plain = new byte[sealedBody.Length - Overhead];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(new byte[12], sealedBody.AsSpan(Header, plain.Length), sealedBody.AsSpan(Header + plain.Length, 16), plain, sealedBody.AsSpan(0, Header));
            return plain;
        }
        catch (AuthenticationTagMismatchException)
        {
            CryptographicOperations.ZeroMemory(plain);
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Derive(TenantContext database, byte[] dataKey, byte[] header) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, header.AsSpan(30, 32),
            Encoding.UTF8.GetBytes("TBP1 transaction\0" + BackupLayout.KeyScope(database)));
}
