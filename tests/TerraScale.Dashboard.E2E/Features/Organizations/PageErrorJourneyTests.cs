using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Analytics;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Features.Organizations
{
    [ClassDataSource<DashboardE2EFixture>]
    public sealed class PageErrorJourneyTests(DashboardE2EFixture fixture)
    {
        [Test]
        public async Task InvalidAnalyticsSelectionKeepsNavigationAndOffersFilterRecovery()
        {
            using var client = fixture.CreateClient();
            using var login = await HttpJourney.LoginAsync(client);
            using var response = await client.GetAsync("/analytics?project=missing-project");
            await NoticeAsync(response, HttpStatusCode.NotFound);
            var html = await response.Content.ReadAsStringAsync();
            await Assert.That(html).Contains("Project is unavailable.");
            await Assert.That(html).Contains("Reset analytics filters");
        }

        [Test]
        public async Task AnalyticsAccessRefusalKeepsTheShellAndExplainsHowToGetAccess()
        {
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            await using var app = environment.CreateTestHost(fixture.Root, 0, "http://127.0.0.1", builder =>
                builder.Services.Replace(ServiceDescriptor.Singleton<IAnalyticsBackend>(new RefusedAnalytics())));
            await app.StartAsync();
            using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false })
            { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
            using var login = await HttpJourney.LoginAsync(client);
            using var response = await client.GetAsync("/analytics");
            await NoticeAsync(response, HttpStatusCode.Forbidden);
            await Assert.That(await response.Content.ReadAsStringAsync()).Contains("Ask an owner or admin for access.");
        }

        private sealed class RefusedAnalytics : IAnalyticsBackend
        {
            public Task<AnalyticsBatch> ReadAsync(AnalyticsSelection selection, CancellationToken cancellationToken)
            {
                throw new AccountAccessException("Organization access is required.");
            }
        }

        [Test]
        public async Task BillingFailureKeepsOrganizationTabsAndDoesNotBreakOtherSections()
        {
            using var client = fixture.CreateClient();
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            using var identity = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
            var organization = identity.RootElement.GetProperty("organizationId").GetString()!;
            var state = fixture.Service<IDashboardState>();
            // A corrupt binding exercises the billing load failure without breaking account reads.
            await state.PutAsync(StripeBilling.Collection, organization,
                JsonSerializer.SerializeToElement(new { OrganizationId = organization }), TerraBasePrecondition.Absent, default);
            try
            {
                using var billing = await client.GetAsync("/organization?tab=billing");
                await NoticeAsync(billing, HttpStatusCode.ServiceUnavailable);
                var html = await billing.Content.ReadAsStringAsync();
                await Assert.That(html).Contains("Billing information could not be loaded.");
                await Assert.That(html).Contains("Retry billing");
                await Assert.That(html).Contains("aria-label=\"Organization sections\"");
                foreach (var route in new[] { "/organization", "/organization?tab=projects" })
                {
                    using var other = await client.GetAsync(route);
                    await Assert.That(other.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    await Assert.That(await other.Content.ReadAsStringAsync()).DoesNotContain("Billing information could not be loaded.");
                }
            }
            finally
            {
                var binding = await state.GetAsync(StripeBilling.Collection, organization, default);
                if (binding is not null)
                {
                    await state.DeleteAsync(StripeBilling.Collection, organization, binding.Version, default);
                }
            }
        }

        private static async Task NoticeAsync(HttpResponseMessage response, HttpStatusCode status)
        {
            await Assert.That(response.StatusCode).IsEqualTo(status);
            await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/html");
            var html = await response.Content.ReadAsStringAsync();
            await Assert.That(html).Contains("id=\"ts-navigation\"");
            await Assert.That(html).Contains("role=\"alert\"");
            await Assert.That(html).Contains("Request ID:");
            await Assert.That(html).Contains(response.Headers.GetValues("X-Request-ID").Single());
        }
    }
}
