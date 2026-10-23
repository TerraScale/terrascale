using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using TerraBase.Features.Shapes;

namespace TerraBase.Testing;

/// <summary>Client-side handling of mirrored shape objects, shared by the in-memory and the real-bucket mirror tests.</summary>
public static class ShapeObjects
{
    /// <summary>Opens a TBS1 object (shape-reads.md section 12): header, then length-word framed AES-GCM; a bad tag throws.</summary>
    public static byte[] Open(byte[] bytes, string handle, string offset, long epoch, string keyId, byte[] epochKey)
    {
        const int HeaderLength = ShapeCrypto.HeaderBytes;
        const int Tag = 16;
        Require(bytes.AsSpan(0, 4).SequenceEqual("TBS1"u8), "the object is TBS1");
        Require((long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(4)) == epoch, "the header names the listed epoch");
        Require(Encoding.ASCII.GetString(bytes, 12, keyId.Length) == keyId, "the header names the listed key id");
        var objectKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, epochKey, 32, bytes[(12 + keyId.Length)..HeaderLength],
            Encoding.UTF8.GetBytes("TBS1 object\0" + handle + "\0" + offset));
        using var aes = new AesGcm(objectKey, Tag);
        var plain = new List<byte>();
        var nonce = new byte[12];
        var associated = new byte[HeaderLength + 8 + 4];
        bytes.AsSpan(0, HeaderLength).CopyTo(associated);
        for (int at = HeaderLength, index = 0; ; index++)
        {
            var word = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at));
            var length = (int)(word & ~(1u << 31));
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), (ulong)index);
            BinaryPrimitives.WriteUInt64BigEndian(associated.AsSpan(HeaderLength), (ulong)index);
            BinaryPrimitives.WriteUInt32BigEndian(associated.AsSpan(HeaderLength + 8), word);
            var piece = new byte[length];
            aes.Decrypt(nonce, bytes.AsSpan(at + 4, length), bytes.AsSpan(at + 4 + length, Tag), piece, associated);
            plain.AddRange(piece);
            at += 4 + length + Tag;
            if ((word & (1u << 31)) != 0)
            {
                Require(at == bytes.Length, "nothing follows the last frame");
                return [.. plain];
            }
        }
    }

    private static void Require(bool condition, string because)
    {
        if (!condition)
        {
            throw new InvalidDataException(because);
        }
    }
}
