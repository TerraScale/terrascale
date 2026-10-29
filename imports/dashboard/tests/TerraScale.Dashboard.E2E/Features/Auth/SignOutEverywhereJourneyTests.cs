using System.Net;

namespace TerraScale.Dashboard.E2E.Features.Auth;

public sealed class SignOutEverywhereJourneyTests
{
    [Test]
    public async Task SignOutEverywhereNeedsCsrfAndEndsEveryLocalSessionOfTheUser()
    {
        await using var fixture = new DashboardE2EFixture();
        await fixture.InitializeAsync();
        using var first = fixture.CreateClient();
        using var second = fixture.CreateClient();
        using var firstLogin = await HttpJourney.LoginAsync(first);
        using var secondLogin = await HttpJourney.LoginAsync(second);
        var profile = await first.GetStringAsync("/profile?tab=sessions");
        await Assert.That(profile).Contains("Sign out everywhere?");

        using (var rejected = await HttpJourney.PostFormAsync(first, "/api/v1/dashboard/profile/sign-out-everywhere", []))
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using (var stillSignedIn = await first.GetAsync("/api/v1/auth/me"))
            await Assert.That(stillSignedIn.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var csrf = await HttpJourney.CsrfAsync(first, "/profile");
        using (var signedOut = await HttpJourney.PostFormAsync(first, "/api/v1/dashboard/profile/sign-out-everywhere", new() { ["_csrf"] = csrf }))
        {
            await Assert.That(signedOut.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(signedOut.Headers.Location!.OriginalString).IsEqualTo("/login?returnTo=%2Fprofile");
        }

        foreach (var client in new[] { first, second })
        {
            using var me = await client.GetAsync("/api/v1/auth/me");
            await Assert.That(me.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
    }
}
