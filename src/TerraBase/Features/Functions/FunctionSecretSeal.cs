using System.Security.Cryptography;
using System.Text;
using TerraBase.Engine;
using TerraBase.Features.Backup;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Functions;

internal sealed class FunctionSecretSeal(IKeyring keyring)
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public static bool IsKind(string kind) =>
        kind.StartsWith("databases/", StringComparison.Ordinal) && kind.Contains("/function_secrets/", StringComparison.Ordinal);

    public static FunctionSecretRecord Read(StoredRecord stored) =>
        EntityPayload.Read(stored, FunctionsJsonContext.Default.FunctionSecretRecord) is { Format: 1, WrappedKey.Length: > 0, Sealed.Length: > 0 } record
            ? record
            : throw new InvalidDataException("a function secret record is malformed");

    public async Task<FunctionSecretRecord> SealAsync(TenantContext database, string function, string name, string value, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var (key, wrapped) = await keyring.GenerateDataKeyAsync(cancellationToken).ConfigureAwait(false);
        var plain = Encoding.UTF8.GetBytes(value);
        try
        {
            var output = new byte[NonceBytes + plain.Length + TagBytes];
            RandomNumberGenerator.Fill(output.AsSpan(0, NonceBytes));
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(output.AsSpan(0, NonceBytes), plain, output.AsSpan(NonceBytes, plain.Length), output.AsSpan(NonceBytes + plain.Length, TagBytes),
                Associated(database, function, name));
            return new FunctionSecretRecord(1, name, wrapped, Convert.ToBase64String(output), now);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public async Task<string> OpenAsync(TenantContext database, string function, FunctionSecretRecord record, CancellationToken cancellationToken)
    {
        var bytes = new byte[record.Sealed.Length * 3 / 4];
        if (!Convert.TryFromBase64String(record.Sealed, bytes, out var written) || written < NonceBytes + TagBytes)
        {
            throw new InvalidDataException("a function secret is malformed");
        }

        var key = await keyring.UnwrapAsync(record.WrappedKey, cancellationToken).ConfigureAwait(false);
        var plain = new byte[written - NonceBytes - TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(bytes.AsSpan(0, NonceBytes), bytes.AsSpan(NonceBytes, plain.Length), bytes.AsSpan(NonceBytes + plain.Length, TagBytes), plain,
                Associated(database, function, record.Name));
            return Encoding.UTF8.GetString(plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException("a function secret is not authentic under its data key");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] Associated(TenantContext database, string function, string name) =>
        Encoding.UTF8.GetBytes("TBF1 function secret\0" + BackupLayout.KeyScope(database) + "\0" + function + "\0" + name);
}
