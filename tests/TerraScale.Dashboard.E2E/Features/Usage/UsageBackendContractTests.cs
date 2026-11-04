using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Usage;

namespace TerraScale.Dashboard.E2E.Features.Usage
{
    public sealed class UsageBackendContractTests
    {
        [Test]
        public async Task ForeignDatabaseIdentityCannotBecomeUsageForSelectedDatabase()
        {
            using var wire = new Wire { Foreign = true };
            await using var wireRpc = await TestRestRpcHost.StartAsync(wire);
            var result = await new UsageBackend(wireRpc.Client, new Clock(), new Tokens()).ReadAsync(Selection(), CancellationToken.None);
            await Assert.That(result.Scope.State).IsEqualTo(UsageSourceState.Unavailable);
            await Assert.That(result.Scope.Reading).IsNull();
        }

        private static UsageSelection Selection()
        {
            var project = new ProjectRecord { Id = "01JA0000000000000000000001", OrganizationId = "01JA0000000000000000000002", Name = "project" };
            var database = new DatabaseRecord { Id = "01JA0000000000000000000004", ProjectId = project.Id, Name = "app" };
            project.Databases.Add(database.Id, database);
            var organization = new OrganizationRecord { Id = project.OrganizationId };
            organization.Projects.Add(project.Id, project);
            var context = new AccountContext(new("user", "Synthetic", "synthetic@example.test", "original", null, Clock.Now.AddMinutes(5)),
                new() { UserId = "user" }, [organization], organization, project);
            return new(context, "1h", database.Id, true);
        }
        private sealed class Clock : TimeProvider
        {
            public static readonly DateTimeOffset Now = new(2026, 10, 12, 0, 15, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow()
            {
                return Now;
            }
        }
        private sealed class Tokens(bool missing = false) : IProjectAccessTokens
        {
            public Task<string> GetAsync(SessionRecord session, string organizationId, string projectId, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
            {
                if (missing)
                {
                    throw new HttpRequestException("Project token target unavailable.", null, HttpStatusCode.NotFound);
                }

                if (scopes.Single() != "databases:read")
                {
                    throw new InvalidOperationException("Wrong meter authority.");
                }

                return Task.FromResult("meter-project-token");
            }
        }
        private sealed class Wire : HttpMessageHandler, IHttpClientFactory
        {
            public int Failure { get; init; }
            public bool Foreign { get; init; }
            public string? Month { get; init; } = "2026-10";
            public long FileBytes { get; init; }
            public int SeriesMode { get; init; }
            public int Requests;
            public HashSet<string> Allowed { get; } = [];
            public HttpClient CreateClient(string name)
            {
                return new(this, false);
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Requests);
                var path = request.RequestUri!.AbsolutePath;
                var database = path == "/v1/projects/01JA0000000000000000000001/databases/app/usage";
                if (!database && path != "/v1/projects/01JA0000000000000000000001/usage" && !Allowed.Contains(path) || request.Headers.Authorization?.Parameter != "meter-project-token")
                {
                    throw new InvalidOperationException("Noncanonical meter request.");
                }

                if (Failure > 0)
                {
                    return Task.FromResult(Response((HttpStatusCode)Failure, "{\"error\":{\"code\":\"invalid_query\",\"message\":\"failed\"}}"));
                }

                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                var granularity = query["granularity"].ToString();
                var from = DateTimeOffset.Parse(query["from"].ToString(), CultureInfo.InvariantCulture);
                var to = granularity == "day" ? Clock.Now.AddDays(1).Date : Clock.Now.AddMinutes(45).UtcDateTime;
                var period = new { from, to = new DateTimeOffset(to, TimeSpan.Zero), granularity, reads = 12, writes = 3 };
                var step = granularity == "day" ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1);
                var series = Enumerable.Range(0, (int)((new DateTimeOffset(to, TimeSpan.Zero) - from).Ticks / step.Ticks))
                    .Where(index => SeriesMode == 0 || SeriesMode == 2 && index == 0)
                    .Select(index => new { start = from + step * index, reads = index == 0 ? 12 : 0, writes = index == 0 ? 3 : 0 }).ToArray();
                var databaseId = Foreign ? "01JA0000000000000000000005" : "01JA0000000000000000000004";
                var row = new { databaseId, storageBytes = 2048, logicalBytes = 1024, collections = 1, documents = 2, reads = 22, writes = 5, period, series };
                var child = new { databaseId, storageBytes = 2048, logicalBytes = 1024, collections = 1, documents = 2, reads = 22, writes = 5, period, series = Array.Empty<object>() };
                var month = Month is null ? null : new { month = Month, readUnits = 6, writeUnits = 3, units = 9, organizationUnits = 29 };
                var value = database ? JsonSerializer.Serialize(row) : JsonSerializer.Serialize(new
                { projectId = "01JA0000000000000000000001", databases = 1, storageBytes = 2048 + FileBytes, logicalBytes = 1024 + FileBytes, fileBytes = FileBytes, documents = 2, reads = 22, writes = 5, period, series, items = new[] { child }, month });
                return Task.FromResult(Response(HttpStatusCode.OK, value));
            }
            private static HttpResponseMessage Response(HttpStatusCode status, string body)
            {
                return new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }
        }
    }
}
