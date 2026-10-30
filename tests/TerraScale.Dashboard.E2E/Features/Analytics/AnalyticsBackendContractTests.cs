using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.Features.Analytics;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.E2E.Features.Analytics
{
    public sealed class AnalyticsBackendContractTests
    {
        [Test]
        [Arguments(false)]
        [Arguments(true)]
        public async Task SixDenseDatabaseRegionSeriesRemainMeasuredBeyondTheSmallPlatformEnvelope(bool streaming)
        {
            var selection = Selection("1h") with { DatabaseId = "", Region = "" };
            var project = selection.Context.Project!;
            foreach (var id in new[] { "01JA0000000000000000000005", "01JA0000000000000000000006" })
            {
                project.Databases.Add(id, new() { Id = id, ProjectId = project.Id, Name = "app-" + id, Region = "GRU" });
            }

            foreach (var database in project.Databases.Values) { database.Regions = ["GRU", "SJC"]; database.Name = "app"; }
            var to = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
            var from = to.AddHours(-1);
            var items = project.Databases.Keys.SelectMany(id => new[] { "gru", "sjc" }.Select(region => new
            {
                databaseId = id,
                region,
                series = Enumerable.Range(0, 60).Select(minute => new
                {
                    start = from.AddMinutes(minute),
                    requests = new { GET = 10L },
                    operations = new { read = 4L },
                    errors = 1L,
                    latency = new { p50Ms = .125d, p95Ms = 256d, p99Ms = 300d },
                    storageBytes = 4096L,
                    clientBandwidth = new { bytesSent = 13L, bytesReceived = 17L },
                    replicationBandwidth = new { bytesSent = 8L, bytesReceived = 9L }
                }).ToArray()
            })).ToArray();
            var payload = JsonSerializer.Serialize(new { projectId = project.Id, range = "1h", from, to, bucketSeconds = 60, items });
            await Assert.That(Encoding.UTF8.GetByteCount(payload) > 65536).IsTrue();
            using var wire = new Wire { Payload = payload, Streaming = streaming };
            await using var wireRpc = await TestRestRpcHost.StartAsync(wire);
            var tokens = new Tokens(selection);

            var result = await new AnalyticsBackend(wireRpc.Client, tokens, new DatabaseBackend(wireRpc.Client, tokens)).ReadAsync(selection, CancellationToken.None);

            await Assert.That(result.State).IsEqualTo(AnalyticsSourceState.Available);
            await Assert.That(result.Points.Count).IsEqualTo(360);
            await Assert.That(result.Points.Sum(point => point.Requests["GET"])).IsEqualTo(3600);
            await Assert.That(result.Points.Select(point => (point.DatabaseId, point.Region)).Distinct().Count()).IsEqualTo(6);
            var latency = AnalyticsService.Build(selection, result.Points).Charts.Single(chart => chart.Title.Contains("latency", StringComparison.Ordinal));
            await Assert.That(latency.Plot.Paths.Count).IsEqualTo(18).Because("equal database names cannot overwrite each other's percentiles");
            await Assert.That(latency.Plot.Paths.Select(path => path.Name).Distinct().Count()).IsEqualTo(18);
            await Assert.That(latency.Title).Contains("(ms)");
            await Assert.That(latency.Plot.Columns.SelectMany(column => column.Rows).Any(row => row.Name.StartsWith("p95", StringComparison.Ordinal) && row.Value == "256 ms")).IsTrue();
        }

        [Test]
        public async Task OversizedRpcAnalyticsResponseStaysUnavailable()
        {
            using var wire = new Wire { Payload = new string('x', TerraScale.TerraBase.Contracts.TerraBaseRpc.MaxMessageBytes + 1) };
            await using var wireRpc = await TestRestRpcHost.StartAsync(wire);
            var selection = Selection("1h");
            var tokens = new Tokens(selection);
            HttpRequestException? refused = null;
            try { await new AnalyticsBackend(wireRpc.Client, tokens, new DatabaseBackend(wireRpc.Client, tokens)).ReadAsync(selection, CancellationToken.None); }
            catch (HttpRequestException error) { refused = error; }
            await Assert.That(refused).IsNotNull();
            await Assert.That(wire.Reads).IsEqualTo(1);
        }

        [Test]
        [Arguments("1h", 60, 3600)]
        [Arguments("24h", 3600, 86400)]
        [Arguments("7d", 21600, 604800)]
        [Arguments("30d", 86400, 2592000)]
        public async Task EachAnalyticsReadRequestsOnlyCurrentDatabaseReadAuthorityAndPreservesFilters(string range, int bucketSeconds, int seconds)
        {
            using var wire = new Wire { BucketSeconds = bucketSeconds, Seconds = seconds };
            await using var wireRpc = await TestRestRpcHost.StartAsync(wire);
            var selection = Selection(range);
            var tokens = new Tokens(selection);
            var backend = new AnalyticsBackend(wireRpc.Client, tokens, new DatabaseBackend(wireRpc.Client, tokens));

            for (var index = 0; index < 2; index++)
            {
                var result = await backend.ReadAsync(selection, CancellationToken.None);
                await Assert.That(result.State).IsEqualTo(AnalyticsSourceState.Available);
                await Assert.That(result.Points.Count).IsEqualTo(0);
            }

            await Assert.That(tokens.Calls).IsEqualTo(2);
            await Assert.That(wire.Reads).IsEqualTo(2);
            await Assert.That(wire.Bearers).IsEquivalentTo(new[] { "analytics-project-1", "analytics-project-2" });
            await Assert.That(wire.Path).IsEqualTo("/v1/projects/" + selection.Context.Project!.Id + "/analytics");
            var query = QueryHelpers.ParseQuery(wire.Query);
            await Assert.That(query.Count).IsEqualTo(3);
            await Assert.That(query["range"].ToString()).IsEqualTo(range);
            await Assert.That(query["database"].ToString()).IsEqualTo(selection.DatabaseId);
            await Assert.That(query["region"].ToString()).IsEqualTo("gru");
        }

        [Test]
        [Arguments(401)]
        [Arguments(403)]
        [Arguments(404)]
        [Arguments(429)]
        [Arguments(503)]
        [Arguments(0)]
        public async Task RefusedExchangeNeverSendsAnalyticsOrBecomesAnEmptyMeasurement(int status)
        {
            using var wire = new Wire();
            await using var wireRpc = await TestRestRpcHost.StartAsync(wire);
            var selection = Selection("1h");
            var tokens = new Tokens(selection) { Refusal = status };
            var backend = new AnalyticsBackend(wireRpc.Client, tokens, new DatabaseBackend(wireRpc.Client, tokens));
            HttpRequestException? refused = null;

            try { await backend.ReadAsync(selection, CancellationToken.None); }
            catch (HttpRequestException error) { refused = error; }

            await Assert.That(refused).IsNotNull();
            await Assert.That(refused!.StatusCode).IsEqualTo(status == 0 ? (HttpStatusCode?)null : (HttpStatusCode)status);
            await Assert.That(tokens.Calls).IsEqualTo(1);
            await Assert.That(wire.Reads).IsEqualTo(0);
        }

        [Test]
        public async Task HistoricalRegionsRemainSelectedAcrossDatabaseChanges()
        {
            var context = Selection("24h").Context;
            var organization = context.Organization!;
            organization.Members["user"] = new() { UserId = "user", Email = "synthetic@example.test", Role = "owner" };
            var project = context.Project!;
            var orders = project.Databases.Values.Single();
            orders.Regions = ["GRU", "SFO"];
            const string archive = "01JA0000000000000000000007";
            project.Databases.Add(archive, new() { Id = archive, ProjectId = project.Id, Name = "archive", Region = "SFO", Regions = ["SFO", "SJC"] });
            await Assert.That(AnalyticsSelection.For(context, project.Id, archive, "GRU", "24h").Region).IsEqualTo("GRU");
            await Assert.That(AnalyticsSelection.For(context, project.Id, archive, "sjc", "24h").Region).IsEqualTo("SJC");
            await Assert.That(AnalyticsSelection.For(context, project.Id, orders.Id, "gru", "24h").Region).IsEqualTo("GRU");
            var unknown = Assert.Throws<AccountAccessException>(() => AnalyticsSelection.For(context, project.Id, archive, "bad/region", "24h"));
            await Assert.That(unknown.StatusCode).IsEqualTo(400);
        }

        private static AnalyticsSelection Selection(string range)
        {
            var project = new ProjectRecord { Id = "01JA0000000000000000000001", OrganizationId = "01JA0000000000000000000002", Name = "project" };
            var database = new DatabaseRecord { Id = "01JA0000000000000000000004", ProjectId = project.Id, Name = "app", Region = "GRU" };
            project.Databases.Add(database.Id, database);
            var organization = new OrganizationRecord { Id = project.OrganizationId };
            organization.Projects.Add(project.Id, project);
            var context = new AccountContext(new("user", "Synthetic", "synthetic@example.test", "original-user-token", null, DateTimeOffset.UtcNow.AddMinutes(5)),
                new() { UserId = "user" }, [organization], organization, project);
            return new(context, range, database.Id, "GRU");
        }

        private sealed class Tokens(AnalyticsSelection selection) : IProjectAccessTokens
        {
            public int Calls { get; private set; }
            public int? Refusal { get; init; }
            public Task<string> GetAsync(SessionRecord session, string organizationId, string projectId, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
            {
                if (!ReferenceEquals(session, selection.Context.Session) || session.AccessToken != "original-user-token" ||
                    organizationId != selection.Context.Organization!.Id || projectId != selection.Context.Project!.Id ||
                    scopes.Count != 1 || scopes[0] != "databases:read" || cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException("Analytics requested authority outside the current read scope.");
                }

                Calls++;
                if (Refusal is { } status)
                {
                    throw new HttpRequestException("Synthetic exchange refusal.", null, status == 0 ? null : (HttpStatusCode)status);
                }

                return Task.FromResult("analytics-project-" + Calls);
            }
        }

        private sealed class Wire : HttpMessageHandler, IHttpClientFactory
        {
            public string? Payload { get; init; }
            public bool Streaming { get; init; }
            public int BucketSeconds { get; init; } = 60;
            public int Seconds { get; init; } = 3600;
            public int Reads { get; private set; }
            public List<string> Bearers { get; } = [];
            public string Path { get; private set; } = "";
            public string Query { get; private set; } = "";
            public HttpClient CreateClient(string name)
            {
                return new(this, false);
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method != HttpMethod.Get || request.RequestUri!.Host != "api.terrascale.tech")
                {
                    throw new InvalidOperationException("Unexpected analytics origin or method.");
                }

                if (request.RequestUri.AbsolutePath == "/v1/regions")
                {
                    if (request.Headers.Authorization is not null)
                    {
                        throw new InvalidOperationException("Region catalog read received a user token.");
                    }

                    return Task.FromResult(Response("{\"regions\":[\"gru\"]}"));
                }
                if (request.RequestUri.AbsolutePath == "/healthz")
                {
                    if (request.Headers.Authorization is not null)
                    {
                        throw new InvalidOperationException("Health read received a user token.");
                    }

                    var health = Response("{\"region\":\"gru\"}");
                    health.Headers.Add("X-TerraBase-Region", "gru");
                    return Task.FromResult(health);
                }
                Path = request.RequestUri.AbsolutePath;
                Query = request.RequestUri.Query;
                if (Path != "/v1/projects/01JA0000000000000000000001/analytics" || request.Headers.Authorization?.Scheme != "Bearer")
                {
                    throw new InvalidOperationException("Unexpected analytics contract request.");
                }

                Bearers.Add(request.Headers.Authorization.Parameter!);
                Reads++;
                if (Payload is not null)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = Streaming ? new StreamingContent(Encoding.UTF8.GetBytes(Payload)) : new StringContent(Payload, Encoding.UTF8, "application/json") });
                }

                var query = QueryHelpers.ParseQuery(Query);
                var to = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
                var response = Response(JsonSerializer.Serialize(new
                {
                    projectId = "01JA0000000000000000000001",
                    range = query["range"].ToString(),
                    from = to.AddSeconds(-Seconds),
                    to,
                    bucketSeconds = BucketSeconds,
                    items = Array.Empty<object>()
                }));
                return Task.FromResult(response);
            }
            private static HttpResponseMessage Response(string body)
            {
                return new(HttpStatusCode.OK)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }
        }

        private sealed class StreamingContent(byte[] body) : HttpContent
        {
            protected override bool TryComputeLength(out long length) { length = 0; return false; }
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                return stream.WriteAsync(body).AsTask();
            }

            protected override Task<Stream> CreateContentReadStreamAsync()
            {
                return Task.FromResult<Stream>(new MemoryStream(body, false));
            }
        }
    }
}
