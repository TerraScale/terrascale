using System.Net;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

/// <summary>
/// TerraBase's side of the ts-auth token contract in every region (decision 0007): nodes in gru, sjc and fra trust one
/// <c>auth-issuer</c> and read its keys from one JWKS, which every ts-auth replica serves from the shared key ring. A
/// <see cref="TestIssuer"/> stands in for that issuer with fake keys; nothing reaches a real ts-auth.
/// </summary>
public sealed class AuthTokenContractTests
{
    private static readonly string[] Regions = ["gru", "sjc", "fra"];

    [Test]
    public async Task A_token_validates_in_every_region_and_every_region_enforces_issuer_kid_type_and_expiry()
    {
        using var issuer = new TestIssuer();
        var tls = new ClientTls();
        var nodes = new List<TerraBaseTestServer>();
        try
        {
            foreach (var region in Regions)
            {
                nodes.Add(await TerraBaseTestServer.StartAsync(settings =>
                {
                    TestIssuer.Configure(settings);
                    settings["region"] = region;
                }, issuer.Route));
            }

            var user = EntityIds.New().ToString();
            await AssertEverywhereAsync(nodes, tls, issuer.Mint(user), HttpStatusCode.OK);

            // A rotation published to the shared JWKS is picked up by every region on first sight of the new kid. Before the
            // unpublished kid below: unknown kids refresh at most every 30 seconds per node.
            issuer.Rotate();
            await AssertEverywhereAsync(nodes, tls, issuer.Mint(user), HttpStatusCode.OK);

            var now = DateTime.UtcNow;
            // Past the 30-second clock skew.
            await AssertEverywhereAsync(nodes, tls, issuer.Mint(user, token =>
            {
                token.NotBefore = now.AddMinutes(-10);
                token.IssuedAt = now.AddMinutes(-10);
                token.Expires = now.AddMinutes(-1);
            }), HttpStatusCode.Unauthorized);
            // A region-specific issuer is another issuer: tokens carry the one global iss.
            await AssertEverywhereAsync(nodes, tls, issuer.Mint(user, token => token.Issuer = "https://gru.auth.test/terrascale"), HttpStatusCode.Unauthorized);
            // A key no replica publishes (one region's ring that never reached the shared JWKS).
            var unpublished = issuer.AddKey(SecurityAlgorithms.EcdsaSha256, publish: false);
            await AssertEverywhereAsync(nodes, tls, issuer.Mint(user, token => token.SigningCredentials = unpublished), HttpStatusCode.Unauthorized);
            // An ID token from the same issuer is not an access token.
            await AssertEverywhereAsync(nodes, tls, issuer.Mint(user, token => token.TokenType = "JWT"), HttpStatusCode.Unauthorized);
        }
        finally
        {
            foreach (var node in nodes)
            {
                await node.DisposeAsync();
            }

            tls.Dispose();
        }
    }

    private static async Task AssertEverywhereAsync(List<TerraBaseTestServer> nodes, ClientTls tls, string token, HttpStatusCode status)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            using var client = tls.CreateClient(nodes[i].ClientAddress!, token);
            using var response = await client.GetAsync(new Uri("/v1/management/organizations", UriKind.Relative));
            await Assert.That(response.StatusCode).IsEqualTo(status);
            await Assert.That(response.Headers.GetValues("X-TerraBase-Region")).IsEquivalentTo([Regions[i]]);
        }
    }
}
