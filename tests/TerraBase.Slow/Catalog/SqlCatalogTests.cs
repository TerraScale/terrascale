using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;

namespace TerraBase.Slow.Catalog;

public sealed class SqlCatalogTests
{
    [Test]
    public async Task Databases_are_created_listed_dropped_and_survive_a_restart_in_the_sql_catalog(CancellationToken cancellationToken)
    {
        var faker = new Faker();
        var directory = Directory.CreateTempSubdirectory("terrabase-sql-catalog-").FullName;
        try
        {
            string databases;
            string project = "";
            string organization = "";
            string kept;
            string dropped;
            var names = new[] { "orders", "users", "events" };
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                organization = await api.CreateOrganizationAsync(faker.Company.CompanyName());
                project = await api.CreateProjectAsync(organization, faker.Commerce.Department());
                databases = $"/v1/projects/{project}/databases";
                using var management = api.ProjectClient(project);
                var ids = new List<string>();
                foreach (var name in names)
                {
                    ids.Add(await api.CreateDatabaseAsync(organization, project, name));
                }

                using var duplicate = await management.PostAsJsonAsync(ManagementApi.Uri(databases), new { name = names[0] }, cancellationToken);
                await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);

                await Assert.That(await ListAsync(management, databases, cancellationToken)).IsEquivalentTo(names);

                (kept, dropped) = (ids[0], ids[1]);
                using var deleted = await management.DeleteAsync(ManagementApi.Uri($"{databases}/{dropped}"), cancellationToken);
                await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
                await Assert.That(await ListAsync(management, databases, cancellationToken)).IsEquivalentTo([names[0], names[2]]);
                await Assert.That(await CatalogRowsAsync(api.Server, cancellationToken)).IsEqualTo(2L);
            }

            await using var restarted = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory);
            restarted.RegisterProject(project, organization);
            using var owner = restarted.ProjectClient(project);
            await Assert.That(await ListAsync(owner, databases, cancellationToken)).IsEquivalentTo([names[0], names[2]]);
            await Assert.That(await CatalogRowsAsync(restarted.Server, cancellationToken)).IsEqualTo(2L);

            using var gone = await owner.GetAsync(ManagementApi.Uri($"{databases}/{dropped}"), cancellationToken);
            await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            using var found = await owner.GetAsync(ManagementApi.Uri($"{databases}/{kept}"), cancellationToken);
            await Assert.That(found.StatusCode).IsEqualTo(HttpStatusCode.OK);

            using var recreated = await owner.PostAsJsonAsync(ManagementApi.Uri(databases), new { name = names[1] }, cancellationToken);
            await Assert.That(recreated.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var body = await recreated.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            await Assert.That(body.GetProperty("id").GetString()).IsNotEqualTo(dropped);
            await Assert.That(await ListAsync(owner, databases, cancellationToken)).IsEquivalentTo(names);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<List<string>> ListAsync(HttpClient client, string databases, CancellationToken cancellationToken)
    {
        var list = await client.GetFromJsonAsync<JsonElement>(ManagementApi.Uri(databases), cancellationToken);
        return [.. list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!)];
    }

    private static async Task<long> CatalogRowsAsync(TerraBaseTestServer server, CancellationToken cancellationToken)
    {
        var store = await server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(cancellationToken);
        var transaction = await store.BeginTransactionAsync(write: false, cancellationToken);
        await using (transaction)
        {
            var result = await transaction.ExecuteStatementAsync(
                new SqlStatement("SELECT count(*) FROM catalog_databases", [], new Dictionary<string, object?>(), 1, 1024, TimeSpan.FromSeconds(10)), cancellationToken);
            return (long)result.Rows[0][0]!;
        }
    }
}
