using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Bogus;
using Npgsql;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class TransportSecurityTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Rest_requests_that_bypass_the_tls_edge_are_refused_and_probes_still_answer()
    {
        var s3 = new FakeS3();
        await using var server = await TerraBaseTestServer.StartAsync(s3.Configure, s3.Route, environmentName: "Production");
        using var direct = new HttpClient { BaseAddress = new Uri($"http://{NonLoopbackAddress()}:{server.ClientAddress!.Port}") };
        using var edge = new HttpClient { BaseAddress = server.ClientAddress };

        using var refused = await direct.GetAsync(new Uri("/v1/regions", UriKind.Relative));
        using var probe = await direct.GetAsync(new Uri("/readyz", UriKind.Relative));
        using var served = await edge.GetAsync(new Uri("/v1/regions", UriKind.Relative));
        var error = JsonElement.Parse(await refused.Content.ReadAsStringAsync()).GetProperty("error");

        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("tls_required");
        await Assert.That(error.GetProperty("message").GetString()).Contains("https://");
        await Assert.That(probe.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(served.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Postgres_sessions_that_bypass_the_tls_edge_are_refused_before_any_password_is_requested()
    {
        var s3 = new FakeS3();
        await using var server = await TerraBaseTestServer.StartAsync(s3.Configure, s3.Route, environmentName: "Production");
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(NonLoopbackAddress(), server.PostgresPort);
        var stream = tcp.GetStream();

        var ssl = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(ssl, 8);
        BinaryPrimitives.WriteInt32BigEndian(ssl.AsSpan(4), 80877103);
        await stream.WriteAsync(ssl);
        var answer = new byte[1];
        await stream.ReadExactlyAsync(answer);
        byte[] startup = [0, 3, 0, 0, .. CString("user"), .. CString("wire"), .. CString("database"), .. CString(Faker.Database.Column()), 0];
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, startup.Length + 4);
        await stream.WriteAsync((byte[])[.. length, .. startup]);
        var head = new byte[5];
        await stream.ReadExactlyAsync(head);
        var body = new byte[BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(1)) - 4];
        await stream.ReadExactlyAsync(body);
        var fields = Encoding.UTF8.GetString(body).Split('\0');
        var rest = await stream.ReadAsync(new byte[1]);

        await Assert.That((char)answer[0]).IsEqualTo('N');
        await Assert.That((char)head[0]).IsEqualTo('E');
        await Assert.That(fields).Contains("C28000");
        await Assert.That(fields).Contains("MTLS is required: use sslmode=require");
        await Assert.That(rest).IsEqualTo(0);
    }

    [Test]
    public async Task Tokens_open_every_surface_and_wrong_tokens_are_refused_on_each()
    {
        await using var db = await SqlNativeDatabase.StartAsync();
        var wrong = Faker.Random.AlphaNumeric(48);
        using var wrongRest = db.Api.DataClient(wrong);
        await using var wrongPostgres = PostgresSql.Source(db.Api, wrong, db.Database.Name);

        var rest = await db.OkAsync("SELECT 1");
        var (restRefused, _) = await db.PostAsync(wrongRest, SqlNativeDatabase.Stmt("SELECT 1"));
        await using var connection = await db.Postgres.OpenConnectionAsync();
        var postgres = await SqlNativeDatabase.ScalarAsync(connection, "SELECT 1");
        var postgresRefused = await Assert.ThrowsAsync<PostgresException>(async () => await wrongPostgres.OpenConnectionAsync());

        await Assert.That(rest[0].GetProperty("rows")[0][0].GetInt64()).IsEqualTo(1);
        await Assert.That(restRefused).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(postgres).IsEqualTo(1);
        await Assert.That(postgresRefused!.SqlState).IsEqualTo("28P01");
    }

    private static IPAddress NonLoopbackAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(network => network.GetIPProperties().UnicastAddresses)
        .Select(unicast => unicast.Address)
        .First(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));

    private static byte[] CString(string value) => [.. Encoding.UTF8.GetBytes(value), 0];
}
