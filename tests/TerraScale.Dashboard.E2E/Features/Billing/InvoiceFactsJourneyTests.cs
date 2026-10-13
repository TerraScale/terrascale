using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TerraScale.Dashboard.E2E.Features.Billing;

public sealed class InvoiceFactsJourneyTests
{
    [Test]
    [Arguments("usd", 1234L, 234L, "USD 12.34", "$2.34")]
    [Arguments("jpy", 1500L, 500L, "JPY 1500", "JPY 500")]
    [Arguments("clp", 1234L, 234L, "CLP 1234", "CLP 234")]
    public async Task StripeInvoiceHistoryRetainsOriginalTotalButOutstandingUsesActualRemainingChargeUnits(string currency, long amount, long remaining, string totalLabel, string balanceLabel)
    {
        var environment = new DemoEnvironment();
        await environment.InitializeAsync();
        InvoiceWire wire = null!;
        await using var app = environment.CreateHost(new DashboardE2EFixture().Root, 0, "http://127.0.0.1", builder =>
        {
            var original = (IHttpClientFactory)builder.Services.Last(item => item.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
            wire = new(original, currency, amount, remaining);
            builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(wire));
        });
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new() })
        { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
        var suffix = Guid.NewGuid().ToString("N");
        await BillingPlanJourneyTests.SignUpPaidAsync(client, "invoice-" + suffix + "@example.test", "E2E-invoice-" + suffix, suffix);
        await Assert.That(wire.ReturnHosts).IsEquivalentTo(new[] { "dash.terrascale.tech", "dash.terrascale.tech" });
        using var response = await client.GetAsync("/organization?tab=billing");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        await Assert.That(html).Contains(totalLabel);
        await Assert.That(html).Contains(balanceLabel);
        await Assert.That(wire.Calls).IsGreaterThan(0);
        await app.StopAsync();
        wire.Dispose();
    }

    private sealed class InvoiceWire(IHttpClientFactory original, string currency, long amount, long remaining) : HttpMessageHandler, IHttpClientFactory
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public ConcurrentQueue<string> ReturnHosts { get; } = new();
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is { Host: "api.stripe.com", AbsolutePath: "/v1/checkout/sessions" } && request.Method == HttpMethod.Post)
            {
                var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
                foreach (var field in new[] { "success_url", "cancel_url" }) ReturnHosts.Enqueue(new Uri(form[field].ToString()).Host);
            }
            if (request.RequestUri is not { Host: "api.stripe.com", AbsolutePath: "/v1/invoices" } uri || request.Method != HttpMethod.Get)
            {
                using var client = original.CreateClient("platform");
                return await client.SendAsync(request, cancellationToken);
            }
            var customer = QueryHelpers.ParseQuery(uri.Query)["customer"].ToString();
            if (customer.Length == 0) throw new InvalidOperationException("Invoice request lost customer scope.");
            Interlocked.Increment(ref calls);
            var now = DateTimeOffset.UtcNow;
            var body = JsonSerializer.Serialize(new
            {
                @object = "list",
                has_more = false,
                data = new[] {
                new { id = "in_synthetic", @object = "invoice", customer, number = "SYNTHETIC-1", status = "open", currency,
                    amount_due = amount, amount_paid = amount - remaining, amount_remaining = remaining,
                    created = now.AddDays(-1).ToUnixTimeSeconds(), period_start = now.AddDays(-10).ToUnixTimeSeconds(), period_end = now.AddDays(20).ToUnixTimeSeconds() }
            }
            });
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
