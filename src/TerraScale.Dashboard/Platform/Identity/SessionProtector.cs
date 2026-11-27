using System.Security.Cryptography;
using System.Text.Json;

namespace TerraScale.Dashboard.Platform.Identity;

internal static class SessionProtector
{
    private static ReadOnlySpan<byte> Context => "TerraScale.Dashboard/session/v1"u8;

    public static (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Seal(SessionRecord record, string key)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[bytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Convert.FromBase64String(key), 16);
        try
        {
            aes.Encrypt(nonce, bytes, cipher, tag, Context);
            return (nonce, cipher, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static SessionRecord? TryOpen(SessionRow row, string key)
    {
        if (row.Nonce.Length != 12 || row.Tag.Length != 16)
        {
            return null;
        }

        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(key);
        }
        catch (FormatException)
        {
            return null;
        }

        if (keyBytes.Length is not (16 or 24 or 32))
        {
            return null;
        }

        var plain = new byte[row.Ciphertext.Length];
        using var aes = new AesGcm(keyBytes, 16);
        try
        {
            aes.Decrypt(row.Nonce, row.Ciphertext, row.Tag, plain, Context);
            var record = JsonSerializer.Deserialize<SessionRecord>(plain);
            if (record is null || string.IsNullOrWhiteSpace(record.UserId) || string.IsNullOrWhiteSpace(record.Email) ||
                string.IsNullOrWhiteSpace(record.AccessToken) || record.RefreshLease is null || record.UserId != row.UserId)
            {
                return null;
            }

            return record;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }
}
