using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase.Shapes;

/// <summary>
/// TBS1 sealed object conformance (shape reads protocol v1, sections 12 and 16) over the shared
/// <c>shape-vectors.json</c> fixture. Objects are sealed in test code with fixed keys and salts only; no server, fake
/// HTTP or downloaded data is involved.
/// </summary>
public sealed class TerraBaseShapeCryptoTests
{
    private const int MiB = 1 << 20;
    private const int HeaderLength = 70;
    private const int TagLength = 16;
    private const uint LastFrameFlag = 0x8000_0000;

    [Test]
    public async Task PositiveVectorsDeriveEveryIntermediateAndDecrypt()
    {
        using var fixture = LoadFixture();
        var vectors = fixture.RootElement.GetProperty("tbs1_vectors").EnumerateArray().ToList();
        await Assert.That(vectors.Count).IsGreaterThan(0);

        foreach (var vector in vectors)
        {
            var handle = Text(vector, "handle");
            var offset = Text(vector, "request_offset");
            var epoch = ParseEpoch(vector, "epoch");
            var keyId = Text(vector, "key_id");
            var salt = Bytes(vector, "object_salt_hex");
            var epochKey = Bytes(vector, "epoch_key_hex");
            var tbs1 = Bytes(vector, "tbs1_hex");
            var plaintext = Encoding.UTF8.GetBytes(Text(vector, "plaintext_utf8"));

            await Assert.That(Hex(DeriveEpochKey(Bytes(vector, "project_data_key_hex"), handle, epoch))).IsEqualTo(Text(vector, "epoch_key_hex"));
            await Assert.That(Hex(Encoding.ASCII.GetBytes($"{handle}\0{offset}"))).IsEqualTo(Text(vector, "binding_hex"));

            var objectKey = TerraBaseShapeCrypto.DeriveObjectKey(epochKey, salt, handle, offset);
            await Assert.That(Hex(objectKey)).IsEqualTo(Text(vector, "object_key_hex"));
            await Assert.That(Hex(tbs1[..HeaderLength])).IsEqualTo(Text(vector, "header_hex"));
            await Assert.That(tbs1.Length).IsEqualTo(Convert.ToInt32(vector.GetProperty("ciphertext_size").GetInt64(), CultureInfo.InvariantCulture));
            await AssertFramesMatchFixture(vector, objectKey, [plaintext]);

            var decrypted = TerraBaseShapeCrypto.Decrypt(tbs1, epochKey, handle, offset, epoch, keyId);
            await Assert.That(Encoding.UTF8.GetString(decrypted)).IsEqualTo(Text(vector, "plaintext_utf8"));
        }
    }

    [Test]
    public async Task RejectionVectorsRejectWholeObject()
    {
        using var fixture = LoadFixture();
        var vectors = fixture.RootElement.GetProperty("tbs1_rejection_vectors").EnumerateArray().ToList();
        await Assert.That(vectors.Count).IsGreaterThan(0);

        foreach (var vector in vectors)
        {
            await Assert.That(Text(vector, "expected_result")).IsEqualTo("reject_without_applying");
            var tbs1 = Bytes(vector, "tbs1_hex");
            var epochKey = Bytes(vector, "epoch_key_hex");
            var handle = Text(vector, "handle");
            var offset = Text(vector, "request_offset");
            var epoch = ParseEpoch(vector, "epoch");
            var keyId = Text(vector, "key_id");

            Assert.Throws<CryptographicException>(() => TerraBaseShapeCrypto.Decrypt(tbs1, epochKey, handle, offset, epoch, keyId));
        }
    }

    private const string Handle = "h1-f3171a2230109328";
    private const string Offset = "2";
    private const string KeyId = "01ARZ3NDEKTSV4RRFFQ69G5FC0";
    private const ulong Epoch = 100;

    private static JsonDocument LoadFixture() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "contracts", "terrabase", "fixtures", "shape-vectors.json")));

    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString()!;

    private static byte[] Bytes(JsonElement element, string name) => Convert.FromHexString(Text(element, name));

    private static ulong ParseEpoch(JsonElement element, string name) => ulong.Parse(Text(element, name), NumberStyles.None, CultureInfo.InvariantCulture);

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static byte[] Filled(byte value, int length = 32)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, value);
        return bytes;
    }

    /// <summary>
    /// Compares each fixture frame's nonce, AAD, length word, ciphertext hash and tag against an independent AES-GCM
    /// seal of the given plaintext frame.
    /// </summary>
    private static async Task AssertFramesMatchFixture(JsonElement vector, byte[] objectKey, byte[][] framePlaintexts)
    {
        var header = Bytes(vector, "header_hex");
        var frames = vector.GetProperty("frames").EnumerateArray().ToList();
        await Assert.That(frames.Count).IsEqualTo(framePlaintexts.Length);

        using var aes = new AesGcm(objectKey, TagLength);
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            var plaintext = framePlaintexts[index];
            var word = Bytes(frame, "length_word_hex");
            var nonce = FrameNonce(index);
            var aad = Concat(header, FrameIndex(index), word);

            await Assert.That(Hex(word)).IsEqualTo(Hex(LengthWord(plaintext.Length, index == frames.Count - 1)));
            await Assert.That(Hex(nonce)).IsEqualTo(Text(frame, "nonce_hex"));
            await Assert.That(Hex(aad)).IsEqualTo(Text(frame, "aad_hex"));

            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagLength];
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            await Assert.That(Hex(SHA256.HashData(ciphertext))).IsEqualTo(Text(frame, "ciphertext_sha256"));
            await Assert.That(Hex(tag)).IsEqualTo(Text(frame, "tag_hex"));
        }
    }

    /// <summary>Seals plaintext frames with the fixed epoch key and salt, following section 12 exactly.</summary>
    private static byte[] Seal(byte[] epochKey, string handle, string offset, ulong epoch, string keyId, byte[] salt, params (byte[] Plaintext, bool Last)[] frames)
    {
        var header = new byte[HeaderLength];
        "TBS1"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(4), epoch);
        Encoding.ASCII.GetBytes(keyId, header.AsSpan(12));
        salt.CopyTo(header.AsSpan(38));

        var objectKey = TerraBaseShapeCrypto.DeriveObjectKey(epochKey, salt, handle, offset);
        try
        {
            using var aes = new AesGcm(objectKey, TagLength);
            var output = new MemoryStream();
            output.Write(header);
            for (var index = 0; index < frames.Length; index++)
            {
                var (plaintext, last) = frames[index];
                var word = LengthWord(plaintext.Length, last);
                var ciphertext = new byte[plaintext.Length];
                var tag = new byte[TagLength];
                aes.Encrypt(FrameNonce(index), plaintext, ciphertext, tag, Concat(header, FrameIndex(index), word));
                output.Write(word);
                output.Write(ciphertext);
                output.Write(tag);
            }

            return output.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(objectKey);
        }
    }

    /// <summary>K_e = HKDF(K_d, empty salt, "TBS1 epoch" ‖ 0x00 ‖ handle ‖ 0x00 ‖ decimal(E)), section 12 step 3.</summary>
    private static byte[] DeriveEpochKey(byte[] projectDataKey, string handle, ulong epoch)
    {
        var info = Encoding.ASCII.GetBytes($"TBS1 epoch\0{handle}\0{epoch.ToString(CultureInfo.InvariantCulture)}");
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, projectDataKey, 32, info: info);
    }

    private static byte[] LengthWord(int length, bool last)
    {
        var word = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)length | (last ? LastFrameFlag : 0));
        return word;
    }

    private static byte[] FrameNonce(int index)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), (ulong)index);
        return nonce;
    }

    private static byte[] FrameIndex(int index)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, (ulong)index);
        return bytes;
    }

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(part => part)];
}
