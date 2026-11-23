using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
namespace TerraScale.QuicRpc;

public sealed record QuicMeshCertificates(X509Certificate2 Certificate, IReadOnlyList<X509Certificate2> TrustedAuthorities, IReadOnlyList<X509Certificate2> Intermediates);

public static class QuicMeshPki
{
    public const string AuthorityCommonName = "TerraScale Mesh Authority";

    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    private static readonly byte[] AuthoritySalt = "terrascale/mesh/authority/v1"u8.ToArray();
    private static readonly DateTimeOffset AuthorityNotBefore = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AuthorityNotAfter = new(2125, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan NodeValidity = TimeSpan.FromDays(30);
    private static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromMinutes(10);

    private static readonly BigInteger CurveOrder = BigInteger.Parse(
        "0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551",
        NumberStyles.HexNumber,
        CultureInfo.InvariantCulture);

    public static byte[] DecodeSecret(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var trimmed = secret.Trim();
        return trimmed.Length % 2 == 0 && trimmed.Length >= 32 && IsHex(trimmed)
            ? Convert.FromHexString(trimmed)
            : Encoding.UTF8.GetBytes(trimmed);
    }

    public static X509Certificate2 DeriveAuthority(ReadOnlySpan<byte> secret)
    {
        using var key = DeriveAuthorityKey(secret);
        return CreateAuthority(key);
    }

    public static QuicMeshCertificates DeriveNode(ReadOnlySpan<byte> secret, string nodeId, string region, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(secret.Length, 16, nameof(secret));
        using var authorityKey = DeriveAuthorityKey(secret);
        using var authorityWithKey = CreateAuthority(authorityKey);
        var authority = X509CertificateLoader.LoadCertificate(authorityWithKey.RawData);
        using var nodeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var name = new X500DistinguishedNameBuilder();
        name.AddCommonName(nodeId);
        name.AddOrganizationName("TerraScale");
        var request = new CertificateRequest(name.Build(), nodeKey, HashAlgorithmName.SHA256);
        var alternativeNames = new SubjectAlternativeNameBuilder();
        alternativeNames.AddUri(QuicMeshIdentity.UriFor(nodeId, region));
        request.CertificateExtensions.Add(alternativeNames.Build(critical: false));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthOid), new Oid(ClientAuthOid)], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authorityWithKey, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        var now = timeProvider.GetUtcNow();
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var issued = request.Create(authorityWithKey, now - ClockSkewAllowance, now + NodeValidity, serial);
        using var withKey = issued.CopyWithPrivateKey(nodeKey);
        var certificate = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
        return new QuicMeshCertificates(certificate, [authority], []);
    }

    public static Result<QuicMeshCertificates, Validation> LoadPem(string certificatePath, string keyPath, string authorityPath, string nodeId, string region, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return (ReadText(certificatePath), ReadText(keyPath), ReadText(authorityPath)) switch
        {
            (string certificate, string key, string authority) => FromPem(certificate, key, authority, nodeId, region),
            (Validation invalid, _, _) => invalid,
            (_, Validation invalid, _) => invalid,
            (_, _, Validation invalid) => invalid,
        };
    }

    private static Result<QuicMeshCertificates, Validation> FromPem(string certificatePem, string keyPem, string authorityPem, string nodeId, string region)
    {
        var loaded = LoadPemPair(certificatePem, keyPem);
        var authorities = LoadPemCollection(authorityPem);
        if (loaded is null || authorities is null || authorities.Count == 0)
        {
            return new Validation("mesh certificate, key or authority PEM is malformed", "cluster_mesh_pem");
        }

        var chain = LoadPemCollection(certificatePem) ?? [];
        IReadOnlyList<X509Certificate2> intermediates = [.. chain.Where(candidate => !string.Equals(candidate.Thumbprint, loaded.Thumbprint, StringComparison.Ordinal))];
        if (QuicMeshIdentity.Read(loaded) is not { } identity
            || !string.Equals(identity.NodeId, nodeId, StringComparison.Ordinal)
            || !string.Equals(identity.Region, region, StringComparison.Ordinal))
        {
            return new Validation("mesh certificate identity does not match the local node and region", "cluster_mesh_identity");
        }

        return new QuicMeshCertificates(loaded, authorities, intermediates);
    }

    private static Result<string, Validation> ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return Unreadable(path);
        }
        catch (UnauthorizedAccessException)
        {
            return Unreadable(path);
        }
    }

    private static Validation Unreadable(string path) =>
        new(string.Concat("mesh file '", Path.GetFileName(path), "' could not be read"), "cluster_file_unreadable");

    private static X509Certificate2? LoadPemPair(string certificatePem, string keyPem)
    {
        try
        {
            using var pair = X509Certificate2.CreateFromPem(certificatePem, keyPem);
            return X509CertificateLoader.LoadPkcs12(pair.Export(X509ContentType.Pkcs12), null);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static List<X509Certificate2>? LoadPemCollection(string pem)
    {
        try
        {
            var collection = new X509Certificate2Collection();
            collection.ImportFromPem(pem);
            return [.. collection];
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static ECDsa DeriveAuthorityKey(ReadOnlySpan<byte> secret)
    {
        Span<byte> scalar = stackalloc byte[32];
        Span<byte> info = stackalloc byte[4];
        for (var attempt = 0u; ; attempt++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(info, attempt);
            HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, scalar, AuthoritySalt, info);
            var value = new BigInteger(scalar, isUnsigned: true, isBigEndian: true);
            if (!value.IsZero && value < CurveOrder)
            {
                return ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = scalar.ToArray() });
            }
        }
    }

    private static X509Certificate2 CreateAuthority(ECDsa key)
    {
        var name = new X500DistinguishedNameBuilder();
        name.AddCommonName(AuthorityCommonName);
        name.AddOrganizationName("TerraScale");
        var subject = name.Build();
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        var keyIdentifier = new X509SubjectKeyIdentifierExtension(request.PublicKey, false);
        request.CertificateExtensions.Add(keyIdentifier);
        var serial = SHA256.HashData(request.PublicKey.EncodedKeyValue.RawData)[..16];
        serial[0] &= 0x7F;
        using var unsigned = request.Create(subject, X509SignatureGenerator.CreateForECDsa(key), AuthorityNotBefore, AuthorityNotAfter, serial);
        return unsigned.CopyWithPrivateKey(key);
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
