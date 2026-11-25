using System.Buffers;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TerraScale.QuicRpc;

public sealed record QuicPeerIdentity(string NodeId, string Region);

public static class QuicMeshIdentity
{
    public const string TrustDomain = "terrascale";

    private const string SubjectAlternativeNameOid = "2.5.29.17";
    private const string UriPrefix = "spiffe://" + TrustDomain + "/region/";
    private const string NodeSegment = "/node/";

    private static readonly SearchValues<char> SegmentCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.");

    private static readonly Asn1Tag UriTag = new(TagClass.ContextSpecific, 6);

    public static Uri UriFor(string nodeId, string region)
    {
        EnsureValidSegment(nodeId, nameof(nodeId));
        EnsureValidSegment(region, nameof(region));
        return new Uri(UriPrefix + region + NodeSegment + nodeId);
    }

    public static QuicPeerIdentity? Read(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var commonName = certificate.GetNameInfo(X509NameType.SimpleName, false);
        QuicPeerIdentity? identity = null;
        foreach (var uri in ReadUris(certificate))
        {
            if (!TryParse(uri, out var parsed))
            {
                continue;
            }

            if (identity is not null || !string.Equals(parsed.NodeId, commonName, StringComparison.Ordinal))
            {
                return null;
            }

            identity = parsed;
        }

        return identity;
    }

    internal static QuicPeerIdentity? ReadRemote(X509Certificate? certificate)
    {
        if (certificate is null)
        {
            return null;
        }

        if (certificate is X509Certificate2 typed)
        {
            return Read(typed);
        }

        using var loaded = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return Read(loaded);
    }

    internal static void EnsureValidSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.AsSpan().ContainsAnyExcept(SegmentCharacters))
        {
            throw new ArgumentException($"'{value}' may only contain ASCII letters, digits, '-', '_' and '.'.", parameterName);
        }
    }

    private static bool TryParse(string uri, out QuicPeerIdentity identity)
    {
        identity = new QuicPeerIdentity(string.Empty, string.Empty);
        if (!uri.StartsWith(UriPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = uri.AsSpan(UriPrefix.Length);
        var split = rest.IndexOf(NodeSegment, StringComparison.Ordinal);
        if (split <= 0)
        {
            return false;
        }

        var region = rest[..split];
        var nodeId = rest[(split + NodeSegment.Length)..];
        if (nodeId.IsEmpty || region.ContainsAnyExcept(SegmentCharacters) || nodeId.ContainsAnyExcept(SegmentCharacters))
        {
            return false;
        }

        identity = new QuicPeerIdentity(nodeId.ToString(), region.ToString());
        return true;
    }

    private static List<string> ReadUris(X509Certificate2 certificate)
    {
        if (certificate.Extensions[SubjectAlternativeNameOid] is not { } extension)
        {
            return [];
        }

        try
        {
            var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
            var names = reader.ReadSequence();
            reader.ThrowIfNotEmpty();
            List<string> uris = [];
            while (names.HasData)
            {
                if (names.PeekTag().HasSameClassAndValue(UriTag))
                {
                    uris.Add(names.ReadCharacterString(UniversalTagNumber.IA5String, UriTag));
                }
                else
                {
                    names.ReadEncodedValue();
                }
            }

            return uris;
        }
        catch (AsnContentException)
        {
            return [];
        }
    }
}

internal sealed class QuicCertificateValidator(IReadOnlyList<X509Certificate2> trustedAuthorities, TimeProvider timeProvider)
{
    public const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    public const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    public QuicPeerIdentity? Validate(X509Certificate2 certificate, IReadOnlyList<X509Certificate2> intermediates) =>
        Build(certificate, intermediates, null, ServerAuthOid) is not null ? Build(certificate, intermediates, null, ClientAuthOid) : null;

    public QuicPeerIdentity? ValidateRemote(X509Certificate? certificate, X509Chain? presented, string usageOid)
    {
        if (certificate is null)
        {
            return null;
        }

        if (certificate is X509Certificate2 typed)
        {
            return Build(typed, [], presented, usageOid);
        }

        using var loaded = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return Build(loaded, [], presented, usageOid);
    }

    private QuicPeerIdentity? Build(X509Certificate2 leaf, IReadOnlyList<X509Certificate2> intermediates, X509Chain? presented, string usageOid)
    {
        using var chain = new X509Chain();
        var policy = chain.ChainPolicy;
        policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        policy.RevocationMode = X509RevocationMode.NoCheck;
        policy.DisableCertificateDownloads = true;
        policy.VerificationTimeIgnored = false;
        policy.VerificationTime = timeProvider.GetUtcNow().UtcDateTime;
        policy.ApplicationPolicy.Add(new Oid(usageOid));
        foreach (var authority in trustedAuthorities)
        {
            policy.CustomTrustStore.Add(authority);
        }

        foreach (var intermediate in intermediates)
        {
            policy.ExtraStore.Add(intermediate);
        }

        if (presented is not null)
        {
            foreach (var element in presented.ChainElements)
            {
                policy.ExtraStore.Add(element.Certificate);
            }
        }

        return BuildChain(chain, leaf) ? QuicMeshIdentity.Read(leaf) : null;
    }

    private static bool BuildChain(X509Chain chain, X509Certificate2 leaf)
    {
        try
        {
            return chain.Build(leaf);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
