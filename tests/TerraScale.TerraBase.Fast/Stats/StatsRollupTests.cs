using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Features.CrossRegion;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Stats;

public sealed class StatsRollupTests
{
    private static readonly Faker Faker = new();
    private static readonly string[] InSfo = ["sfo-1"];
    private const string Credentials = "-----BEGIN NATS USER JWT-----\neyJ0.e30.x\n------END NATS USER JWT------\n-----BEGIN USER NKEY SEED-----\nSUAEXAMPLE\n------END USER NKEY SEED------\n";

    [Test]
    public async Task A_rollup_lists_the_database_with_the_region_it_ships_to_and_a_detail_answer_counts_its_collection()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var network = new FakeRegionNetwork();
        var directory = Directory.CreateTempSubdirectory("terrabase-stats-rollup-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            await using var gru = await StartRegionAsync(network, credentials, "gru-1", "gru");
            await using var sfo = await StartRegionAsync(network, credentials, "sfo-1", "sfo");
            var organization = EntityIds.New().ToString();
            var project = EntityIds.New().ToString();
            await RegisterAsync(gru, organization, project, token);
            await RegisterAsync(sfo, organization, project, token);
            var database = await CreateDatabaseAsync(gru, organization, project, "stats", token);
            using var data = await gru.CreateClientAsync(organization, "documents:read", "documents:write");
            await ManagementApi.CreateCollectionAsync(data, database, "people", token);
            // Each poll writes one more document before it asks, so the last closed window holds the latest writes whichever window closed.
            var requester = network.Join("gru");
            var written = 0;
            JsonElement? entry = null;
            await TerraBaseTestServer.EventuallyAsync(async cancellation =>
            {
                using var put = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", Faker.Random.AlphaNumeric(12))),
                    new { text = Faker.Lorem.Sentence(12) }, cancellation);
                put.EnsureSuccessStatusCode();
                written++;
                entry = await DetailEntryAsync(requester, database.Id, cancellation);
                return entry is { } found && Collection(found, "people") is { } counted
                    && counted.GetProperty("documents").GetInt64() == written && counted.GetProperty("complete").GetBoolean()
                    && counted.GetProperty("writes").GetInt64() > 0;
            }, TimeSpan.FromSeconds(60));
            var detailed = entry!.Value;
            var people = Collection(detailed, "people")!.Value;
            await Assert.That(detailed.GetProperty("org").GetString()).IsEqualTo(organization);
            await Assert.That(detailed.GetProperty("database").GetString()).IsEqualTo(database.Id);
            await Assert.That(people.GetProperty("documents").GetInt64()).IsEqualTo(written);
            await Assert.That(people.GetProperty("complete").GetBoolean()).IsTrue();
            await Assert.That(people.GetProperty("writes").GetInt64()).IsGreaterThan(0);

            // The window that ships the database to sfo-1 names it in top, with sfo-1 as a peer that received bytes.
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(Rollups(network, "gru").Any(rollup => Ships(rollup, database.Id))),
                TimeSpan.FromSeconds(60));
            var shipped = Rollups(network, "gru").First(rollup => Ships(rollup, database.Id));
            var top = TopEntry(shipped, database.Id)!.Value;
            var totals = shipped.GetProperty("totals");
            await Assert.That(totals.GetProperty("writes").GetInt64()).IsGreaterThanOrEqualTo(top.GetProperty("writes").GetInt64());
            await Assert.That(totals.GetProperty("sentBytes").GetInt64()).IsGreaterThanOrEqualTo(top.GetProperty("sentBytes").GetInt64());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Task<ManagementApi> StartRegionAsync(FakeRegionNetwork network, string credentials, string region, string cell) =>
        ManagementApi.StartAsync(
            settings =>
            {
                settings["region"] = region;
                settings["node-name"] = $"{cell}-member";
                settings["wan-regions"] = "gru-1=gru,sfo-1=sfo";
                settings["nats-url"] = "nats://127.0.0.1:4222";
                settings["nats-credentials-file"] = credentials;
            },
            services =>
            {
                services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, new FakeTimeProvider());
                services.AddSingleton(network.Join(cell, $"{cell}-member"));
                services.AddSingleton<IMemberStore>(new FakeMemberStore(TimeProvider.System));
            },
            managementCredential: true);

    private static async Task RegisterAsync(ManagementApi api, string organization, string project, CancellationToken token)
    {
        using var body = new StringContent(JsonSerializer.Serialize(new
        {
            id = EntityIds.New().ToString(),
            ops = new object[]
            {
                new { op = "create_organization", id = organization, name = Faker.Company.CompanyName() },
                new { op = "create_project", organization, id = project, name = Faker.Random.AlphaNumeric(12) },
            },
        }), Encoding.UTF8, "application/json");
        using var service = api.Service(api.User);
        using var registered = await service.PostAsync(ManagementApi.Uri("/v1/management/commits"), body, token);
        registered.EnsureSuccessStatusCode();
    }

    private static async Task<ManagedDatabaseRef> CreateDatabaseAsync(
        ManagementApi api, string organization, string project, string name, CancellationToken token)
    {
        using var response = await api.Client.PostAsJsonAsync(
            ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases"),
            new { name, homeRegion = "gru-1", replicaRegions = InSfo }, token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var record = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        return new ManagedDatabaseRef(organization, project, record.GetProperty("id").GetString()!, name);
    }

    // The database's entry in the last window of the member that answers, or null while no member answers or it does not track the database.
    private static async Task<JsonElement?> DetailEntryAsync(IRegionBus requester, string database, CancellationToken token)
    {
        byte[] reply;
        try
        {
            var request = JsonSerializer.SerializeToUtf8Bytes(new { v = 1, database });
            reply = (await requester.RequestAsync("ts.orch.v1.gru.query.terrabase-stats.detail", request, token).ConfigureAwait(false)).ToArray();
        }
        catch (IOException)
        {
            // The member has not subscribed to its requests yet.
            return null;
        }

        foreach (var entry in JsonSerializer.Deserialize<JsonElement>(reply).GetProperty("databases").EnumerateArray())
        {
            if (entry.GetProperty("database").GetString() == database)
            {
                return entry;
            }
        }

        return null;
    }

    private static JsonElement? Collection(JsonElement database, string name)
    {
        foreach (var collection in database.GetProperty("collections").EnumerateArray())
        {
            if (collection.GetProperty("name").GetString() == name)
            {
                return collection;
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> Rollups(FakeRegionNetwork network, string cell) =>
        network.Published($"ts.orch.v1.{cell}.obs.terrabase").Select(bytes => JsonSerializer.Deserialize<JsonElement>(bytes));

    private static bool Ships(JsonElement rollup, string database) =>
        TopEntry(rollup, database) is { } entry
        && entry.GetProperty("peers").EnumerateArray().Any(peer => peer.GetProperty("region").GetString() == "sfo-1" && peer.GetProperty("sentBytes").GetInt64() > 0);

    private static JsonElement? TopEntry(JsonElement rollup, string database)
    {
        foreach (var entry in rollup.GetProperty("top").EnumerateArray())
        {
            if (entry.GetProperty("database").GetString() == database)
            {
                return entry;
            }
        }

        return null;
    }
}
