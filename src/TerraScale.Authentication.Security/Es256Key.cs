using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace TerraScale.Authentication.Security;

public sealed class Es256Key : IDisposable
{
    private const int CoordinateSize = 32;

    private static readonly CryptoProviderFactory NoProviderCache = new() { CacheSignatureProviders = false };

    private readonly ECDsa ecdsa;

    private Es256Key(string keyId, ECDsa ecdsa)
    {
        this.KeyId = keyId;
        this.ecdsa = ecdsa;
        this.SecurityKey = new ECDsaSecurityKey(ecdsa) { KeyId = keyId, CryptoProviderFactory = NoProviderCache };
    }

    public string KeyId { get; }

    public ECDsaSecurityKey SecurityKey { get; }

    public static Es256Key Generate(string keyId)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        return new Es256Key(keyId, ECDsa.Create(ECCurve.NamedCurves.nistP256));
    }

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
