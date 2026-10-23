using System.Net;
using System.Text.Json;
using Bogus;
using TerraBase.Testing;

namespace TerraBase.Fast.Wire;

public sealed class WireProtocolTests
{
    private const string Items = "items";

    private static readonly Faker Faker = new();

    [Test]
    public async Task A_session_starts_only_with_a_hello_and_a_key_that_reaches_the_database()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key, _) = await DatabaseAsync(api, "documents:read");

        await using var noHello = await WireClient.ConnectAsync(api.Server.WirePort);
        var first = await noHello.GetAsync(Items, Ulid.NewUlid().ToString());
        await using var badKey = await WireClient.ConnectAsync(api.Server.WirePort);
        var refused = await badKey.HelloAsync("tb_" + Faker.Random.AlphaNumeric(43), database.Project, database.Name);
        await using var noKey = await WireClient.ConnectAsync(api.Server.WirePort);
        var empty = await noKey.HelloAsync("", database.Project, database.Name);
        await using var elsewhere = await WireClient.ConnectAsync(api.Server.WirePort);
        var unknown = await elsewhere.HelloAsync(key, database.Project, "nowhere");
        await using var readOnly = await WireClient.OpenAsync(api.Server.WirePort, key, database.Project, database.Name);
        var put = await readOnly.PutAsync(Items, Ulid.NewUlid().ToString(), "{\"n\":1}");
        var get = await readOnly.GetAsync(Items, Ulid.NewUlid().ToString());
        var badId = await readOnly.GetAsync(Items, "not-a-ulid-but-26-chars-xx");

        await Assert.That((first.Status, first.Code)).IsEqualTo((400, "unsupported_version"));
        await Assert.That((refused.Status, refused.Code)).IsEqualTo((401, "invalid_key"));
        await Assert.That((empty.Status, empty.Code)).IsEqualTo((401, "invalid_key"));
        await Assert.That((unknown.Status, unknown.Code)).IsEqualTo((404, "unknown_database"));
        await Assert.That((put.Status, put.Code)).IsEqualTo((403, "forbidden_scope"));
        await Assert.That((get.Status, get.Code)).IsEqualTo((404, "not_found"));
        await Assert.That((badId.Status, badId.Code)).IsEqualTo((400, "invalid_document_id"));
        await noHello.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        await badKey.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        await elsewhere.Closed.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task A_revoked_key_is_refused_on_its_open_session_and_on_new_ones()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key, keyId) = await DatabaseAsync(api, "documents:read", "documents:write");
        await using var session = await WireClient.OpenAsync(api.Server.WirePort, key, database.Project, database.Name);
        var id = Ulid.NewUlid().ToString();

        var before = await session.PutAsync(Items, id, "{\"n\":1}");
        using var revoked = await api.Client.DeleteAsync(ManagementApi.Uri($"{database.ProjectPath}/api-keys/{keyId}"));
        var after = await session.GetAsync(Items, id);
        await session.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        await using var again = await WireClient.ConnectAsync(api.Server.WirePort);
        var hello = await again.HelloAsync(key, database.Project, database.Name);

        await Assert.That(before.Status).IsEqualTo(201);
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That((after.Status, after.Code)).IsEqualTo((401, "invalid_key"));
        await Assert.That((hello.Status, hello.Code)).IsEqualTo((401, "invalid_key"));
    }

    [Test]
    public async Task Pipelined_puts_and_gets_answer_by_request_id_and_see_the_connection_s_earlier_puts()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key, _) = await DatabaseAsync(api, "documents:read", "documents:write");
        await using var session = await WireClient.OpenAsync(api.Server.WirePort, key, database.Project, database.Name);
        var ids = Enumerable.Range(0, 32).Select(_ => Ulid.NewUlid().ToString()).ToArray();
        var first = ids.ToDictionary(id => id, _ => JsonSerializer.Serialize(new { name = Faker.Name.FullName() }));
        var second = ids.ToDictionary(id => id, _ => JsonSerializer.Serialize(new { name = Faker.Name.FullName(), age = Faker.Random.Int(1, 99) }));

        // Every request is sent before any answer is awaited.
        var sent = ids.Select(id => (Id: id, Put1: session.PutAsync(Items, id, first[id]), Get1: session.GetAsync(Items, id),
            Put2: session.PutAsync(Items, id, second[id]), Get2: session.GetAsync(Items, id))).ToList();
        var reads = Enumerable.Range(0, 64).Select(i => session.GetAsync(Items, ids[i % ids.Length])).ToList();
        await Task.WhenAll(sent.SelectMany(request => new[] { request.Put1, request.Get1, request.Put2, request.Get2 }).Concat(reads));
        using var data = api.DataClient(key);
        var stored = await data.GetFromJsonAsync<JsonElement>(ManagementApi.Uri(ManagementApi.DocumentPath(database, Items, ids[0])));

        foreach (var (id, put1, get1, put2, get2) in sent)
        {
            await Assert.That((put1.Result.Status, put2.Result.Status, get1.Result.Status, get2.Result.Status)).IsEqualTo((201, 200, 200, 200));
            await Assert.That(JsonElement.Parse(get1.Result.Json).GetProperty("name").GetString()).IsEqualTo(JsonElement.Parse(first[id]).GetProperty("name").GetString());
            await Assert.That(JsonElement.Parse(get2.Result.Json).GetProperty("age").GetInt32()).IsEqualTo(JsonElement.Parse(second[id]).GetProperty("age").GetInt32());
            await Assert.That(get1.Result.ETag).IsEqualTo(put1.Result.ETag);
            await Assert.That(get2.Result.ETag).IsEqualTo(put2.Result.ETag);
        }

        await Assert.That(reads.Select(read => read.Result.Status).Distinct()).IsEquivalentTo([200]);
        await Assert.That(stored.GetProperty("document").GetProperty("age").GetInt32()).IsEqualTo(JsonElement.Parse(second[ids[0]]).GetProperty("age").GetInt32());
    }

    [Test]
    public async Task A_put_answers_once_durable_and_survives_a_restart()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-wire-durable-").FullName;
        try
        {
            ManagedDatabaseRef database;
            string key;
            var id = Ulid.NewUlid().ToString();
            var json = JsonSerializer.Serialize(new { city = Faker.Address.City() });
            WireAnswer put;
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                (database, key, _) = await DatabaseAsync(api, "documents:read", "documents:write");
                await using var session = await WireClient.OpenAsync(api.Server.WirePort, key, database.Project, database.Name);
                put = await session.PutAsync(Items, id, json);
            }

            await using var reopened = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory);
            await using var again = await WireClient.OpenAsync(reopened.Server.WirePort, key, database.Project, database.Name);
            var get = await again.GetAsync(Items, id);

            // A cell of one answers local_durable, as the HTTP API does.
            await Assert.That((put.Status, put.RemoteDurable)).IsEqualTo((201, false));
            await Assert.That((get.Status, get.ETag, get.Json)).IsEqualTo((200, put.ETag, json));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Usage_counts_each_wire_read_and_write_once()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key, _) = await DatabaseAsync(api, "documents:read", "documents:write");
        await using var session = await WireClient.OpenAsync(api.Server.WirePort, key, database.Project, database.Name);
        var puts = Faker.Random.Int(3, 12);
        var gets = Faker.Random.Int(3, 20);
        var ids = Enumerable.Range(0, puts).Select(_ => Ulid.NewUlid().ToString()).ToArray();

        var written = await Task.WhenAll(ids.Select(id => session.PutAsync(Items, id, "{\"n\":1}")));
        var read = await Task.WhenAll(Enumerable.Range(0, gets).Select(i => session.GetAsync(Items, ids[i % puts])));
        using var usage = JsonDocument.Parse(await api.Client.GetStringAsync(ManagementApi.Uri(database.Path + "/usage")));

        await Assert.That(written.Select(answer => answer.Status).Distinct()).IsEquivalentTo([201]);
        await Assert.That(read.Select(answer => answer.Status).Distinct()).IsEquivalentTo([200]);
        await Assert.That(usage.RootElement.GetProperty("writes").GetInt64()).IsEqualTo(puts);
        await Assert.That(usage.RootElement.GetProperty("reads").GetInt64()).IsEqualTo(gets);
        await Assert.That(usage.RootElement.GetProperty("documents").GetInt64()).IsEqualTo(puts);
    }

    // An enterprise database with the collection, and a project key of the given scopes: its secret and its id.
    private static async Task<(ManagedDatabaseRef Database, string Key, string KeyId)> DatabaseAsync(ManagementApi api, params string[] scopes)
    {
        var database = await api.CreateManagedDatabaseAsync();
        // Enterprise: no request rate or concurrency limit, so pipelined requests are never refused as tenant_busy.
        await api.SetPlanAsync(api.User, database.Organization, "enterprise");
        using var created = await api.Client.PostAsJsonAsync(ManagementApi.Uri($"{database.ProjectPath}/api-keys"), new { name = Faker.Hacker.Noun(), scopes });
        created.EnsureSuccessStatusCode();
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var (secret, id) = (body.GetProperty("secret").GetString()!, body.GetProperty("key").GetProperty("id").GetString()!);
        using var owner = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(owner, database, Items);
        return (database, secret, id);
    }
}
