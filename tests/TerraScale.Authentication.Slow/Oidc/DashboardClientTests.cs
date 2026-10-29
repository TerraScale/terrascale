using System.Net;
using System.Security.Cryptography;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Slow.Oidc;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class DashboardClientTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task Replicas_register_one_client_without_its_secret_and_a_restart_applies_new_redirect_uris()
    {
        var (tenant, database) = await fixture.CreateDatabaseAsync();
        var email = new CapturingEmailSender();
        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        void Shared(Dictionary<string, string> values) => values["master-key"] = masterKey;
        var started = await Task.WhenAll(OidcHarness.StartServerAsync(fixture, tenant, email, Shared), OidcHarness.StartServerAsync(fixture, tenant, email, Shared));
        await using var first = started[0];
        await using var second = started[1];

        var stored = (await database.ListDocumentsAsync(AuthStore.OidcApplicationCollection)).Items.Single(item => item.Document.GetProperty("clientId").GetString() == OidcIssuer.DashboardClientId).Document;
        await Assert.That(stored.GetProperty("clientId").GetString()).IsEqualTo(OidcIssuer.DashboardClientId);
        await Assert.That(stored.GetProperty("redirectUris").EnumerateArray().Select(uri => uri.GetString()!).ToArray()).IsEquivalentTo([OidcHarness.RedirectUri, OidcHarness.LoopbackRedirectUri]);
        await Assert.That(stored.GetProperty("clientSecret").GetString()).IsNotEmpty();
        await Assert.That(stored.GetRawText()).DoesNotContain(OidcHarness.ClientSecret);

        await using var restarted = await OidcHarness.StartServerAsync(fixture, tenant, email, values =>
        {
            Shared(values);
            values["dashboard-redirect-uris"] = "https://dash.example.test/new/callback";
        });
        var updated = (await database.ListDocumentsAsync(AuthStore.OidcApplicationCollection)).Items.Single(item => item.Document.GetProperty("clientId").GetString() == OidcIssuer.DashboardClientId).Document;
        await Assert.That(updated.GetProperty("redirectUris").EnumerateArray().Select(uri => uri.GetString()!).ToArray()).IsEquivalentTo(["https://dash.example.test/new/callback"]);
        await Assert.That(updated.GetRawText()).DoesNotContain(OidcHarness.ClientSecret);

        // Every replica reads the client from storage, so a removed redirect URI stops working everywhere.
        var (_, challenge) = OidcHarness.Pkce();
        foreach (var server in new[] { restarted, first })
        {
            using var removed = await server.Client.GetAsync(OidcHarness.AuthorizePath(challenge));
            await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            using var added = await server.Client.GetAsync(OidcHarness.AuthorizePath(challenge, redirectUri: "https://dash.example.test/new/callback"));
            await Assert.That(added.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(added.Headers.Location!.OriginalString).StartsWith($"/ui/{OidcIssuer.Slug}/sign-in?next=");
        }
    }
}
