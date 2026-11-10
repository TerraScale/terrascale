using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E.Features.Billing
{
    [ClassDataSource<DashboardE2EFixture>]
    public sealed class StripeBindingRefreshJourneyTests(DashboardE2EFixture fixture)
    {
        [Test]
        public async Task NightlyRunRefreshesTheStripeBindingOnceAndRecordsTheRepair()
        {
            using var client = fixture.CreateClient();
            await DatabaseJourney.NativeWorkspaceAsync(client);
            var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=billing");
            using var checkout = await HttpJourney.PostFormAsync(client, "/billing/checkout", new()
            { ["_csrf"] = csrf, ["plan"] = "starter", ["interval"] = "monthly", ["return"] = "/organization?tab=billing" });
            using var completed = await client.GetAsync(checkout.Headers.Location);
            using var callback = await client.GetAsync(completed.Headers.Location);
            await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            using var me = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
            var organizationId = me.RootElement.GetProperty("organizationId").GetString()!;

            var storage = fixture.Service<IDbContextFactory<DashboardDbContext>>();
            long revision;
            await using (var db = await storage.CreateDbContextAsync())
            {
                var binding = await db.Billing.SingleAsync(row => row.OrganizationId == organizationId);
                revision = binding.Revision;
                binding.Status = "past_due";
                binding.EffectivePlan = "";
                await db.SaveChangesAsync();
            }

            await fixture.Service<StripeBindingRefresh>().RunOnceAsync(CancellationToken.None);

            await using var read = await storage.CreateDbContextAsync();
            var repaired = await read.Billing.AsNoTracking().SingleAsync(row => row.OrganizationId == organizationId);
            await Assert.That(repaired.Status).IsEqualTo("active");
            await Assert.That(repaired.EffectivePlan).IsEqualTo("starter");
            await Assert.That(repaired.Revision).IsEqualTo(revision + 1);
        }
    }
}
