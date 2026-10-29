using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.Databases
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class DatabaseReadinessJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task InterruptedCreationRecoversOnlyItsActualHomeAndPreservesProducerReplicas()
        {
            var rejectCreate = false;
            var organizationId = "";
            var projectId = "";
            var refusedCreates = 0;
            TestAuth auth = null!;
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, DatabaseSdkJourneyTests.CaptureAuth(value => auth = value), http =>
            {
                if (!Volatile.Read(ref rejectCreate) || http.Request.Method != "POST" || http.Request.Path != "/v1/projects/" + projectId + "/databases")
                {
                    return Task.FromResult<IResult?>(null);
                }

                DatabaseSdkJourneyTests.DemandProjectToken(auth, http, organizationId, projectId, "databases:write");
                Interlocked.Increment(ref refusedCreates);
                return Task.FromResult<IResult?>(Results.Json(new { error = new { code = "unavailable", message = "Scoped test-only producer outage", fix = "retry" } }, statusCode: 503));
            });
            using var client = scenario.CreateClient();
            using var replica = scenario.CreateClient(replica: true);
            using var producer = scenario.CreateProviderClient();
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            using var login = await HttpJourney.LoginAsync(replica, account.Email, account.Password);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var identity = await JsonAsync(client, "/api/v1/auth/me");
            organizationId = identity.GetProperty("organizationId").GetString()!;
            projectId = identity.GetProperty("projectId").GetString()!;
            await ControlAsync(producer, new { command = "catalog", value = new[] { "gru", "fra" } });
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            var replicaCsrf = await HttpJourney.CsrfAsync(replica, "/databases");
            const string name = "interrupted-home";
            Volatile.Write(ref rejectCreate, true);
            using (var unavailable = await PostAsync(client, "/api/v1/dashboard/databases", csrf, new() { ["name"] = name }, ["GRU", "FRA"]))
            {
                await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable).Because(await EvidenceAsync(unavailable, producer));
                await Assert.That(unavailable.Headers.Location is null).IsTrue();
            }
            await Assert.That(refusedCreates).IsEqualTo(1);
            await Assert.That((await JsonAsync(producer, "/__test/observations")).GetProperty("creates").GetInt32()).IsEqualTo(0);
            await Assert.That((await ManagementListJourney.ReadListAllAsync(replica, "/api/v1/dashboard/databases")).GetArrayLength()).IsEqualTo(0);
            // A region the catalog does not list cannot be chosen as a new home.
            using (var wrongHome = await PostAsync(replica, "/api/v1/dashboard/databases", replicaCsrf, new() { ["name"] = name }, ["SFO", "FRA"]))
            {
                await Assert.That(wrongHome.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity).Because(await EvidenceAsync(wrongHome, producer));
            }

            Volatile.Write(ref rejectCreate, false);
            await ControlAsync(producer, new { command = "fail-database-metadata", organizationId, projectId, name });
            await ControlAsync(producer, new { command = "pause-database-creates" });
            // SFO joins below through the producer, outside the discovered GRU/FRA placement.
            var pending = PostAsync(client, "/api/v1/dashboard/databases", csrf, new() { ["name"] = name }, ["GRU", "FRA"]);
            try
            {
                await WaitAsync(producer, "pausedCreates");
                await Assert.That((await ManagementListJourney.ReadListAllAsync(replica, "/api/v1/dashboard/databases")).GetArrayLength()).IsEqualTo(0);
            }
            finally { await ControlAsync(producer, new { command = "release-database-creates" }); }
            using (var interrupted = await pending)
            {
                await Assert.That(interrupted.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable).Because(await EvidenceAsync(interrupted, producer));
            }

            var observed = await JsonAsync(producer, "/__test/observations");
            await Assert.That(observed.GetProperty("creates").GetInt32()).IsEqualTo(1);
            await Assert.That(observed.GetProperty("failedDatabaseMetadataWrites").GetInt32()).IsEqualTo(1);
            var actualId = observed.GetProperty("databases")[0].GetProperty("id").GetString()!;
            await ControlAsync(producer, new { command = "add-replica", value = "sfo" });
            // No listing runs before the retry: it would record the database TerraBase has, as the retry does.
            // The retry omits SFO. Recovery must retain the producer's actual SFO
            // replica rather than claim this new request changed placement.
            using (var recovered = await PostAsync(replica, "/api/v1/dashboard/databases", replicaCsrf, new() { ["name"] = name }, ["GRU", "FRA"]))
            {
                await Assert.That(recovered.StatusCode).IsEqualTo(HttpStatusCode.SeeOther).Because(await EvidenceAsync(recovered, producer));
                await Assert.That(recovered.Headers.Location?.OriginalString).IsEqualTo("/databases/" + actualId + "?created=1");
            }
            var rows = await ManagementListJourney.ReadListAllAsync(client, "/api/v1/dashboard/databases");
            await Assert.That(rows.GetArrayLength()).IsEqualTo(1);
            await Assert.That(rows[0].GetProperty("id").GetString()).IsEqualTo(actualId);
            await Assert.That(rows[0].GetProperty("status").GetString()).IsEqualTo("ready");
            await Assert.That(rows[0].GetProperty("region").GetString()).IsEqualTo("GRU");
            await Assert.That(Regions(rows[0])).IsEqualTo("GRU,FRA,SFO");
            var page = await PageAsync(client, "/databases/" + actualId + "?created=1");
            await Assert.That(page).Contains(name + " is ready</p>");
            await Assert.That(page).Contains("Its home region is ready. Replica progress is shown in Replication.");
            await Assert.That(await PageAsync(client, "/databases/" + actualId + "/replication")).Contains("<dt>Configured regions</dt><dd>gru · São Paulo, fra · Frankfurt, sfo · San Francisco</dd>");
            using (var collection = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/collections", new()
            { ["_csrf"] = csrf, ["databaseId"] = actualId, ["name"] = "users" }))
            {
                await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
            }

            using (var document = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/data/documents", new()
            { ["_csrf"] = csrf, ["databaseId"] = actualId, ["collection"] = "users", ["id"] = "01K78D0C000000000000000013", ["operation"] = "create", ["document"] = "{\"ready\":true}" }))
            {
                await Assert.That(document.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await EvidenceAsync(document, producer));
            }

            var documents = await JsonAsync(replica, "/api/v1/dashboard/data/documents?databaseId=" + actualId + "&collection=users");
            await Assert.That(documents.GetProperty("documents")[0].GetProperty("value").GetProperty("ready").GetBoolean()).IsTrue();
            await DeleteAsync(client, csrf, actualId, name);
            observed = await JsonAsync(producer, "/__test/observations");
            await Assert.That(observed.GetProperty("creates").GetInt32()).IsEqualTo(1);
            await Assert.That(observed.GetProperty("databases").GetArrayLength()).IsEqualTo(0);
            await Assert.That(observed.GetProperty("failures").GetArrayLength()).IsEqualTo(0);
        }

        [Test]
        [Arguments("id")]
        [Arguments("project")]
        [Arguments("name")]
        [Arguments("home")]
        [Arguments("replicas")]
        [Arguments("missing-replicas")]
        public async Task NativePlacementRefusesAnUnconfirmedProducerResponse(string mismatch)
        {
            var invalid = false;
            var organizationId = "";
            var projectId = "";
            var id = "";
            var name = "confirmed-placement";
            TestAuth auth = null!;
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, DatabaseSdkJourneyTests.CaptureAuth(value => auth = value), http =>
            {
                if (!Volatile.Read(ref invalid) || http.Request.Method != "PATCH" || http.Request.Path != "/v1/projects/" + projectId + "/databases/" + name)
                {
                    return Task.FromResult<IResult?>(null);
                }

                DatabaseSdkJourneyTests.DemandProjectToken(auth, http, organizationId, projectId, "databases:write");
                http.Response.Headers["X-TerraBase-Region"] = "gru";
                var value = new Dictionary<string, object?>
                {
                    ["id"] = mismatch == "id" ? "01K78000000000000000000000" : id,
                    ["projectId"] = mismatch == "project" ? "01K78000000000000000000001" : projectId,
                    ["name"] = mismatch == "name" ? "another-database" : name,
                    ["createdAt"] = DateTimeOffset.UtcNow,
                    ["region"] = "gru",
                    ["homeRegion"] = mismatch == "home" ? "sfo" : "gru",
                    ["replicaRegions"] = mismatch == "replicas" ? new[] { "sfo" } : new[] { "fra" }
                };
                if (mismatch == "missing-replicas")
                {
                    value.Remove("replicaRegions");
                }

                return Task.FromResult<IResult?>(Results.Json(value));
            });
            using var client = scenario.CreateClient();
            using var producer = scenario.CreateProviderClient();
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var identity = await JsonAsync(client, "/api/v1/auth/me");
            organizationId = identity.GetProperty("organizationId").GetString()!;
            projectId = identity.GetProperty("projectId").GetString()!;
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            id = await CreateAsync(client, csrf, name);
            await ControlAsync(producer, new { command = "catalog", value = new[] { "gru", "sfo", "fra" } });
            Volatile.Write(ref invalid, true);
            // A valid two-region request reaches the producer; only the chosen response field is incorrect.
            using (var response = await PostAsync(client, "/api/v1/dashboard/databases/regions", csrf, new() { ["databaseId"] = id }, ["GRU", "FRA"]))
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable).Because(await EvidenceAsync(response, producer));
            }

            var database = (await ManagementListJourney.ReadListAllAsync(client, "/api/v1/dashboard/databases"))[0];
            await Assert.That(database.GetProperty("id").GetString()).IsEqualTo(id);
            await Assert.That(database.GetProperty("name").GetString()).IsEqualTo(name);
            await Assert.That(database.GetProperty("region").GetString()).IsEqualTo("GRU");
            await Assert.That(Regions(database)).IsEqualTo("GRU,SFO");
            Volatile.Write(ref invalid, false);
            await DeleteAsync(client, csrf, id, name);
            await Assert.That((await JsonAsync(producer, "/__test/observations")).GetProperty("failures").GetArrayLength()).IsEqualTo(0);
        }

        // TerraBase DatabaseRegions.Misdirected (terrascale e30ffdc2): a region that does not manage the database answers
        // 421 wrong_region with X-TerraBase-Database-Home/-Regions. The dashboard pins its request, reports the home, and
        // neither retries nor redirects nor changes its record.
        [Test]
        public async Task WrongRegionAnswerIsReportedAndNeverRetried()
        {
            var misdirect = false;
            var organizationId = "";
            var projectId = "";
            var id = "";
            var attempts = new System.Collections.Concurrent.ConcurrentQueue<string>();
            const string name = "wrong-region";
            TestAuth auth = null!;
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, DatabaseSdkJourneyTests.CaptureAuth(value => auth = value), http =>
            {
                if (!Volatile.Read(ref misdirect) || http.Request.Method != "PATCH" || http.Request.Path != "/v1/projects/" + projectId + "/databases/" + name)
                {
                    return Task.FromResult<IResult?>(null);
                }

                DatabaseSdkJourneyTests.DemandProjectToken(auth, http, organizationId, projectId, "databases:write");
                attempts.Enqueue(http.Request.Host.Host + " pin=" + http.Request.Headers["X-TerraScale-Region-Pin"]);
                http.Response.Headers["X-TerraBase-Region"] = "gru";
                http.Response.Headers["X-TerraBase-Database-Home"] = "sfo";
                http.Response.Headers["X-TerraBase-Database-Regions"] = "sfo,gru";
                http.Response.Headers.CacheControl = "no-store";
                return Task.FromResult<IResult?>(Results.Json(new { error = new { code = "wrong_region", message = "the database is served in sfo" } }, statusCode: 421));
            });
            using var client = scenario.CreateClient();
            using var producer = scenario.CreateProviderClient();
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var identity = await JsonAsync(client, "/api/v1/auth/me");
            organizationId = identity.GetProperty("organizationId").GetString()!;
            projectId = identity.GetProperty("projectId").GetString()!;
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            id = await CreateAsync(client, csrf, name);
            await ControlAsync(producer, new { command = "catalog", value = new[] { "gru", "sfo" } });
            Volatile.Write(ref misdirect, true);
            using (var response = await PostAsync(client, "/api/v1/dashboard/databases/regions", csrf, new() { ["databaseId"] = id }, ["GRU", "SFO"]))
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict).Because(await EvidenceAsync(response, producer));
                var html = System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
                await Assert.That(html).Contains("The database is managed in sfo · San Francisco. Nothing was changed.");
            }
            await Assert.That(string.Join(";", attempts)).IsEqualTo("gru.api.terrascale.tech pin=gru").Because("one pinned request, no retry or redirect");
            var database = (await ManagementListJourney.ReadListAllAsync(client, "/api/v1/dashboard/databases"))[0];
            await Assert.That(database.GetProperty("region").GetString()).IsEqualTo("GRU");
            await Assert.That(Regions(database)).IsEqualTo("GRU,SFO");
            Volatile.Write(ref misdirect, false);
            await DeleteAsync(client, csrf, id, name);
            await Assert.That((await JsonAsync(producer, "/__test/observations")).GetProperty("failures").GetArrayLength()).IsEqualTo(0);
        }

        // terrascale e30ffdc2 HealthzEndpoint and region-identity.md: /healthz names its region in the body and the
        // X-TerraBase-Region header. Without the /v1/regions catalog, an answer that does not name one valid region, or a
        // refused one, provisions nothing.
        [Test]
        [Arguments("missing-region")]
        [Arguments("invalid-region")]
        [Arguments("host-region")]
        [Arguments("header-mismatch")]
        [Arguments("missing-header")]
        [Arguments("rate-limited")]
        [Arguments("unavailable")]
        public async Task InvalidRegionAnswerNeverProvisions(string defect)
        {
            var invalid = false;
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
            {
                if (!Volatile.Read(ref invalid) || http.Request.Host.Host != "api.terrascale.tech" || http.Request.Path.Value is not ("/healthz" or "/v1/regions"))
                {
                    return Task.FromResult<IResult?>(null);
                }
                // A TerraBase without the region catalog: the healthz region is the only one the dashboard can verify.
                if (http.Request.Path.Value == "/v1/regions")
                {
                    return Task.FromResult<IResult?>(Results.NotFound());
                }

                if (defect != "missing-header")
                {
                    http.Response.Headers["X-TerraBase-Region"] = defect == "header-mismatch" ? "sfo" : "gru";
                }

                if (defect == "rate-limited")
                {
                    http.Response.Headers.RetryAfter = "30";
                    return Task.FromResult<IResult?>(Results.Json(new { error = new { code = "rate_limited", message = "too many requests" } }, statusCode: 429));
                }
                if (defect == "unavailable")
                {
                    return Task.FromResult<IResult?>(Results.Json(new { error = new { code = "region_unavailable", message = "no machine serves the region" } }, statusCode: 503));
                }

                var body = new Dictionary<string, object?> { ["status"] = "ok", ["version"] = "test", ["storageFormat"] = "test", ["revision"] = null, ["machine"] = "local" };
                if (defect != "missing-region")
                {
                    body["region"] = defect switch { "invalid-region" => "GRU", "host-region" => "gru.attacker.example", _ => "gru" };
                }

                return Task.FromResult<IResult?>(Results.Json(body));
            });
            using var client = scenario.CreateClient();
            using var producer = scenario.CreateProviderClient();
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            Volatile.Write(ref invalid, true);
            using (var response = await PostAsync(client, "/api/v1/dashboard/databases", csrf, new() { ["name"] = "catalog-refused" }, ["GRU"]))
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable).Because(await EvidenceAsync(response, producer));
            }

            Volatile.Write(ref invalid, false);
            await Assert.That((await ManagementListJourney.ReadListAllAsync(client, "/api/v1/dashboard/databases")).GetArrayLength()).IsEqualTo(0);
            await Assert.That((await JsonAsync(producer, "/__test/observations")).GetProperty("creates").GetInt32()).IsEqualTo(0);
            var id = await CreateAsync(client, csrf, "catalog-refused");
            await DeleteAsync(client, csrf, id, "catalog-refused");
            await Assert.That((await JsonAsync(producer, "/__test/observations")).GetProperty("failures").GetArrayLength()).IsEqualTo(0);
        }

        [Test]
        [Arguments("provisioning")]
        [Arguments("accepted")]
        [Arguments("pending")]
        [Arguments("failed")]
        public async Task CreatedNoticeUsesTheRecordedStatusInsteadOfAlwaysClaimingReady(string status)
        {
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root);
            using var client = scenario.CreateClient();
            using var producer = scenario.CreateProviderClient();
            await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var identity = await JsonAsync(client, "/api/v1/auth/me");
            var organizationId = identity.GetProperty("organizationId").GetString()!;
            var projectId = identity.GetProperty("projectId").GetString()!;
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            const string name = "status-notice";
            var id = await CreateAsync(client, csrf, name);
            var ready = await PageAsync(client, "/databases/" + id + "?created=1");
            await Assert.That(ready).Contains("ts-db-created ts-alert-success\" data-status=\"ready\"");
            await Assert.That(ready).Contains(name + " is ready</p>");
            await ControlAsync(producer, new { command = "database-status", organizationId, projectId, databaseId = id, value = status });
            var page = await PageAsync(client, "/databases/" + id + "?created=1");
            await Assert.That(page).Contains("ts-db-created ts-alert-" + (status == "failed" ? "danger" : "info") + "\" data-status=\"" + status + "\"");
            await Assert.That(page).Contains(name + (status == "failed" ? " could not be created</p>" : " is being created</p>"));
            await Assert.That(page.Contains(name + " is ready</p>", StringComparison.Ordinal)).IsFalse();
            var list = await PageAsync(client, "/databases");
            var total = System.Text.RegularExpressions.Regex.Match(list,
                "<div class=\"ts-stat\" data-tone=\"warning\">.*?<dd class=\"ts-stat-value\">([0-9]+)</dd>",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            await Assert.That(total.Success).IsTrue();
            await Assert.That(total.Groups[1].Value).IsEqualTo(status == "failed" ? "0" : "1")
                .Because("The original native summary counts provisioning, accepted and pending as in progress.");
            await Assert.That(list).Contains(status);
            await DeleteAsync(client, csrf, id, name);
            await Assert.That((await JsonAsync(producer, "/__test/observations")).GetProperty("failures").GetArrayLength()).IsEqualTo(0);
        }

        private static async Task<string> CreateAsync(HttpClient client, string csrf, string name)
        {
            using var response = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases", new() { ["_csrf"] = csrf, ["name"] = name, ["region"] = "GRU" });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await response.Content.ReadAsStringAsync());
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("id").GetString()!;
        }
        private static async Task DeleteAsync(HttpClient client, string csrf, string id, string name)
        {
            using var deleted = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/databases/delete", new()
            { ["_csrf"] = csrf, ["databaseId"] = id, ["confirmName"] = name });
            await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await deleted.Content.ReadAsStringAsync());
            await Assert.That((await ManagementListJourney.ReadListAllAsync(client, "/api/v1/dashboard/databases")).GetArrayLength()).IsEqualTo(0);
        }
        private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string csrf, Dictionary<string, string> fields, string[] regions)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Accept.ParseAdd("text/html");
            request.Content = new FormUrlEncodedContent(fields.Append(new("_csrf", csrf)).Concat(regions.Select(region => new KeyValuePair<string, string>("region", region))));
            return await client.SendAsync(request);
        }
        private static async Task<JsonElement> JsonAsync(HttpClient client, string path)
        {
            using var response = await client.GetAsync(path);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await response.Content.ReadAsStringAsync());
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone();
        }
        private static async Task<string> PageAsync(HttpClient client, string path)
        { using var response = await client.GetAsync(path); await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK); return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()); }
        private static async Task ControlAsync(HttpClient producer, object body)
        { using var response = await producer.PostAsJsonAsync("/__test/control", body); await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent).Because(await response.Content.ReadAsStringAsync()); }
        private static string Regions(JsonElement database)
        {
            return string.Join(',', database.GetProperty("regions").EnumerateArray().Select(region => region.GetString()));
        }

        private static async Task<string> EvidenceAsync(HttpResponseMessage response, HttpClient producer)
        { var body = await response.Content.ReadAsStringAsync(); return body[..Math.Min(body.Length, 1000)] + "; producer failures: " + (await JsonAsync(producer, "/__test/observations")).GetProperty("failures").GetRawText(); }
        private static async Task WaitAsync(HttpClient producer, string counter)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while ((await JsonAsync(producer, "/__test/observations")).GetProperty(counter).GetInt32() == 0)
            {
                await Task.Delay(20, deadline.Token);
            }
        }
    }
}
