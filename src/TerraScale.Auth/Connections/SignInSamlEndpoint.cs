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
using TerraScale.Endpoints;
using static TerraScale.Auth.Connections.ConnectionFlow;

using static TerraScale.Auth.Connections.SamlLogin;

internal sealed class SignInSamlEndpoint : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) =>
        routes.MapGet("/ui/{slug}/connections/{connectionId}/saml/login", HandleAsync);

    private static async Task<IResult> HandleAsync(string slug, string connectionId, AuthOptions options, AuthStore store, CancellationToken ct)
    {
        var instance = await store.FindInstanceBySlugAsync(slug, ct);
        if (instance is null)
            return ConnectionFlow.NotFoundPage();
        var connection = await ConnectionFlow.FindConnectionAsync(store, instance.Id, connectionId, "saml", ct);
        if (connection is null)
            return ConnectionFlow.NotFoundPage();

        var acs = AcsUrl(options, slug, connection.Id);
        var spEntityId = connection.SpEntityId ?? acs;
        var requestId = "_" + SecureTokens.RandomToken(20);
        var issueInstant = Timestamp(DateTimeOffset.UtcNow);
        var authnRequest = $"""
            <samlp:AuthnRequest xmlns:samlp="{ProtocolNamespace}" xmlns:saml="{AssertionNamespace}" ID="{requestId}" Version="2.0" IssueInstant="{issueInstant}" Destination="{SecurityElement.Escape(connection.SsoRedirectUrl!)}" AssertionConsumerServiceURL="{SecurityElement.Escape(acs)}"><saml:Issuer>{SecurityElement.Escape(spEntityId)}</saml:Issuer></samlp:AuthnRequest>
            """;
        var relayState = await ConnectionFlow.MintStateAsync(store, instance.Id, connection.Id, "saml.state", requestId, ConnectionFlow.SamlStateLifetime, ct);
        return Results.Redirect(
            $"{connection.SsoRedirectUrl}?SAMLRequest={Uri.EscapeDataString(Convert.ToBase64String(Deflate(Encoding.UTF8.GetBytes(authnRequest))))}"
            + $"&RelayState={Uri.EscapeDataString(relayState)}");
    }

    private static string Timestamp(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Raw DEFLATE (RFC 1951), the HTTP-Redirect binding payload format.</summary>
    private static byte[] Deflate(byte[] input)
    {
        using var output = new MemoryStream();
        using (var compressor = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(input);
        return output.ToArray();
    }
}
