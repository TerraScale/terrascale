using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace TerraScale.Auth.Security;

/// <summary>An ES256 (ECDSA P-256 with SHA-256) key and its key id.</summary>
public sealed class Es256Key : IDisposable
{
    private const int CoordinateSize = 32;

    // IdentityModel otherwise caches signature providers by key thumbprint, process-wide: a
    // later instance of the same key would reuse a provider bound to a disposed ECDsa.
    private static readonly CryptoProviderFactory NoProviderCache = new() { CacheSignatureProviders = false };

    private readonly ECDsa ecdsa;

    private Es256Key(string keyId, ECDsa ecdsa)
    {
        this.KeyId = keyId;
        this.ecdsa = ecdsa;
        this.SecurityKey = new ECDsaSecurityKey(ecdsa) { KeyId = keyId, CryptoProviderFactory = NoProviderCache };
    }

    /// <summary>The <c>kid</c> published in JWT headers and the JWKS.</summary>
    public string KeyId { get; }

    /// <summary>The IdentityModel key for validation, and signing when it contains a private part.</summary>
    public ECDsaSecurityKey SecurityKey { get; }

    /// <summary>Generates a new key from the OS CSPRNG.</summary>
    public static Es256Key Generate(string keyId)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        return new Es256Key(keyId, ECDsa.Create(ECCurve.NamedCurves.nistP256));
    }

    /// <summary>
    /// Restores a key from its persisted form d ‖ X ‖ Y (three 32-byte big-endian values).
    /// Throws <see cref="CryptographicException"/> unless the stored public point belongs to d.
    /// </summary>
    public static Es256Key Import(string keyId, ReadOnlySpan<byte> secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        if (secret.Length != 3 * CoordinateSize)
        {
            throw new CryptographicException("An ES256 secret is 96 bytes.");
        }

        var ecdsa = ECDsa.Create();
        var scalar = secret[..CoordinateSize].ToArray();
        try
        {
            // Only d is imported; the runtime derives the public point, which must equal the stored one.
            ecdsa.ImportParameters(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = scalar });
            var point = ecdsa.ExportParameters(false).Q;
            if (!secret[CoordinateSize..].SequenceEqual([.. point.X!, .. point.Y!]))
            {
                throw new CryptographicException("The stored public point does not belong to the private key.");
            }

            return new Es256Key(keyId, ecdsa);
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scalar);
        }
    }

    /// <summary>
    /// Imports only the public P-256 point from canonical, unpadded base64url JWK coordinates.
    /// Throws <see cref="CryptographicException"/> for malformed coordinates or an invalid point.
    /// </summary>
    public static Es256Key ImportPublic(string keyId, string x, string y)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        var point = new ECPoint { X = DecodeCoordinate(x), Y = DecodeCoordinate(y) };
        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportParameters(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = point });
            return new Es256Key(keyId, ecdsa);
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }

    /// <summary>The persisted form d ‖ X ‖ Y. It contains the private key: seal it, then zero it.</summary>
    public byte[] ExportSecret()
    {
        var parameters = this.ecdsa.ExportParameters(true);
        try
        {
            return [.. parameters.D!, .. parameters.Q.X!, .. parameters.Q.Y!];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parameters.D);
        }
    }

    /// <summary>The public JWK; it never carries the private part.</summary>
    public JsonWebKey PublicJwk()
    {
        var point = this.ecdsa.ExportParameters(false).Q;
        return new JsonWebKey
        {
            Kty = JsonWebAlgorithmsKeyTypes.EllipticCurve,
            Crv = JsonWebKeyECTypes.P256,
            Use = JsonWebKeyUseNames.Sig,
            Alg = SecurityAlgorithms.EcdsaSha256,
            Kid = this.KeyId,
            X = Base64UrlEncoder.Encode(point.X),
            Y = Base64UrlEncoder.Encode(point.Y),
        };
    }

    /// <inheritdoc />
    public void Dispose() => this.ecdsa.Dispose();

    private static byte[] DecodeCoordinate(string coordinate)
    {
        if (coordinate is null || coordinate.Length != 43)
        {
            throw new CryptographicException("An ES256 coordinate is 32 bytes in canonical base64url.");
        }

        try
        {
            var bytes = Base64UrlEncoder.DecodeBytes(coordinate);
            if (bytes.Length != CoordinateSize || !string.Equals(Base64UrlEncoder.Encode(bytes), coordinate, StringComparison.Ordinal))
            {
                throw new CryptographicException("An ES256 coordinate is 32 bytes in canonical base64url.");
            }

            return bytes;
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("An ES256 coordinate is 32 bytes in canonical base64url.", exception);
        }
    }
}
