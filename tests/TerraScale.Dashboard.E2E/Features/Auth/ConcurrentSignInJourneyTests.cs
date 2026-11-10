using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace TerraScale.Dashboard.E2E.Features.Auth;

[ClassDataSource<SharedAuthProvider>(Shared = SharedType.PerTestSession)]
public sealed class ConcurrentSignInJourneyTests(SharedAuthProvider shared)
{
    [Test]
    public async Task ParallelSignInsOfOneAccountAllSucceedAndProvisionOnePersonalOrganization()
    {
        var scenario = shared.Scenario;
        var email = "parallel-" + Guid.NewGuid().ToString("N")[..12] + "@example.test";
        await SignInTogetherAsync(scenario, email, [false, false, false, true, true]);
        await SignInTogetherAsync(scenario, email, [false, false, false, false, true, true]);

        await using var storage = await scenario.StorageAsync();
        var accounts = await storage.Accounts.AsNoTracking().Where(row => row.Email == email).ToListAsync();
        await Assert.That(accounts.Count).IsEqualTo(1);
        var account = accounts[0];
        await Assert.That(account.PersonalClaimUntil).IsNull();
        await Assert.That(JsonSerializer.Deserialize<string[]>(account.OrganizationIds)).IsEquivalentTo([account.PersonalOrganizationId]);
        var personal = await storage.Organizations.AsNoTracking().SingleAsync(row => row.Id == account.PersonalOrganizationId);
        await Assert.That(personal.Kind).IsEqualTo("personal");
        var signIns = await storage.AuditEvents.AsNoTracking().CountAsync(row => row.OrganizationId == personal.Id && row.Action == "session.created");
        await Assert.That(signIns).IsEqualTo(11);
    }

    private static async Task SignInTogetherAsync(AuthProviderScenario scenario, string email, bool[] replicas)
    {
        var browsers = replicas.Select(replica => scenario.CreateClient(replica)).ToArray();
        try
        {
            var answers = await Task.WhenAll(browsers.Select(browser => HttpJourney.LoginAsync(browser, email, returnTo: "/databases", signUp: true)));
            foreach (var answer in answers)
            {
                using (answer)
                {
                    await Assert.That(answer.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                    await Assert.That(answer.Headers.Location!.OriginalString).IsEqualTo("/databases");
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
