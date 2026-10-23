using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Bogus;
using TerraBase.Testing;

namespace TerraBase.Fast.Wire;

public sealed class WireProtocolTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task A_session_starts_only_with_a_hello_and_a_key_that_reaches_the_database()
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
        var (helloId, helloStatus, _) = await ExchangeAsync(stream, 1, 0, Hello(key, database.Project, database.Name));
        var operations = new List<(uint, int, string)>();
        for (var id = 2u; id < 2 + Faker.Random.UInt(1, 10); id++)
        {
            var (answered, status, code) = await ExchangeAsync(stream, id, Faker.Random.Byte(3, 255), []);
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
    public async Task Pipelined_requests_answer_by_request_id()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key, _) = await DatabaseAsync(api, "documents:read");
        await using var session = await WireClient.OpenAsync(api.Server.WirePort, key, database.Project, database.Name);

        // Every request is sent before any answer is awaited; more than run at once on one connection.
        var answers = await Task.WhenAll(Enumerable.Range(0, Faker.Random.Int(200, 400)).Select(_ => session.SendAsync(Faker.Random.Byte(3, 255), Faker.Random.Bytes(Faker.Random.Int(0, 64)))));

        await Assert.That(answers.Select(answer => (answer.Status, answer.Code)).Distinct()).IsEquivalentTo([(400, "unknown_operation")]);
        await Assert.That(session.Closed.IsCompleted).IsFalse();
    }

    [Test]
    public async Task A_revoked_key_is_refused_on_its_open_session_and_on_new_ones()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key, keyId) = await DatabaseAsync(api, "documents:read");
        await using var session = await WireClient.OpenAsync(api.Server.WirePort, key, database.Project, database.Name);

        var before = await session.SendAsync(3, []);
        using var revoked = await api.Client.DeleteAsync(ManagementApi.Uri($"{database.ProjectPath}/api-keys/{keyId}"));
        var after = await session.SendAsync(3, []);
        await session.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        await using var again = await WireClient.ConnectAsync(api.Server.WirePort);
        var hello = await again.HelloAsync(key, database.Project, database.Name);

        await Assert.That((before.Status, before.Code)).IsEqualTo((400, "unknown_operation"));
        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That((after.Status, after.Code)).IsEqualTo((401, "invalid_key"));
        await Assert.That((hello.Status, hello.Code)).IsEqualTo((401, "invalid_key"));
    }

    // An enterprise database and a project key of the given scopes: its secret and its id.
    private static async Task<(ManagedDatabaseRef Database, string Key, string KeyId)> DatabaseAsync(ManagementApi api, params string[] scopes)
    {
        var database = await api.CreateManagedDatabaseAsync();
        // Enterprise: no request rate or concurrency limit, so pipelined requests are never refused as tenant_busy.
        await api.SetPlanAsync(api.User, database.Organization, "enterprise");
        using var created = await api.Client.PostAsJsonAsync(ManagementApi.Uri($"{database.ProjectPath}/api-keys"), new { name = Faker.Hacker.Noun(), scopes });
        created.EnsureSuccessStatusCode();
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        return (database, body.GetProperty("secret").GetString()!, body.GetProperty("key").GetProperty("id").GetString()!);
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
