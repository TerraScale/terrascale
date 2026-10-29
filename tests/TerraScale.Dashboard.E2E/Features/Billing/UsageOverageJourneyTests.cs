using System.Globalization;
using System.Net;
using System.Text.Json;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Billing
{
    [ClassDataSource<DashboardE2EFixture>]
    public sealed class UsageOverageJourneyTests(DashboardE2EFixture fixture)
    {
        private const long StarterAllowance = 250_000;

        [Test]
        public async Task StarterOverageBillsOnePendingSubscriptionItemAboveWhatIsInvoiced()
        {
            using var client = fixture.CreateClient();
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
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
        }

        [Test]
        public async Task FreeStopsAtItsAllowanceWithoutOverageItems()
        {
            using var client = fixture.CreateClient();
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            await Assert.That((await ReportAsync(client, 10_000_000)).Length).IsEqualTo(0);
            await Assert.That(await UsageAsync(client)).IsEqualTo("Free stops new writes at its 50,000 operation units a month; reads and deletes keep working. No usage charges.");
            await AssertSpendingAsync(client, "Free stops new writes at its included operation allowance; reads and deletes keep working. No usage charges.");
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

        private static async Task<JsonElement[]> ReportAsync(HttpClient client, long units, bool invoice = false)
        {
            var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=billing");
            using var response = await HttpJourney.PostFormAsync(client, "/__test/billing/overage", new()
            { ["_csrf"] = csrf, ["units"] = units.ToString(CultureInfo.InvariantCulture), ["invoice"] = invoice ? "1" : "" });
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
