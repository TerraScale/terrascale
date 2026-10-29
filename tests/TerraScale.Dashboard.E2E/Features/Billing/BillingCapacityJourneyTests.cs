using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Accounts;

namespace TerraScale.Dashboard.E2E.Features.Billing
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class BillingCapacityJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task UnqualifiedActiveStripePriceRemovesPaidCapacityWhileKeepingTheExistingDatabase()
        {
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            SubscriptionWire wire = null!;
            await using var app = environment.CreateTestHost(fixture.Root, 0, "http://127.0.0.1", builder =>
            {
                var original = (IHttpClientFactory)builder.Services.Last(item => item.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
                wire = new(original);
                builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(wire));
            });
            using var wireLifetime = wire;
            await app.StartAsync();
            using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() })
            { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
            var suffix = Guid.NewGuid().ToString("N");
            await BillingPlanJourneyTests.SignUpPaidAsync(client, "capacity-" + suffix + "@example.test", "E2E-capacity-" + suffix, suffix);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            using var first = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = "capacity-first", ["region"] = "GRU" });
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Created);
            using var database = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            var databaseId = database.RootElement.GetProperty("id").GetString()!;
            using var identity = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
            var organizationId = identity.RootElement.GetProperty("organizationId").GetString();
            using var generated = await HttpJourney.PostFormAsync(client, "/__test/billing/webhook/event", new()
            { ["_csrf"] = csrf, ["liveMode"] = "false" });
            await Assert.That(generated.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var delivery = JsonDocument.Parse(await generated.Content.ReadAsStringAsync());
            wire.Disqualify();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/stripe/webhook")
            { Content = new StringContent(delivery.RootElement.GetProperty("body").GetString()!, Encoding.UTF8, "application/json") };
            request.Headers.Add("Stripe-Signature", delivery.RootElement.GetProperty("signature").GetString());
            using var reconciled = await client.SendAsync(request);
            await Assert.That(reconciled.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(wire.DisqualifiedReads).IsGreaterThan(0);
            using var context = JsonDocument.Parse(await client.GetStringAsync("/api/v1/dashboard/context"));
            var organization = context.RootElement.GetProperty("organizations").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == organizationId);
            await Assert.That(organization.GetProperty("planId").GetString()).IsEqualTo(OrganizationRecord.FreePlanId);
            await Assert.That(organization.GetProperty("billingStatus").GetString()).IsEqualTo("billing_required");
            using var second = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new()
            { ["_csrf"] = csrf, ["name"] = "capacity-second", ["region"] = "GRU" });
            await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            using var existing = await client.GetAsync("/databases/" + databaseId);
            await Assert.That(existing.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await existing.Content.ReadAsStringAsync()).Contains("capacity-first");
            await app.StopAsync();
        }

        private sealed class SubscriptionWire(IHttpClientFactory original) : HttpMessageHandler, IHttpClientFactory
        {
            private int disqualified;
            private int reads;
            public int DisqualifiedReads => Volatile.Read(ref reads);
            public void Disqualify()
            {
                Volatile.Write(ref disqualified, 1);
            }

            public HttpClient CreateClient(string name)
            {
                return new(this, false);
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
                { Version = request.Version, VersionPolicy = request.VersionPolicy };
                foreach (var header in request.Headers)
                {
                    forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                foreach (var option in request.Options)
                {
                    forwarded.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
                }

                if (request.Content is { } content)
                {
                    forwarded.Content = new ByteArrayContent(await content.ReadAsByteArrayAsync(cancellationToken));
                    foreach (var header in content.Headers)
                    {
                        forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
                using var client = original.CreateClient("platform");
                var response = await client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.RequestMessage = request;
                if (Volatile.Read(ref disqualified) == 0 || request.Method != HttpMethod.Get ||
                    request.RequestUri is not { Host: "api.stripe.com" } uri || !uri.AbsolutePath.StartsWith("/v1/subscriptions/", StringComparison.Ordinal) ||
                    !response.IsSuccessStatusCode)
                {
                    return response;
                }

                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!;
                body["status"] = "active";
                body["items"]!["data"]![0]!["price"]!["currency"] = "eur";
                response.Content.Dispose();
                response.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                Interlocked.Increment(ref reads);
                return response;
            }
        }
    }
}
