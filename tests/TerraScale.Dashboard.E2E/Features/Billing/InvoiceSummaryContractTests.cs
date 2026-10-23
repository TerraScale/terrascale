using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.Features.Billing.Invoices;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.E2E.Features.Billing;

public sealed class InvoiceSummaryContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 18, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task ActiveAnnualSubscriptionShowsTheRenewalInvoiceAndCurrentPeriod()
    {
        var paid = new BillingInvoice("in_1", "7LHJXQY7-0001", "paid", 3000, "usd", Now.AddHours(-1), Now.AddHours(-1), Now.AddYears(1).AddHours(-1));
        var summary = InvoiceHistory.Summarize([paid], Now, new(Now.AddYears(1).AddHours(-1), 3000, "usd", "annual"));
        await Assert.That(summary.NextInvoice).IsEqualTo("$30.00");
        await Assert.That(summary.NextDetail).IsEqualTo("Renewal invoice on Oct 23, 2027");
        await Assert.That(summary.Period).IsEqualTo("Oct 23 – Oct 23, 2027");
        await Assert.That(summary.HasPeriod).IsTrue();
        await Assert.That(summary.OutstandingDetail).IsEqualTo("0 open invoices in recorded history");
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
        await Assert.That(summary.NextDetail).StartsWith("The subscription ends on ");
    }

    [Test]
    public async Task CountsArePluralizedCorrectly()
    {
        await Assert.That(InvoiceHistory.Plural(1, "recorded invoice")).IsEqualTo("1 recorded invoice");
        await Assert.That(InvoiceHistory.Plural(0, "open invoice")).IsEqualTo("0 open invoices");
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
        if (referer.Length > 0) http.Request.Headers.Referer = referer;
        await Assert.That(ShellModel.PageUrl(http)).IsEqualTo(expected);
    }
}
