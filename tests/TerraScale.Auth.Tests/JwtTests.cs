namespace TerraScale.Auth.Tests;

using System.Security.Cryptography;
using TerraScale.Auth.Crypto;

public sealed class JwtTests
{
    [Test]
    public async Task Es256SignVerifyRoundTrip()
    {
        var key = EcdsaSigningKey.Generate("key-1");
        var token = JsonWebToken.Sign(new { sub = "user_123", role = "admin" }, DateTimeOffset.UtcNow.AddMinutes(5), key);
        var claims = JsonWebToken.Verify(token, kid => kid == "key-1" ? key : null);
        await Assert.That(claims).IsNotNull();
        await Assert.That(claims!["sub"].ToString()).IsEqualTo("user_123");
        await Assert.That(claims["role"].ToString()).IsEqualTo("admin");
    }

    [Test]
    public async Task SecretExportRestoreRoundTrip()
    {
        var key = EcdsaSigningKey.Generate("key-1");
        var restored = EcdsaSigningKey.FromSecret(key.ExportSecret(), "key-1");
        var token = JsonWebToken.Sign(new { sub = "user_123" }, DateTimeOffset.UtcNow.AddMinutes(5), key);
        await Assert.That(JsonWebToken.Verify(token, kid => restored)).IsNotNull();
    }

    [Test]
    public async Task ExpiredTokenIsRejected()
    {
        var key = EcdsaSigningKey.Generate("key-1");
        var token = JsonWebToken.Sign(new { sub = "user_123" }, DateTimeOffset.UtcNow.AddMinutes(-1), key);
        await Assert.That(JsonWebToken.Verify(token, kid => key)).IsNull();
    }

    [Test]
    public async Task TamperedPayloadIsRejected()
    {
        var key = EcdsaSigningKey.Generate("key-1");
        var token = JsonWebToken.Sign(new { sub = "user_123" }, DateTimeOffset.UtcNow.AddMinutes(5), key);
        var parts = token.Split('.');
        var tampered = JsonWebToken.Sign(new { sub = "attacker" }, DateTimeOffset.UtcNow.AddMinutes(5), key).Split('.');
        await Assert.That(JsonWebToken.Verify($"{parts[0]}.{tampered[1]}.{parts[2]}", kid => key)).IsNull();
    }

    [Test]
    public async Task AlgorithmConfusionIsRejected()
    {
        // A token claiming HS256 while the resolved key is ES256 must fail before verification.
        var esKey = EcdsaSigningKey.Generate("key-1");
        var hsKey = new HmacSigningKey(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))) { Id = "key-1" };
        var hsToken = JsonWebToken.Sign(new { sub = "user_123" }, DateTimeOffset.UtcNow.AddMinutes(5), hsKey);
        await Assert.That(JsonWebToken.Verify(hsToken, kid => esKey)).IsNull();
        await Assert.That(JsonWebToken.Verify(hsToken, kid => hsKey)).IsNotNull();
    }

    [Test]
    public async Task UnknownKeyIsRejected()
    {
        var key = EcdsaSigningKey.Generate("key-1");
        var token = JsonWebToken.Sign(new { sub = "user_123" }, DateTimeOffset.UtcNow.AddMinutes(5), key);
        await Assert.That(JsonWebToken.Verify(token, kid => null)).IsNull();
    }

    [Test]
    public async Task Hs256RoundTrip()
    {
        var key = new HmacSigningKey(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))) { Id = "hmac-1" };
        var token = JsonWebToken.Sign(new { sub = "user_123" }, DateTimeOffset.UtcNow.AddMinutes(5), key);
        await Assert.That(JsonWebToken.Verify(token, kid => key)).IsNotNull();
    }

    [Test]
    public async Task MalformedSignatureSegmentIsRejectedNotThrown()
    {
        var key = EcdsaSigningKey.Generate("key-1");
        var token = JsonWebToken.Sign(new { sub = "user_123" }, DateTimeOffset.UtcNow.AddMinutes(5), key);
        var parts = token.Split('.');
        // Attacker-controlled third segment: bad base64url must yield null, not an exception.
        await Assert.That(JsonWebToken.Verify($"{parts[0]}.{parts[1]}.!!!", kid => key)).IsNull();
        await Assert.That(JsonWebToken.Verify($"{parts[0]}.{parts[1]}.a", kid => key)).IsNull();
        await Assert.That(JsonWebToken.Verify($"{parts[0]}.{parts[1]}", kid => key)).IsNull();
    }

    [Test]
    public async Task AlgNoneIsRejected()
    {
        var key = EcdsaSigningKey.Generate("key-1");
        var header = Encodings.Base64UrlEncode("""{"alg":"none","typ":"JWT","kid":"key-1"}"""u8);
        var payload = Encodings.Base64UrlEncode("""{"sub":"user_123"}"""u8);
        await Assert.That(JsonWebToken.Verify($"{header}.{payload}.", kid => key)).IsNull();
    }

    [Test]
    public async Task PublicJwkExposesNoPrivateMaterial()
    {
        var key = EcdsaSigningKey.Generate("key-1");
        var jwk = key.ToPublicJwk();
        await Assert.That(jwk["kty"]).IsEqualTo("EC");
        await Assert.That(jwk.ContainsKey("d")).IsFalse();
        await Assert.That(jwk["crv"]).IsEqualTo("P-256");
    }
}
