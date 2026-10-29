using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TerraScale.Dashboard.E2E.Features.Billing
{
    public sealed class BillingTaxJourneyTests
    {
        [Test]
        [Arguments("verified")]
        [Arguments("unverified")]
        public async Task CheckoutCollectsTaxFactsAndOwnerSeesStripeVerificationWithoutInventingExemption(string verification)
        {
            await using var scenario = await Scenario.StartAsync();
            scenario.Wire.Verification = verification;
            using var owner = scenario.Client();
            await scenario.SignUpAsync(owner);
            var checkout = scenario.Wire.Checkout;
            foreach (var (field, value) in new[] { ("billing_address_collection", "required"),
                ("tax_id_collection[enabled]", "true"), ("customer_update[address]", "auto"), ("customer_update[name]", "auto") })
            {
                await Assert.That(checkout.GetValueOrDefault(field)).IsEqualTo(value);
            }
            // Stripe refuses automatic_tax on an account whose country Stripe Tax does not support, and with it every checkout.
            await Assert.That(checkout.ContainsKey("automatic_tax[enabled]")).IsFalse();
            using var page = await owner.GetAsync("/organization?tab=billing");
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var html = await page.Content.ReadAsStringAsync();
            foreach (var text in new[] { "Billing details and tax IDs", "Tax fixture GmbH", "Example Street 10", "10115 Berlin", "DE123456789", verification, "Edit billing details" })
            {
                await Assert.That(html).Contains(text);
            }

            await Assert.That(html.Contains("<script>tax-fixture", StringComparison.Ordinal)).IsFalse();
            await Assert.That(scenario.Wire.TaxReads).IsGreaterThan(0);
            await Assert.That(scenario.Wire.CustomerWrites).IsEqualTo(1).Because("Only initial customer creation occurs; a submitted VAT ID never causes an exemption write");
            using var portal = await owner.GetAsync("/billing/portal");
            await Assert.That(portal.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(scenario.Wire.PortalCustomer).IsEqualTo(checkout["customer"]);
        }

        [Test]
        [Arguments("customer-mismatch")]
        [Arguments("foreign-tax-id")]
        [Arguments("incomplete-tax-ids")]
        public async Task UntrustedOrIncompleteStripeTaxFactsAreHiddenAndCanBeRetried(string fault)
        {
            await using var scenario = await Scenario.StartAsync();
            using var owner = scenario.Client();
            await scenario.SignUpAsync(owner);
            scenario.Wire.Fault = fault;
            using var page = await owner.GetAsync("/organization?tab=billing");
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
            var html = await page.Content.ReadAsStringAsync();
            await Assert.That(html).Contains("Billing details could not be loaded.");
            await Assert.That(html.Contains("DE123456789", StringComparison.Ordinal)).IsFalse();
            scenario.Wire.Fault = "";
            using var retried = await owner.GetAsync("/organization?tab=billing");
            await Assert.That(await retried.Content.ReadAsStringAsync()).Contains("DE123456789");
        }

        private sealed class Scenario : IAsyncDisposable
        {
            private Microsoft.AspNetCore.Builder.WebApplication app = null!;
            public TaxWire Wire { get; private set; } = null!;
            public static async Task<Scenario> StartAsync()
            {
                var environment = new DemoEnvironment();
                await environment.InitializeAsync();
                var scenario = new Scenario();
                scenario.app = environment.CreateTestHost(new DashboardE2EFixture().Root, 0, "http://127.0.0.1", builder =>
                {
                    var original = (IHttpClientFactory)builder.Services.Last(service => service.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
                    scenario.Wire = new(original);
                    builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(scenario.Wire));
                });
                await scenario.app.StartAsync();
                return scenario;
            }
            public HttpClient Client()
            {
                return new(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() })
                { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
            }

            public Task SignUpAsync(HttpClient client)
            {
                var tag = Guid.NewGuid().ToString("N");
                return BillingPlanJourneyTests.SignUpPaidAsync(client, "tax+" + tag + "@example.test", "Tax-" + tag, tag);
            }
            public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); Wire.Dispose(); }
        }

        private sealed class TaxWire(IHttpClientFactory original) : HttpMessageHandler, IHttpClientFactory
        {
            public Dictionary<string, string> Checkout { get; private set; } = [];
            public string Verification { get; set; } = "verified";
            public string Fault { get; set; } = "";
            public string PortalCustomer { get; private set; } = "";
            public int TaxReads { get; private set; }
            public int CustomerWrites { get; private set; }
            public HttpClient CreateClient(string name)
            {
                return new(this, false);
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri is { Host: "api.stripe.com" } uri && request.Method == HttpMethod.Post)
                {
                    var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
                    if (uri.AbsolutePath == "/v1/checkout/sessions")
                    {
                        Checkout = form.ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
                    }

                    if (uri.AbsolutePath.StartsWith("/v1/customers", StringComparison.Ordinal))
                    {
                        CustomerWrites++;
                    }

                    if (uri.AbsolutePath == "/v1/billing_portal/sessions")
                    {
                        PortalCustomer = form["customer"].ToString();
                    }
                }
                using var client = original.CreateClient("platform");
                var response = await HttpForwarding.SendAsync(client, request, cancellationToken);
                if (request.RequestUri is not { Host: "api.stripe.com" } stripe || request.Method != HttpMethod.Get ||
                    !stripe.AbsolutePath.StartsWith("/v1/customers/", StringComparison.Ordinal) || QueryHelpers.ParseQuery(stripe.Query).GetValueOrDefault("expand[0]") != "tax_ids")
                {
                    return response;
                }

                TaxReads++;
                var customer = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!;
                var customerId = customer["id"]!.GetValue<string>();
                customer["name"] = "Tax fixture GmbH <script>tax-fixture</script>";
                customer["address"] = new JsonObject { ["line1"] = "Example Street 10", ["postal_code"] = "10115", ["city"] = "Berlin", ["country"] = "DE" };
                customer["tax_ids"] = new JsonObject
                {
                    ["object"] = "list",
                    ["has_more"] = Fault == "incomplete-tax-ids",
                    ["data"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "txi_fixture",
                        ["object"] = "tax_id",
                        ["type"] = "eu_vat",
                        ["value"] = "DE123456789",
                        ["customer"] = Fault == "foreign-tax-id" ? "cus_foreign" : customerId,
                        ["verification"] = new JsonObject { ["status"] = Verification }
                    })
                };
                if (Fault == "customer-mismatch")
                {
                    customer["metadata"]!["organization_id"] = "00000000000000000000000009";
                }

                response.Content.Dispose();
                response.Content = new StringContent(customer.ToJsonString(), Encoding.UTF8, "application/json");
                return response;
            }
        }
    }
}
