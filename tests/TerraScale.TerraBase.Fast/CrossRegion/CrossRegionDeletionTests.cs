using System.Net;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Cluster;
using TerraBase.Testing;

namespace TerraBase.Fast.CrossRegion;

public sealed class CrossRegionDeletionTests
{
    private const string Credentials = "-----BEGIN NATS USER JWT-----\neyJ0.e30.x\n------END NATS USER JWT------\n-----BEGIN USER NKEY SEED-----\nSUAEXAMPLE\n------END USER NKEY SEED------\n";

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_restarted_region_removes_a_database_deleted_at_home_only_after_a_complete_heads_round(bool localReplicaCopy)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        var faker = new Faker();
        var network = new FakeRegionNetwork();
        var directory = Directory.CreateTempSubdirectory("terrabase-wan-deletion-").FullName;
        try
        {
            var credentials = Path.Combine(directory, "user.creds");
            await File.WriteAllTextAsync(credentials, Credentials, token);
            var gruDirectory = Directory.CreateDirectory(Path.Combine(directory, "gru")).FullName;
            var sfoDirectory = Directory.CreateDirectory(Path.Combine(directory, "sfo")).FullName;
            var gruMembers = new FakeMemberStore(TimeProvider.System);
            var sfoMembers = new FakeMemberStore(TimeProvider.System);
            await using var gru = await StartRegionAsync(network, credentials, "gru-1", "gru", gruDirectory, gruMembers);
            var organization = Guid.NewGuid().ToString("D");
            var project = Guid.NewGuid().ToString("D");
            var organizationName = faker.Company.CompanyName();
            var projectName = faker.Random.AlphaNumeric(12);
            string[] inSfo = ["sfo-1"];
            var deletedValue = faker.Lorem.Sentence();
            var survivingValue = faker.Lorem.Sentence();
            var localValue = faker.Lorem.Sentence();
            ManagedDatabaseRef deleted, surviving, locallyOwned;
            string sfoUser, sfoKey;

            await RegisterAsync(gru, organization, project, organizationName, projectName, token);
            using var gruData = await gru.CreateClientAsync(organization, "documents:read", "documents:write");
            await using (var sfo = await StartRegionAsync(network, credentials, "sfo-1", "sfo", sfoDirectory, sfoMembers))
            {
                sfoUser = sfo.User;
                await RegisterAsync(sfo, organization, project, organizationName, projectName, token);
                sfoKey = await sfo.Server.IssueAsync(organization, "documents:read", "documents:write");
                using var sfoData = sfo.DataClient(sfoKey);
                deleted = await CreateDatabaseAsync(gru, organization, project, "deleted", "gru-1", localReplicaCopy ? inSfo : [], token);
                surviving = await CreateDatabaseAsync(gru, organization, project, "surviving", "gru-1", inSfo, token);
                locallyOwned = await CreateDatabaseAsync(sfo, organization, project, "sfo-owned", "sfo-1", [], token);
                await WriteAsync(gruData, deleted, deletedValue, token);
                await WriteAsync(gruData, surviving, survivingValue, token);
                await WriteAsync(sfoData, locallyOwned, localValue, token);

                await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
                    await StatusAsync(sfo.Client, deleted.Path, cancellationToken) == HttpStatusCode.OK);
                var record = await sfo.Client.GetFromJsonAsync<JsonElement>(ManagementApi.Uri(deleted.Path), token);
                await Assert.That(record.GetProperty("homeRegion").GetString()).IsEqualTo("gru-1");
                await Assert.That(record.GetProperty("replicaRegions").GetArrayLength()).IsEqualTo(localReplicaCopy ? 1 : 0);
                if (localReplicaCopy)
                {
                    await EventuallyReadAsync(sfoData, deleted, deletedValue);
                }
                else
                {
                    await Assert.That(await StatusAsync(sfoData, Document(deleted), token)).IsEqualTo(HttpStatusCode.MisdirectedRequest);
                }

                await Assert.That(File.Exists(DatabaseFile(sfoDirectory, deleted))).IsEqualTo(localReplicaCopy);
                await EventuallyReadAsync(sfoData, surviving, survivingValue);
                await AssertReadAsync(sfoData, locallyOwned, localValue, token);
            }

            // SFO's persisted catalog and optional replica outlive its puller's in-memory history.
            network.Partition("gru", "sfo");
            if (localReplicaCopy)
            {
                using var removed = await gru.Client.PatchAsJsonAsync(ManagementApi.Uri(deleted.Path), new { replicaRegions = Array.Empty<string>() }, token);
                await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            using var deletion = await gru.Client.DeleteAsync(ManagementApi.Uri(deleted.Path), token);
            await Assert.That(deletion.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            await Assert.That(await StatusAsync(gru.Client, deleted.Path, token)).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(File.Exists(DatabaseFile(gruDirectory, deleted))).IsFalse();

            await using var restarted = await StartRegionAsync(network, credentials, "sfo-1", "sfo", sfoDirectory, sfoMembers, storageQueue: 1);
            using var owner = restarted.As(sfoUser);
            using var data = restarted.DataClient(sfoKey);
            await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
                await HeadsFailuresAsync(restarted.Server, cancellationToken) > 0);

            // No remote member answered: an absent head cannot authorize deletion.
            await Assert.That(await StatusAsync(owner, deleted.Path, token)).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(File.Exists(DatabaseFile(sfoDirectory, deleted))).IsEqualTo(localReplicaCopy);
            if (localReplicaCopy)
            {
                await AssertReadAsync(data, deleted, deletedValue, token);
            }
            else
            {
                await Assert.That(await StatusAsync(data, Document(deleted), token)).IsEqualTo(HttpStatusCode.MisdirectedRequest);
            }

            await Assert.That(await StatusAsync(owner, surviving.Path, token)).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await StatusAsync(owner, locallyOwned.Path, token)).IsEqualTo(HttpStatusCode.OK);
            await AssertReadAsync(data, surviving, survivingValue, token);
            await AssertReadAsync(data, locallyOwned, localValue, token);

            network.Heal();
            await TerraBaseTestServer.EventuallyAsync(async cancellationToken =>
                await StatusAsync(owner, deleted.Path, cancellationToken) == HttpStatusCode.NotFound
                && !File.Exists(DatabaseFile(sfoDirectory, deleted)));
            await Assert.That(await StatusAsync(data, Document(deleted), token)).IsEqualTo(HttpStatusCode.NotFound);

            // The same scan preserves a live home database and a database owned by this region.
            await Assert.That(await StatusAsync(owner, surviving.Path, token)).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await StatusAsync(gru.Client, surviving.Path, token)).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await StatusAsync(owner, locallyOwned.Path, token)).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(File.Exists(DatabaseFile(sfoDirectory, surviving))).IsTrue();
            await Assert.That(File.Exists(DatabaseFile(sfoDirectory, locallyOwned))).IsTrue();
            await AssertReadAsync(data, surviving, survivingValue, token);
            await AssertReadAsync(gruData, surviving, survivingValue, token);
            await AssertReadAsync(data, locallyOwned, localValue, token);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Task<ManagementApi> StartRegionAsync(
        FakeRegionNetwork network, string credentials, string region, string cell, string dataDirectory, FakeMemberStore members, int storageQueue = 1024) =>
        ManagementApi.StartAsync(settings =>
        {
            settings["region"] = region;
            settings["node-name"] = $"{cell}-member";
            settings["data-directory"] = dataDirectory;
            settings["storage-queue"] = storageQueue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings["wan-regions"] = "gru-1=gru,sfo-1=sfo";
            settings["nats-url"] = "nats://127.0.0.1:4222";
            settings["nats-credentials-file"] = credentials;
        }, services =>
        {
            services.AddSingleton(network.Join(cell));
            services.AddSingleton<IMemberStore>(members);
        });

    private static async Task RegisterAsync(
        ManagementApi api, string organization, string project, string organizationName, string projectName, CancellationToken token)
    {
        using var body = new StringContent(JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString("D"),
            ops = new object[]
            {
                new { op = "create_organization", id = organization, name = organizationName },
                new { op = "create_project", organization, id = project, name = projectName },
            },
        }), Encoding.UTF8, "application/json");
        using var registered = await api.Client.PostAsync(ManagementApi.Uri("/v1/management/commits"), body, token);
        registered.EnsureSuccessStatusCode();
    }

    private static async Task<ManagedDatabaseRef> CreateDatabaseAsync(
        ManagementApi api, string organization, string project, string name, string homeRegion, string[] replicaRegions, CancellationToken token)
    {
        using var response = await api.Client.PostAsJsonAsync(
            ManagementApi.Uri($"/v1/management/organizations/{organization}/projects/{project}/databases"), new { name, homeRegion, replicaRegions }, token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var record = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        return new ManagedDatabaseRef(organization, project, record.GetProperty("id").GetString()!, name);
    }

    private static string Document(ManagedDatabaseRef database) => ManagementApi.DocumentPath(database, "items", "value");

    private static string DatabaseFile(string dataDirectory, ManagedDatabaseRef database) =>
        Path.Combine(dataDirectory, database.Organization, database.Project, database.Id + ".db");

    private static async Task WriteAsync(HttpClient client, ManagedDatabaseRef database, string value, CancellationToken token)
    {
        using var collection = await client.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/collections"), new { name = "items" }, token);
        collection.EnsureSuccessStatusCode();
        using var written = await client.PutAsJsonAsync(ManagementApi.Uri(Document(database)), new { value }, token);
        await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient client, string path, CancellationToken token)
    {
        using var response = await client.GetAsync(ManagementApi.Uri(path), token);
        return response.StatusCode;
    }

    private static Task EventuallyReadAsync(HttpClient client, ManagedDatabaseRef database, string value) =>
        TerraBaseTestServer.EventuallyAsync(async token =>
        {
            using var response = await client.GetAsync(ManagementApi.Uri(Document(database)), token);
            return response.StatusCode == HttpStatusCode.OK
                && (await response.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("document").GetProperty("value").GetString() == value;
        });

    private static async Task AssertReadAsync(HttpClient client, ManagedDatabaseRef database, string value, CancellationToken token)
    {
        using var response = await client.GetAsync(ManagementApi.Uri(Document(database)), token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        await Assert.That(body.GetProperty("document").GetProperty("value").GetString()).IsEqualTo(value);
    }

    private static async Task<double> HeadsFailuresAsync(TerraBaseTestServer server, CancellationToken token)
    {
        try
        {
            return await server.MetricAsync("terrabase_wan_failures_total", "reason=\"heads\"", token);
        }
        catch (InvalidOperationException)
        {
            // Prometheus exposes this counter only after its first failure.
            return 0;
        }
    }
}
