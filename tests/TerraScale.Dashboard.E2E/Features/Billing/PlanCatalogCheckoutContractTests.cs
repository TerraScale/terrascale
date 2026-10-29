using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Billing
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class PlanCatalogCheckoutContractTests(DashboardRootFixture fixture)
    {
        [Test]
        [Arguments("valid")]
        [Arguments("amount")]
        [Arguments("interval")]
        [Arguments("interval-count")]
        [Arguments("product")]
        [Arguments("metered")]
        [Arguments("inactive")]
        [Arguments("inactive-product")]
        [Arguments("currency")]
        public async Task CheckoutRequiresThePublishedStripeAmountIntervalAndProduct(string scenario)
        {
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            CatalogStripeTransport wire = null!;
            await using var app = environment.CreateTestHost(fixture.Root, 0, "http://127.0.0.1", builder =>
            {
                var original = (IHttpClientFactory)builder.Services.Last(item => item.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
                wire = new(original, scenario);
                builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(wire));
            });
            using var wireLifetime = wire;
            await app.StartAsync();
            using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() })
            { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)), Timeout = TimeSpan.FromSeconds(30) };
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=billing");
            using var response = await CheckoutAsync(client, csrf, "pro", "monthly", "/organization?tab=billing");
            await Assert.That(wire.CatalogReads).IsGreaterThan(0);
            await Assert.That(response.StatusCode).IsEqualTo(scenario == "valid" ? HttpStatusCode.SeeOther : HttpStatusCode.ServiceUnavailable);
            await Assert.That(wire.CheckoutWrites).IsEqualTo(scenario == "valid" ? 1 : 0);
            if (scenario == "valid")
            {
                await Assert.That(wire.CheckoutPrice).IsEqualTo("price_pro_monthly");
            }
            else
            {
                await Assert.That(await response.Content.ReadAsStringAsync()).Contains("Checkout is temporarily unavailable.");
            }

            await app.StopAsync();
        }

        private static Task<HttpResponseMessage> CheckoutAsync(HttpClient client, string csrf, string plan, string interval, string path)
        {
            return HttpJourney.PostFormAsync(client, "/billing/checkout", new() { ["_csrf"] = csrf, ["plan"] = plan, ["interval"] = interval, ["return"] = path });
        }
    }
}
