namespace TerraScale.Auth.Connections;

using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Crypto;
using TerraScale.Auth.Store;
using TerraScale.Auth.Users;

/// <summary>
/// Enterprise SAML SP: HTTP-Redirect-binding AuthnRequests and a POST-binding
/// ACS, verified with framework XMLDSig against the configured IdP certificate.
/// </summary>
internal static class SamlLogin
{
    private const string ProtocolNamespace = "urn:oasis:names:tc:SAML:2.0:protocol";
    private const string AssertionNamespace = "urn:oasis:names:tc:SAML:2.0:assertion";
    private const string SuccessStatus = "urn:oasis:names:tc:SAML:2.0:status:Success";
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(90);

    internal static async Task<IResult> LoginAsync(string slug, string connectionId, AuthOptions options, AuthStore store, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return ConnectionEndpoints.NotFoundPage();
        var connection = await ConnectionEndpoints.FindConnectionAsync(store, instance.Id, connectionId, "saml", ct);
        if (connection is null)
            return ConnectionEndpoints.NotFoundPage();

        var acs = AcsUrl(options, slug, connection.Id);
        var spEntityId = connection.SpEntityId ?? acs;
        var requestId = "_" + SecureTokens.RandomToken(20);
        var issueInstant = Timestamp(DateTimeOffset.UtcNow);
        var authnRequest = $"""
            <samlp:AuthnRequest xmlns:samlp="{ProtocolNamespace}" xmlns:saml="{AssertionNamespace}" ID="{requestId}" Version="2.0" IssueInstant="{issueInstant}" Destination="{SecurityElement.Escape(connection.SsoRedirectUrl!)}" AssertionConsumerServiceURL="{SecurityElement.Escape(acs)}"><saml:Issuer>{SecurityElement.Escape(spEntityId)}</saml:Issuer></samlp:AuthnRequest>
            """;
        var relayState = await ConnectionEndpoints.MintStateAsync(store, instance.Id, connection.Id, "saml.state", requestId, ConnectionEndpoints.SamlStateLifetime, ct);
        return Results.Redirect(
            $"{connection.SsoRedirectUrl}?SAMLRequest={Uri.EscapeDataString(Convert.ToBase64String(Deflate(Encoding.UTF8.GetBytes(authnRequest))))}"
            + $"&RelayState={Uri.EscapeDataString(relayState)}");
    }

    internal static async Task<IResult> AcsAsync(
        string slug, string connectionId, HttpRequest request, AuthOptions options, AuthStore store, SessionService sessions, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return ConnectionEndpoints.NotFoundPage();

        if (!request.HasFormContentType)
            return ConnectionEndpoints.ErrorPage(slug, "The SAML response must be posted as a form.");
        var form = await request.ReadFormAsync(ct);
        var relayState = form["RelayState"].ToString();
        var samlResponse = form["SAMLResponse"].ToString();

        // RelayState first: single-use, unexpired, minted for this connection.
        if (await ConnectionEndpoints.ConsumeStateAsync(store, instance, connectionId, relayState, "saml.state", ct) is not { } state)
            return ConnectionEndpoints.ErrorPage(slug, "This sign-in attempt is invalid, expired, or already used.");
        var connection = await ConnectionEndpoints.FindConnectionAsync(store, instance.Id, connectionId, "saml", ct);
        if (connection is null)
            return ConnectionEndpoints.NotFoundPage();
        var requestId = state.Id[(state.Id.IndexOf(':') + 1)..]; // "vrf_<rand>:<requestId>"

        XmlDocument document;
        try
        {
            document = new XmlDocument { PreserveWhitespace = true };
            document.LoadXml(Encoding.UTF8.GetString(Convert.FromBase64String(samlResponse)));
        }
        catch (Exception exception) when (exception is FormatException or XmlException)
        {
            return ConnectionEndpoints.ErrorPage(slug, "The SAML response could not be parsed.");
        }

        var namespaces = Namespaces(document);
        var response = document.DocumentElement!;
        if (response.NamespaceURI != ProtocolNamespace || response.LocalName != "Response")
            return ConnectionEndpoints.ErrorPage(slug, "The SAML response is not a protocol Response.");
        if (response.SelectSingleNode("samlp:Status/samlp:StatusCode", namespaces) is not { } status || status.Attributes?["Value"]?.Value != SuccessStatus)
            return ConnectionEndpoints.ErrorPage(slug, "The identity provider reported a sign-in error.");
        if (response.GetAttribute("InResponseTo") != requestId)
            return ConnectionEndpoints.ErrorPage(slug, "The SAML response does not answer this sign-in request.");

        var assertion = (XmlElement?)response.SelectSingleNode("saml:Assertion", namespaces);
        if (assertion is null)
            return ConnectionEndpoints.ErrorPage(slug, "The SAML response carries no assertion.");

        // Reject unsigned and unverifiable responses: at least one enveloped signature (Response
        // and/or Assertion) must verify against the certificate configured on the connection.
        using var certificate = ConnectionEndpoints.TryLoadCertificate(connection.X509CertificatePem!);
        if (certificate is null)
            return ConnectionEndpoints.ErrorPage(slug, "The connection certificate could not be loaded.", 500);
        var responseSigned = response.SelectSingleNode("ds:Signature", namespaces) is not null;
        var assertionSigned = assertion.SelectSingleNode("ds:Signature", namespaces) is not null;
        if (!responseSigned && !assertionSigned)
            return ConnectionEndpoints.ErrorPage(slug, "The SAML response is not signed.");
        if (responseSigned && !VerifySignature(response, certificate))
            return ConnectionEndpoints.ErrorPage(slug, "The SAML response signature is invalid.");
        if (assertionSigned && !VerifySignature(assertion, certificate))
            return ConnectionEndpoints.ErrorPage(slug, "The assertion signature is invalid.");

        var acs = AcsUrl(options, slug, connection.Id);
        var spEntityId = connection.SpEntityId ?? acs;
        var now = DateTimeOffset.UtcNow;
        var conditions = assertion.SelectSingleNode("saml:Conditions", namespaces);
        if (conditions is not null)
        {
            var notBefore = conditions.Attributes?["NotBefore"]?.Value;
            var notOnOrAfter = conditions.Attributes?["NotOnOrAfter"]?.Value;
            var before = TryParseUtc(notBefore);
            var after = TryParseUtc(notOnOrAfter);
            if ((notBefore is { Length: > 0 } && before is null) || (notOnOrAfter is { Length: > 0 } && after is null))
                return ConnectionEndpoints.ErrorPage(slug, "The assertion conditions are malformed.");
            if ((before is { } notValidYet && now + ClockSkew < notValidYet)
                || (after is { } expired && now - ClockSkew >= expired))
                return ConnectionEndpoints.ErrorPage(slug, "The assertion is outside its validity window.");
        }
        // Audience: exact match only (never a substring). Any audience within a restriction
        // satisfies it, but every AudienceRestriction must name this SP, and there must be one.
        var restrictions = assertion.SelectNodes("saml:Conditions/saml:AudienceRestriction", namespaces);
        if (restrictions is null || restrictions.Count == 0)
            return ConnectionEndpoints.ErrorPage(slug, "The assertion names no audience for this service provider.");
        foreach (XmlElement restriction in restrictions.Cast<XmlElement>())
        {
            var named = restriction.SelectNodes("saml:Audience", namespaces)?
                .Cast<XmlElement>()
                .Select(element => element.InnerText.Trim());
            if (named?.Contains(spEntityId) != true)
                return ConnectionEndpoints.ErrorPage(slug, "The assertion audience does not match this service provider.");
        }

        var nameId = assertion.SelectSingleNode("saml:Subject/saml:NameID", namespaces)?.InnerText.Trim();
        var email = AttributeValue(assertion, "email")
            ?? (nameId is not null && SessionService.IsValidEmail(nameId) ? nameId : null);
        if (email is null)
            return ConnectionEndpoints.ErrorPage(slug, "The assertion carries no email address.");

        var firstName = AttributeValue(assertion, "firstName");
        var lastName = AttributeValue(assertion, "lastName");
        var name = firstName is null && lastName is null ? null : $"{firstName} {lastName}".Trim();

        // Enterprise IdPs assert verified addresses at the organization level.
        var user = await ConnectionEndpoints.FindOrCreateUserAsync(store, sessions, instance, email, emailVerified: true, name, ct);
        if (user is null)
            return ConnectionEndpoints.ErrorPage(slug, "Could not create or access an account for this identity.");

        await ConnectionEndpoints.SignInAsync(store, sessions, request.HttpContext, instance, user, $"saml:{connection.Id}", ct);
        return Results.Redirect($"/ui/{slug}/account");
    }

    /// <summary>Enveloped-signature check in a standalone document: the element is re-serialized
    /// alone so canonicalization matches exactly what the IdP signed. Signature-wrapping
    /// hardening: the signature's single Reference must point at the trusted element's own ID,
    /// and that ID must be unique in the document, so a valid signature over an embedded decoy
    /// element can never be taken as proof of the element whose claims are trusted.</summary>
    private static bool VerifySignature(XmlElement signedElement, X509Certificate2 certificate)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(signedElement.OuterXml);
        var id = document.DocumentElement!.GetAttribute("ID");
        if (id.Length == 0)
            return false;

        // Exactly one element may carry this ID: duplicate IDs are wrapping fuel.
        var idClaims = 0;
        foreach (var element in document.SelectNodes("//*[@ID or @Id or @id]")!.Cast<XmlElement>())
            if (element.GetAttribute("ID") == id || element.GetAttribute("Id") == id || element.GetAttribute("id") == id)
                idClaims++;
        if (idClaims != 1)
            return false;

        foreach (var signature in document.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)!.Cast<XmlElement>())
        {
            try
            {
                var signedXml = new SignedXml(document);
                signedXml.LoadXml(signature);
                var signedInfo = signedXml.SignedInfo;
                if (signedInfo is null || signedInfo.References.Count != 1)
                    continue;
                if (((Reference)signedInfo.References[0]!).Uri != "#" + id)
                    continue;
                if (signedXml.CheckSignature(certificate, verifySignatureOnly: true))
                    return true;
            }
            catch (CryptographicException)
            {
            }
            catch (XmlException)
            {
            }
        }
        return false;
    }

    private static string? AttributeValue(XmlElement assertion, string name) => assertion
        .SelectNodes($"saml:AttributeStatement/saml:Attribute[@Name='{name}' or @FriendlyName='{name}']/saml:AttributeValue", Namespaces(assertion.OwnerDocument!))
        ?.Cast<XmlElement>()
        .Select(attribute => attribute.InnerText.Trim())
        .FirstOrDefault(value => value.Length > 0);

    private static XmlNamespaceManager Namespaces(XmlDocument document)
    {
        var namespaces = new XmlNamespaceManager(document.NameTable);
        namespaces.AddNamespace("samlp", ProtocolNamespace);
        namespaces.AddNamespace("saml", AssertionNamespace);
        namespaces.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);
        return namespaces;
    }

    private static string AcsUrl(AuthOptions options, string slug, string connectionId) =>
        $"{options.PublicOrigin.ToString().TrimEnd('/')}/{slug}/connections/{connectionId}/saml/acs";

    private static string Timestamp(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset? TryParseUtc(string? value)
    {
        if (value is not { Length: > 0 })
            return null;
        try
        {
            return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Raw DEFLATE (RFC 1951), the HTTP-Redirect binding payload format.</summary>
    private static byte[] Deflate(byte[] input)
    {
        using var output = new MemoryStream();
        using (var compressor = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(input);
        return output.ToArray();
    }
}
