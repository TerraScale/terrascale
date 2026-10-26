using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        await using var app = environment.CreateTestHost(new DashboardE2EFixture().Root, 0, "http://127.0.0.1", builder =>
        {
            var original = (IHttpClientFactory)builder.Services.Last(item => item.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
            wire = new(original, currency, amount, remaining);
            builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(wire));
        });
        using var wireLifetime = wire;
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() })
        { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
        var suffix = Guid.NewGuid().ToString("N");
        await BillingPlanJourneyTests.SignUpPaidAsync(client, "invoice-" + suffix + "@example.test", "E2E-invoice-" + suffix, suffix);
        await Assert.That(wire.ReturnHosts).IsEquivalentTo(new[] { "dash.terrascale.tech", "dash.terrascale.tech" });
        using var portal = await client.GetAsync("/billing/portal?return=https%3A%2F%2Fattacker.invalid");
        await Assert.That(portal.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        await Assert.That(wire.PortalReturn).IsEqualTo("https://dash.terrascale.tech/billing");
        await Assert.That(wire.ReturnHosts).IsEquivalentTo(new[] { "dash.terrascale.tech", "dash.terrascale.tech", "dash.terrascale.tech" });
        await Assert.That(wire.ReturnSchemes.All(scheme => scheme == "https")).IsTrue();
        var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=billing");
        var vendorCalls = wire.StripeCalls;
        foreach (var hostile in new[] { "https://attacker.invalid/", "//attacker.invalid/", "https://dash.terrascale.tech@attacker.invalid/" })
        {
            using var refused = await HttpJourney.PostFormAsync(client, "/billing/checkout", new()
            { ["_csrf"] = csrf, ["plan"] = "starter", ["interval"] = "annual", ["return"] = hostile });
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        }
        await Assert.That(wire.StripeCalls).IsEqualTo(vendorCalls);
        using var response = await client.GetAsync("/organization?tab=billing");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        await Assert.That(html).Contains(totalLabel);
        await Assert.That(html).Contains(balanceLabel);
        await Assert.That(WebUtility.HtmlDecode(html)).Contains("Day 3 of 27 · based on the current subscription period");
        await Assert.That(wire.Calls).IsGreaterThan(0);
        await app.StopAsync();
    }

    private sealed class InvoiceWire(IHttpClientFactory original, string currency, long amount, long remaining) : HttpMessageHandler, IHttpClientFactory
    {
        private readonly DateTimeOffset periodStart = DateTimeOffset.UtcNow.AddDays(-3);
        private int calls;
        private int stripeCalls;
        public int Calls => Volatile.Read(ref calls);
        public int StripeCalls => Volatile.Read(ref stripeCalls);
        public ConcurrentQueue<string> ReturnHosts { get; } = new();
        public ConcurrentQueue<string> ReturnSchemes { get; } = new();
        public string? PortalReturn { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is { Host: "api.stripe.com" } stripe)
            {
                Interlocked.Increment(ref stripeCalls);
                string[] fields = stripe.AbsolutePath switch
                {
                    "/v1/checkout/sessions" => ["success_url", "cancel_url"],
                    "/v1/billing_portal/sessions" => ["return_url"],
                    _ => []
                };
                if (request.Method == HttpMethod.Post && fields.Length > 0)
                {
                    var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
                    foreach (var field in fields)
                    {
                        var returnUri = new Uri(form[field].ToString());
                        ReturnHosts.Enqueue(returnUri.Host);
                        ReturnSchemes.Enqueue(returnUri.Scheme);
                        if (stripe.AbsolutePath == "/v1/billing_portal/sessions") PortalReturn = returnUri.AbsoluteUri;
                    }
                }
            }
            if (request.RequestUri is not { Host: "api.stripe.com", AbsolutePath: "/v1/invoices" } uri || request.Method != HttpMethod.Get)
            {
                // A second HttpClient requires a fresh message; the SDK owns the original.
                using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
                { Version = request.Version, VersionPolicy = request.VersionPolicy };
                foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                foreach (var option in request.Options) forwarded.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
                if (request.Content is { } content)
                {
                    forwarded.Content = new ByteArrayContent(await content.ReadAsByteArrayAsync(cancellationToken));
                    foreach (var header in content.Headers) forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                using var client = original.CreateClient("platform");
                var response = await client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.RequestMessage = request;
                if (response.IsSuccessStatusCode && request.Method == HttpMethod.Get && request.RequestUri is { Host: "api.stripe.com" } subscriptionUri &&
                    subscriptionUri.AbsolutePath.StartsWith("/v1/subscriptions/", StringComparison.Ordinal))
                {
                    var subscription = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!;
                    subscription["items"]!["data"]![0]!["current_period_start"] = periodStart.ToUnixTimeSeconds();
                    subscription["items"]!["data"]![0]!["current_period_end"] = periodStart.AddDays(27).ToUnixTimeSeconds();
                    response.Content.Dispose();
                    response.Content = new StringContent(subscription.ToJsonString(), Encoding.UTF8, "application/json");
                }
                return response;
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
