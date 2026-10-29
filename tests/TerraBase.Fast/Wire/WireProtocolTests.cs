using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Fast.Documents;
using TerraBase.Features.Memory;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Wire;

public sealed class WireProtocolTests
{
    private const string Items = "items";

    private static readonly Faker Faker = new();

    [Test]
    public async Task Outgoing_memory_refusal_does_not_accept_a_wire_read_charge()
    {
        await using var api = await DocumentApi.StartAsync(services: services =>
        {
            var memory = services.Last(service => service.ServiceType == typeof(IMemoryBudget));
            services.AddSingleton<IMemoryBudget>(provider => new RefusedWireAnswer((IMemoryBudget)memory.ImplementationFactory!(provider)));
        });
        var id = DocumentApi.NewId();
        var document = new { body = new string('x', 10 * 1024) };
        var put = await api.PutAsync(id, document);
        put.Status.ShouldBe(HttpStatusCode.Created);
        var budget = (RefusedWireAnswer)api.Server.Services.GetRequiredService<IMemoryBudget>();
        budget.Bytes = 11 + put.Version.Length + Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(document));
        var key = await api.Server.IssueNarrowedAsync(api.Tenant.OrganizationId, api.Tenant.ProjectId, null, "documents:read");
        await using var session = await WireClient.OpenAsync(api.Server.WirePort, key, api.Tenant.ProjectId, DocumentApi.Database);
        var refused = await session.GetAsync(Items, id);
        await Assert.That((refused.Status, refused.Code)).IsEqualTo((429, "tenant_memory"));
        await Assert.That(await api.ReadUsageAsync()).IsEqualTo((0L, 0L));
        budget.Bytes = 0;
        await Assert.That((await session.GetAsync(Items, id)).Status).IsEqualTo(200);
        await Assert.That(await api.ReadUsageAsync()).IsEqualTo((3L, 1L));
    }

    private sealed class RefusedWireAnswer(IMemoryBudget inner) : IMemoryBudget
    {
        public long Bytes { get; set; } = -1;
        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes) => inner.TryReserve(lane, bytes);
        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant) =>
            tenant is not null && lane == MemoryLane.Foreground && bytes == Bytes ? null : inner.TryReserve(lane, bytes, tenant);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_session_starts_only_with_a_hello_and_a_key_that_reaches_the_database(bool idRoute)
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key, _) = await DatabaseAsync(api, "documents:read");
        var other = await api.CreateManagedDatabaseAsync();

        var first = await RefusedAsync(api, 1, [1, 2, 3]);
        var badKey = await RefusedAsync(api, 0, Hello("tb_" + Faker.Random.AlphaNumeric(43), database.Project, database.Name));
        var noKey = await RefusedAsync(api, 0, Hello("", database.Project, database.Name));
        var unknown = await RefusedAsync(api, 0, Hello(key, database.Project, "nowhere"));
        var (outOfReach, _) = await RefusedAsync(api, 0, Hello(key, other.Project, other.Name));
        using var tcp = await ConnectAsync(api);
        var stream = tcp.GetStream();
        var (helloId, helloStatus, _) = await ExchangeAsync(stream, 1, 0, Hello(key, database.Project, idRoute ? database.Id : database.Name));
        var operations = new List<(uint, int, string)>();
        for (var id = 2u; id < 2 + Faker.Random.UInt(1, 10); id++)
        {
            var (answered, status, code) = await ExchangeAsync(stream, id, Faker.Random.Byte(4, 255), []);
            operations.Add((answered - id, status, code));
        }

        await Assert.That(first).IsEqualTo((400, "unsupported_version"));
        await Assert.That(badKey).IsEqualTo((401, "invalid_key"));
        await Assert.That(noKey).IsEqualTo((401, "invalid_key"));
        await Assert.That(unknown).IsEqualTo((404, "unknown_database"));
        await Assert.That(outOfReach).IsEqualTo(404);
        await Assert.That((helloId, helloStatus)).IsEqualTo((1u, 200));
        await Assert.That(operations.Distinct()).IsEquivalentTo([(0u, 400, "unknown_operation")]);
    }

    [Test]
    public async Task Deletes_validate_the_frame_and_preserve_the_session_after_refusals()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key, _) = await DatabaseAsync(api, "documents:read", "documents:write");
        await using var session = await WireClient.OpenAsync(api.Server.WirePort, key, database.Project, database.Name);
        var id = Ulid.NewUlid().ToString();
        byte[] address = [(byte)Items.Length, .. Encoding.UTF8.GetBytes(Items), .. Encoding.ASCII.GetBytes(id)];
        var missing = await session.DeleteAsync(Items, id);
        var trailing = await session.SendAsync(3, [.. address, 0, 123]);
        var noDurability = await session.SendAsync(3, address);
        var badDurability = await session.SendAsync(3, [.. address, 2]);
        var inserted = await session.PutAsync(Items, id, "{\"n\":1}");
        var deleted = await session.DeleteAsync(Items, id);
        var read = await session.GetAsync(Items, id);

        await Assert.That((missing.Status, missing.Code)).IsEqualTo((404, "not_found"));
        await Assert.That((trailing.Status, trailing.Code)).IsEqualTo((400, "invalid_frame"));
        await Assert.That(noDurability.Status).IsEqualTo(400);
        await Assert.That(badDurability.Status).IsEqualTo(400);
        await Assert.That(inserted.Status).IsEqualTo(201);
        await Assert.That(deleted.Status).IsEqualTo(200);
        await Assert.That(deleted.RemoteDurable).IsFalse();
        await Assert.That(deleted.ETag).StartsWith("tbv1-");
        await Assert.That((read.Status, read.Code)).IsEqualTo((404, "not_found"));
    }

    [Test]
    public async Task Pinned_hello_bounds_and_region_syntax_fail_closed_before_key_lookup()
    {
        await using var api = await ManagementApi.StartAsync();
        var hello = Hello("invalid", Ulid.NewUlid().ToString(), "main");
        await Assert.That(await RefusedAsync(api, 4, hello)).IsEqualTo((400, "invalid_frame"));
        await Assert.That(await RefusedAsync(api, 4, [.. hello, 0])).IsEqualTo((400, "invalid_frame"));
        await Assert.That(await RefusedAsync(api, 4, [.. hello, 64, .. Encoding.ASCII.GetBytes(new string('a', 64))])).IsEqualTo((400, "invalid_frame"));
        await Assert.That(await RefusedAsync(api, 4, [.. hello, 3, .. "gru"u8, 0])).IsEqualTo((400, "invalid_frame"));
        await Assert.That(await RefusedAsync(api, 4, [.. hello, 3, .. "GRU"u8])).IsEqualTo((421, "misdirected_request"));
    }

    [Test]
    public async Task A_frame_beyond_its_bound_answers_invalid_frame_and_closes_the_connection()
    {
        await using var api = await ManagementApi.StartAsync();
        using var tcp = await ConnectAsync(api);
        var stream = tcp.GetStream();
        // A hello may be at most 4 KiB.
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)Faker.Random.Int(4097, 1 << 20));
        await stream.WriteAsync(header);

        var answer = await ReadAnswerAsync(stream);
        var closed = await stream.ReadAsync(new byte[1]);

        await Assert.That(answer).IsEqualTo((0u, 400, "invalid_frame"));
        await Assert.That(closed).IsEqualTo(0);
    }

    [Test]
    public async Task A_read_only_key_reads_but_cannot_put_and_requests_are_validated_as_on_http()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key, _) = await DatabaseAsync(api, "documents:read");
        await using var session = await WireClient.OpenAsync(api.Server.WirePort, key, database.Project, database.Name);

        var put = await session.PutAsync(Items, Ulid.NewUlid().ToString(), "{\"n\":1}");
        var delete = await session.DeleteAsync(Items, Ulid.NewUlid().ToString());
        var missing = await session.GetAsync(Items, Ulid.NewUlid().ToString());
        var badId = await session.GetAsync(Items, "not-a-ulid-but-26-chars-xx");
        var badCollection = await session.GetAsync("Bad Collection!", Ulid.NewUlid().ToString());

        await Assert.That((put.Status, put.Code)).IsEqualTo((403, "forbidden_scope"));
        await Assert.That((delete.Status, delete.Code)).IsEqualTo((403, "forbidden_scope"));
        await Assert.That((missing.Status, missing.Code)).IsEqualTo((404, "not_found"));
        await Assert.That((badId.Status, badId.Code)).IsEqualTo((400, "invalid_document_id"));
        await Assert.That(badCollection.Status).IsEqualTo(400);
    }


    [Test]
    public async Task Pipelined_requests_answer_by_request_id()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key, _) = await DatabaseAsync(api, "documents:read");
        await using var session = await WireClient.OpenAsync(api.Server.WirePort, key, database.Project, database.Name);

        // Every request is sent before any answer is awaited; more than run at once on one connection.
        var answers = await Task.WhenAll(Enumerable.Range(0, Faker.Random.Int(200, 400)).Select(_ => session.SendAsync(Faker.Random.Byte(4, 255), Faker.Random.Bytes(Faker.Random.Int(0, 64)))));

        await Assert.That(answers.Select(answer => (answer.Status, answer.Code)).Distinct()).IsEquivalentTo([(400, "unknown_operation")]);
        await Assert.That(session.Closed.IsCompleted).IsFalse();
    }

    [Test]
    public async Task A_revoked_key_is_refused_on_its_open_session_and_on_new_ones()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key, keyId) = await DatabaseAsync(api, "documents:read", "documents:write");
        await using var session = await WireClient.OpenAsync(api.Server.WirePort, key, database.Project, database.Name);
        var id = Ulid.NewUlid().ToString();

        var before = await session.PutAsync(Items, id, "{\"n\":1}");
        using var revoked = await api.Server.Client.DeleteAsync(ManagementApi.Uri($"/admin/api-keys/{keyId}"));
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
            // The server answers raw UTF-8; a generated "O'Hara" would serialize as \u0027 here and never compare equal.
            var json = JsonSerializer.Serialize(new { city = Faker.Address.City().Replace("'", "", StringComparison.Ordinal) });
            WireAnswer put, unknownDurability;
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                (database, key, _) = await DatabaseAsync(api, "documents:read", "documents:write");
                await using var session = await WireClient.OpenAsync(api.Server.WirePort, key, database.Project, database.Name);
                unknownDurability = await session.SendAsync(2, [(byte)Items.Length, .. Encoding.ASCII.GetBytes(Items), .. Encoding.ASCII.GetBytes(id),
                    Faker.Random.Byte(2), .. Encoding.UTF8.GetBytes(json)]);
                put = await session.PutAsync(Items, id, json);
            }

            await using var reopened = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory);
            await using var again = await WireClient.OpenAsync(reopened.Server.WirePort, key, database.Project, database.Name);
            var get = await again.GetAsync(Items, id);

            // Asked for local_durable, it answers after this region's durable commit, as the HTTP API does.
            await Assert.That(unknownDurability.Status).IsEqualTo(400);
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
        using var created = await api.Server.Client.PostAsJsonAsync(ManagementApi.Uri("/admin/api-keys"),
            new { name = Faker.Hacker.Noun(), scopes, organizationId = database.Organization, projectId = database.Project });
        created.EnsureSuccessStatusCode();
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        using var owner = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(owner, database, Items);
        return (database, body.GetProperty("secret").GetString()!, body.GetProperty("id").GetString()!);
    }

    private static async Task<TcpClient> ConnectAsync(ManagementApi api)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(IPAddress.Loopback, api.Server.WirePort);
        return tcp;
    }

    // The answer to a refused first frame, which also closes the connection.
    private static async Task<(int Status, string Code)> RefusedAsync(ManagementApi api, byte operation, byte[] body)
    {
        using var tcp = await ConnectAsync(api);
        var stream = tcp.GetStream();
        var (_, status, code) = await ExchangeAsync(stream, 1, operation, body);
        await Assert.That(await stream.ReadAsync(new byte[1])).IsEqualTo(0);
        return (status, code);
    }

    private static byte[] Hello(string key, string project, string database) => [1, .. Text(key), .. Text(project), .. Text(database)];

    private static byte[] Text(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return [(byte)bytes.Length, (byte)(bytes.Length >> 8), .. bytes];
    }

    // Sends one request frame and reads one answer: its request id, status and error code.
    private static async Task<(uint Id, int Status, string Code)> ExchangeAsync(NetworkStream stream, uint id, byte operation, byte[] body)
    {
        var frame = new byte[9 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)(frame.Length - 4));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), id);
        frame[8] = operation;
        body.CopyTo(frame.AsSpan(9));
        await stream.WriteAsync(frame);
        return await ReadAnswerAsync(stream);
    }

    private static async Task<(uint Id, int Status, string Code)> ReadAnswerAsync(NetworkStream stream)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header);
        var answer = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header)];
        await stream.ReadExactlyAsync(answer);
        var status = BinaryPrimitives.ReadUInt16LittleEndian(answer.AsSpan(4));
        return (BinaryPrimitives.ReadUInt32LittleEndian(answer), status, status >= 400 ? Encoding.ASCII.GetString(answer, 7, answer[6]) : "");
    }
}
