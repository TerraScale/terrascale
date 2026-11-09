using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.IdentityModel.JsonWebTokens;
using TerraBase.Testing;

namespace TerraBase.Slow.Management;

public sealed class UserAuthTests
{
    [Test]
    public async Task Access_tokens_from_the_real_ts_auth_authenticate_on_the_management_api()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var auth = await RealAuthFixture.StartAsync(token);
        var accessToken = await auth.AccessTokenAsync(token);
        var jwt = new JsonWebToken(accessToken);
        await Assert.That(jwt.Issuer).IsEqualTo(RealAuthFixture.Issuer);
        await Assert.That(jwt.Audiences).Contains(RealAuthFixture.Audience);
        await Assert.That(jwt.Alg).IsEqualTo("ES256");
        await Assert.That(jwt.Typ).IsEqualTo("at+jwt");
        await Assert.That(jwt.Subject).IsEqualTo(auth.Subject);

        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            RealAuthFixture.Configure(settings);
        }, auth.TrustIssuer);
        using var client = tls.CreateClient(server.ClientAddress!, accessToken);
        var organizations = new Uri("/v1/management/organizations", UriKind.Relative);
        using var created = await client.PostAsJsonAsync(organizations, new { name = new Faker().Company.CompanyName() }, token);
        await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var organization = await created.Content.ReadFromJsonAsync<JsonElement>(token);
        await Assert.That(organization.GetProperty("createdBy").GetString()).IsEqualTo(auth.Subject);
        using var listed = await client.GetAsync(organizations, token);
        await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var items = await listed.Content.ReadFromJsonAsync<JsonElement>(token);
        await Assert.That(items.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()))
            .IsEquivalentTo([organization.GetProperty("id").GetString()]);
        using var anonymous = tls.CreateClient(server.ClientAddress!);
        await Assert.That(await StatusAsync(anonymous, organizations.OriginalString, token)).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string path, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
        return response.StatusCode;
    }
}
