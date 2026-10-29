using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TerraScale.DotNet.TerraBase;

/// <summary>
/// Opens TBS1 sealed shape bodies (shape reads protocol v1, section 12). Whole-object rejection: the complete object is
/// validated and authenticated before any plaintext is returned, so a rejected object applies nothing.
/// </summary>
internal static class TerraBaseShapeCrypto
{
    private const int HeaderLength = 70;
    private const int EpochOffset = 4;
    private const int KeyIdOffset = 12;
    private const int KeyIdLength = 26;
    private const int SaltOffset = 38;
    private const int SaltLength = 32;
    private const int KeyLength = 32;
    private const int LengthWordLength = 4;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int FrameSize = 1 << 20;
    private const long MaxPlaintextLength = 8L << 20;
    private const uint LastFrameFlag = 0x8000_0000;
    private const uint LengthMask = 0x7FFF_FFFF;

    /// <summary>
    /// Authenticates and decrypts one TBS1 object. <paramref name="epochKey"/> is <c>K_e</c> for the caller's
    /// <paramref name="handle"/> and request <paramref name="offset"/>; the object key is bound to both.
    /// </summary>
    /// <exception cref="CryptographicException">The object is malformed, has the wrong epoch or key id, or fails authentication.</exception>
    internal static byte[] Decrypt(byte[] ciphertext, byte[] epochKey, string handle, string offset, ulong expectedEpoch, string expectedKeyId)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(epochKey);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(offset);
        ArgumentNullException.ThrowIfNull(expectedKeyId);
        if (epochKey.Length != KeyLength)
        {
            throw new ArgumentException($"TBS1 epoch key must be {KeyLength} bytes.", nameof(epochKey));
        }

        RequirePrintableAscii(handle, nameof(handle));
        RequirePrintableAscii(offset, nameof(offset));
        RequirePrintableAscii(expectedKeyId, nameof(expectedKeyId));
        if (expectedKeyId.Length != KeyIdLength)
        {
            throw new ArgumentException($"TBS1 key id must be {KeyIdLength} ASCII characters.", nameof(expectedKeyId));
        }

        if (ciphertext.Length < HeaderLength)
        {
            throw Reject("header is short");
        }

        var header = ciphertext.AsSpan(0, HeaderLength);
        if (!header[..4].SequenceEqual("TBS1"u8))
        {
            throw Reject("magic differs");
        }

        if (BinaryPrimitives.ReadUInt64BigEndian(header[EpochOffset..]) != expectedEpoch)
        {
            throw Reject("epoch differs");
        }

        if (!header.Slice(KeyIdOffset, KeyIdLength).SequenceEqual(Encoding.ASCII.GetBytes(expectedKeyId)))
        {
            throw Reject("key id differs");
        }

        var frames = ReadFrameLayout(ciphertext);
        var plaintextLength = frames.Sum(frame => (long)frame.Length);
        var salt = header.Slice(SaltOffset, SaltLength);

        var objectKey = DeriveObjectKey(epochKey, salt, handle, offset);
        try
        {
            using var aes = new AesGcm(objectKey, TagLength);
            var plaintext = new byte[plaintextLength];
            try
            {
                DecryptFrames(aes, ciphertext, frames, plaintext);
                return plaintext;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(objectKey);
        }
    }

    /// <summary>
    /// Derives <c>K_o</c> = HKDF(ikm: <c>K_e</c>, salt: <c>salt_obj</c>, info: "TBS1 object" ‖ 0x00 ‖ handle ‖ 0x00 ‖ offset).
    /// </summary>
    internal static byte[] DeriveObjectKey(byte[] epochKey, ReadOnlySpan<byte> salt, string handle, string offset)
    {
        byte[] info = [.. "TBS1 object"u8, 0, .. Encoding.ASCII.GetBytes(handle), 0, .. Encoding.ASCII.GetBytes(offset)];
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, epochKey, KeyLength, salt.ToArray(), info);
    }

    /// <summary>
    /// Walks the frame headers and checks every length rule before any decryption, so the total is bounded before the
    /// plaintext buffer is allocated.
    /// </summary>
    private static List<FrameLayout> ReadFrameLayout(byte[] ciphertext)
    {
        var frames = new List<FrameLayout>();
        var position = HeaderLength;
        long total = 0;
        while (true)
        {
            if (ciphertext.Length - position < LengthWordLength)
            {
                throw Reject("object ends before a last frame");
            }

            var word = BinaryPrimitives.ReadUInt32BigEndian(ciphertext.AsSpan(position));
            var last = (word & LastFrameFlag) != 0;
            var length = word & LengthMask;
            if (length > FrameSize)
            {
                throw Reject("frame exceeds 1 MiB");
            }

            if (!last && length != FrameSize)
            {
                throw Reject("non-last frame is not 1 MiB");
            }

            if (last && length == FrameSize)
            {
                throw Reject("last frame is 1 MiB");
            }

            total += length;
            if (total > MaxPlaintextLength)
            {
                throw Reject("plaintext exceeds 8 MiB");
            }

            var frameSize = LengthWordLength + (int)length + TagLength;
            if (ciphertext.Length - position < frameSize)
            {
                throw Reject("object ends inside a frame");
            }

            frames.Add(new FrameLayout(position, (int)length));
            position += frameSize;
            if (last)
            {
                break;
            }
        }

        if (position != ciphertext.Length)
        {
            throw Reject("bytes follow the last frame");
        }

        return frames;
    }

    private static void DecryptFrames(AesGcm aes, byte[] ciphertext, List<FrameLayout> frames, byte[] plaintext)
    {
        var nonce = new byte[NonceLength];
        var associatedData = new byte[HeaderLength + sizeof(ulong) + LengthWordLength];
        ciphertext.AsSpan(0, HeaderLength).CopyTo(associatedData);

        var outputOffset = 0;
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            var word = ciphertext.AsSpan(frame.Offset, LengthWordLength);
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), (ulong)index);
            BinaryPrimitives.WriteUInt64BigEndian(associatedData.AsSpan(HeaderLength), (ulong)index);
            word.CopyTo(associatedData.AsSpan(HeaderLength + sizeof(ulong)));

            var body = ciphertext.AsSpan(frame.Offset + LengthWordLength, frame.Length);
            var tag = ciphertext.AsSpan(frame.Offset + LengthWordLength + frame.Length, TagLength);
            aes.Decrypt(nonce, body, tag, plaintext.AsSpan(outputOffset, frame.Length), associatedData);
            outputOffset += frame.Length;
        }
    }

    private static void RequirePrintableAscii(string value, string parameterName)
    {
        if (value.Length == 0 || value.Any(c => c < 0x21 || c > 0x7E))
        {
            throw new ArgumentException("TBS1 binding and key id values must be printable ASCII without spaces.", parameterName);
        }
    }

    private static CryptographicException Reject(string reason) =>
        new($"TBS1 object rejected: {reason}. Nothing was applied.");

    private readonly record struct FrameLayout(int Offset, int Length);
}
