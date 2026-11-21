using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Tenancy;

namespace TerraBase.Features.Shapes;

internal sealed record ShapeEpochKey(string Handle, long Epoch, string KeyId, byte[] Key);

internal sealed class ShapeCiphertext(byte[] bytes, string keyId, long epoch, IMemoryReservation reservation) : IDisposable
{
    public ReadOnlyMemory<byte> Bytes { get; } = bytes;

    public string KeyId { get; } = keyId;

    public long Epoch { get; } = epoch;

    public void Dispose() => reservation.Dispose();
}

internal readonly union GetKeyResult(ShapeEpochKey, NotFound, Unavailable);

internal readonly union CurrentKeyResult(ShapeEpochKey, Unavailable);

internal sealed record EpochBoundary;

internal readonly union EncryptResult(ShapeCiphertext, Unavailable, EpochBoundary);

internal interface IShapeKeyRetention
{
    ValueTask<bool> HasObjectAsync(TenantContext database, string handle, long epoch, string keyId, CancellationToken cancellationToken);
}

internal interface IShapeCrypto
{
    long CurrentEpoch { get; }

    Task<GetKeyResult> GetKeyAsync(TenantContext database, string handle, long epoch, string keyId, CancellationToken cancellationToken);

    Task<CurrentKeyResult> CurrentKeyAsync(TenantContext database, string handle, CancellationToken cancellationToken);

    Task<EncryptResult> EncryptAsync(TenantContext database, string handle, string requestOffset, ReadOnlyMemory<byte> plain, CancellationToken cancellationToken);
}

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

    public async Task<GetKeyResult> GetKeyAsync(TenantContext database, string handle, long epoch, string keyId, CancellationToken cancellationToken)
    {
        ValidateHandle(handle);
        if (keys is not { } store)
        {
            return new Unavailable("shape keys need the database data key store");
        }

        if (epoch < 0 || epoch > CurrentEpoch || !Ulid.TryParse(keyId, out var parsed) || parsed.ToString() != keyId)
        {
            return new NotFound();
        }

        var scope = Scope(database);
        if (epoch == CurrentEpoch)
        {
            string id;
            byte[] dataKey;
            if (await store.CurrentAsync(scope, cancellationToken).ConfigureAwait(false) is not DataKey { Id: var currentId, Key: var currentKey })
            {
                return new NotFound();
            }
            id = currentId;
            dataKey = currentKey;

            if (id == keyId && epoch == CurrentEpoch)
            {
                return new ShapeEpochKey(handle, epoch, keyId, EpochKey(dataKey, handle, epoch));
            }
        }

        if (!await retention.HasObjectAsync(database, handle, epoch, keyId, cancellationToken).ConfigureAwait(false))
        {
            return new NotFound();
        }

        try
        {
            if (await store.GetAsync(scope, keyId, cancellationToken).ConfigureAwait(false) is not KeyBytes { Key: { } epochKey })
            {
                return new NotFound();
            }
            return new ShapeEpochKey(handle, epoch, keyId, EpochKey(epochKey, handle, epoch));
        }
        catch (Exception error) when (error is InvalidDataException)
        {
            return new NotFound();
        }
    }

    public async Task<CurrentKeyResult> CurrentKeyAsync(TenantContext database, string handle, CancellationToken cancellationToken)
    {
        ValidateHandle(handle);
        if (keys is not { } store)
        {
            return new Unavailable("shape keys need the database data key store");
        }

        if (await store.CurrentAsync(Scope(database), cancellationToken).ConfigureAwait(false) is not DataKey { Id: var id, Key: var dataKey })
        {
            return new Unavailable("shape keys need the database data key store");
        }
        var epoch = CurrentEpoch;
        return new ShapeEpochKey(handle, epoch, id, EpochKey(dataKey, handle, epoch));
    }

    public async Task<EncryptResult> EncryptAsync(TenantContext database, string handle, string requestOffset, ReadOnlyMemory<byte> plain, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(plain.Length, MaxPlainBytes);
        return await CurrentKeyAsync(database, handle, cancellationToken).ConfigureAwait(false) switch
        {
            ShapeEpochKey key => await EncryptKeyedAsync(key, database, requestOffset, plain, cancellationToken).ConfigureAwait(false),
            Unavailable error => error,
        };
    }

    private async Task<EncryptResult> EncryptKeyedAsync(ShapeEpochKey key, TenantContext database, string requestOffset, ReadOnlyMemory<byte> plain, CancellationToken cancellationToken)
    {
        try
        {
            var reservation = memory.TryReserve(SealedLength(plain.Length), database) ?? throw new QosMemoryException();
            try
            {
                var bytes = Seal(plain.Span, key.Handle, requestOffset, key.Epoch, key.KeyId, key.Key, RandomNumberGenerator.GetBytes(SaltBytes), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (key.Epoch != CurrentEpoch)
                {
                    reservation.Dispose();
                    return new EpochBoundary();
                }

                return new ShapeCiphertext(bytes, key.KeyId, key.Epoch, reservation);
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

    internal static long SealedLength(int plainLength) => HeaderBytes + (plainLength / FrameBytes + 1) * (4L + TagBytes) + plainLength;

    internal static byte[] EpochKey(byte[] dataKey, string handle, long epoch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(epoch);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, [],
            Encoding.UTF8.GetBytes("TBS1 epoch\0" + handle + "\0" + epoch.ToString(CultureInfo.InvariantCulture)));
    }

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
