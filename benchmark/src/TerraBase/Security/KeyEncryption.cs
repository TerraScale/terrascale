using System.Security.Cryptography;

namespace TerraBase.Security;

/// <summary>The operation for which a key provider lease is being requested.</summary>
public enum KeyEncryptionPurpose
{
    /// <summary>Encrypt or decrypt active database data.</summary>
    Data,

    /// <summary>Encrypt or decrypt a backup artifact.</summary>
    Backup,

    /// <summary>Authorize a restore operation.</summary>
    Restore,
}

/// <summary>Exact scope a key provider must authorize before returning a lease.</summary>
public sealed record KeyEncryptionScope
{
    /// <summary>Creates an exact tenant/database/key/region/purpose scope.</summary>
    public KeyEncryptionScope(
        TenantId tenant,
        string databaseId,
        string keyVersion,
        string region,
        KeyEncryptionPurpose purpose)
    {
        tenant.EnsureValid(nameof(tenant));
        DatabaseId = Validate(databaseId, nameof(databaseId));
        KeyVersion = Validate(keyVersion, nameof(keyVersion));
        Region = Validate(region, nameof(region));
        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "The key purpose is unknown.");
        }

        Tenant = tenant;
        Purpose = purpose;
    }

    /// <summary>The owning tenant.</summary>
    public TenantId Tenant { get; }

    /// <summary>The database identity.</summary>
    public string DatabaseId { get; }

    /// <summary>The provider key version.</summary>
    public string KeyVersion { get; }

    /// <summary>The admitted provider region.</summary>
    public string Region { get; }

    /// <summary>The operation purpose.</summary>
    public KeyEncryptionPurpose Purpose { get; }

    private static string Validate(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException($"{parameterName} must not have surrounding whitespace.", parameterName);
        }

        return value;
    }
}

/// <summary>Authenticated ciphertext returned by a key provider wrapper operation.</summary>
public sealed record WrappedDataKey
{
    /// <summary>Creates a wrapped data-key value.</summary>
    public WrappedDataKey(
        KeyEncryptionScope scope,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> ciphertext,
        ReadOnlyMemory<byte> tag)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        if (nonce.Length != 12)
        {
            throw new ArgumentException("AES-GCM nonces must be 96 bits.", nameof(nonce));
        }

        if (tag.Length != 16)
        {
            throw new ArgumentException("AES-GCM tags must be 128 bits.", nameof(tag));
        }

        if (ciphertext.Length != 32)
        {
            throw new ArgumentException("Wrapped data keys must contain one 256-bit key.", nameof(ciphertext));
        }

        Nonce = nonce.ToArray();
        Ciphertext = ciphertext.ToArray();
        Tag = tag.ToArray();
    }

    /// <summary>The scope authenticated by the provider.</summary>
    public KeyEncryptionScope Scope { get; }

    /// <summary>The unique AES-GCM nonce.</summary>
    public ReadOnlyMemory<byte> Nonce { get; }

    /// <summary>The wrapped 256-bit data key.</summary>
    public ReadOnlyMemory<byte> Ciphertext { get; }

    /// <summary>The AES-GCM authentication tag.</summary>
    public ReadOnlyMemory<byte> Tag { get; }
}

/// <summary>Short-lived lease for an unwrapped 256-bit data key.</summary>
public interface IKeyEncryptionLease : IAsyncDisposable
{
    /// <summary>The exact scope authorized by the provider.</summary>
    KeyEncryptionScope Scope { get; }

    /// <summary>The time after which new key use must fail.</summary>
    DateTimeOffset ExpiresAt { get; }

    /// <summary>The unwrapped key material for the active operation.</summary>
    ReadOnlyMemory<byte> KeyMaterial { get; }

    /// <summary>Whether the lease has expired at the supplied UTC time.</summary>
    bool IsExpired(DateTimeOffset utcNow);
}

/// <summary>Provider adapter for customer or service-managed encryption keys.</summary>
public interface IKeyEncryptionProvider
{
    /// <summary>The stable provider name used for diagnostics.</summary>
    string Name { get; }

    /// <summary>Opens a bounded lease for an exact key scope.</summary>
    ValueTask<IKeyEncryptionLease> OpenAsync(
        KeyEncryptionScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>Wraps one 256-bit database key under the exact scope.</summary>
    ValueTask<WrappedDataKey> WrapAsync(
        KeyEncryptionScope scope,
        ReadOnlyMemory<byte> dataKey,
        CancellationToken cancellationToken = default);

    /// <summary>Unwraps a provider value and returns a bounded key lease.</summary>
    ValueTask<IKeyEncryptionLease> UnwrapAsync(
        KeyEncryptionScope scope,
        WrappedDataKey wrappedKey,
        CancellationToken cancellationToken = default);
}

/// <summary>Common validation and zeroizing implementation for provider leases.</summary>
public sealed class KeyEncryptionLease : IKeyEncryptionLease
{
    private byte[] keyMaterial;
    private int disposed;

    /// <summary>Creates a lease and copies the provider key into private memory.</summary>
    public KeyEncryptionLease(KeyEncryptionScope scope, ReadOnlySpan<byte> keyMaterial, DateTimeOffset expiresAt)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        if (keyMaterial.Length != 32)
        {
            throw new ArgumentException("Data keys must be exactly 256 bits.", nameof(keyMaterial));
        }

        var now = DateTimeOffset.UtcNow;
        if (expiresAt <= now || expiresAt > now.Add(SecurityLimits.MaximumKeyLease))
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAt),
                "Key leases must expire in the future and no later than 1,800 seconds from now.");
        }

        this.keyMaterial = keyMaterial.ToArray();
        ExpiresAt = expiresAt;
    }

    /// <inheritdoc />
    public KeyEncryptionScope Scope { get; }

    /// <inheritdoc />
    public DateTimeOffset ExpiresAt { get; }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> KeyMaterial
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (IsExpired(DateTimeOffset.UtcNow))
            {
                throw new InvalidOperationException("The key authorization lease has expired.");
            }

            return keyMaterial;
        }
    }

    /// <inheritdoc />
    public bool IsExpired(DateTimeOffset utcNow) => utcNow >= ExpiresAt || Volatile.Read(ref disposed) != 0;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            CryptographicOperations.ZeroMemory(keyMaterial);
            keyMaterial = Array.Empty<byte>();
        }

        return ValueTask.CompletedTask;
    }
}

