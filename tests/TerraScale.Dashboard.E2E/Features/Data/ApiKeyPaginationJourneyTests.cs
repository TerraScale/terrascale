using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TerraScale.Dashboard.E2E.Features.Data
{
    // The credential list is bounded: one issuer page per request, traversal by the issuer's own cursor.
    [ClassDataSource<DashboardRootFixture>]
    public sealed class ApiKeyPaginationJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task CredentialPagesTraverseEveryKeyExactlyOnceAndARepeatedCursorEndsTheList()
        {
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            var issuer = new PagingIssuer();
            await using var app = environment.CreateTestHost(fixture.Root, 0, "http://127.0.0.1", builder =>
            {
                var transport = (IHttpClientFactory)builder.Services.Last(service => service.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
                issuer.Inner = transport;
                builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(issuer));
            });
            await app.StartAsync();
            using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() })
            { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
            using var login = await HttpJourney.LoginAsync(client);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);

            var seen = new List<string>();
            string? after = null;
            var pages = 0;
            while (true)
            {
                using var response = await client.GetAsync("/api/v1/dashboard/credentials?limit=2" + (after is null ? "" : "&after=" + Uri.EscapeDataString(after)));
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new InvalidOperationException("Page " + pages + " failed: " + await response.Content.ReadAsStringAsync());
                }

                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                foreach (var item in body.RootElement.GetProperty("items").EnumerateArray())
                {
                    seen.Add(item.GetProperty("keyId").GetString()!);
                }

                pages++;
                after = body.RootElement.GetProperty("nextCursor").GetString();
                if (body.RootElement.GetProperty("hasMore").GetBoolean() is false)
                {
                    await Assert.That(after).IsNull();
                    break;
                }
            }

            await Assert.That(pages).IsEqualTo(3);
            // The duplicate row the issuer repeats inside page two is dropped, not surfaced twice.
            await Assert.That(seen.Order(StringComparer.Ordinal).ToArray()).IsEqualTo(PagingIssuer.TraversedIds.Order(StringComparer.Ordinal).ToArray()).Because("no key may be omitted or repeated");
            await Assert.That(seen.Count).IsEqualTo(PagingIssuer.TraversedIds.Length);
            await Assert.That(issuer.ListReads).IsEqualTo(3);
            // A cursor the issuer keeps repeating terminates the list instead of looping.
            using var repeat = await client.GetAsync("/api/v1/dashboard/credentials?limit=2&after=" + Uri.EscapeDataString("stuck"));
            await Assert.That(repeat.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var repeated = JsonDocument.Parse(await repeat.Content.ReadAsStringAsync());
            await Assert.That(repeated.RootElement.GetProperty("hasMore").GetBoolean()).IsFalse();
            await Assert.That(repeated.RootElement.GetProperty("nextCursor").ValueKind).IsEqualTo(JsonValueKind.Null);

            // The page UI pages with plain links: page one shows Next, page two shows Next and First page.
            using var ui = await client.GetAsync("/api-keys");
            var html = await ui.Content.ReadAsStringAsync();
            await Assert.That(ui.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(html).Contains("href=\"/api-keys?cursor=cursor-2\"").Because("the Next link carries the issuer cursor");
            using var second = await client.GetAsync("/api-keys?cursor=cursor-2");
            var secondHtml = await second.Content.ReadAsStringAsync();
            await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(secondHtml).Contains("href=\"/api-keys?cursor=cursor-3\"").Because("the Next link keeps the issuer cursor");
            await Assert.That(secondHtml).Contains("href=\"/api-keys\"").Because("First page returns to the first keys");
        }

        // Three pages: 2 keys, a page repeating one earlier key beside a new one, then 2 more; a "stuck" cursor repeats itself.
        private sealed class PagingIssuer : IHttpClientFactory
        {
            public static readonly string[] TraversedIds = ["01JD000000000000KEY0000000", "01JD000000000000KEY0010000", "01JD000000000000KEY0020000", "01JD000000000000KEY0030000", "01JD000000000000KEY0040000"];
            public IHttpClientFactory? Inner { get; set; }
            public int ListReads { get; private set; }

            public HttpClient CreateClient(string name)
            {
                return new(new Transport(this, Inner!.CreateClient(name)));
            }

            private sealed class Transport(PagingIssuer owner, HttpClient inner) : HttpMessageHandler
            {
                protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                {
                    var uri = request.RequestUri!;
                    if (uri.Host != "auth.terrascale.tech" || !uri.AbsolutePath.EndsWith("/api-keys", StringComparison.Ordinal))
                    {
                        return await HttpForwarding.SendAsync(inner, request, cancellationToken);
                    }

                    owner.ListReads++;
                    if (request.Method != HttpMethod.Get)
                    {
                        return await HttpForwarding.SendAsync(inner, request, cancellationToken);
                    }
                    var match = System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath, "/organizations/([^/]+)/projects/([^/]+)/api-keys$");
                    var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
                    var after = query.GetValueOrDefault("after").ToString();
                    var limit = int.Parse(query.GetValueOrDefault("limit").ToString(), System.Globalization.CultureInfo.InvariantCulture);
                    (string[] Ids, string? Next) page = after switch
                    {
                        "" => (TraversedIds[..2], "cursor-2"),
                        "cursor-2" => (new[] { TraversedIds[2], TraversedIds[2], TraversedIds[3] }, "cursor-3"),
                        "cursor-3" => (TraversedIds[3..5], null),
                        _ => ([], "stuck"),
                    };
                    var items = page.Ids.Take(limit).ToArray();
                    var next = items.Length < page.Ids.Length ? after : page.Next;
                    return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        items = items.Select(id => new
                        {
                            id,
                            name = "Key " + id[^1],
                            organization_id = match.Groups[1].Value,
                            project_id = match.Groups[2].Value,
                            database = (string?)null,
                            scopes = new[] { "documents:read" },
                            created_by = "user",
                            created_at = "2026-01-01T00:00:00+00:00",
                            expires_at = (string?)null,
                            previous_secret_expires_at = (string?)null,
                            last_used_at = (DateTimeOffset?)null,
                            allowed_cidrs = Array.Empty<string>()
                        }),
                        next
                    }), System.Text.Encoding.UTF8, "application/json") };
                }
                protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); } base.Dispose(disposing); }
            }
        }
    }
}
