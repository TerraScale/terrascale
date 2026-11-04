using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Features.Databases
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class DatabasePlacementRefreshJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task CanonicalPlacementRefreshesDetailAndMcp()
        {
            using var diagnostics = new InitializationDiagnostics();
            var id = "";
            var projectId = "";
            var invalid = false;
            await using var scenario = await DatabaseSdkScenario.StartAsync(fixture.Root, producerExtension: http =>
            {
                if (!Volatile.Read(ref invalid) || http.Request.Method != "GET" || http.Request.Path != "/v1/projects/" + projectId + "/databases")
                {
                    return Task.FromResult<IResult?>(null);
                }

                http.Response.Headers["X-TerraBase-Region"] = "gru";
                var items = new[] { new { id, projectId, name = "refresh", branchedFrom = (string?)null, createdAt = DateTimeOffset.UnixEpoch,
                    region = "gru", homeRegion = "gru", replicaRegions = new[] { "fra", "fra" }, allRegions = false } };
                return Task.FromResult<IResult?>(Results.Json(new { items, next = (string?)null }));
            });
            using var client = scenario.CreateClient();
            using var producer = scenario.CreateProviderClient();
            var account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(client);
            var identity = await ReadAsync(client, "/api/v1/auth/me");
            var organizationId = identity.GetProperty("organizationId").GetString()!;
            projectId = identity.GetProperty("projectId").GetString()!;
            await producer.PostAsJsonAsync("/__test/control", new { command = "catalog", value = new[] { "gru", "sfo", "fra" } });
            var csrf = await HttpJourney.CsrfAsync(client, "/databases");
            using (var created = await PostAsync("/api/v1/dashboard/databases", [new("_csrf", csrf), new("name", "refresh"), new("homeRegion", "GRU")]))
            { await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created); id = (await JsonAsync(created)).GetProperty("id").GetString()!; }
            var secret = await HttpJourney.CreatePatAsync(client, "Placement refresh", account.Password);
            // A direct detail GET must discover an external placement change without first visiting the list.
            await producer.PostAsJsonAsync("/__test/control", new { command = "add-replica", value = "fra" });
            var detail = WebUtility.HtmlDecode(await client.GetStringAsync("/databases/" + id + "/replication"));
            await Assert.That(detail).Contains("<dt>Configured regions</dt><dd>gru · São Paulo, fra · Frankfurt</dd>");

            await producer.PostAsJsonAsync("/__test/control", new { command = "add-replica", value = "sfo" });
            var rpc = (await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "get_database", arguments = new { databaseId = id, organizationId, projectId } })).GetProperty("result");
            await Assert.That(rpc.GetProperty("structuredContent").GetProperty("placement").EnumerateArray().Select(x => x.GetString()!).ToArray()).IsEquivalentTo(new[] { "GRU", "FRA", "SFO" });
            // Invalid canonical placement must leave the last validated record intact.
            Volatile.Write(ref invalid, true);
            var listed = await ManagementListJourney.ReadListAllAsync(client, "/api/v1/dashboard/databases");
            await Assert.That(listed[0].GetProperty("regions").EnumerateArray().Select(x => x.GetString()!).ToArray()).IsEquivalentTo(new[] { "GRU", "FRA", "SFO" });
            Volatile.Write(ref invalid, false);
            var page = (await HttpJourney.McpAsync(client, secret, "tools/call", new { name = "list_databases", arguments = new { organizationId, projectId, limit = 1 } })).GetProperty("result").GetProperty("structuredContent");
            await Assert.That(page.GetProperty("databases")[0].GetProperty("placement").GetArrayLength()).IsEqualTo(3);
            await Assert.That(page.GetProperty("limit").GetInt32()).IsEqualTo(1);
            await Assert.That(page.GetProperty("hasMore").GetBoolean()).IsFalse();
            await Assert.That(page.GetProperty("nextCursor").ValueKind).IsEqualTo(JsonValueKind.Null);
            var list = await client.GetStringAsync("/databases");
            await Assert.That(list).Contains("<dt class=\"ts-stat-label\"><span class=\"ts-dot\" aria-hidden=\"true\"></span>Regions</dt><dd class=\"ts-stat-value\">3</dd>");

            Task<HttpResponseMessage> PostAsync(string path, IEnumerable<KeyValuePair<string, string>> fields)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
                request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
                return client.SendAsync(request);
            }
        }

        private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
        { using var response = await client.GetAsync(path); await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK); return await JsonAsync(response); }
        private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        { using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.Clone(); }

        private sealed class InitializationDiagnostics : IDisposable
        {
            public InitializationDiagnostics()
            {
                AppDomain.CurrentDomain.FirstChanceException += Record;
            }
            private static void Record(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
            {
                if (args.Exception is not TypeInitializationException error)
                {
                    return;
                }

                var chain = new List<string>();
                for (Exception? cause = error; cause is not null; cause = cause.InnerException)
                {
                    chain.Add(cause.GetType().FullName!);
                    var methods = new System.Diagnostics.StackTrace(cause).GetFrames().Select(frame => frame.GetMethod())
                        .OfType<System.Reflection.MethodBase>().Select(method => method.DeclaringType?.FullName + "." + method.Name);
                    Console.Error.WriteLine("Initializer: " + error.TypeName + "; exception: " + cause.GetType().FullName + "; methods: " + string.Join(" > ", methods));
                }
                Console.Error.WriteLine("Initializer exception chain: " + string.Join(" > ", chain));
            }
            public void Dispose()
            {
                AppDomain.CurrentDomain.FirstChanceException -= Record;
            }
        }
    }
}
