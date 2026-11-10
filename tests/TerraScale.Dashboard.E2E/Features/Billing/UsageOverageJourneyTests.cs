using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TerraScale.Dashboard.E2E.Demo;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E.Features.Billing
{
    [ClassDataSource<DashboardE2EFixture>(Shared = SharedType.PerTestSession)]
    public sealed class UsageOverageJourneyTests(DashboardE2EFixture fixture)
    {
        private const long StarterAllowance = 250_000;
        private const long StarterStorageBytes = 1_000_000_000;

        [Test]
        public async Task StarterOverageBillsOnePendingSubscriptionItemAboveWhatIsInvoiced()
        {
            using var client = fixture.CreateClient();
            await DatabaseJourney.NativeWorkspaceAsync(client);
            var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=billing");
            using var checkout = await HttpJourney.PostFormAsync(client, "/billing/checkout", new()
            { ["_csrf"] = csrf, ["plan"] = "starter", ["interval"] = "annual", ["return"] = "/organization?tab=billing" });
            using var completed = await client.GetAsync(checkout.Headers.Location);
            using var callback = await client.GetAsync(completed.Headers.Location);
            await Assert.That(callback.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await AssertSpendingAsync(client, "Operation units above Starter's included allowance are billed at $5.00 per million operation units.");
            using var me = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
            var organizationId = me.RootElement.GetProperty("organizationId").GetString()!;

            await Assert.That((await ReportAsync(client, StarterAllowance)).Length).IsEqualTo(0).Because("Usage within the allowance is not billed.");
            await Assert.That(await UsageAsync(client)).IsEqualTo("Usage this period: $0.00 so far, within Starter's 250,000 included operation units. Units above them cost $5.00 per million.");

            var created = (await ReportAsync(client, StarterAllowance + 123_456)).Single();
            await Assert.That(created.GetProperty("quantity").GetInt64()).IsEqualTo(123_456);
            await Assert.That(decimal.Parse(created.GetProperty("unitAmountDecimal").GetString()!, CultureInfo.InvariantCulture)).IsEqualTo(0.0005m);
            await Assert.That(created.GetProperty("subscription").GetString()).StartsWith("sub_");
            await Assert.That(created.GetProperty("pendingInvoiceItemInterval").GetString()).IsEqualTo("month").Because("Starter is billed yearly; its overage is invoiced monthly.");
            var metadata = created.GetProperty("metadata");
            await Assert.That(metadata.GetProperty("terrascale_kind").GetString()).IsEqualTo("operation_unit_overage");
            await Assert.That(metadata.GetProperty("terrascale_month").GetString()).IsEqualTo(DateTimeOffset.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture));
            await Assert.That(metadata.GetProperty("terrascale_org").GetString()).IsEqualTo(organizationId);
            await Assert.That(await UsageAsync(client)).StartsWith("Usage this period: $0.62 so far, for 123,456 operation units over Starter's 250,000 included units");

            var updated = (await ReportAsync(client, StarterAllowance + 2_000_000)).Single();
            await Assert.That(updated.GetProperty("id").GetString()).IsEqualTo(created.GetProperty("id").GetString()).Because("A later run updates the month's pending item.");
            await Assert.That(updated.GetProperty("quantity").GetInt64()).IsEqualTo(2_000_000);

            // Once Stripe invoices the item, only units above it go onto a new pending item.
            var items = await ReportAsync(client, StarterAllowance + 2_500_000, invoice: true);
            await Assert.That(items.Length).IsEqualTo(2);
            await Assert.That(items.Single(item => item.GetProperty("invoice").ValueKind == JsonValueKind.Null).GetProperty("quantity").GetInt64()).IsEqualTo(500_000);
            await Assert.That(await UsageAsync(client)).StartsWith("Usage this period: $12.50 so far, for 2,500,000 operation units");

            var now = DateTimeOffset.UtcNow;
            var month = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            var hours = (long)(month.AddMonths(1) - month).TotalHours;
            var storage = (await ReportAsync(client, StarterAllowance + 2_500_000, storageByteHours: (StarterStorageBytes + 2_000_000_000) * hours))
                .Single(item => item.GetProperty("metadata").GetProperty("terrascale_kind").GetString() == "storage_overage");
            await Assert.That(storage.GetProperty("quantity").GetInt64()).IsEqualTo(2_000).Because("2 GB over the allowance for the whole month is 2,000 MB-months.");
            await Assert.That(decimal.Parse(storage.GetProperty("unitAmountDecimal").GetString()!, CultureInfo.InvariantCulture)).IsEqualTo(0.05m);
            var again = await ReportAsync(client, StarterAllowance + 2_500_000, storageByteHours: (StarterStorageBytes + 2_000_000_000) * hours);
            await Assert.That(again.Count(item => item.GetProperty("metadata").GetProperty("terrascale_kind").GetString() == "storage_overage")).IsEqualTo(1)
                .Because("reporting the same month again never charges twice");
        }

        [Test]
        public async Task FreeStopsAtItsAllowanceWithoutOverageItems()
        {
            using var client = fixture.CreateClient();
            await DatabaseJourney.NativeWorkspaceAsync(client);
            await Assert.That((await ReportAsync(client, 10_000_000)).Length).IsEqualTo(0);
            await Assert.That(await UsageAsync(client)).IsEqualTo("Free stops new writes at its 50,000 operation units a month; reads keep working. No usage charges.");
            await AssertSpendingAsync(client, "Free stops new writes at its included operation allowance; reads keep working. No usage charges.");
        }

        [Test]
        public async Task StripeBilledOrganizationSavesReportOnlySpendAlertAndRecordsTheNoticeOnce()
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

            var settings = WebUtility.HtmlDecode(await client.GetStringAsync("/usage?tab=settings"));
            await Assert.That(settings).Contains("Monthly alert (USD)").Because("A Stripe-billed organization sees the spending alert form.");
            await Assert.That(settings).Contains("Customer hard spending caps are unavailable.");
            var capCsrf = await HttpJourney.CsrfAsync(client, "/usage?tab=settings");
            using var saved = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/usage/spending-cap", new()
            { ["_csrf"] = capCsrf, ["spendCap"] = "1.00" });
            await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(saved.Headers.Location!.OriginalString).IsEqualTo("/usage?tab=settings&saved=ok");

            var backend = (TestAllowanceNotifications)fixture.Service<IAllowanceNotificationBackend>();
            var notifications = fixture.Service<AllowanceNotifications>();
            backend.Observe(new AllowanceUsageObservation(organizationId, StarterAllowance + 300_000, null));
            await notifications.RunOnceAsync(CancellationToken.None);
            backend.Observe(new AllowanceUsageObservation(organizationId, StarterAllowance + 2_000_000, null));
            await notifications.RunOnceAsync(CancellationToken.None);
            var spendAlerts = await SpendAlertsAsync(organizationId);
            await Assert.That(spendAlerts.Length).IsEqualTo(1).Because("Crossing the threshold repeatedly records the notice once per month.");
            await Assert.That(spendAlerts[0].Used).IsEqualTo(150L).Because("300,000 operation units over Starter's allowance are $1.50 at $5.00 per million.");
            await Assert.That(spendAlerts[0].Allowance).IsEqualTo(100L);

            var raiseCsrf = await HttpJourney.CsrfAsync(client, "/usage?tab=settings");
            using var raised = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/usage/spending-cap", new()
            { ["_csrf"] = raiseCsrf, ["spendCap"] = "2.00" });
            await Assert.That(raised.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            backend.Observe(new AllowanceUsageObservation(organizationId, StarterAllowance + 500_000, null));
            await notifications.RunOnceAsync(CancellationToken.None);
            await notifications.RunOnceAsync(CancellationToken.None);
            spendAlerts = await SpendAlertsAsync(organizationId);
            await Assert.That(spendAlerts.Length).IsEqualTo(1).Because("A raised cap re-arms the alert and the higher crossing is recorded once.");
            await Assert.That(spendAlerts[0].Used).IsEqualTo(250L);
            await Assert.That(spendAlerts[0].Allowance).IsEqualTo(200L);

            var inbox = WebUtility.HtmlDecode(await client.GetStringAsync("/notifications"));
            await Assert.That(inbox).Contains("Monthly spending alert reached");
            await Assert.That(inbox).Contains("$1.50 reached the $1.00 spending alert");
            await Assert.That(inbox).Contains("$2.50 reached the $2.00 spending alert");
        }

        private async Task<AllowanceAlertRow[]> SpendAlertsAsync(string organizationId)
        {
            await using var db = await fixture.Service<IDbContextFactory<DashboardDbContext>>().CreateDbContextAsync();
            return await db.AllowanceAlerts.AsNoTracking().Where(alert => alert.OrganizationId == organizationId && alert.Metric == "spend").ToArrayAsync();
        }

        private static async Task AssertSpendingAsync(HttpClient client, string disclosure)
        {
            var html = WebUtility.HtmlDecode(await client.GetStringAsync("/usage?tab=settings"));
            await Assert.That(html).Contains(disclosure);
            await Assert.That(html).Contains("$5.00 per million operation units");
            await Assert.That(html).Contains("Customer hard spending caps are unavailable.");
            await Assert.That(html).Contains("href=\"/organization?tab=billing\">Manage billing and upgrade</a>");
            await Assert.That(html.Contains("paid overages are not supported", StringComparison.OrdinalIgnoreCase)).IsFalse();
        }

        private static async Task<JsonElement[]> ReportAsync(HttpClient client, long units, bool invoice = false, long? storageByteHours = null)
        {
            var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=billing");
            using var response = await HttpJourney.PostFormAsync(client, "/__test/billing/overage", new()
            {
                ["_csrf"] = csrf,
                ["units"] = units.ToString(CultureInfo.InvariantCulture),
                ["invoice"] = invoice ? "1" : "",
                ["storageByteHours"] = storageByteHours?.ToString(CultureInfo.InvariantCulture) ?? ""
            });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
        }

        private static async Task<string> UsageAsync(HttpClient client)
        {
            var html = await client.GetStringAsync("/organization?tab=billing");
            const string marker = "id=\"billing-usage\">";
            var start = html.IndexOf(marker, StringComparison.Ordinal);
            await Assert.That(start).IsGreaterThan(-1).Because("The billing page shows usage this period.");
            start += marker.Length;
            return WebUtility.HtmlDecode(html[start..html.IndexOf("</p>", start, StringComparison.Ordinal)]);
        }
    }
}
