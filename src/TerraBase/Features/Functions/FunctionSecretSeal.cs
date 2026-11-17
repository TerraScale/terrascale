using System.Security.Cryptography;
using System.Text;
using TerraBase.Features.Backup;
using TerraBase.Tenancy;

namespace TerraBase.Features.Functions;

internal sealed class FunctionSecretSeal(DataKeys? keys)
{
    private const int KeyIdBytes = 26;
    private const int SaltBytes = 32;
    private const int TagBytes = 16;
    private const int Header = 4 + KeyIdBytes + SaltBytes;

    private static ReadOnlySpan<byte> Magic => "TBF1"u8;

    public bool Available => keys is not null;

    public async Task<string> SealAsync(TenantContext database, string function, string name, string value, CancellationToken cancellationToken)
    {
        var store = keys ?? throw Missing();
        var (id, dataKey) = await store.ForSealingAsync(BackupLayout.KeyScope(database), cancellationToken).ConfigureAwait(false);
        var plain = Encoding.UTF8.GetBytes(value);
        var output = new byte[Header + plain.Length + TagBytes];
        Magic.CopyTo(output);
        Encoding.ASCII.GetBytes(id, output.AsSpan(4, KeyIdBytes));
        RandomNumberGenerator.Fill(output.AsSpan(4 + KeyIdBytes, SaltBytes));
        var key = Derive(database, function, name, dataKey, output);
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(new byte[12], plain, output.AsSpan(Header, plain.Length), output.AsSpan(Header + plain.Length, TagBytes), output.AsSpan(0, Header));
            return Convert.ToBase64String(output);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public async Task<string> OpenAsync(TenantContext database, string function, string name, string sealedValue, CancellationToken cancellationToken)
    {
        var store = keys ?? throw Missing();
        var bytes = new byte[sealedValue.Length * 3 / 4];
        if (!Convert.TryFromBase64String(sealedValue, bytes, out var written) || written < Header + TagBytes || !bytes.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("a function secret is malformed");
        }

        var id = Encoding.ASCII.GetString(bytes, 4, KeyIdBytes);
        if (!Ulid.TryParse(id, out var parsed) || parsed.ToString() != id)
        {
            throw new InvalidDataException("a function secret names a malformed data key");
        }

        var dataKey = await store.GetAsync(BackupLayout.KeyScope(database), id, cancellationToken).ConfigureAwait(false);
        var key = Derive(database, function, name, dataKey, bytes);
        var plain = new byte[written - Header - TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(new byte[12], bytes.AsSpan(Header, plain.Length), bytes.AsSpan(Header + plain.Length, TagBytes), plain, bytes.AsSpan(0, Header));
            return Encoding.UTF8.GetString(plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException("a function secret is not authentic under its database key");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static IOException Missing() => new("unavailable: function secrets need the database key store");

    private static byte[] Derive(TenantContext database, string function, string name, byte[] dataKey, byte[] header)
    {
        var derived = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, derived, header.AsSpan(4 + KeyIdBytes, SaltBytes),
            Encoding.UTF8.GetBytes("TBF1 function secret\0" + BackupLayout.KeyScope(database) + "\0" + function + "\0" + name));
        return derived;
    }
}
