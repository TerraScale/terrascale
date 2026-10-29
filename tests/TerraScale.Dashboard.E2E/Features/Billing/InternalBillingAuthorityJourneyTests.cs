using System.Net.Http.Json;
using System.Text.Json;
using TUnit.Core;

namespace TerraScale.Dashboard.E2E.Features.Billing
{
    [ClassDataSource<DashboardRootFixture>]
    [Category("Fast")]
    public sealed class InternalBillingAuthorityJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task CurrentPlatformAdministratorCannotConfigureWithOnlyStoredOrganizationMembership()
        {
            await using var scenario = await AuthProviderScenario.StartAsync(fixture.Root);
            using var producer = scenario.CreateProviderClient();
            using var browser = scenario.CreateClient();
            await InternalBillingAuthenticationJourneyTests.SignInAsync(browser, producer, "admin");
            using var seeded = await producer.PostAsJsonAsync("/__test/control", new { command = "internal-seed", count = 1, paymentMethodValidated = false });
            seeded.EnsureSuccessStatusCode();
            using var targets = JsonDocument.Parse(await seeded.Content.ReadAsStringAsync());
            var organization = targets.RootElement.GetProperty("targets")[0].GetProperty("organizationId").GetString()!;

            var result = await InternalBillingAuthenticationJourneyTests.RunApprovedAsync(scenario, producer,
                InternalBillingAuthenticationJourneyTests.CommandArguments("configure-internal-billing", organization), beforeDecision: async () =>
                {
                    using var removed = await producer.PostAsJsonAsync("/__test/control", new { command = "workspace-role", role = "" });
                    removed.EnsureSuccessStatusCode();
                });

            await Assert.That(result.ExitCode).IsEqualTo(1);
            await Assert.That(result.Error).IsEqualTo("configure-internal-billing: Workspace is unavailable.\n");
            // Browser sign-in and the device grant; membership fails before the privileged rotation.
            await Assert.That(result.Observed.GetProperty("issuedAdminRoleGrants").GetInt32()).IsEqualTo(2);
            var billing = result.Observed.GetProperty("billing");
            var stored = billing.GetProperty("organizations").EnumerateArray().Single(row => row.GetProperty("id").GetString() == organization).GetProperty("document");
            await Assert.That(stored.TryGetProperty("Members", out var members) &&
                (members.ValueKind != JsonValueKind.Object || members.EnumerateObject().Any())).IsFalse();
            await Assert.That(stored.GetProperty("InternalBilling").ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(billing.GetProperty("bindings").GetArrayLength()).IsEqualTo(0);
            await Assert.That(billing.GetProperty("singleton").ValueKind).IsEqualTo(JsonValueKind.Null);
            await Assert.That(billing.GetProperty("writes").EnumerateArray().Any(write =>
                write.GetProperty("collection").GetString() is "dashboard_billing" or "dashboard_internal_billing")).IsFalse();
            await Assert.That(billing.GetProperty("stripeRequests").GetArrayLength()).IsEqualTo(0);
            await Assert.That(result.Observed.GetProperty("contractFailures").GetInt32()).IsEqualTo(0);
            await Assert.That(result.Observed.GetProperty("managementMutations").GetInt32()).IsEqualTo(0);
        }
    }
}
