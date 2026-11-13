using System.Security.Cryptography;
using System.Text;
using TerraBase.Configuration;

namespace TerraBase.Features.Backup;

internal interface IKeyring
{
    string ActiveId { get; }

    IReadOnlyCollection<string> KeyIds { get; }

    bool NeedsRewrap(string wrapped);

    string KeyId(string wrapped);

    string Wrap(ReadOnlySpan<byte> key);

    string Rewrap(ReadOnlySpan<byte> key, string item);

    byte[] Unwrap(string wrapped);

    Task<(byte[] Key, string Wrapped)> GenerateDataKeyAsync(CancellationToken cancellationToken);

    Task<byte[]> UnwrapAsync(string wrapped, CancellationToken cancellationToken);
}

internal sealed class Keyring(KeyringSettings settings, KeyRewrapFaults faults) : IKeyring
{
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public string ActiveId => settings.Active;

    public IReadOnlyCollection<string> KeyIds => settings.Keys.Keys;

    public Task<(byte[] Key, string Wrapped)> GenerateDataKeyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = RandomNumberGenerator.GetBytes(KeyBytes);
        try
        {
            return Task.FromResult((key, Wrap(key)));
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    public Task<byte[]> UnwrapAsync(string wrapped, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Unwrap(wrapped));
    }

    public byte[] Unwrap(string wrapped)
    {
        var id = KeyId(wrapped);
        if (!settings.Keys.TryGetValue(id, out var master))
        {
            throw new InvalidDataException($"wrapped data key names an unconfigured key id '{id}'");
        }

        var versioned = wrapped.StartsWith("v2.", StringComparison.Ordinal);
        var body = versioned ? wrapped[(id.Length + 4)..] : wrapped;
        var bytes = new byte[NonceBytes + KeyBytes + TagBytes];
        if (!Convert.TryFromBase64String(body, bytes, out var written) || written != bytes.Length)
        {
            throw new InvalidDataException("wrapped data key is malformed");
        }

        var key = new byte[KeyBytes];
        using var aes = new AesGcm(master, TagBytes);
        try
        {
            aes.Decrypt(bytes.AsSpan(0, NonceBytes), bytes.AsSpan(NonceBytes, KeyBytes), bytes.AsSpan(NonceBytes + KeyBytes), key,
                versioned ? Encoding.ASCII.GetBytes("v2." + id) : []);
            return key;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidDataException($"wrapped data key is not authentic under key id '{id}'");
        }
    }

    public string KeyId(string wrapped)
    {
        if (!wrapped.Contains('.', StringComparison.Ordinal))
        {
            return "k0";
        }

        var parts = wrapped.Split('.');
        return parts is ["v2", var id, { Length: > 0 }] && KeyringSettings.ValidId(id)
            ? id
            : throw new InvalidDataException("wrapped data key version or key id is malformed");
    }

    public bool NeedsRewrap(string wrapped) => KeyId(wrapped) != ActiveId;

    public string Rewrap(ReadOnlySpan<byte> key, string item)
    {
        try
        {
            var wrapped = Wrap(key);
            var verified = Unwrap(wrapped);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(key, verified))
                {
                    throw new InvalidDataException("rewrapped data key verification failed");
                }
                faults.Verified(item);
                return wrapped;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(verified);
            }
        }
        catch (Exception error) when (error is InvalidDataException or CryptographicException)
        {
            faults.Failed(item);
            throw new InvalidDataException("rewrapped data key verification failed");
        }
    }

    public string Wrap(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyBytes)
        {
            throw new InvalidDataException("data key must contain 256 bits");
        }

        var wrapped = new byte[NonceBytes + KeyBytes + TagBytes];
        RandomNumberGenerator.Fill(wrapped.AsSpan(0, NonceBytes));
        using var aes = new AesGcm(settings.Keys[ActiveId], TagBytes);
        var versioned = ActiveId != "k0";
        aes.Encrypt(wrapped.AsSpan(0, NonceBytes), key, wrapped.AsSpan(NonceBytes, KeyBytes), wrapped.AsSpan(NonceBytes + KeyBytes),
            versioned ? Encoding.ASCII.GetBytes("v2." + ActiveId) : []);
        return versioned ? $"v2.{ActiveId}.{Convert.ToBase64String(wrapped)}" : Convert.ToBase64String(wrapped);
    }
}
