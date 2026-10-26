using System.Net;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E;

[ClassDataSource<DashboardE2EFixture>]
public sealed class HostedSecurityReturnJourneyTests(DashboardE2EFixture fixture)
{
    [Test]
    public async Task HostedSecurityLinksCarryTheExactFirstPartyAccountReturn()
    {
        using var client = fixture.CreateClient();
        using var login = await HttpJourney.LoginAsync(client);
        var html = await client.GetStringAsync("/profile?tab=security");
        var next = Uri.EscapeDataString("https://dash.terrascale.tech/profile?tab=security");
        await Assert.That(html).Contains("https://auth.terrascale.tech/ui/terrascale/reset?next=" + next);
        await Assert.That(html).Contains("https://auth.terrascale.tech/ui/terrascale/security?next=" + next);
    }

    [Test]
    public async Task Password_reset_revocation_requires_normal_login_and_never_reuses_the_invalidated_dashboard_session()
    {
        const string Target = "/profile?tab=security";
        const string NewPassword = "new synthetic hosted reset password";
        using var client = fixture.CreateClient();
        using var login = await HttpJourney.LoginAsync(client, returnTo: Target);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var oldCookie = login.Headers.GetValues("Set-Cookie").Single(cookie => cookie.StartsWith("ts-dashboard-session=", StringComparison.Ordinal)).Split(';')[0];
        using var before = await client.GetAsync(Target);
        await Assert.That(before.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var state = fixture.Service<IDashboardState>();
        var stored = (await state.ListAsync("sessions", CancellationToken.None)).Single();
        // The fixture's ts-auth boundary models reset revocation; the dashboard keeps its real stored session.
        ((TestAuth)fixture.Service<IAuthGateway>()).ChangePassword("user@example.com", NewPassword);
        using var rejected = await client.GetAsync(Target);
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(rejected.Headers.Location!.OriginalString).IsEqualTo("/login?returnTo=" + Uri.EscapeDataString(Target));
        await Assert.That(rejected.Headers.GetValues("Set-Cookie").Any(cookie => cookie.StartsWith("ts-dashboard-session=;", StringComparison.Ordinal))).IsTrue();
        await Assert.That(await state.GetAsync("sessions", stored.Id, CancellationToken.None)).IsNull();
        var authorize = await client.GetAsync(rejected.Headers.Location);
        await Assert.That(authorize.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(authorize.Headers.Location!.OriginalString).StartsWith("/__test/oidc?state=");
        using var renewed = await HttpJourney.HostedSignInAsync(client, authorize, password: NewPassword);
        await Assert.That(renewed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(renewed.Headers.Location!.OriginalString).IsEqualTo(Target);
        var newCookie = renewed.Headers.GetValues("Set-Cookie").Single(cookie => cookie.StartsWith("ts-dashboard-session=", StringComparison.Ordinal)).Split(';')[0];
        await Assert.That(newCookie).IsNotEqualTo(oldCookie);
        using var returned = await client.GetAsync(renewed.Headers.Location);
        await Assert.That(returned.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await returned.Content.ReadAsStringAsync()).Contains("tab=security");
        using var replayClient = fixture.CreateClient();
        using var replay = new HttpRequestMessage(HttpMethod.Get, Target);
        replay.Headers.Add("Cookie", oldCookie);
        using var refused = await replayClient.SendAsync(replay);
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(refused.Headers.Location!.OriginalString).IsEqualTo("/login?returnTo=" + Uri.EscapeDataString(Target));
    }
}
