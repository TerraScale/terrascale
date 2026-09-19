namespace TerraScale.Auth.Tests;

using System.Security.Cryptography;
using TerraScale.Auth.Keys;

/// <summary>SecretBox AES-GCM boxing and KeyRing first-use key generation.</summary>
public sealed class KeyRingTests
{
    [Test]
    public async Task SecretBoxRoundTrips()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        var plaintext = new byte[37];
        Random.Shared.NextBytes(plaintext);

        var boxed = SecretBox.Encrypt(key, plaintext);
        await Assert.That(boxed.StartsWith("v1:")).IsTrue();
        await Assert.That(SecretBox.Decrypt(key, boxed).SequenceEqual(plaintext)).IsTrue();
    }

    [Test]
    public async Task SecretBoxUsesAFreshNoncePerEncryption()
    {
        var key = new byte[32];
        var boxed = Enumerable.Range(0, 8).Select(_ => SecretBox.Encrypt(key, "same plaintext"u8.ToArray())).ToHashSet();
        await Assert.That(boxed.Count).IsEqualTo(8);
    }

    [Test]
    public async Task SecretBoxRejectsTamperedCiphertext()
    {
        var key = new byte[32];
        var raw = Convert.FromBase64String(SecretBox.Encrypt(key, "secret"u8.ToArray())["v1:".Length..]);
        raw[^3] ^= 0x01;
        var boxed = "v1:" + Convert.ToBase64String(raw);
        Assert.Throws<CryptographicException>(() => SecretBox.Decrypt(key, boxed));
    }

    [Test]
    public async Task SecretBoxRejectsWrongKey()
    {
        var wrongKey = new byte[32];
        wrongKey[0] = 1;
        var boxed = SecretBox.Encrypt(new byte[32], "secret"u8.ToArray());
        Assert.Throws<CryptographicException>(() => SecretBox.Decrypt(wrongKey, boxed));
    }

    [Test]
    [Arguments("v2:AAAA")]
    [Arguments("")]
    public async Task SecretBoxRejectsUnknownVersion(string boxed)
    {
        Assert.Throws<FormatException>(() => SecretBox.Decrypt(new byte[32], boxed));
    }

    [Test]
    [Arguments(0)]
    [Arguments(11)]
    [Arguments(27)]
    public async Task SecretBoxRejectsTruncatedBox(int length)
    {
        Assert.Throws<FormatException>(() => SecretBox.Decrypt(new byte[32],
            "v1:" + Convert.ToBase64String(new byte[length])));
    }

    [Test]
    public async Task ConcurrentFirstUseGeneratesOneSigningKey()
    {
        var fixture = await AuthServiceFixture.GetAsync();
        var store = fixture.Store;
        var ring = new KeyRing(store);
        var instanceId = "keyring-race-" + Guid.NewGuid().ToString("N");

        var keys = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => ring.GetCurrentAsync(instanceId)));

        await Assert.That(keys.Select(k => k.Id).Distinct().Count()).IsEqualTo(1);
        var stored = await store.ListSigningKeysAsync(instanceId);
        await Assert.That(stored.Count).IsEqualTo(1);
    }
}
