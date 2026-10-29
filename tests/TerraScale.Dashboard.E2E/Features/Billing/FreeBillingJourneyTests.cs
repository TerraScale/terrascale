using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E.Features.Billing
{
    public sealed class FreeBillingJourneyTests
    {
        [Test]
        public async Task FreePlanDisplaysWithoutSubscriptionAndCannotCreateStripeCheckout()
        {
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            RejectVendorCalls vendor = null!;
            TestTerraBasePlans plans = null!;
            await using var app = environment.CreateTestHost(new DashboardE2EFixture().Root, 0, "http://127.0.0.1", builder =>
            {
                plans = (TestTerraBasePlans)builder.Services.Last(service => service.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
                vendor = new(plans);
                builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(vendor));
            });
            using var vendorLifetime = vendor;
            await app.StartAsync();
            using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() })
            {
                BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal))
            };
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            using var page = await client.GetAsync("/organization?tab=billing");
            await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await page.Content.ReadAsStringAsync()).Contains("Free plan. No subscription or payment method is required; no Stripe charges.");
            var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=billing");
            foreach (var rejectedPlan in new[] { "free", "business", "enterprise", "unknown" })
            {
                using var checkout = await HttpJourney.PostFormAsync(client, "/billing/checkout", new()
                {
                    ["_csrf"] = csrf,
                    ["plan"] = rejectedPlan,
                    ["interval"] = "monthly",
                    ["return"] = "/organization?tab=billing"
                });
                await Assert.That(checkout.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
                await Assert.That(vendor.Calls).IsEqualTo(0);
            }
            using var portalProblem = await client.GetAsync("/billing/portal");
            await Assert.That(portalProblem.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            await Assert.That(portalProblem.Content.Headers.ContentType!.MediaType).IsEqualTo("application/problem+json");
            using var portalRequest = new HttpRequestMessage(HttpMethod.Get, "/billing/portal");
            portalRequest.Headers.Accept.ParseAdd("text/html");
            using var portalPage = await client.SendAsync(portalRequest);
            await Assert.That(portalPage.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            await Assert.That(portalPage.Content.Headers.ContentType!.MediaType).IsEqualTo("text/html");
            var portalHtml = await portalPage.Content.ReadAsStringAsync();
            foreach (var text in new[] { "<html lang=\"en\"", "<title>Billing portal unavailable · TerraScale</title>", "role=\"alert\"", "Set up billing before opening the customer portal.", ">Return to billing</a>", "autofocus" })
            {
                await Assert.That(portalHtml).Contains(text);
            }

            await Assert.That(System.Text.RegularExpressions.Regex.IsMatch(portalHtml, "<a[^>]+href=\"/organization\\?tab=billing[^\"]*\"[^>]*>Return to billing</a>")).IsTrue();
            await Assert.That(vendor.Calls).IsEqualTo(0);
            await Assert.That(plans.Requests).IsNotEmpty();
            await Assert.That(plans.Requests.All(request => request.Plan == "hobby" && request.Status == HttpStatusCode.NotFound)).IsTrue();
            await app.StopAsync();
        }

        private sealed class RejectVendorCalls(IHttpClientFactory fallback) : HttpMessageHandler, IHttpClientFactory
        {
            private int calls;
            public int Calls => Volatile.Read(ref calls);
            public HttpClient CreateClient(string name)
            {
                return name == TerraBaseDashboardState.ClientName
                ? fallback.CreateClient(name)
                : new(this, disposeHandler: false);
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref calls);
                throw new HttpRequestException("A free-plan view must not call Stripe.");
            }
        }
    }
}
