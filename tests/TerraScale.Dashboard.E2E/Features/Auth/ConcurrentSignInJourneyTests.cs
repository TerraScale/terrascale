using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace TerraScale.Dashboard.E2E.Features.Auth;

[ClassDataSource<DashboardRootFixture>]
public sealed class ConcurrentSignInJourneyTests(DashboardRootFixture fixture)
{
    private const string Email = "parallel-sign-in@example.com";

    [Test]
    public async Task ParallelSignInsOfOneAccountAllSucceedAndProvisionOnePersonalOrganization()
    {
        await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
        int personalBefore;
        await using (var storage = await scenario.StorageAsync())
        {
            personalBefore = await storage.Organizations.AsNoTracking().CountAsync(row => row.Kind == "personal");
        }

        await SignInTogetherAsync(scenario, [false, false, false, true, true]);
        await SignInTogetherAsync(scenario, [false, false, false, false, true, true]);

        await using (var storage = await scenario.StorageAsync())
        {
            var accounts = await storage.Accounts.AsNoTracking().Where(row => row.Email == Email).ToListAsync();
            await Assert.That(accounts.Count).IsEqualTo(1);
            var account = accounts[0];
            await Assert.That(account.PersonalOrganizationId.Length).IsGreaterThan(0);
            await Assert.That(account.PersonalClaimUntil).IsNull();
            await Assert.That(JsonSerializer.Deserialize<string[]>(account.OrganizationIds)).IsEquivalentTo([account.PersonalOrganizationId]);
            await Assert.That(await storage.Organizations.AsNoTracking().CountAsync(row => row.Kind == "personal")).IsEqualTo(personalBefore + 1);
            var signIns = await storage.AuditEvents.AsNoTracking()
                .CountAsync(row => row.OrganizationId == account.PersonalOrganizationId && row.Action == "session.created");
            await Assert.That(signIns).IsEqualTo(11);
        }
    }

    private static async Task SignInTogetherAsync(AuthProviderScenario scenario, bool[] replicas)
    {
        var browsers = replicas.Select(replica => scenario.CreateClient(replica)).ToArray();
        try
        {
            var answers = await Task.WhenAll(browsers.Select(browser => HttpJourney.LoginAsync(browser, Email, returnTo: "/api/v1/auth/me", signUp: true)));
            foreach (var answer in answers)
            {
                using (answer)
                {
                    await Assert.That(answer.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                    await Assert.That(answer.Headers.Location!.OriginalString).IsEqualTo("/api/v1/auth/me");
                }
            }

            foreach (var browser in browsers)
            {
                using var me = await browser.GetAsync("/api/v1/auth/me");
                await Assert.That(me.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }
        }
        finally
        {
            foreach (var browser in browsers)
            {
                browser.Dispose();
            }
        }
    }
}
