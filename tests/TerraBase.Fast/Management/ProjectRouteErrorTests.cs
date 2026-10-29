using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class ProjectRouteErrorTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Project_JWTs_cannot_use_legacy_database_creation_and_receive_the_unknown_database_envelope(bool narrowed)
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        using var client = ProjectClient(api, database.Project, narrowed ? database.Id : null);

        using var before = await client.GetAsync(Uri(database.Data));
        before.EnsureSuccessStatusCode();
        await ErrorAssert.ErrorAsync(client.PutAsync(Uri(database.Data), null), HttpStatusCode.NotFound,
            "unknown_database", "the database does not exist");
        var unchanged = await client.GetFromJsonAsync<JsonElement>(Uri(database.Data));
        await Assert.That(unchanged.GetProperty("id").GetString()).IsEqualTo(database.Id);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Canonical_usage_routes_return_invalid_query_when_the_engine_read_budget_is_exhausted(bool projectUsage)
    {
        var clock = new SteppingClock();
        var hook = new UsageCommitHook();
        await using var api = await ManagementApi.StartAsync(TerraBaseTestServer.UseProductionQueryBudget, services: services =>
        {
            hook.Install(services);
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, clock);
        });
        var database = await api.CreateManagedDatabaseAsync();
        using var client = ProjectClient(api, database.Project);
        await ManagementApi.CreateCollectionAsync(client, database, "people");
        using var seeded = await client.PutAsJsonAsync(Uri(ManagementApi.DocumentPath(database, "people", "saved")), new { name = new Faker().Name.FullName() });
        seeded.EnsureSuccessStatusCode();
        var path = Uri(projectUsage ? $"/v1/projects/{database.Project}/usage" : database.Data + "/usage");
        using var before = await client.GetAsync(path);
        before.EnsureSuccessStatusCode();
        hook.BeforeBegin = (tenant, _) =>
        {
            clock.Armed |= tenant.DatabaseId == database.Id;
            return ValueTask.CompletedTask;
        };

        await ErrorAssert.ErrorAsync(client.GetAsync(path), HttpStatusCode.BadRequest, "invalid_query", "query too expensive");
    }

    private static HttpClient ProjectClient(ManagementApi api, string project, string? database = null) =>
        api.WithToken(api.Issuer.Mint(EntityIds.New().ToString(), token =>
        {
            token.Claims["project_id"] = project;
            token.Claims["scope"] = "databases:read databases:write documents:read documents:write";
            if (database is not null)
            {
                token.Claims["database"] = database;
            }
        }));

    private static Uri Uri(string path) => ManagementApi.Uri(path);
}
