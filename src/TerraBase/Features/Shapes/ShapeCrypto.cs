using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TerraBase.Features.Backup;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Features.Shapes;

/// <summary>An epoch key <c>K_e</c> released for one handle, epoch and project data key.</summary>
internal sealed record ShapeEpochKey(string Handle, long Epoch, string KeyId, byte[] Key);

/// <summary>A TBS1 object's exact bytes and the epoch and data key that sealed them; disposing releases its memory.</summary>
internal sealed class ShapeCiphertext(byte[] bytes, string keyId, long epoch, IMemoryReservation reservation) : IDisposable
{
    public ReadOnlyMemory<byte> Bytes { get; } = bytes;

    public string KeyId { get; } = keyId;

    public long Epoch { get; } = epoch;

    public void Dispose() => reservation.Dispose();
}

/// <summary>The project has no data key store on this machine (no backup bucket), so no shape key exists.</summary>
internal sealed class ShapeKeysUnavailableException() : IOException("unavailable: shape keys need the project data key store");

/// <summary>
/// Proof that a retained TBS1 object of <c>handle</c> names an <c>(epoch, key_id)</c> pair. Historic keys are released
/// only with this proof (<c>shape-reads.md</c> section 13); the T1 mirror (FR-22) owns the objects and the answer.
/// </summary>
internal interface IShapeKeyRetention
{
    ValueTask<bool> HasObjectAsync(TenantContext database, string handle, long epoch, string keyId, CancellationToken cancellationToken);
}

/// <summary>Encrypt-then-cache for T1 shape objects (<c>docs/terrabase/shape-reads.md</c> sections 12 and 13).</summary>
internal interface IShapeCrypto
{
    /// <summary>The current epoch, <c>floor(unix_seconds / 600)</c>.</summary>
    long CurrentEpoch { get; }

    /// <summary>
    /// <c>K_e</c> for a served pair, or <see langword="null"/> (<c>409 must_refetch</c>). Served: the current epoch under
    /// the project's current data key, and any pair a retained object proves. The caller has already checked that
    /// <paramref name="handle"/> is current and answered a future epoch with <c>400</c>.
    /// </summary>
    /// <exception cref="ShapeKeysUnavailableException">This machine has no data key store.</exception>
    Task<ShapeEpochKey?> GetKeyAsync(TenantContext database, string handle, long epoch, string keyId, CancellationToken cancellationToken);

    /// <summary><c>K_e</c> of the current epoch under the project's current data key.</summary>
    /// <exception cref="ShapeKeysUnavailableException">This machine has no data key store.</exception>
    Task<ShapeEpochKey> CurrentKeyAsync(TenantContext database, string handle, CancellationToken cancellationToken);

    /// <summary>Seals <paramref name="plain"/> as TBS1 under the current epoch and data key, with a fresh random salt.</summary>
    /// <exception cref="ShapeKeysUnavailableException">This machine has no data key store.</exception>
    /// <exception cref="QosMemoryException">The ciphertext cannot be reserved.</exception>
    Task<ShapeCiphertext> EncryptAsync(TenantContext database, string handle, string requestOffset, ReadOnlyMemory<byte> plain, CancellationToken cancellationToken);
}

/// <summary>TBS1 over the project data keys backups use; the epoch and object steps are domain-separated from backup keys.</summary>
internal sealed class ShapeCrypto(DataKeys? keys, IShapeKeyRetention retention, IMemoryBudget memory, TimeProvider time) : IShapeCrypto
{
    internal const int EpochSeconds = 600;
    internal const int HeaderBytes = 4 + 8 + KeyIdBytes + SaltBytes;
    internal const int FrameBytes = 1 << 20;
    internal const int MaxPlainBytes = 8 * FrameBytes;
    private const int KeyIdBytes = 26;
    private const int SaltBytes = 32;
    private const int TagBytes = 16;
    private const uint LastFrame = 1u << 31;

    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    private static ReadOnlySpan<byte> Magic => "TBS1"u8;

    public long CurrentEpoch => time.GetUtcNow().ToUnixTimeSeconds() / EpochSeconds;

    public async Task<ShapeEpochKey?> GetKeyAsync(TenantContext database, string handle, long epoch, string keyId, CancellationToken cancellationToken)
    {
        ValidateHandle(handle);
        var store = keys ?? throw new ShapeKeysUnavailableException();
        if (epoch < 0 || epoch > CurrentEpoch || !Ulid.TryParse(keyId, out var parsed) || parsed.ToString() != keyId)
        {
            return null;
        }

        var scope = Scope(database);
        if (epoch == CurrentEpoch)
        {
            var (id, dataKey) = await store.CurrentAsync(scope, cancellationToken).ConfigureAwait(false);
            if (id == keyId && epoch == CurrentEpoch)
            {
                return new(handle, epoch, keyId, EpochKey(dataKey, handle, epoch));
            }
        }

        // Ask for the proof first: an unproven key id never reaches the bucket.
        if (!await retention.HasObjectAsync(database, handle, epoch, keyId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            return new(handle, epoch, keyId, EpochKey(await store.GetAsync(scope, keyId, cancellationToken).ConfigureAwait(false), handle, epoch));
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    public async Task<ShapeEpochKey> CurrentKeyAsync(TenantContext database, string handle, CancellationToken cancellationToken)
    {
        ValidateHandle(handle);
        var store = keys ?? throw new ShapeKeysUnavailableException();
        var (id, dataKey) = await store.CurrentAsync(Scope(database), cancellationToken).ConfigureAwait(false);
        // Read the epoch after the key: a slow key read must not seal new data under the epoch it started in.
        var epoch = CurrentEpoch;
        return new(handle, epoch, id, EpochKey(dataKey, handle, epoch));
    }

    public async Task<ShapeCiphertext> EncryptAsync(TenantContext database, string handle, string requestOffset, ReadOnlyMemory<byte> plain, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(plain.Length, MaxPlainBytes);
        var key = await CurrentKeyAsync(database, handle, cancellationToken).ConfigureAwait(false);
        try
        {
            var reservation = memory.TryReserve(MemoryLane.Bulk, SealedLength(plain.Length), database) ?? throw new QosMemoryException();
            try
            {
                var bytes = Seal(plain.Span, handle, requestOffset, key.Epoch, key.KeyId, key.Key, RandomNumberGenerator.GetBytes(SaltBytes), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (key.Epoch != CurrentEpoch)
                {
                    throw new IOException("shape encryption crossed an epoch boundary; retry");
                }

                return new(bytes, key.KeyId, key.Epoch, reservation);
            }
            catch
            {
                reservation.Dispose();
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key.Key);
        }
    }

    /// <summary>The TBS1 size of <paramref name="plainLength"/> bytes: the header and one length word and tag per frame.</summary>
    internal static long SealedLength(int plainLength) => HeaderBytes + (plainLength / FrameBytes + 1) * (4L + TagBytes) + plainLength;

    /// <summary><c>K_e = HKDF(K_d, salt = empty, info = "TBS1 epoch" ‖ 0 ‖ handle ‖ 0 ‖ decimal(E))</c>.</summary>
    internal static byte[] EpochKey(byte[] dataKey, string handle, long epoch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(epoch);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, [],
            Encoding.UTF8.GetBytes("TBS1 epoch\0" + handle + "\0" + epoch.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// The TBS1 format writer. <see cref="EncryptAsync"/> is the only production caller and passes a fresh CSPRNG salt;
    /// conformance tests fix it. Every salt yields its own object key, so a (key, nonce) pair never repeats.
    /// </summary>
    internal static byte[] Seal(ReadOnlySpan<byte> plain, string handle, string requestOffset, long epoch, string keyId, byte[] epochKey, byte[] salt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(plain.Length, MaxPlainBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(epoch);
        ArgumentException.ThrowIfNullOrEmpty(requestOffset);
        if (keyId.Length != KeyIdBytes || !Ascii.IsValid(keyId))
        {
            throw new ArgumentException("a data key id is 26 ASCII characters", nameof(keyId));
        }

        if (salt.Length != SaltBytes || epochKey.Length != 32)
        {
            throw new ArgumentException("TBS1 takes a 32-byte salt and a 32-byte epoch key");
        }

        // A multiple of 1 MiB, including empty, ends with an empty last frame.
        var frames = plain.Length / FrameBytes + 1;
        var output = new byte[SealedLength(plain.Length)];
        var header = output.AsSpan(0, HeaderBytes);
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt64BigEndian(header[4..], (ulong)epoch);
        Encoding.ASCII.GetBytes(keyId, header[12..]);
        salt.CopyTo(header[(12 + KeyIdBytes)..]);

        var objectKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, epochKey, 32, salt,
            Encoding.UTF8.GetBytes("TBS1 object\0" + handle + "\0" + requestOffset));
        Span<byte> nonce = stackalloc byte[12];
        var associated = new byte[HeaderBytes + 8 + 4];
        header.CopyTo(associated);
        try
        {
            using var aes = new AesGcm(objectKey, TagBytes);
            var at = HeaderBytes;
            for (var index = 0; index < frames; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var piece = plain.Slice(index * FrameBytes, Math.Min(FrameBytes, plain.Length - index * FrameBytes));
                var word = (uint)piece.Length | (index == frames - 1 ? LastFrame : 0);
                BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(at), word);
                nonce.Clear();
                BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], (ulong)index);
                BinaryPrimitives.WriteUInt64BigEndian(associated.AsSpan(HeaderBytes), (ulong)index);
                BinaryPrimitives.WriteUInt32BigEndian(associated.AsSpan(HeaderBytes + 8), word);
                aes.Encrypt(nonce, piece, output.AsSpan(at + 4, piece.Length), output.AsSpan(at + 4 + piece.Length, TagBytes), associated);
                at += 4 + piece.Length + TagBytes;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(objectKey);
        }

        return output;
    }

    // K_d is the project's data key; without a project there is no scope to derive from.
    private static string Scope(TenantContext database)
    {
        ArgumentException.ThrowIfNullOrEmpty(database.ProjectId);
        return BackupLayout.KeyScope(database);
    }

    private static void ValidateHandle(string handle)
    {
        if (handle is not { Length: 19 } || !handle.StartsWith("h1-", StringComparison.Ordinal) || handle.AsSpan(3).ContainsAnyExcept(LowerHex))
        {
            throw new ArgumentException("a shape handle is h1- and 16 lowercase hex characters", nameof(handle));
        }
    }
}
