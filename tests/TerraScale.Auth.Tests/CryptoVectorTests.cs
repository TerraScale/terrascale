namespace TerraScale.Auth.Tests;

using System.Text;
using TerraScale.Auth.Crypto;

public sealed class CryptoVectorTests
{
    [Test]
    public async Task Blake2bEmpty512()
    {
        var digest = Blake2b.Hash(64, []);
        await Assert.That(Encodings.HexEncode(digest)).IsEqualTo(
            "786a02f742015903c6c6fd852552d272912f4740e15847618a86e217f71f5419d25e1031afee585313896444934eb04b903a685b1448b755d56f701afe9be2ce");
    }

    [Test]
    public async Task Blake2bAbc512()
    {
        var digest = Blake2b.Hash(64, "abc"u8);
        await Assert.That(Encodings.HexEncode(digest)).IsEqualTo(
            "ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d17d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923");
    }

    [Test]
    public async Task Blake2bStreamingMatchesOneShot()
    {
        var data = new byte[1000];
        Random.Shared.NextBytes(data);
        var oneShot = Blake2b.Hash(32, data);
        var stream = new Blake2b(32);
        foreach (var chunk in data.Chunk(97))
            stream.Update(chunk);
        await Assert.That(stream.Final().SequenceEqual(oneShot)).IsTrue();
    }

    [Test]
    [Arguments(1)]
    [Arguments(31)]
    [Arguments(64)]
    public async Task Blake2bDigestSizesAreStable(int size)
    {
        var digest = Blake2b.Hash(size, "payload"u8);
        await Assert.That(digest.Length).IsEqualTo(size);
        await Assert.That(Blake2b.Hash(size, "payload"u8).SequenceEqual(digest)).IsTrue();
        await Assert.That(Blake2b.Hash(size, "other"u8).SequenceEqual(digest)).IsFalse();
    }

    private static byte[] Repeat(byte value, int length)
    {
        var data = new byte[length];
        Array.Fill(data, value);
        return data;
    }

    // RFC 9106 section 5: Argon2id test vector (m=32 KiB, t=3, p=4, tag=32).
    [Test]
    public async Task Argon2idRfc9106Tag()
    {
        var tag = Argon2id.Derive(
            password: Repeat(0x01, 32),
            salt: Repeat(0x02, 16),
            memoryKiB: 32,
            passes: 3,
            lanes: 4,
            tagSize: 32,
            secret: Repeat(0x03, 8),
            associatedData: Repeat(0x04, 12));
        await Assert.That(Encodings.HexEncode(tag)).IsEqualTo(
            "0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659");
    }

    [Test]
    public async Task Argon2idLargerMemoryProducesDifferentTag()
    {
        var first = Argon2id.Derive("pw"u8, "saltsalt"u8, memoryKiB: 64, passes: 2, lanes: 1, tagSize: 32);
        var second = Argon2id.Derive("pw"u8, "saltsalt"u8, memoryKiB: 64, passes: 2, lanes: 1, tagSize: 32);
        var third = Argon2id.Derive("pw2"u8, "saltsalt"u8, memoryKiB: 64, passes: 2, lanes: 1, tagSize: 32);
        await Assert.That(second.SequenceEqual(first)).IsTrue();
        await Assert.That(third.SequenceEqual(first)).IsFalse();
    }

    [Test]
    public async Task PasswordHasherRoundTrip()
    {
        var phc = PasswordHasher.Hash("correct horse battery staple", memoryKiB: 32, passes: 1, lanes: 1);
        await Assert.That(phc.StartsWith("$argon2id$v=19$m=32,t=1,p=1$")).IsTrue();
        await Assert.That(PasswordHasher.Verify("correct horse battery staple", phc)).IsTrue();
        await Assert.That(PasswordHasher.Verify("wrong", phc)).IsFalse();
        await Assert.That(PasswordHasher.Verify("correct horse battery staple", "$argon2id$v=19$garbage")).IsFalse();
    }

    // RFC 6238 appendix B test vectors (8 digits, ASCII seeds).
    [Test]
    [MethodDataSource(nameof(Rfc6238Cases))]
    public async Task TotpRfc6238(long unixSeconds, string expected, string seed, string algorithm)
    {
        var code = Totp.Compute(Encoding.ASCII.GetBytes(seed), DateTimeOffset.FromUnixTimeSeconds(unixSeconds), digits: 8, algorithm: algorithm);
        await Assert.That(code).IsEqualTo(expected);
    }

    public static IEnumerable<(long UnixSeconds, string Expected, string Seed, string Algorithm)> Rfc6238Cases()
    {
        const string sha1 = "12345678901234567890";
        const string sha256 = "12345678901234567890123456789012";
        const string sha512 = "1234567890123456789012345678901234567890123456789012345678901234";
        yield return (59L, "94287082", sha1, "SHA1");
        yield return (1111111109L, "07081804", sha1, "SHA1");
        yield return (1111111111L, "14050471", sha1, "SHA1");
        yield return (1234567890L, "89005924", sha1, "SHA1");
        yield return (2000000000L, "69279037", sha1, "SHA1");
        yield return (20000000000L, "65353130", sha1, "SHA1");
        yield return (59L, "46119246", sha256, "SHA256");
        yield return (1111111109L, "68084774", sha256, "SHA256");
        yield return (1111111111L, "67062674", sha256, "SHA256");
        yield return (1234567890L, "91819424", sha256, "SHA256");
        yield return (2000000000L, "90698825", sha256, "SHA256");
        yield return (20000000000L, "77737706", sha256, "SHA256");
        yield return (59L, "90693936", sha512, "SHA512");
        yield return (1111111109L, "25091201", sha512, "SHA512");
        yield return (1111111111L, "99943326", sha512, "SHA512");
        yield return (1234567890L, "93441116", sha512, "SHA512");
        yield return (2000000000L, "38618901", sha512, "SHA512");
        yield return (20000000000L, "47863826", sha512, "SHA512");
    }

    [Test]
    public async Task TotpVerifyWindow()
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(59);
        var code = Totp.Compute(secret, timestamp);
        await Assert.That(Totp.Verify(secret, code, timestamp)).IsTrue();
        await Assert.That(Totp.Verify(secret, code, timestamp.AddSeconds(30))).IsTrue(); // +1 step tolerance
        await Assert.That(Totp.Verify(secret, code, timestamp.AddSeconds(120))).IsFalse();
    }

    [Test]
    public async Task Base32Rfc4648()
    {
        await Assert.That(Encodings.Base32Encode("foob"u8)).IsEqualTo("MZXW6YQ"); // unpadded by design (TOTP style)
        await Assert.That(Encodings.Base32Decode("MZXW6YQ=").SequenceEqual("foob"u8.ToArray())).IsTrue(); // padded input accepted
        await Assert.That(Encodings.Base32Decode("MZXW6YTBOI").SequenceEqual("foobar"u8.ToArray())).IsTrue();
        await Assert.That(Encodings.Base32Decode(Encodings.Base32Encode("hello world"u8)).SequenceEqual("hello world"u8.ToArray())).IsTrue();
    }

    [Test]
    public async Task Base64UrlRoundTrip()
    {
        var data = System.Security.Cryptography.RandomNumberGenerator.GetBytes(33);
        var encoded = Encodings.Base64UrlEncode(data);
        await Assert.That(encoded.Contains('=')).IsFalse();
        await Assert.That(encoded.Contains('+')).IsFalse();
        await Assert.That(encoded.Contains('/')).IsFalse();
        await Assert.That(Encodings.Base64UrlDecode(encoded).SequenceEqual(data)).IsTrue();
    }

    [Test]
    public async Task SecretTokensAreUnique()
    {
        var (id, secret, combined) = SecureTokens.NewSecret("sk");
        await Assert.That(id.StartsWith("sk_")).IsTrue();
        await Assert.That(combined).IsEqualTo($"{id}_{secret}");
        await Assert.That(SecureTokens.NewSecret("sk").Combined).IsNotEqualTo(combined);
        await Assert.That(SecureTokens.Sha256Hex("abc")).IsEqualTo(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }
}
