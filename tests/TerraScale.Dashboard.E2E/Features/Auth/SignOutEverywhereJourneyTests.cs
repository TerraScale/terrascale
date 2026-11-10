using System.Net;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Auth
{
    [ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
    public sealed class SignOutEverywhereJourneyTests(DashboardE2EFixture fixture)
    {
        [Test]
        public async Task SignOutEverywhereNeedsCsrfAndEndsEveryLocalSessionOfTheUser()
        {
            using var first = fixture.CreateClient();
            using var second = fixture.CreateClient();
            var account = await DatabaseJourney.NativeWorkspaceAsync(first);
            using var secondLogin = await HttpJourney.LoginAsync(second, account.Email, account.Password);
            await Assert.That(secondLogin.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var profile = await first.GetStringAsync("/profile?tab=sessions");
            await Assert.That(profile).Contains("Sign out everywhere?");

            using (var rejected = await HttpJourney.PostFormAsync(first, "/api/v1/dashboard/profile/sign-out-everywhere", []))
            {
                await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            }

            using (var stillSignedIn = await first.GetAsync("/api/v1/auth/me"))
            {
                await Assert.That(stillSignedIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

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
}
