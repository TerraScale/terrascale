using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Turso;
using TerraBase.Testing;
using static TerraBase.Testing.SharedProcess;

namespace TerraBase.Slow.Tenancy;

public sealed class SharedProcessTests
{
    private const int Databases = 1000;

    private static readonly Faker Faker = new();

    [Test]
    public async Task A_thousand_databases_share_one_process_within_the_writer_bound_and_keep_their_data()
    {
        await using var api = await ManagementApi.StartAsync();
        var organization = await api.CreateOrganizationAsync("Org " + Faker.Random.AlphaNumeric(8));
        var project = await api.CreateProjectAsync(organization, "Project");
        await api.SetPlanAsync(api.User, organization, "enterprise");
        using var client = api.DataClient(await api.Server.IssueNarrowedAsync(organization, project, null, "sql:read", "sql:write"));
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var notes = Enumerable.Range(0, Databases).ToDictionary(i => $"db{i:D4}", _ => Faker.Lorem.Sentence());

        await Parallel.ForEachAsync(notes, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (note, _) =>
        {
            var database = (await api.CreateManagedDatabaseAsync(organization, project, note.Key)).Data;
            await Ok(SqlAsync(client, database, "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT NOT NULL)"));
            await Ok(SqlAsync(client, database, "INSERT INTO notes (body) VALUES (?)", note.Value));
        });

        await Assert.That(engine.Pool.OpenWriters).IsLessThanOrEqualTo(engine.Pool.MaxWriters);
        await Assert.That(engine.Pool.MaxWriters).IsLessThan(Databases);

        foreach (var (name, body) in notes)
        {
            var rows = JsonElement.Parse(await Ok(SqlAsync(client, $"/v1/projects/{project}/databases/{name}", "SELECT body FROM notes")))
                .GetProperty("results")[0].GetProperty("rows");

            await Assert.That(rows.GetArrayLength()).IsEqualTo(1);
            await Assert.That(rows[0][0].GetString()).IsEqualTo(body);
        }

        await Assert.That(engine.Pool.OpenWriters).IsLessThanOrEqualTo(engine.Pool.MaxWriters);
    }

    private static async Task<string> Ok(Task<(HttpStatusCode Status, string Body)> request)
    {
        var (status, body) = await request;
        await Assert.That(status).IsEqualTo(HttpStatusCode.OK).Because(body);
        return body;
    }
}
