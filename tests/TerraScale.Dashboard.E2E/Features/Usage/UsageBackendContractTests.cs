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
        public async Task ProjectUsageIncludesFileReservationsAlongsideDatabaseStorage()
        {
            using var wire = new Wire { FileBytes = 512 };
            await using var wireRpc = await TestRestRpcHost.StartAsync(wire);
            var selection = Selection();
            var result = await new UsageBackend(wireRpc.Client, new Clock(), new Tokens()).ReadAsync(selection, CancellationToken.None);
            var monthly = result.Monthly[selection.Context.Project!.Id];
            await Assert.That(monthly.State).IsEqualTo(UsageSourceState.Measured);
            await Assert.That(monthly.Reading!.LogicalBytes).IsEqualTo(1536);
            await Assert.That(result.Scope.Reading!.LogicalBytes).IsEqualTo(1024);
        }

        [Test]
        public async Task ProducerMonthlyUsageComputesInternalBillAndCapWithHonestRoundedPeriodEnd()
        {
            using var wire = new Wire();
            await using var wireRpc = await TestRestRpcHost.StartAsync(wire);
            var selection = Selection();
            var organization = selection.Context.Organization!;
            const string planId = "01JA0000000000000000000006";
            organization.PlanId = planId;
            organization.SpendCapCents = 2000;
            organization.InternalBilling = new(organization.Id, planId, "pro", "01JA0000000000000000000007",
                PlanCatalog.For("pro") with { Id = planId, Key = "internal", Name = "TerraScale Internal" },
                PlanCatalog.Limits("pro"), PlanCatalog.ResourceLimits("pro"), 100, 20);
            var measured = await new UsageBackend(wireRpc.Client, new Clock(), new Tokens()).ReadAsync(selection, CancellationToken.None);
            var monthly = measured.Monthly[selection.Context.Project!.Id];
            await Assert.That(monthly.State).IsEqualTo(UsageSourceState.Measured);

            var bill = InternalBillingService.Estimate(organization, measured, Clock.Now);

            await Assert.That(bill.Available).IsTrue();
            await Assert.That(bill.PeriodStart).IsEqualTo(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
            await Assert.That(bill.PeriodEnd).IsEqualTo(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero));
            await Assert.That(bill.Reads).IsEqualTo(12);
            await Assert.That(bill.Writes).IsEqualTo(3);
            await Assert.That(bill.LogicalBytes).IsEqualTo(1024);
            await Assert.That(bill.TotalCents).IsEqualTo(2000);
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
        public async Task InternalBillChargesStorageAboveTheAllowancePerDecimalGigabyte()
        {
            using var wire = new Wire();
            await using var wireRpc = await TestRestRpcHost.StartAsync(wire);
            var selection = Selection();
            var organization = selection.Context.Organization!;
            const string planId = "01JA0000000000000000000006";
            organization.PlanId = planId;
            organization.InternalBilling = new(organization.Id, planId, "pro", "01JA0000000000000000000007",
                PlanCatalog.For("pro") with { Id = planId, Key = "internal", Name = "TerraScale Internal" },
                PlanCatalog.Limits("pro"), PlanCatalog.ResourceLimits("pro"), 0, 50);
            var measured = await new UsageBackend(wireRpc.Client, new Clock(), new Tokens()).ReadAsync(selection, CancellationToken.None);
            var reading = measured.Monthly[selection.Context.Project!.Id].Reading!;
            // Pro includes 10 GB; 2 GB (2,000,000,000 bytes) above it at the catalog's per-GB rate is exactly $1.00.
            var allowance = PlanCatalog.Limits("pro").StorageBytes;
            await Assert.That(allowance).IsEqualTo(10_000_000_000L);
            var over = measured with
            {
                Monthly = new Dictionary<string, UsageObservation>
                {
                    [reading.ProjectId] = new(UsageSourceState.Measured, reading with { LogicalBytes = allowance + 2_000_000_000 })
                }
            };

            var bill = InternalBillingService.Estimate(organization, over, Clock.Now);

            await Assert.That(bill.Available).IsTrue();
            await Assert.That(bill.StorageCents).IsEqualTo(100).Because("binary GiB would bill 94 cents for the same bytes");
            await Assert.That(bill.StorageCentsPerGBMonth).IsEqualTo(50);
        }

        [Test]
        public async Task CurrentHourAcceptsProducersRoundedUtcEndAndUsesCanonicalDatabaseName()
        {
            using var wire = new Wire();
            await using var wireRpc = await TestRestRpcHost.StartAsync(wire);
            var selection = Selection();
            var result = await new UsageBackend(wireRpc.Client, new Clock(), new Tokens()).ReadAsync(selection, CancellationToken.None);
            await Assert.That(result.Scope.State).IsEqualTo(UsageSourceState.Measured);
            await Assert.That(result.Scope.Reading!.Period.To).IsEqualTo(Clock.Now.AddMinutes(45));
            await Assert.That(result.Scope.Reading.Period.Reads).IsEqualTo(12);
            await Assert.That(result.Scope.Reading.DatabaseId).IsEqualTo(selection.DatabaseId);
            await Assert.That(result.Monthly.Values.Single().State).IsEqualTo(UsageSourceState.Measured);
        }

        [Test]
        public async Task ProjectUsageCarriesTerraBasesBilledOperationUnitsForTheUtcMonth()
        {
            using var wire = new Wire();
            await using var wireRpc = await TestRestRpcHost.StartAsync(wire);
            var selection = Selection();
            var result = await new UsageBackend(wireRpc.Client, new Clock(), new Tokens()).ReadAsync(selection, CancellationToken.None);
            await Assert.That(result.Monthly[selection.Context.Project!.Id].Reading!.Month).IsEqualTo(new UsageMonth("2026-10", 6, 3, 9, 29));
            await Assert.That(result.OperationUnits([selection.Context.Project.Id], Clock.Now)).IsEqualTo(29);
            await Assert.That(result.OperationUnits([selection.Context.Project.Id], Clock.Now.AddMonths(1))).IsNull()
                .Because("Last month's units are not this month's usage.");

            using var older = new Wire { Month = null };
            await using var olderRpc = await TestRestRpcHost.StartAsync(older);
            var omitted = await new UsageBackend(olderRpc.Client, new Clock(), new Tokens()).ReadAsync(selection, CancellationToken.None);
            await Assert.That(omitted.Monthly[selection.Context.Project.Id].State).IsEqualTo(UsageSourceState.Measured);
            await Assert.That(omitted.Monthly[selection.Context.Project.Id].Reading!.Month).IsNull();
            await Assert.That(omitted.OperationUnits([selection.Context.Project.Id], Clock.Now)).IsNull()
                .Because("TerraBase without monthly units stays not metered instead of zero.");
        }

        [Test]
        public async Task OrganizationLedgerRetainsDeletedProjectsWithoutDoubleCountingOrInventingMissingUnits()
        {
            UsageObservation Project(string id, UsageMonth? month) => new(UsageSourceState.Measured,
                new(id, "", 0, 0, new(Clock.Now, Clock.Now, "day", 0, 0), [], [], month));
            var measured = new UsageMeasurements(new(UsageSourceState.Measured), new Dictionary<string, UsageObservation>
            {
                ["a"] = Project("a", new("2026-10", 6, 3, 9, 29)),
                ["b"] = Project("b", new("2026-10", 1, 1, 2, 29)),
                ["older"] = Project("older", null),
                ["stale"] = Project("stale", new("2026-09", 5, 5, 10)),
                ["down"] = new(UsageSourceState.Unavailable)
            });
            await Assert.That(measured.OperationUnits(["a", "b"], Clock.Now)).IsEqualTo(29);
            await Assert.That(measured.OperationUnits(["a"], Clock.Now)).IsEqualTo(29).Because("Deleted projects remain in the organization ledger.");
            await Assert.That(measured.OperationUnits([], Clock.Now)).IsNull();
            var undercount = measured with { Monthly = new Dictionary<string, UsageObservation> { ["a"] = Project("a", new("2026-10", 6, 3, 9, 9)), ["b"] = Project("b", new("2026-10", 6, 3, 9, 9)) } };
            await Assert.That(undercount.OperationUnits(["a", "b"], Clock.Now)).IsNull().Because("The organization ledger cannot be smaller than its current projects combined.");
            foreach (var missing in new[] { "older", "stale", "down", "absent" })
            {
                await Assert.That(measured.OperationUnits(["a", missing], Clock.Now)).IsNull();
            }

            foreach (var invalid in new UsageMonth[] { new("2026-10", 1, 1, 2), new("2026-10", 1, 1, 2, 30), new("2026-10", 1, 1, 2, -1),
                new("2026-10", 6, 3, 9, 8), new("2026-10", 1, 1, 3, 29), new("2026-10", 1, long.MaxValue, long.MaxValue, long.MaxValue) })
            {
                var inconsistent = measured with { Monthly = new Dictionary<string, UsageObservation>(measured.Monthly) { ["b"] = Project("b", invalid) } };
                await Assert.That(inconsistent.OperationUnits(["a", "b"], Clock.Now)).IsNull();
                if (invalid.OrganizationUnits != 30)
                {
                    await Assert.That(inconsistent.OperationUnits(["b"], Clock.Now)).IsNull();
                }
            }
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
            await using var wireRpc = await TestRestRpcHost.StartAsync(wire);
            var result = await new UsageBackend(wireRpc.Client, new Clock(), new Tokens()).ReadAsync(Selection(), CancellationToken.None);
            await Assert.That(result.Scope.State).IsEqualTo(expected);
            await Assert.That(result.Scope.Reading).IsNull();
            await Assert.That(result.Monthly.Values.Single().Reading).IsNull();
        }

        [Test]
        public async Task AProjectWithoutDatabasesCountsAsZeroWhenTerraBaseHasNotRegisteredIt()
        {
            using var wire = new Wire { Failure = 404 };
            await using var wireRpc = await TestRestRpcHost.StartAsync(wire);
            var selection = Selection();
            var empty = new ProjectRecord { Id = "01JA0000000000000000000006", OrganizationId = selection.Context.Organization!.Id, Name = "empty" };
            selection.Context.Organization.Projects.Add(empty.Id, empty);
            wire.Allowed.Add("/v1/projects/" + empty.Id + "/usage");
            var result = await new UsageBackend(wireRpc.Client, new Clock(), new Tokens()).ReadAsync(selection with { Context = selection.Context with { Project = empty }, DatabaseId = "" }, CancellationToken.None);
            await Assert.That(result.Scope.Reading!.Series.Count).IsEqualTo(0);
            await Assert.That(result.Monthly[empty.Id].State).IsEqualTo(UsageSourceState.Measured);
            await Assert.That(result.Monthly[empty.Id].Reading!.Period.Reads + result.Monthly[empty.Id].Reading!.Period.Writes).IsEqualTo(0);
            await Assert.That(result.Monthly[empty.Id].Reading!.Month).IsNull();
            await Assert.That(result.Monthly["01JA0000000000000000000001"].State).IsEqualTo(UsageSourceState.Restricted)
                .Because("A project with databases that TerraBase does not know stays unknown.");
        }

        [Test]
        public async Task EmptyProjectTokenExchange404NeverBecomesMeasuredZero()
        {
            using var wire = new Wire();
            await using var wireRpc = await TestRestRpcHost.StartAsync(wire);
            var selection = Selection() with { DatabaseId = "" };
            selection.Context.Project!.Databases.Clear();
            var result = await new UsageBackend(wireRpc.Client, new Clock(), new Tokens(true)).ReadAsync(selection, CancellationToken.None);
            await Assert.That(result.Scope.State).IsEqualTo(UsageSourceState.Restricted);
            await Assert.That(result.Scope.Reading).IsNull();
            await Assert.That(result.Monthly.Values.Single().Reading).IsNull();
            await Assert.That(wire.Requests).IsEqualTo(0);
        }

        [Test]
        [Arguments(false, 1)]
        [Arguments(false, 2)]
        [Arguments(true, 1)]
        [Arguments(true, 2)]
        public async Task TopLevelSeriesMustBeDenseEvenWhenChildSeriesAreEmpty(bool database, int seriesMode)
        {
            using var wire = new Wire { SeriesMode = seriesMode };
            await using var wireRpc = await TestRestRpcHost.StartAsync(wire);
            var selection = Selection();
            var result = await new UsageBackend(wireRpc.Client, new Clock(), new Tokens()).ReadAsync(selection with { Range = "24h", DatabaseId = database ? selection.DatabaseId : "" }, CancellationToken.None);
            await Assert.That(result.Scope.State).IsEqualTo(UsageSourceState.Unavailable);
            await Assert.That(result.Scope.Reading).IsNull();
            await Assert.That(result.Monthly.Values.Single().Reading).IsNull();
        }

        [Test]
        public async Task ForeignDatabaseIdentityCannotBecomeUsageForSelectedDatabase()
        {
            using var wire = new Wire { Foreign = true };
            await using var wireRpc = await TestRestRpcHost.StartAsync(wire);
            var result = await new UsageBackend(wireRpc.Client, new Clock(), new Tokens()).ReadAsync(Selection(), CancellationToken.None);
            await Assert.That(result.Scope.State).IsEqualTo(UsageSourceState.Unavailable);
            await Assert.That(result.Scope.Reading).IsNull();
        }

        [Test]
        public async Task AProjectWithoutDatabasesReportsNoReplicationInsteadOfAnError()
        {
            using var wire = new Wire { Failure = 404 };
            await using var wireRpc = await TestRestRpcHost.StartAsync(wire);
            var selection = Selection();
            selection.Context.Project!.Databases.Clear();
            // The wire refuses the replication path, so any TerraBase call would surface as an error.
            var result = await new UsageReplicationBackend(wireRpc.Client, new Tokens(), null!).ReadAsync(selection with { DatabaseId = "" }, CancellationToken.None);
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
