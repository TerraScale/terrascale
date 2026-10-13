using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Analytics;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Databases;

namespace TerraScale.Dashboard.E2E.Features.Analytics;

public sealed class AnalyticsBackendContractTests
{
    [Test]
    [Arguments("1h", 60, 3600)]
    [Arguments("24h", 3600, 86400)]
    [Arguments("7d", 21600, 604800)]
    [Arguments("30d", 86400, 2592000)]
    public async Task EachAnalyticsReadRequestsOnlyCurrentDatabaseReadAuthorityAndPreservesFilters(string range, int bucketSeconds, int seconds)
    {
        using var wire = new Wire { BucketSeconds = bucketSeconds, Seconds = seconds };
        var selection = Selection(range);
        var tokens = new Tokens(selection);
        var backend = new AnalyticsBackend(wire, tokens, new DatabaseBackend(wire, tokens));

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
        var selection = Selection("1h");
        var tokens = new Tokens(selection) { Refusal = status };
        var backend = new AnalyticsBackend(wire, tokens, new DatabaseBackend(wire, tokens));
        HttpRequestException? refused = null;

        try { await backend.ReadAsync(selection, CancellationToken.None); }
        catch (HttpRequestException error) { refused = error; }

        await Assert.That(refused).IsNotNull();
        await Assert.That(refused!.StatusCode).IsEqualTo(status == 0 ? (HttpStatusCode?)null : (HttpStatusCode)status);
        await Assert.That(tokens.Calls).IsEqualTo(1);
        await Assert.That(wire.Reads).IsEqualTo(0);
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
                throw new InvalidOperationException("Analytics requested authority outside the current read scope.");
            Calls++;
            if (Refusal is { } status) throw new HttpRequestException("Synthetic exchange refusal.", null, status == 0 ? null : (HttpStatusCode)status);
            return Task.FromResult("analytics-project-" + Calls);
        }
    }

    private sealed class Wire : HttpMessageHandler, IHttpClientFactory
    {
        public int BucketSeconds { get; init; } = 60;
        public int Seconds { get; init; } = 3600;
        public int Reads { get; private set; }
        public List<string> Bearers { get; } = [];
        public string Path { get; private set; } = "";
        public string Query { get; private set; } = "";
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri!.Host != "api.terrascale.tech")
                throw new InvalidOperationException("Unexpected analytics origin or method.");
            if (request.RequestUri.AbsolutePath == "/healthz")
            {
                if (request.Headers.Authorization is not null) throw new InvalidOperationException("Health read received a user token.");
                var health = Response("{\"region\":\"gru\"}");
                health.Headers.Add("X-TerraBase-Region", "gru");
                return Task.FromResult(health);
            }
            Path = request.RequestUri.AbsolutePath;
            Query = request.RequestUri.Query;
            if (Path != "/v1/projects/01JA0000000000000000000001/analytics" || request.Headers.Authorization?.Scheme != "Bearer")
                throw new InvalidOperationException("Unexpected analytics contract request.");
            Bearers.Add(request.Headers.Authorization.Parameter!);
            Reads++;
            var query = QueryHelpers.ParseQuery(Query);
            var to = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
            return Task.FromResult(Response(JsonSerializer.Serialize(new
            {
                projectId = "01JA0000000000000000000001",
                range = query["range"].ToString(),
                from = to.AddSeconds(-Seconds),
                to,
                bucketSeconds = BucketSeconds,
                items = Array.Empty<object>()
            })));
        }
        private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
