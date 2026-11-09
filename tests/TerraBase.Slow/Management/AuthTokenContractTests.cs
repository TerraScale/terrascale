using System.Net;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

// TerraBase's side of the ts-auth token contract in every region (decision 0007).
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

            issuer.Rotate();
            await AssertEverywhereAsync(nodes, tls, issuer.Mint(user), HttpStatusCode.OK);

            var now = DateTime.UtcNow;
            await AssertEverywhereAsync(nodes, tls, issuer.Mint(user, token =>
            {
                token.NotBefore = now.AddMinutes(-10);
                token.IssuedAt = now.AddMinutes(-10);
                token.Expires = now.AddMinutes(-1);
            }), HttpStatusCode.Unauthorized);
            await AssertEverywhereAsync(nodes, tls, issuer.Mint(user, token => token.Issuer = "https://gru.auth.test/terrascale"), HttpStatusCode.Unauthorized);
            var unpublished = issuer.AddKey(SecurityAlgorithms.EcdsaSha256, publish: false);
            await AssertEverywhereAsync(nodes, tls, issuer.Mint(user, token => token.SigningCredentials = unpublished), HttpStatusCode.Unauthorized);
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
