using System.Security.Cryptography;
using System.Text;
using TerraBase.Features.Backup;
using TerraBase.Tenancy;

namespace TerraBase.Features.HomePush;

internal interface ITransactionCrypto
{
    bool Seals { get; }

    Task PrepareAsync(TenantContext database, CancellationToken cancellationToken);
    Task<byte[]> SealAsync(TenantContext database, byte[] packed, CancellationToken cancellationToken);
    Task<byte[]?> OpenAsync(TenantContext database, byte[] sealedBody, CancellationToken cancellationToken);
}

internal sealed class TransactionCrypto(DataKeys? keys) : ITransactionCrypto
{
    internal const int Overhead = 78;
    private const int Header = 62;

    public bool Seals => keys is not null;

    public async Task PrepareAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (keys is not null)
        {
            _ = await keys.ForSealingAsync(BackupLayout.KeyScope(database), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<byte[]> SealAsync(TenantContext database, byte[] packed, CancellationToken cancellationToken)
    {
        var store = keys ?? throw new InvalidOperationException("durable local writes require the database key store");
        if (await store.ForSealingAsync(BackupLayout.KeyScope(database), cancellationToken).ConfigureAwait(false) is not DataKey { Id: var id, Key: var dataKey })
        {
            throw new InvalidOperationException("durable local writes require the database key store");
        }
        return Seal(database, packed, id, dataKey);
    }

    internal static byte[] Seal(TenantContext database, byte[] packed, string id, byte[] dataKey)
    {
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

        var id = Encoding.ASCII.GetString(sealedBody, 4, 26);
        if (!Ulid.TryParse(id, out var parsed) || parsed.ToString() != id)
        {
            return null;
        }

        var store = keys ?? throw new IOException("durable local writes require the database key store");
        byte[] dataKey;
        if (await store.GetAsync(BackupLayout.KeyScope(database), id, cancellationToken).ConfigureAwait(false) is not KeyBytes { Key: { } read })
        {
            return null;
        }
        dataKey = read;

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

    private static byte[] Derive(TenantContext database, byte[] dataKey, byte[] header)
    {
        var derived = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, derived, header.AsSpan(30, 32),
            Encoding.UTF8.GetBytes("TBP1 transaction\0" + BackupLayout.KeyScope(database)));
        return derived;
    }
}
