using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.Features.Billing.Invoices;
using TerraScale.Dashboard.Features.Billing;

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
            await Assert.That(summary.NextInvoice).IsEqualTo("$30.00 base fee on Oct 23, 2027");
            await Assert.That(summary.Period).IsEqualTo("Oct 23, 2026 – Oct 23, 2027");
            await Assert.That(summary.HasPeriod).IsTrue();
            await Assert.That(summary.Outstanding).IsEqualTo("0 open invoices");
        }

        [Test]
        public async Task RecordedInvoiceTotalOverridesBaseEstimateWithoutConfusingRemainingBalance()
        {
            var renewal = new InvoiceRenewal(Now.AddDays(20), 2000, "usd", "monthly");
            var outstanding = new BillingInvoice("in_old", "OLD-1", "open", 500, "usd", Now.AddDays(-1), null, null) { RemainingCents = 250 };
            var estimate = InvoiceHistory.Summarize([outstanding], Now, renewal);
            await Assert.That(estimate.NextInvoice).IsEqualTo("$20.00 base fee on Nov 12, 2026");
            await Assert.That(estimate.Outstanding).IsEqualTo("$2.50");
            var adjusted = new BillingInvoice("in_next", "NEXT-1", "open", 2750, "usd", Now.AddDays(2), null, null) { RemainingCents = 1000 };
            var recorded = InvoiceHistory.Summarize([outstanding, adjusted], Now, renewal);
            await Assert.That(recorded.NextInvoice).IsEqualTo("$27.50 on Oct 25, 2026");
            await Assert.That(recorded.Outstanding).IsEqualTo("$12.50");
            await Assert.That(recorded.Period).IsEqualTo(estimate.Period);
        }

    }
}
