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
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E
{
    // Owns the isolated HTTP host and synthetic state controls; journeys invoke cleanup through HTTP.
    internal sealed class DeletionProjectRecordsScenario : IAsyncDisposable
    {
        public static readonly string[] Records = ["dashboard_saved_queries", SqlWorkspace.Collection, AlertRules.Collection, LogDrains.Collection];
        public static readonly string[] Indexes = [AlertRules.IndexCollection, LogDrains.IndexCollection];
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
            scenario.app = scenario.environment.CreateTestHost(root, 0, "http://127.0.0.1");
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
            foreach (var collection in Records)
            {
                foreach (var project in new[] { Project, Retained })
                {
                    await State.PutAsync(collection, Key(collection, project), JsonSerializer.SerializeToElement(Record(collection, Organization, project)), TerraBasePrecondition.Absent, default);
                }
            }

            foreach (var collection in Indexes)
            {
                await State.PutAsync(collection, "all", JsonSerializer.SerializeToElement(new AlertProjectIndex(new()
                { [Project] = Organization, [Retained] = Organization, [ForeignProject] = ForeignOrganization })), TerraBasePrecondition.Absent, default);
            }
        }

        public async Task SeedMismatchAsync(string collection, bool wrongDocumentId)
        {
            var project = Indexes.Contains(collection) ? "all" : Project;
            var key = Key(collection, project);
            var document = (await GetAsync(collection, project))!;
            if (wrongDocumentId)
            {
                environment.State.Items[collection + "/" + key] = document with { Id = ForeignProject };
            }
            else
            {
                object body = Indexes.Contains(collection)
                    ? new AlertProjectIndex(new() { [Project] = ForeignOrganization, [Retained] = Organization })
                    : collection == SqlWorkspace.Collection
                        // Keep the queried organization/project but corrupt the remaining key identity.
                        ? new SqlWorkspaceRecord(Organization, Project, ForeignProject, User, "private draft", ["SELECT 1"])
                        : Record(collection, ForeignOrganization, Project);
                await State.PutAsync(collection, key, JsonSerializer.SerializeToElement(body), TerraBasePrecondition.Matches(document.Version), default);
            }
        }

        public string ArrangeConcurrentIndexUpdate()
        {
            var concurrentProject = AccountService.NewId();
            environment.State.BeforeWriteAsync = async (collection, key) =>
            {
                if (collection != AlertRules.IndexCollection || key != "all")
                {
                    return;
                }

                environment.State.BeforeWriteAsync = null;
                var document = (await GetAsync(collection, key))!;
                var index = AccountService.Read<AlertProjectIndex>(document);
                index.Projects[concurrentProject] = ForeignOrganization;
                await State.PutAsync(collection, key, JsonSerializer.SerializeToElement(index), TerraBasePrecondition.Matches(document.Version), default);
            };
            return concurrentProject;
        }

        public async Task<IReadOnlyDictionary<string, string>> IndexAsync(string collection)
        {
            return AccountService.Read<AlertProjectIndex>((await GetAsync(collection, "all"))!).Projects;
        }

        private object Record(string collection, string organization, string project)
        {
            return collection switch
            {
                "dashboard_saved_queries" => new SavedQueryRecord(organization, project,
                    [new(Guid.CreateVersion7().ToString(), new Bogus.Faker().Lorem.Word(), "SELECT * FROM orders", "", DateTimeOffset.UtcNow)]),
                SqlWorkspace.Collection => new SqlWorkspaceRecord(organization, project, database, User, "private draft", ["SELECT 1"]),
                AlertRules.Collection => new AlertRuleSet(project, organization,
                    [new(AccountService.NewId(), new Bogus.Faker().Lorem.Word(), "p95_latency_ms", 10, null, false, null, null, DateTimeOffset.UtcNow)]),
                LogDrains.Collection => new LogDrain(project, organization, AccountService.NewId(), "https://logs.example.test/v1/logs", null, null, DateTimeOffset.UtcNow),
                _ => throw new InvalidOperationException("Unknown project record collection.")
            };
        }

        private string Key(string collection, string project)
        {
            return collection == SqlWorkspace.Collection ? Organization + "/" + project + "/" + database + "/" + User : collection == "dashboard_saved_queries"
            ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Organization + "/" + project))) : project;
        }

        public Task<TerraBaseDocument?> GetAsync(string collection, string project)
        {
            return State.GetAsync(collection, Key(collection, project), default);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
