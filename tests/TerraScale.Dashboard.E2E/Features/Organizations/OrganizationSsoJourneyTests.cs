using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.E2E.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Organizations
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class OrganizationSsoJourneyTests(DashboardRootFixture fixture)
    {
        private const string Secret = "synthetic-sso-client-secret";

        [Test]
        public async Task OwnerConfiguresVerifiesAndRemovesSsoOnlyWritesOnTeamPlan()
        {
            var sso = new SsoProducer();
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, builder =>
            {
                sso.Inner ??= (IHttpClientFactory)builder.Services.Last(item => item.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
                builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(sso));
            });
            using var client = scenario.CreateClient();
            using var producer = scenario.CreateProviderClient();
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            using var identity = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/me"));
            var organizationId = identity.RootElement.GetProperty("organizationId").GetString()!;

            // Free: the section reads, shows the upgrade prompt, and the server refuses a write without calling ts-auth.
            var free = await client.GetStringAsync("/organization?tab=sso");
            await Assert.That(free).Contains("id=\"sso-upgrade\"");
            await Assert.That(free).Contains("No connection");
            await Assert.That(free).DoesNotContain("id=\"configure-sso\"");
            var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=sso");
            var form = new Dictionary<string, string>
            {
                ["_csrf"] = csrf,
                ["domain"] = "Acme.example",
                ["default_role"] = "member",
                ["oidc_issuer"] = "https://idp.example/realms/acme",
                ["oidc_client_id"] = "dashboard",
                ["oidc_client_secret"] = Secret
            };
            using (var refused = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/sso", form))
            {
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
                await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("Team or Enterprise plan");
            }
            await Assert.That(sso.Calls.Any(call => call.Method != "GET")).IsFalse().Because("The plan gate refuses before ts-auth is called.");

            // Team: save sends snake_case with the write-only secret, which the page never renders back.
            await PlanAsync(producer, organizationId, PlanCatalog.For("team").Id);
            using (var saved = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/sso", form))
            {
                await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            using (var body = JsonDocument.Parse(sso.Calls.Single(call => call.Method == "PUT").Body))
            {
                await Assert.That(body.RootElement.GetProperty("domain").GetString()).IsEqualTo("acme.example");
                await Assert.That(body.RootElement.GetProperty("default_role").GetString()).IsEqualTo("member");
                await Assert.That(body.RootElement.GetProperty("oidc").GetProperty("client_secret").GetString()).IsEqualTo(Secret);
                await Assert.That(body.RootElement.GetProperty("protocol").GetString()).IsEqualTo("oidc");
            }
            var pending = await client.GetStringAsync("/organization?tab=sso");
            await Assert.That(pending).Contains("_terrascale-sso.acme.example");
            await Assert.That(pending).Contains("terrascale-sso-verification=synthetic");
            await Assert.That(pending).Contains("Not verified");
            await Assert.That(pending).Contains("<dt>Client secret</dt><dd>Set</dd>");
            await Assert.That(pending).DoesNotContain(Secret);

            csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=sso");
            var token = new Dictionary<string, string> { ["_csrf"] = csrf };
            using (var unverified = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/sso/verify", token))
            {
                await Assert.That(unverified.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
                await Assert.That(await unverified.Content.ReadAsStringAsync()).Contains("The TXT record was not found yet.");
            }
            sso.RecordPublished = true;
            using (var verified = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/sso/verify", token))
            {
                await Assert.That(verified.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            await Assert.That(await client.GetStringAsync("/organization?tab=sso")).DoesNotContain("id=\"sso-verification\"");

            // Downgraded: the connection stays visible, edits are refused, and removal still works.
            await PlanAsync(producer, organizationId, OrganizationRecord.FreePlanId);
            await Assert.That(await client.GetStringAsync("/organization?tab=sso")).Contains("You can still remove it.");
            using (var refused = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/sso/verify", token))
            {
                await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            }

            using (var removed = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/sso/delete", token))
            {
                await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            await Assert.That(sso.Calls.Count(call => call.Method == "DELETE")).IsEqualTo(1);
            await Assert.That(sso.Calls.All(call => call.Path == $"/v1/terrascale/organizations/{organizationId}/sso" + (call.Method == "POST" ? "/verify" : ""))).IsTrue();

            // ts-auth not live yet: the page still loads and says so.
            sso.Down = true;
            using var down = await client.GetAsync("/organization?tab=sso");
            await Assert.That(down.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await down.Content.ReadAsStringAsync()).Contains("Single sign-on settings are unavailable right now.");
        }

        private static async Task PlanAsync(HttpClient producer, string organizationId, string planId)
        {
            using var response = await producer.PostAsJsonAsync("/__test/control", new { command = "plan", organizationId, value = planId });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        }

        // Test-only stand-in for ts-auth's SSO routes; every other call goes to the scenario's producer.
        private sealed class SsoProducer : HttpMessageHandler, IHttpClientFactory
        {
            public IHttpClientFactory? Inner { get; set; }
            public ConcurrentQueue<(string Method, string Path, string Body)> Calls { get; } = new();
            public bool RecordPublished { get; set; }
            public bool Down { get; set; }
            private JsonObject? connection;

            public HttpClient CreateClient(string name)
            {
                return new(this, disposeHandler: false);
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                if (request.RequestUri.Host != "auth.terrascale.tech" || !path.StartsWith("/v1/terrascale/organizations/", StringComparison.Ordinal) || !path.Contains("/sso", StringComparison.Ordinal))
                {
                    // The outer client marked this request as sent; the scenario's client gets a copy.
                    using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri) { Content = request.Content, Version = request.Version };
                    foreach (var header in request.Headers)
                    {
                        forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }

                    using var inner = Inner!.CreateClient("platform");
                    return await inner.SendAsync(forwarded, cancellationToken);
                }
                if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter.Length: > 0 })
                {
                    return Answer(HttpStatusCode.Unauthorized, Error("invalid_token"));
                }

                if (Down)
                {
                    return Answer(HttpStatusCode.ServiceUnavailable, Error("unavailable"));
                }

                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                Calls.Enqueue((request.Method.Method, path, body));
                switch (request.Method.Method)
                {
                    case "PUT":
                        var update = JsonNode.Parse(body)!.AsObject();
                        // As on prod: only oidc is accepted and an unknown member is refused.
                        if (update.Any(member => member.Key is not ("domain" or "protocol" or "default_role" or "oidc")) || update["protocol"]?.GetValue<string>() != "oidc" ||
                            update["oidc"]!.AsObject().Any(member => member.Key is not ("issuer" or "client_id" or "client_secret")))
                        {
                            return Answer(HttpStatusCode.BadRequest, Error("invalid_request"));
                        }

                        var domain = update["domain"]!.GetValue<string>();
                        connection = new JsonObject
                        {
                            ["organization_id"] = path.Split('/')[4],
                            ["domain"] = domain,
                            ["domain_verified"] = false,
                            ["verified_at"] = null,
                            ["verification"] = new JsonObject { ["type"] = "TXT", ["name"] = "_terrascale-sso." + domain, ["value"] = "terrascale-sso-verification=synthetic" },
                            ["protocol"] = "oidc",
                            ["default_role"] = update["default_role"]!.GetValue<string>(),
                            ["saml"] = null,
                            ["oidc"] = new JsonObject
                            {
                                ["issuer"] = update["oidc"]!["issuer"]!.GetValue<string>(),
                                ["client_id"] = update["oidc"]!["client_id"]!.GetValue<string>(),
                                ["client_secret_set"] = true,
                                ["redirect_uri"] = "https://auth.terrascale.tech/ui/terrascale/sso/callback"
                            },
                            ["created_at"] = "2026-10-24T12:00:00Z",
                            ["updated_at"] = "2026-10-24T12:00:00Z"
                        };
                        return Answer(HttpStatusCode.OK, connection);
                    case "POST" when connection is not null:
                        if (!RecordPublished)
                        {
                            return Answer(HttpStatusCode.Conflict, Error("domain_unverified"));
                        }

                        connection["domain_verified"] = true;
                        connection["verified_at"] = "2026-10-24T12:05:00Z";
                        return Answer(HttpStatusCode.OK, connection);
                    case "DELETE" when connection is not null:
                        connection = null;
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    case "GET" when connection is not null:
                        return Answer(HttpStatusCode.OK, connection);
                    default:
                        return Answer(HttpStatusCode.NotFound, Error("not_found"));
                }
            }

            private static JsonObject Error(string code)
            {
                return new() { ["error"] = new JsonObject { ["code"] = code, ["message"] = "Synthetic " + code } };
            }

            private static HttpResponseMessage Answer(HttpStatusCode status, JsonNode body)
            {
                return new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            }
        }
    }
}
