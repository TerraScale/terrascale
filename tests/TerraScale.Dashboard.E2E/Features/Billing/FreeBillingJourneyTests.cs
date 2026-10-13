using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TerraScale.Dashboard.E2E.Features.Billing;

public sealed class FreeBillingJourneyTests
{
    [Test]
    public async Task FreePlanDisplaysWithoutSubscriptionAndCannotCreateStripeCheckout()
    {
        var environment = new DemoEnvironment();
        await environment.InitializeAsync();
        using var vendor = new RejectVendorCalls();
        await using var app = environment.CreateHost(new DashboardE2EFixture().Root, 0, "http://127.0.0.1", builder =>
            builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(vendor)));
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new() })
        {
            BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal))
        };
        await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
        using var page = await client.GetAsync("/organization?tab=billing");
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await page.Content.ReadAsStringAsync()).Contains("Free plan. No subscription or payment method is required; no Stripe charges.");
        var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=billing");
        using var checkout = await HttpJourney.PostFormAsync(client, "/billing/checkout", new()
        {
            ["_csrf"] = csrf,
            ["plan"] = "free",
            ["interval"] = "monthly",
            ["return"] = "/organization?tab=billing"
        });
        await Assert.That(checkout.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(vendor.Calls).IsEqualTo(0);
        await app.StopAsync();
    }

    private sealed class RejectVendorCalls : HttpMessageHandler, IHttpClientFactory
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            throw new HttpRequestException("A free-plan view must not call Stripe.");
        }
    }
}
