using System.Security.Cryptography;
using System.Text;

namespace TerraScale.Auth.Security;

/// <summary>
/// Encrypts recoverable secrets at rest under the 32-byte master key (<c>TS_AUTH_MASTER_KEY</c>)
/// with AES-256-GCM. The stored form is <c>v1:</c> + base64(nonce[12] ‖ ciphertext ‖ tag[16]).
/// The <c>context</c> names the record that owns the secret and is authenticated as associated
/// data, so a box copied into another record or tenant fails to open.
/// </summary>
public static class SecretBox
{
    private const string Prefix = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>Encrypts <paramref name="plaintext"/> with a fresh random nonce.</summary>
    public static string Seal(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> plaintext, string context)
    {
        ArgumentException.ThrowIfNullOrEmpty(context);
        using var aes = Cipher(masterKey);
        var raw = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = raw.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        aes.Encrypt(nonce, plaintext, raw.AsSpan(NonceSize, plaintext.Length), raw.AsSpan(NonceSize + plaintext.Length), Encoding.UTF8.GetBytes(context));
        return Prefix + Convert.ToBase64String(raw);
    }

    /// <summary>
    /// Decrypts a box sealed under the same key and context.
    /// Throws <see cref="CryptographicException"/> for any malformed, tampered or misbound box.
    /// </summary>
    public static byte[] Open(ReadOnlySpan<byte> masterKey, string boxed, string context)
    {
        ArgumentNullException.ThrowIfNull(boxed);
        ArgumentException.ThrowIfNullOrEmpty(context);
        using var aes = Cipher(masterKey);
        byte[] raw;
        try
        {
            raw = boxed.StartsWith(Prefix, StringComparison.Ordinal)
                ? Convert.FromBase64String(boxed[Prefix.Length..])
                : throw new CryptographicException("Unknown secret box version.");
        }
        catch (FormatException)
        {
            throw new CryptographicException("Malformed secret box.");
        }

        if (raw.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Truncated secret box.");
        }

        var plaintext = new byte[raw.Length - NonceSize - TagSize];
        aes.Decrypt(raw.AsSpan(0, NonceSize), raw.AsSpan(NonceSize, plaintext.Length), raw.AsSpan(raw.Length - TagSize), plaintext, Encoding.UTF8.GetBytes(context));
        return plaintext;
    }

    private static AesGcm Cipher(ReadOnlySpan<byte> masterKey) =>
        masterKey.Length == 32 ? new AesGcm(masterKey, TagSize) : throw new CryptographicException("The master key must be 32 bytes.");
}
