using System.Net;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.E2E.Features.Accounts
{
    [Category("Slow")]
    [ClassDataSource<DashboardRootFixture>]
    public sealed class AccountEnrollmentJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        [Arguments("sessions")]
        [Arguments("auth_credentials")]
        [Arguments("session_indexes")]
        [Arguments(AccessTokens.IndexTable)]
        [Arguments(AccessTokens.Table)]
        [Arguments(AccountService.OrganizationsTable)]
        public async Task SubstitutedReferencesCannotEraseAnotherSubjectOrAnOrganization(string table)
        {
            await using var scenario = await AccountEnrollmentScenario.StartAsync(fixture.Root);
            using var other = await scenario.SignInAsync(scenario.Other);
            var secret = await HttpJourney.CreatePatAsync(other, password: AccountEnrollmentScenario.Password);
            var references = await scenario.ReferencesAsync(scenario.Other);
            var organization = await scenario.OrganizationAsync(scenario.Other);
            using var control = scenario.Client();
            using (var substitute = await control.PostAsync("/__test/enrollment/substitute?table=" + table, null))
            {
                await Assert.That(substitute.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }

            using var replay = await control.PostAsync("/__test/enrollment/replay", null);
            await Assert.That((int)replay.StatusCode).IsGreaterThanOrEqualTo(500);
            foreach (var reference in references)
            {
                await Assert.That(await scenario.GetAsync(reference.Table, reference.RecordId)).IsNotNull();
            }

            await Assert.That((await scenario.GetAsync(AccountService.OrganizationsTable, organization.Id))!.Body.GetRawText())
                .IsEqualTo(organization.Body.GetRawText());
            using var retained = await other.GetAsync("/api/v1/auth/me");
            await Assert.That(retained.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var profile = await HttpJourney.McpAsync(other, secret, "tools/call", new { name = "get_profile", arguments = new { } });
            await Assert.That(profile.GetProperty("result").GetProperty("structuredContent").GetProperty("userId").GetString()).IsEqualTo(scenario.Other);
        }

    }
}
