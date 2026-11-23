using System.Security.Cryptography;
using System.Text;

namespace TerraScale.Platform.Features.Functions;

public sealed class FunctionSecretSeal(PlatformOptions options)
{
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("ts-platform function secret v1");

    public string Seal(Ulid database, string function, string name, string value)
    {
        var plain = Encoding.UTF8.GetBytes(value);
        var output = new byte[SaltBytes + NonceBytes + plain.Length + TagBytes];
        var salt = output.AsSpan(0, SaltBytes);
        var nonce = output.AsSpan(SaltBytes, NonceBytes);
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);
        Span<byte> key = stackalloc byte[KeyBytes];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, key, salt, Info);
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, plain, output.AsSpan(SaltBytes + NonceBytes, plain.Length), output.AsSpan(SaltBytes + NonceBytes + plain.Length, TagBytes),
                Associated(database, function, name));
            return Convert.ToBase64String(output);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public Result<string, Unavailable> Open(Ulid database, string function, string name, string sealedValue)
    {
        var bytes = new byte[sealedValue.Length * 3 / 4 + 3];
        if (!Convert.TryFromBase64String(sealedValue, bytes, out var written) || written < SaltBytes + NonceBytes + TagBytes)
        {
            return new Unavailable($"the sealed secret {name} is malformed", "secret_unreadable");
        }

        var cipherLength = written - SaltBytes - NonceBytes - TagBytes;
        var plain = new byte[cipherLength];
        Span<byte> key = stackalloc byte[KeyBytes];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, options.MasterKey, key, bytes.AsSpan(0, SaltBytes), Info);
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(bytes.AsSpan(SaltBytes, NonceBytes), bytes.AsSpan(SaltBytes + NonceBytes, cipherLength),
                bytes.AsSpan(SaltBytes + NonceBytes + cipherLength, TagBytes), plain, Associated(database, function, name));
            return Encoding.UTF8.GetString(plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            return new Unavailable($"the sealed secret {name} is not authentic under the master key", "secret_unreadable");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] Associated(Ulid database, string function, string name) =>
        Encoding.UTF8.GetBytes("TSP1 function secret\0" + database + "\0" + function + "\0" + name);
}
