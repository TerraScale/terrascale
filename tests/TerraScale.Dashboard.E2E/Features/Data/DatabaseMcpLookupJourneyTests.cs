using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.E2E.Features.Databases;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Documents;

namespace TerraScale.Dashboard.E2E.Features.Data
{
    // A databaseId that misses the project's records must not be blamed on the caller while TerraBase listings are flaky.
    [ClassDataSource<DashboardRootFixture>]
    public sealed class DatabaseMcpLookupJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task CreateDocumentSplitsCallerMistakesFromAbsenceAndOutages()
        {
            await using var probe = await Probe.StartAsync(fixture.Root);
            var kept = await probe.CreateDatabaseAsync("kept-target");
            var dropped = await probe.CreateDatabaseAsync("dropped-target");
            foreach (var database in new[] { kept, dropped })
            {
                await probe.CallAsync("create_collection", new() { ["databaseId"] = database, ["name"] = "records" });
            }

            // A name or malformed id is the caller's mistake: the id rule, and no TerraBase listing is spent.
            await ErrorAsync(probe, "customer-db", "Pass the database ID (a ULID), not its name.");
            await Assert.That(probe.Lists.Count).IsEqualTo(0).Because("A malformed id must never reach TerraBase");

            // A well-formed ULID no listing contains is proven absent, not retryable, and names the id.
            var foreign = new TerraScaleIdGenerator().NewId();
            await ErrorAsync(probe, foreign, "Database " + foreign + " was not found in this project.");
            await Assert.That(probe.Lists.Count).IsGreaterThan(0).Because("Absence must be proven by listings, not the cache");

            // Listings failing everywhere is an outage of the lookup, never a statement about the id.
            probe.Reset(); probe.Fault = "all";
            await ErrorAsync(probe, foreign, "Database " + foreign + " is temporarily unavailable; retry in a few seconds.");
            await ErrorAsync(probe, kept, "Database " + kept + " is temporarily unavailable; retry in a few seconds.");

            // A valid database whose stored record a degraded sync dropped: the write refreshes once and succeeds first try.
            probe.Reset(); probe.Fault = "";
            await probe.DropRecordAsync(dropped);
            await WriteAsync(probe, dropped, "01K78CVSTMER0000000000DROP");

            // The retry after an outage succeeds, proving the refusal was about availability, not the id.
            probe.Reset(); probe.Fault = "all";
            await ErrorAsync(probe, kept, "Database " + kept + " is temporarily unavailable; retry in a few seconds.");
            probe.Fault = "";
            await WriteAsync(probe, kept, "01K78CVSTMER0000000000KEEP");
            await probe.NoProtocolFailuresAsync();
        }

        private static async Task WriteAsync(Probe probe, string databaseId, string documentId)
        {
            var response = await probe.CallAsync("create_document", new()
            { ["databaseId"] = databaseId, ["collection"] = "records", ["documentId"] = documentId, ["document"] = new { value = 1 } });
            var result = response.GetProperty("result");
            await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsFalse().Because(response.GetRawText());
            await Assert.That(result.GetProperty("structuredContent").GetProperty("id").GetString()).IsEqualTo(documentId);
        }

        private static async Task ErrorAsync(Probe probe, string databaseId, string message)
        {
            var response = await probe.CallAsync("create_document", new()
            { ["databaseId"] = databaseId, ["collection"] = "records", ["document"] = new { value = 1 } });
            var result = response.GetProperty("result");
            await Assert.That(result.TryGetProperty("isError", out var error) && error.GetBoolean()).IsTrue().Because("expected a refusal for " + databaseId);
            await Assert.That(response.GetRawText()).Contains(message);
            await Assert.That(response.GetRawText()).DoesNotContain("An error occurred invoking");
        }

        // Only external TerraBase HTTP and state storage are fixture-owned; service, backend and MCP transport are production.
        private sealed class Probe : IDashboardState, IAsyncDisposable
        {
            public DatabaseSdkScenario Scenario = null!;
            public HttpClient Client = null!, Producer = null!;
            public (string Email, string Password) Account;
            public string Organization = "", Project = "", Secret = "", Fault = "";
            public ConcurrentQueue<string> Lists = new();
            private IDashboardState inner = null!;
            private bool dropped;

            public static async Task<Probe> StartAsync(string root)
            {
                var probe = new Probe();
                probe.Scenario = await DatabaseSdkScenario.StartAsync(root, builder =>
                {
                    probe.inner = builder.Services.Last(item => item.ServiceType == typeof(IDashboardState)).ImplementationInstance is IDashboardState state ? state : throw new InvalidOperationException("The scenario must register a singleton IDashboardState instance.");
                    builder.Services.Replace(ServiceDescriptor.Singleton<IDashboardState>(probe));
                }, probe.InterceptAsync);
                probe.Client = probe.Scenario.CreateClient();
                probe.Producer = probe.Scenario.CreateProviderClient();
                probe.Account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(probe.Client);
                using var identity = JsonDocument.Parse(await probe.Client.GetStringAsync("/api/v1/auth/me"));
                probe.Organization = identity.RootElement.GetProperty("organizationId").GetString()!;
                probe.Project = identity.RootElement.GetProperty("projectId").GetString()!;
                await probe.ControlAsync(new { command = "catalog", value = new[] { "gru", "sjc", "fra" } });
                // Test admission permits one database per workspace; the plan control admits the journey's second one.
                await probe.ControlAsync(new { command = "plan", organizationId = probe.Organization, value = OrganizationRecord.StarterPlanId });
                probe.Secret = await HttpJourney.CreatePatAsync(probe.Client, "TS-1385 lookup journey", probe.Account.Password);
                return probe;
            }

            public void Reset()
            {
                Lists.Clear();
            }

            private Task<IResult?> InterceptAsync(HttpContext http)
            {
                if (http.Request.Headers.ContainsKey(DatabaseSdkJourneyTests.Forwarded))
                {
                    return Task.FromResult<IResult?>(null);
                }

                var path = http.Request.Path.Value!;
                if (http.Request.Method == "GET" && path == $"/v1/projects/{Project}/databases")
                {
                    Lists.Enqueue(http.Request.Host.Host.Split('.')[0]);
                    if (Fault == "all")
                    {
                        return Task.FromResult<IResult?>(Results.StatusCode(503));
                    }
                }

                return Task.FromResult<IResult?>(null);
            }

            public async Task<string> CreateDatabaseAsync(string name)
            {
                var created = await CallAsync("create_database", new() { ["name"] = name, ["regions"] = new[] { "GRU", "SJC" } });
                return created.GetProperty("result").GetProperty("structuredContent").GetProperty("id").GetString()!;
            }

            public async Task DropRecordAsync(string id)
            {
                dropped = true;
                var stored = (await inner.GetAsync(AccountService.OrganizationsCollection, Organization, default))!;
                var org = stored.Document.Deserialize<OrganizationRecord>()!;
                if (!org.Projects[Project].Databases.Remove(id))
                {
                    throw new InvalidOperationException("The dropped record must exist before it is dropped.");
                }

                await inner.PutAsync(AccountService.OrganizationsCollection, Organization, JsonSerializer.SerializeToElement(org),
                    TerraBasePrecondition.Matches(stored.Version), default);
            }

            public async Task<Dictionary<string, DatabaseRecord>> RecordsAsync()
            {
                return (await inner.GetAsync(AccountService.OrganizationsCollection, Organization, default))!
                    .Document.Deserialize<OrganizationRecord>()!.Projects[Project].Databases;
            }

            public async Task NoProtocolFailuresAsync()
            {
                await Assert.That((await ReadAsync(Producer, "/__test/observations")).GetProperty("failures").GetArrayLength()).IsEqualTo(0);
            }

            public async Task<JsonElement> CallAsync(string name, Dictionary<string, object?> arguments)
            {
                return await HttpJourney.McpAsync(Client, Secret, "tools/call", new { name, arguments });
            }

            private async Task ControlAsync(object command)
            {
                using var response = await Producer.PostAsJsonAsync("/__test/control", command);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            }

            private static async Task<JsonElement> ReadAsync(HttpClient client, string path)
            {
                using var response = await client.GetAsync(path);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<JsonElement>();
            }

            public Task InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
            public Task CheckReadyAsync(CancellationToken ct) => inner.CheckReadyAsync(ct);

            public Task<TerraBaseDocumentPage> ReadPageAsync(string collection, JsonElement? filter, int limit, string? cursor, CancellationToken ct)
                => inner.ReadPageAsync(collection, filter, limit, cursor, ct);

            public Task<TerraBaseTransactionResult> TransactAsync(IReadOnlyList<DashboardStateOperation> operations, CancellationToken ct)
                => inner.TransactAsync(operations, ct);

            public Task<TerraBaseDocument?> GetAsync(string collection, string id, CancellationToken ct) => inner.GetAsync(collection, id, ct);

            public Task<IReadOnlyList<TerraBaseDocument>> ListAsync(string collection, CancellationToken ct) => inner.ListAsync(collection, ct);

            public Task<TerraBaseDocumentPage> QueryPageAsync(string collection, TerraBaseQuery query, CancellationToken ct)
                => inner.QueryPageAsync(collection, query, ct);

            public Task DeleteAsync(string collection, string id, string version, CancellationToken ct) => inner.DeleteAsync(collection, id, version, ct);

            public Task PutAsync(string collection, string id, JsonElement document, TerraBasePrecondition condition, CancellationToken ct)
                => inner.PutAsync(collection, id, document, condition, ct);

            public async ValueTask DisposeAsync()
            {
                if (dropped)
                {
                    try { await CleanupAsync(); } catch (Exception error) { Console.Error.WriteLine("TS-1385 probe cleanup: " + error.Message); }
                }

                Client.Dispose();
                Producer.Dispose();
                await Scenario.DisposeAsync();
            }

            private async Task CleanupAsync()
            {
                var databases = (await RecordsAsync()).Values.Select(database => database.Id).ToArray();
                foreach (var id in databases)
                {
                    var listed = await HttpJourney.McpAsync(Client, Secret, "tools/call", new
                    { name = "delete_database", arguments = new { databaseId = id, confirmName = (await RecordsAsync())[id].Name } });
                    _ = listed;
                }
            }
        }
    }
}
