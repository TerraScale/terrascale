using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Features.Logs;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Features.Profile;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E
{
    // Owns the isolated HTTP host and synthetic state controls; journeys invoke cleanup through HTTP.
    internal sealed class DeletionProjectRecordsScenario : IAsyncDisposable
    {
        public static readonly string[] Records = ["dashboard_saved_queries", SqlWorkspace.Table, AlertRules.Table, LogDrains.Table];
        public static readonly string[] Indexes = [AlertRules.IndexTable, LogDrains.IndexTable];
        private readonly DemoEnvironment environment = new();
        public string Organization { get; } = AccountService.NewId();
        public string Project { get; } = AccountService.NewId();
        public string Retained { get; } = AccountService.NewId();
        public string ForeignOrganization { get; } = AccountService.NewId();
        public string ForeignProject { get; } = AccountService.NewId();
        private readonly string database = AccountService.NewId();
        public string User { get; } = AccountService.NewId();
        private WebApplication app = null!;
        public HttpClient Client { get; private set; } = null!;
        private IDashboardState State => app.Services.GetRequiredService<IDashboardState>();

        public static async Task<DeletionProjectRecordsScenario> StartAsync(string root)
        {
            var scenario = new DeletionProjectRecordsScenario();
            await scenario.environment.InitializeAsync();
            scenario.app = await scenario.environment.CreateTestHostAsync(root, 0, "http://127.0.0.1");
            scenario.app.MapPost("/__test/project-records/{organizationId}/{projectId}", async (string organizationId, string projectId, HttpContext http) =>
            {
                try
                {
                    await new DeletionProjectRecords(scenario.State).CleanAsync(organizationId, projectId, http.RequestAborted);
                    return Results.NoContent();
                }
                catch (IOException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            });
            scenario.app.MapPost("/__test/sql-user/{subject}", async (string subject, HttpContext http) =>
            {
                await new DeletionRelatedRecords(scenario.State, scenario.app.Services.GetRequiredService<DashboardSessions>()).CleanAccountAsync(subject, http.RequestAborted);
                return Results.NoContent();
            });
            try
            {
                await scenario.app.StartAsync();
                scenario.Client = new(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate }) { BaseAddress = new(scenario.app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
                return scenario;
            }
            catch { await scenario.app.DisposeAsync(); throw; }
        }

        public async Task SeedAsync()
        {
            foreach (var table in Records)
            {
                foreach (var project in new[] { Project, Retained })
                {
                    await State.PutAsync(table, Key(table, project), JsonSerializer.SerializeToElement(Record(table, Organization, project)), StatePrecondition.Absent, default);
                }
            }

            foreach (var table in Indexes)
            {
                await State.PutAsync(table, "all", JsonSerializer.SerializeToElement(new AlertProjectIndex(new()
                { [Project] = Organization, [Retained] = Organization, [ForeignProject] = ForeignOrganization })), StatePrecondition.Absent, default);
            }
        }

        public async Task SeedMismatchAsync(string table, bool wrongRowId)
        {
            var project = Indexes.Contains(table) ? "all" : Project;
            var key = Key(table, project);
            var row = (await GetAsync(table, project))!;
            if (wrongRowId)
            {
                environment.State.Items[table + "/" + key] = row with { Id = ForeignProject };
            }
            else
            {
                object body = Indexes.Contains(table)
                    ? new AlertProjectIndex(new() { [Project] = ForeignOrganization, [Retained] = Organization })
                    : table == SqlWorkspace.Table
                        // Keep the queried organization/project but corrupt the remaining key identity.
                        ? new SqlWorkspaceRecord(Organization, Project, ForeignProject, User, "private draft", ["SELECT 1"])
                        : Record(table, ForeignOrganization, Project);
                await State.PutAsync(table, key, JsonSerializer.SerializeToElement(body), StatePrecondition.Matches(row.Version), default);
            }
        }

        public string ArrangeConcurrentIndexUpdate()
        {
            var concurrentProject = AccountService.NewId();
            environment.State.BeforeWriteAsync = async (table, key) =>
            {
                if (table != AlertRules.IndexTable || key != "all")
                {
                    return;
                }

                environment.State.BeforeWriteAsync = null;
                var row = (await GetAsync(table, key))!;
                var index = AccountService.Read<AlertProjectIndex>(row);
                index.Projects[concurrentProject] = ForeignOrganization;
                await State.PutAsync(table, key, JsonSerializer.SerializeToElement(index), StatePrecondition.Matches(row.Version), default);
            };
            return concurrentProject;
        }

        public async Task<IReadOnlyDictionary<string, string>> IndexAsync(string table)
        {
            return AccountService.Read<AlertProjectIndex>((await GetAsync(table, "all"))!).Projects;
        }

        private object Record(string table, string organization, string project)
        {
            return table switch
            {
                "dashboard_saved_queries" => new SavedQueryRecord(organization, project,
                    [new(Guid.CreateVersion7().ToString(), new Bogus.Faker().Lorem.Word(), "SELECT * FROM orders", "", DateTimeOffset.UtcNow)]),
                SqlWorkspace.Table => new SqlWorkspaceRecord(organization, project, database, User, "private draft", ["SELECT 1"]),
                AlertRules.Table => new AlertRuleSet(project, organization,
                    [new(AccountService.NewId(), new Bogus.Faker().Lorem.Word(), "p95_latency_ms", 10, null, false, null, null, DateTimeOffset.UtcNow)]),
                LogDrains.Table => new LogDrain(project, organization, AccountService.NewId(), "https://logs.example.test/v1/logs", null, null, DateTimeOffset.UtcNow),
                _ => throw new InvalidOperationException("Unknown project record table.")
            };
        }

        private string Key(string table, string project)
        {
            return table == SqlWorkspace.Table ? Organization + "/" + project + "/" + database + "/" + User : table == "dashboard_saved_queries"
            ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Organization + "/" + project))) : project;
        }

        public Task<StateRecord?> GetAsync(string table, string project)
        {
            return State.GetAsync(table, Key(table, project), default);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
