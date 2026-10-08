using System.Globalization;
using System.Net;
using System.Text.Json;
using Bogus;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class UniqueReplicationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Partitioned_unique_writes_converge_and_a_deleted_winner_releases_its_key(bool reverse)
    {
        using var tls = new PeerTls();
        var firstData = Directory.CreateTempSubdirectory("terrabase-unique-first-").FullName;
        var secondData = Directory.CreateTempSubdirectory("terrabase-unique-second-").FullName;
        var low = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
        var high = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
        var email = new Faker().Internet.Email().ToLowerInvariant();
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        try
        {
            ReplicationTenant tenant;
            int firstPort;
            int secondPort;
            await using (var first = await ManagementApi.StartAsync(settings => { tls.Apply(settings); settings["data-directory"] = firstData; }))
            await using (var second = await ManagementApi.StartAsync(settings => { tls.Apply(settings); settings["data-directory"] = secondData; }))
            {
                tenant = await ReplicationTenant.CreateAsync(first.Server, ["db"], token);
                await tenant.BindAsync(second.Server);
                firstPort = first.Server.PeerAddress!.Port;
                secondPort = second.Server.PeerAddress!.Port;
                await SeedAsync(first, tenant, reverse ? low : high, email, token);
                await SeedAsync(second, tenant, reverse ? high : low, email.ToUpperInvariant(), token);
            }

            await using var a = await ManagementApi.StartAsync(settings =>
            {
                tls.Apply(settings);
                settings["data-directory"] = firstData;
                settings["peer-port"] = firstPort.ToString(CultureInfo.InvariantCulture);
                settings["peer-addresses"] = $"https://127.0.0.1:{secondPort}";
            });
            await using var b = await ManagementApi.StartAsync(settings =>
            {
                tls.Apply(settings);
                settings["data-directory"] = secondData;
                settings["peer-port"] = secondPort.ToString(CultureInfo.InvariantCulture);
                settings["peer-addresses"] = a.Server.PeerAddress!.ToString();
            });
            await Task.WhenAll(TerraBaseTestServer.ReplicatedAsync(a.Server), TerraBaseTestServer.ReplicatedAsync(b.Server));

            using var firstClient = a.Tls.CreateClient(a.Server.ClientAddress!, await tenant.KeyAsync(a.Server, "documents:read", "documents:write", "shapes:read"));
            using var secondClient = b.Tls.CreateClient(b.Server.ClientAddress!, await tenant.KeyAsync(b.Server, "documents:read", "documents:write", "shapes:read"));
            foreach (var client in new[] { firstClient, secondClient })
            {
                await VisibleAsync(client, tenant, low, token);
                using var conflict = await client.GetAsync(Document(tenant, high), token);
                await Assert.That(conflict.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
                var error = await conflict.Content.ReadFromJsonAsync<JsonElement>(token);
                await Assert.That(error.GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unique_violation");
                await Assert.That(conflict.Headers.ETag).IsNotNull();
                using var listed = await client.GetAsync(new Uri(tenant.Path("db") + "/collections/people/documents", UriKind.Relative), token);
                listed.EnsureSuccessStatusCode();
                var page = await listed.Content.ReadFromJsonAsync<JsonElement>(token);
                await Assert.That(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!)).IsEquivalentTo([low]);
            }

            using var deleted = await firstClient.DeleteAsync(Document(tenant, low), token);
            deleted.EnsureSuccessStatusCode();
            await TerraBaseTestServer.ReplicatedAsync(a.Server);
            await VisibleAsync(firstClient, tenant, high, token);
            await VisibleAsync(secondClient, tenant, high, token);
            using var duplicate = await secondClient.PutAsJsonAsync(Document(tenant, Ulid.NewUlid().ToString()), new { email }, token);
            await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        }
        finally
        {
            Directory.Delete(firstData, recursive: true);
            Directory.Delete(secondData, recursive: true);
        }
    }

    private static async Task SeedAsync(ManagementApi api, ReplicationTenant tenant, string id, string email, CancellationToken token)
    {
        using var data = api.Tls.CreateClient(api.Server.ClientAddress!, await tenant.KeyAsync(api.Server, ["documents:read", "documents:write"], token));
        using var create = await data.PostAsJsonAsync(new Uri(tenant.Path("db") + "/collections", UriKind.Relative), new { name = "people" }, token);
        create.EnsureSuccessStatusCode();
        using var management = api.As(tenant.UserId);
        using var declared = await management.PutAsJsonAsync(new Uri(tenant.Path("db") + "/schema/people", UriKind.Relative), new
        {
            fields = new[] { new { name = "email", unique = true, normalization = "case_fold" } },
        }, token);
        declared.EnsureSuccessStatusCode();
        using var put = await data.PutAsJsonAsync(Document(tenant, id), new { email }, token);
        put.EnsureSuccessStatusCode();
    }

    private static async Task VisibleAsync(HttpClient client, ReplicationTenant tenant, string id, CancellationToken token)
    {
        using var read = await client.GetAsync(Document(tenant, id), token);
        read.EnsureSuccessStatusCode();
        var document = await read.Content.ReadFromJsonAsync<JsonElement>(token);
        await Assert.That(document.GetProperty("id").GetString()).IsEqualTo(id);
    }

    private static Uri Document(ReplicationTenant tenant, string id) => new(tenant.Path("db") + "/collections/people/documents/" + id, UriKind.Relative);
}
