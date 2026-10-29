using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.Features.Billing.Invoices;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.E2E.Features.Billing
{
    public sealed class InvoiceSummaryContractTests
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 23, 18, 0, 0, TimeSpan.Zero);

        [Test]
        public async Task ActiveAnnualSubscriptionShowsTheBaseRenewalFeeAndCurrentPeriod()
        {
            var paid = new BillingInvoice("in_1", "7LHJXQY7-0001", "paid", 3000, "usd", Now.AddHours(-1), Now.AddHours(-1), Now.AddYears(1).AddHours(-1));
            var summary = InvoiceHistory.Summarize([paid], Now, new(Now.AddYears(1).AddHours(-1), 3000, "usd", "annual"));
            await Assert.That(summary.NextInvoice).IsEqualTo("$30.00 base fee");
            await Assert.That(summary.NextDetail).IsEqualTo("Base subscription at renewal on Oct 23, 2027. Usage, credits, discounts and taxes may change the final invoice.");
            await Assert.That(summary.Period).IsEqualTo("Oct 23, 2026 – Oct 23, 2027");
            await Assert.That(summary.HasPeriod).IsTrue();
            await Assert.That(summary.OutstandingDetail).IsEqualTo("0 open invoices in recorded history");
        }

        [Test]
        public async Task RecordedInvoiceTotalOverridesBaseEstimateWithoutConfusingRemainingBalance()
        {
            var renewal = new InvoiceRenewal(Now.AddDays(20), 2000, "usd", "monthly");
            var outstanding = new BillingInvoice("in_old", "OLD-1", "open", 500, "usd", Now.AddDays(-1), null, null) { RemainingCents = 250 };
            var estimate = InvoiceHistory.Summarize([outstanding], Now, renewal);
            await Assert.That(estimate.NextInvoice).IsEqualTo("$20.00 base fee");
            await Assert.That(estimate.Outstanding).IsEqualTo("$2.50");
            var adjusted = new BillingInvoice("in_next", "NEXT-1", "open", 2750, "usd", Now.AddDays(2), null, null) { RemainingCents = 1000 };
            var recorded = InvoiceHistory.Summarize([outstanding, adjusted], Now, renewal);
            await Assert.That(recorded.NextInvoice).IsEqualTo("$27.50");
            await Assert.That(recorded.NextDetail).IsEqualTo("Recorded open invoice dated Oct 25, 2026");
            await Assert.That(recorded.Outstanding).IsEqualTo("$12.50");
            await Assert.That(recorded.OutstandingDetail).IsEqualTo("2 open invoices in recorded history");
            await Assert.That(recorded.Period).IsEqualTo(estimate.Period);
        }

        [Test]
        [Arguments(0, 0)]
        [Arguments(5, 16)]
        public async Task CurrentMonthlySubscriptionOverridesTheAnnualCreditAndMonthlyDebitInvoiceUnion(int elapsedDays, int progress)
        {
            var start = new DateTimeOffset(2026, 10, 26, 3, 16, 0, TimeSpan.Zero);
            // The invoice table retains the union of its annual credit and monthly debit line periods.
            var proration = new BillingInvoice("in_proration", "PRORATION-1", "paid", 0, "usd", start, start.AddDays(-1), start.AddYears(1));
            var summary = InvoiceHistory.Summarize([proration], start.AddDays(elapsedDays), new(start.AddMonths(1), 2000, "usd", "monthly", PeriodStart: start));
            await Assert.That(summary.Period).IsEqualTo("Oct 26 – Nov 26, 2026");
            await Assert.That(summary.PeriodDetail).IsEqualTo($"Day {elapsedDays} of 31 · based on the current subscription period");
            await Assert.That(summary.PeriodPercent).IsEqualTo(progress);
            await Assert.That(summary.NextInvoice).IsEqualTo("$20.00 base fee");
            await Assert.That(InvoiceHistory.Page([proration], "", "", 1, start.AddDays(5)).Invoices.Single().PeriodEnd).IsEqualTo(start.AddYears(1));
        }

        [Test]
        public async Task MonthEndSubscriptionRetainsJanuary31RatherThanApproximatingJanuary28()
        {
            var start = new DateTimeOffset(2026, 1, 31, 3, 16, 0, TimeSpan.Zero);
            var summary = InvoiceHistory.Summarize([], start.AddDays(10), new(start.AddMonths(1), 2000, "usd", "monthly", PeriodStart: start));
            await Assert.That(summary.Period).IsEqualTo("Jan 31 – Feb 28, 2026");
            await Assert.That(summary.PeriodDetail).IsEqualTo("Day 10 of 28 · based on the current subscription period");
            await Assert.That(summary.PeriodPercent).IsEqualTo(36);
        }

        [Test]
        [Arguments(null, 20)]
        [Arguments(1, 20)]
        [Arguments(21, 20)]
        [Arguments(-5, 0)]
        [Arguments(-5, -1)]
        public async Task MissingOrInvalidSubscriptionPeriodKeepsTheRecordedInvoiceFallback(int? startDays, int endDays)
        {
            var paid = new BillingInvoice("in_existing", "EXISTING-1", "paid", 3000, "usd", Now.AddDays(-1), Now.AddDays(-1), Now.AddYears(1));
            var summary = InvoiceHistory.Summarize([paid], Now, new(Now.AddDays(endDays), 2000, "usd", "monthly", PeriodStart: startDays is { } days ? Now.AddDays(days) : null));
            var recorded = InvoiceHistory.Summarize([paid], Now);
            await Assert.That(summary.Period).IsEqualTo(recorded.Period);
            await Assert.That(summary.PeriodDetail).IsEqualTo(recorded.PeriodDetail);
            await Assert.That(summary.PeriodPercent).IsEqualTo(recorded.PeriodPercent);
        }

        [Test]
        public async Task ExactPeriodStartDoesNotChangeSerializedPlanState()
        {
            var state = new BillingPlanState("pro", "monthly", Now.AddMonths(1), "", "", null, "", "", false, true);
            await Assert.That(JsonSerializer.Serialize(state with { PeriodStart = Now })).IsEqualTo(JsonSerializer.Serialize(state));
        }

        [Test]
        public async Task RenewalDateGivesThePeriodWhenNoInvoiceCoversToday()
        {
            var summary = InvoiceHistory.Summarize([], Now, new(new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero), 1500, "usd", "monthly"));
            await Assert.That(summary.Period).IsEqualTo("Oct 10 – Nov 10, 2026");
            await Assert.That(summary.PeriodDetail).Contains("subscription renewal date");
        }

        [Test]
        public async Task EndingSubscriptionSchedulesNoInvoice()
        {
            var summary = InvoiceHistory.Summarize([], Now, new(Now.AddDays(5), 1500, "usd", "monthly", Ends: true));
            await Assert.That(summary.NextInvoice).IsEqualTo("None scheduled");
            await Assert.That(summary.NextDetail).IsEqualTo("The subscription ends on Oct 28, 2026");
        }

        [Test]
        public async Task CountsArePluralizedCorrectly()
        {
            await Assert.That(TerraScale.Dashboard.Common.Formatting.ValueFormat.Plural(1, "recorded invoice")).IsEqualTo("1 recorded invoice");
            await Assert.That(TerraScale.Dashboard.Common.Formatting.ValueFormat.Plural(0, "open invoice")).IsEqualTo("0 open invoices");
        }

        [Test]
        [Arguments("/api/v1/dashboard/databases", "https://dash.terrascale.tech/databases?q=a", "/databases?q=a")]
        [Arguments("/api/v1/dashboard/profile/delete", "https://dash.terrascale.tech/profile?tab=danger", "/profile?tab=danger")]
        [Arguments("/api/v1/dashboard/databases", "", "/")]
        [Arguments("/api/v1/dashboard/databases", "https://dash.terrascale.tech/api/v1/dashboard/databases", "/")]
        [Arguments("/databases", "https://dash.terrascale.tech/home", "/databases")]
        public async Task FailedFormPostsReportTheSubmittingPageUrl(string path, string referer, string expected)
        {
            var http = new DefaultHttpContext();
            http.Request.Path = path;
            if (referer.Length > 0)
            {
                http.Request.Headers.Referer = referer;
            }

            await Assert.That(ShellModel.PageUrl(http)).IsEqualTo(expected);
        }
    }
}
