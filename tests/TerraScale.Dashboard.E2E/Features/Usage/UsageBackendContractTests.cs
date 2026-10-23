using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Usage;

namespace TerraScale.Dashboard.E2E.Features.Usage;

public sealed class UsageBackendContractTests
{
    [Test]
    public async Task ProducerMonthlyUsageComputesInternalBillAndCapWithHonestRoundedPeriodEnd()
    {
        using var wire = new Wire();
        var selection = Selection();
        var organization = selection.Context.Organization!;
        const string planId = "01JA0000000000000000000006";
        organization.PlanId = planId;
        organization.SpendCapCents = 9900;
        organization.InternalBilling = new(organization.Id, planId, "business", "01JA0000000000000000000007",
            PlanCatalog.For("business") with { Id = planId, Key = "internal", Name = "TerraScale Internal" },
            PlanCatalog.Limits("business"), PlanCatalog.ResourceLimits("business"), 100, 20);
        var measured = await new UsageBackend(wire, new Clock(), new Tokens()).ReadAsync(selection, CancellationToken.None);
        var monthly = measured.Monthly[selection.Context.Project!.Id];
        await Assert.That(monthly.State).IsEqualTo(UsageSourceState.Measured);

        var bill = InternalBillingService.Estimate(organization, measured, Clock.Now);

        await Assert.That(bill.Available).IsTrue();
        await Assert.That(bill.PeriodStart).IsEqualTo(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        await Assert.That(bill.PeriodEnd).IsEqualTo(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero));
        await Assert.That(bill.Reads).IsEqualTo(12);
        await Assert.That(bill.Writes).IsEqualTo(3);
        await Assert.That(bill.LogicalBytes).IsEqualTo(1024);
        await Assert.That(bill.TotalCents).IsEqualTo(9900);
        await Assert.That(bill.CapReached).IsTrue();
        await Assert.That(bill.Alert).IsNotNull();

        // A producer bucket may include today, but cannot cover tomorrow's activity.
        var reading = monthly.Reading!;
        var future = measured with
        {
            Monthly = new Dictionary<string, UsageObservation>
            {
                [reading.ProjectId] = new(UsageSourceState.Measured,
                    reading with { Period = reading.Period with { To = bill.PeriodEnd.AddDays(1) } })
            }
        };
        var refused = InternalBillingService.Estimate(organization, future, Clock.Now);
        await Assert.That(refused.Available).IsFalse();
        await Assert.That(refused.CapReached).IsFalse();
        await Assert.That(refused.Alert).IsNull();
    }

    [Test]
    public async Task CurrentHourAcceptsProducersRoundedUtcEndAndUsesCanonicalDatabaseName()
    {
        using var wire = new Wire();
        var selection = Selection();
        var result = await new UsageBackend(wire, new Clock(), new Tokens()).ReadAsync(selection, CancellationToken.None);
        await Assert.That(result.Scope.State).IsEqualTo(UsageSourceState.Measured);
        await Assert.That(result.Scope.Reading!.Period.To).IsEqualTo(Clock.Now.AddMinutes(45));
        await Assert.That(result.Scope.Reading.Period.Reads).IsEqualTo(12);
        await Assert.That(result.Scope.Reading.DatabaseId).IsEqualTo(selection.DatabaseId);
        await Assert.That(result.Monthly.Values.Single().State).IsEqualTo(UsageSourceState.Measured);
    }

    [Test]
    [Arguments(401, UsageSourceState.Unauthorized)]
    [Arguments(403, UsageSourceState.Unauthorized)]
    [Arguments(404, UsageSourceState.Restricted)]
    [Arguments(400, UsageSourceState.QueryTooExpensive)]
    [Arguments(503, UsageSourceState.Unavailable)]
    public async Task FailedMetersRemainUnknownInsteadOfBecomingZero(int status, UsageSourceState expected)
    {
        using var wire = new Wire { Failure = status };
        var result = await new UsageBackend(wire, new Clock(), new Tokens()).ReadAsync(Selection(), CancellationToken.None);
        await Assert.That(result.Scope.State).IsEqualTo(expected);
        await Assert.That(result.Scope.Reading).IsNull();
        await Assert.That(result.Monthly.Values.Single().Reading).IsNull();
    }

    [Test]
    public async Task AProjectWithoutDatabasesCountsAsZeroWhenTerraBaseHasNotRegisteredIt()
    {
        using var wire = new Wire { Failure = 404 };
        var selection = Selection();
        var empty = new ProjectRecord { Id = "01JA0000000000000000000006", OrganizationId = selection.Context.Organization!.Id, Name = "empty" };
        selection.Context.Organization.Projects.Add(empty.Id, empty);
        wire.Allowed.Add("/v1/projects/" + empty.Id + "/usage");
        var result = await new UsageBackend(wire, new Clock(), new Tokens()).ReadAsync(selection, CancellationToken.None);
        await Assert.That(result.Monthly[empty.Id].State).IsEqualTo(UsageSourceState.Measured);
        await Assert.That(result.Monthly[empty.Id].Reading!.Period.Reads + result.Monthly[empty.Id].Reading!.Period.Writes).IsEqualTo(0);
        await Assert.That(result.Monthly["01JA0000000000000000000001"].State).IsEqualTo(UsageSourceState.Restricted)
            .Because("A project with databases that TerraBase does not know stays unknown.");
    }

    [Test]
    public async Task ForeignDatabaseIdentityCannotBecomeUsageForSelectedDatabase()
    {
        using var wire = new Wire { Foreign = true };
        var result = await new UsageBackend(wire, new Clock(), new Tokens()).ReadAsync(Selection(), CancellationToken.None);
        await Assert.That(result.Scope.State).IsEqualTo(UsageSourceState.Unavailable);
        await Assert.That(result.Scope.Reading).IsNull();
    }

    [Test]
    public async Task AProjectWithoutDatabasesReportsNoReplicationInsteadOfAnError()
    {
        using var wire = new Wire { Failure = 404 };
        var selection = Selection();
        selection.Context.Project!.Databases.Clear();
        // The wire refuses the replication path, so any TerraBase call would surface as an error.
        var result = await new UsageReplicationBackend(wire, new Tokens(), null!).ReadAsync(selection with { DatabaseId = "" }, CancellationToken.None);
        await Assert.That(result.Error).IsEqualTo("");
        await Assert.That(result.Empty).Contains("No activity yet");
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
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Tokens : IProjectAccessTokens
    {
        public Task<string> GetAsync(SessionRecord session, string organizationId, string projectId, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
        {
            if (scopes.Single() != "databases:read") throw new InvalidOperationException("Wrong meter authority.");
            return Task.FromResult("meter-project-token");
        }
    }
    private sealed class Wire : HttpMessageHandler, IHttpClientFactory
    {
        public int Failure { get; init; }
        public bool Foreign { get; init; }
        public HashSet<string> Allowed { get; } = [];
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var database = path == "/v1/projects/01JA0000000000000000000001/databases/app/usage";
            if (!database && path != "/v1/projects/01JA0000000000000000000001/usage" && !Allowed.Contains(path) || request.Headers.Authorization?.Parameter != "meter-project-token")
                throw new InvalidOperationException("Noncanonical meter request.");
            if (Failure > 0) return Task.FromResult(Response((HttpStatusCode)Failure, "{\"error\":{\"code\":\"invalid_query\",\"message\":\"failed\"}}"));
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            var granularity = query["granularity"].ToString();
            var from = DateTimeOffset.Parse(query["from"].ToString(), CultureInfo.InvariantCulture);
            var to = granularity == "day" ? Clock.Now.AddDays(1).Date : Clock.Now.AddMinutes(45).UtcDateTime;
            var period = new { from, to = new DateTimeOffset(to, TimeSpan.Zero), granularity, reads = 12, writes = 3 };
            var series = new[] { new { start = from, reads = 12, writes = 3 } };
            var databaseId = Foreign ? "01JA0000000000000000000005" : "01JA0000000000000000000004";
            var row = new { databaseId, storageBytes = 2048, logicalBytes = 1024, collections = 1, documents = 2, reads = 22, writes = 5, period, series };
            var child = new { databaseId, storageBytes = 2048, logicalBytes = 1024, collections = 1, documents = 2, reads = 22, writes = 5, period, series = Array.Empty<object>() };
            var value = database ? JsonSerializer.Serialize(row) : JsonSerializer.Serialize(new
            { projectId = "01JA0000000000000000000001", databases = 1, storageBytes = 2048, logicalBytes = 1024, documents = 2, reads = 22, writes = 5, period, series, items = new[] { child } });
            return Task.FromResult(Response(HttpStatusCode.OK, value));
        }
        private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
