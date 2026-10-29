using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.E2E.Features.Auth;

[ClassDataSource<DashboardRootFixture>]
public sealed class PersonalAccessTokenIdentityJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task PersonalTokenUsesCanonicalIdentityForListingAndImmediateRevocation()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        using var client = scenario.CreateClient();
        using var login = await HttpJourney.LoginAsync(client);
        var name = "PAT identity " + Guid.NewGuid().ToString("N");
        var secret = await HttpJourney.CreatePatAsync(client, name);
        var html = await client.GetStringAsync("/profile?tab=danger");
        var row = Regex.Matches(html, "<tr\\b[^>]*>.*?</tr>", RegexOptions.Singleline)
            .Single(match => match.Value.Contains(name, StringComparison.Ordinal)).Value;
        var action = Regex.Match(row, "action=\"(/api/v1/dashboard/profile/access-tokens/([^\"/]+)/revoke)\"");
        var path = action.Groups[1].Value;
        await Assert.That(path.Length > 0).IsTrue();
        try
        {
            await Assert.That(TerraScaleIds.IsValid(action.Groups[2].Value)).IsTrue()
                .Because("The created token is listed and revoked by its canonical ULID record identity.");
            await Assert.That(Regex.IsMatch(secret, "^tspat_[A-Za-z0-9_-]{43}$")).IsTrue()
                .Because("The bearer secret retains its independent cryptographic format.");
            using var before = await ListToolsAsync();
            await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        finally
        {
            using var revoke = await HttpJourney.PostFormAsync(client, path,
                new() { ["_csrf"] = await HttpJourney.CsrfAsync(client, "/profile?tab=danger") });
            await Assert.That(revoke.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        }
        using var after = await ListToolsAsync();
        await Assert.That(after.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That((await client.GetStringAsync("/profile?tab=danger")).Contains(name, StringComparison.Ordinal)).IsFalse();

        async Task<HttpResponseMessage> ListToolsAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}", Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            return await client.SendAsync(request);
        }
    }
}
