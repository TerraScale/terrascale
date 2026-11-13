using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TerraScale.DotNet.TerraBase;

/// <summary>Signs short-lived service assertions for TerraBase using a private P-256 JWK.</summary>
public sealed class CallerCredential
{
    /// <summary>The caller assertion header, independent of user authorization.</summary>
    public const string HeaderName = "X-TerraScale-Caller";

    private readonly Lock gate = new();
    private readonly JsonWebTokenHandler handler = new() { SetDefaultTimesOnTokenCreation = false };
    private readonly SigningCredentials signing;
    private readonly string caller;
    private readonly TimeProvider clock;
    private string? token;
    private DateTimeOffset minted;

    /// <summary>Loads a private EC JWK with a key id and signs as the supplied service.</summary>
    public CallerCredential(string caller, string privateJwk, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateJwk);
        ArgumentNullException.ThrowIfNull(clock);
        JsonWebKey key;
        try
        {
            key = new JsonWebKey(privateJwk);
        }
        catch (Exception error) when (error is ArgumentException or JsonException)
        {
            throw new ArgumentException("Caller credentials must be a private P-256 JWK.", nameof(privateJwk));
        }
        if (key.Kty != "EC" || key.Crv != "P-256" || string.IsNullOrWhiteSpace(key.Kid)
            || string.IsNullOrEmpty(key.D) || string.IsNullOrEmpty(key.X) || string.IsNullOrEmpty(key.Y)
            || key.Alg is { Length: > 0 } algorithm && algorithm != SecurityAlgorithms.EcdsaSha256)
        {
            throw new ArgumentException("Caller credentials require a private P-256 JWK with a key id.", nameof(privateJwk));
        }

        this.caller = caller;
        this.clock = clock;
        signing = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256);
        try
        {
            var d = Base64UrlEncoder.DecodeBytes(key.D);
            var x = Base64UrlEncoder.DecodeBytes(key.X);
            var y = Base64UrlEncoder.DecodeBytes(key.Y);
            if (d.Length != 32 || x.Length != 32 || y.Length != 32
                || d.AsSpan().IndexOfAnyExcept((byte)0) < 0
                || d.AsSpan().SequenceCompareTo(Convert.FromHexString("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551")) >= 0)
            {
                throw new CryptographicException("Invalid P-256 scalar or point.");
            }

            var point = new ECPoint { X = x, Y = y };
            using var privateKey = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = point, D = d });
            using var publicKey = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = point });
            var challenge = RandomNumberGenerator.GetBytes(32);
            if (!publicKey.VerifyHash(challenge, privateKey.SignHash(challenge)))
            {
                throw new CryptographicException("The P-256 private scalar does not match the public point.");
            }

            _ = Token;
        }
        catch (Exception error) when (error is ArgumentException or CryptographicException or FormatException)
        {
            throw new ArgumentException("Caller credentials must contain a valid private P-256 key.", nameof(privateJwk));
        }
    }

    /// <summary>Returns a five-minute assertion, replacing the cached assertion after one minute.</summary>
    public string Token
    {
        get
        {
            lock (gate)
            {
                var now = clock.GetUtcNow();
                if (token is null || now < minted || now - minted >= TimeSpan.FromMinutes(1))
                {
                    token = handler.CreateToken(new SecurityTokenDescriptor
                    {
                        TokenType = "ts-caller+jwt",
                        Issuer = caller,
                        Audience = "terrabase",
                        IssuedAt = now.UtcDateTime,
                        Expires = now.AddMinutes(5).UtcDateTime,
                        SigningCredentials = signing,
                        Claims = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["jti"] = Guid.NewGuid().ToString("N"),
                        },
                    });
                    minted = now;
                }

                return token;
            }
        }
    }
}
