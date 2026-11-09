using System.Security.Cryptography;

namespace TerraBase.Features.Backup;

internal interface IMasterKey
{
    Task<(byte[] Key, string Wrapped)> GenerateDataKeyAsync(CancellationToken cancellationToken);

    Task<byte[]> UnwrapAsync(string wrapped, CancellationToken cancellationToken);
}

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

    public string Wrap(ReadOnlySpan<byte> key)
    {
        var wrapped = new byte[NonceBytes + KeyBytes + TagBytes];
        RandomNumberGenerator.Fill(wrapped.AsSpan(0, NonceBytes));
        using var aes = new AesGcm(masterKey, TagBytes);
        aes.Encrypt(wrapped.AsSpan(0, NonceBytes), key, wrapped.AsSpan(NonceBytes, KeyBytes), wrapped.AsSpan(NonceBytes + KeyBytes));
        return Convert.ToBase64String(wrapped);
    }

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
