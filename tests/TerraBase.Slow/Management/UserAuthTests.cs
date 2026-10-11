using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.IdentityModel.JsonWebTokens;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Management;

/// <summary>ts-auth access tokens against a real clock and the qualified ts-auth executable.</summary>
public sealed class UserAuthTests
{
    [Test]
    public async Task A_server_started_while_ts_auth_was_down_is_ready_and_accepts_tokens_once_it_answers()
    {
        using var tls = new ClientTls();
        using var issuer = new TestIssuer { Available = false };
        await using var server = await TerraBaseTestServer.StartAsync(
            settings =>
            {
                tls.Apply(settings);
                TestIssuer.Configure(settings);
            },
            issuer.Route);
        using var client = tls.CreateClient(server.ClientAddress!, issuer.Mint(EntityIds.New().ToString()));
        var rejected = await StatusAsync(client, "/v1/management/organizations");
        var ready = await StatusAsync(server.Client, "/readyz");

        issuer.Available = true;
        // The background fetch retries every 2 s: wait for it by retrying the request. The deadline bounds the delays and
        // every request, so nothing outlives it.
        var accepted = HttpStatusCode.Unauthorized;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (accepted != HttpStatusCode.OK)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
                accepted = await StatusAsync(client, "/v1/management/organizations", deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Still rejected: the assertion below reports it.
        }

        await Assert.That(rejected).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(ready).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(accepted).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Access_tokens_from_the_real_ts_auth_binary_authenticate()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var auth = await RealAuthFixture.StartAsync(token);
        var accessToken = await auth.AccessTokenAsync(token);
        var jwt = new JsonWebToken(accessToken);
        await Assert.That(jwt.Issuer).IsEqualTo(auth.Issuer);
        await Assert.That(jwt.Audiences).Contains(RealAuthFixture.Audience);
        await Assert.That(jwt.Alg).IsEqualTo("ES256");
        await Assert.That(jwt.Typ).IsEqualTo("at+jwt");
        await Assert.That(jwt.Subject).IsEqualTo(auth.Subject);

        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            tls.Apply(settings);
            auth.Configure(settings);
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
