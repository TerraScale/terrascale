using System.Net;

namespace TerraScale.Dashboard.E2E.Features.Auth
{
    public sealed class SessionDeviceJourneyTests
    {
        [Test]
        public async Task SessionsTabShowsTheDeviceEachSessionWasCreatedOn()
        {
            await using var fixture = new DashboardE2EFixture();
            await fixture.InitializeAsync();
            using var firefox = fixture.CreateClient();
            firefox.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0");
            using var login = await HttpJourney.LoginAsync(firefox);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);

            var profile = await firefox.GetStringAsync("/profile?tab=sessions");
            await Assert.That(profile).Contains("Firefox on Linux");
            await Assert.That(profile).Contains("This device");
        }
    }
}
