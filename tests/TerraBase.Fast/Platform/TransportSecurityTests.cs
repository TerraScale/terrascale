using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Bogus;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using MagicOnion.Client;
using MagicOnion.Serialization.MessagePack;
using Npgsql;
using TerraBase.Primitives;
using TerraBase.Testing;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Management;

namespace TerraBase.Fast.Platform;

public sealed class TransportSecurityTests
{
    private static readonly Faker Faker = new();

    [Test]
    [Arguments("rest")]
    [Arguments("postgres")]
    public async Task A_token_opens_the_surface_through_the_tls_edge(string surface)
    {
        await using var db = await SqlNativeDatabase.StartAsync();

        if (surface == "rest")
        {
            var results = await db.OkAsync("SELECT 1");
            await Assert.That(results[0].GetProperty("rows")[0][0].GetInt64()).IsEqualTo(1);
        }
        else
        {
            await using var connection = await db.Postgres.OpenConnectionAsync();
            await Assert.That(await SqlNativeDatabase.ScalarAsync(connection, "SELECT 1")).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments("rest")]
    [Arguments("postgres")]
    public async Task Plaintext_that_bypasses_the_tls_edge_and_wrong_tokens_are_refused(string surface)
    {
        var s3 = new FakeS3();
        await using var server = await TerraBaseTestServer.StartAsync(s3.Configure, s3.Route, environmentName: "Production");
        var direct = NonLoopbackAddress();
        var wrong = Faker.Random.AlphaNumeric(48);

        if (surface == "rest")
        {
            using var plaintext = new HttpClient { BaseAddress = new Uri($"http://{direct}:{server.ClientAddress!.Port}") };
            using var edge = new HttpClient { BaseAddress = server.ClientAddress };
            edge.DefaultRequestHeaders.Authorization = new("Bearer", wrong);
            using var refused = await plaintext.GetAsync(new Uri("/v1/regions", UriKind.Relative));
            using var denied = await edge.PostAsJsonAsync(
                new Uri($"/v1/projects/{EntityIds.New()}/databases/{EntityIds.New()}/sql/execute", UriKind.Relative),
                new { statements = new[] { new { sql = "SELECT 1" } } });
            var error = JsonElement.Parse(await refused.Content.ReadAsStringAsync()).GetProperty("error");

            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("tls_required");
            await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
        else
        {
            var plaintext = await StateAsync(direct.ToString(), server.PostgresPort, wrong, SslMode.Prefer);
            var denied = await StateAsync("127.0.0.1", server.PostgresPort, wrong, SslMode.Disable);

            await Assert.That(plaintext).IsEqualTo(("28000", "TLS is required: use sslmode=require"));
            await Assert.That(denied.Code).IsEqualTo("28P01");
        }
    }

    [Test]
    public async Task The_dashboard_rpc_passes_on_the_private_listener_and_the_same_call_is_refused_on_the_public_one()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var key = api.Issuer.Mint(api.User, token =>
        {
            token.Claims["org_id"] = database.Organization;
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = "databases:read databases:write";
        });
        var source = NonLoopbackAddress();
        using var channel = GrpcChannel.ForAddress(api.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = From(source) });
        var invoker = channel.Intercept(metadata =>
        {
            metadata.Add(TerraBaseRpc.PeerCredentialHeader, PeerTls.Secret);
            metadata.Add(TerraBaseRpc.AuthorizationHeader, "Bearer " + key);
            return metadata;
        });
        var management = MagicOnionClient.Create<IManagementService>(invoker, MessagePackMagicOnionSerializerProvider.Default.WithOptions(TerraBaseRpc.SerializerOptions));
        using var plaintext = new HttpClient(From(source)) { BaseAddress = api.Server.ClientAddress };
        plaintext.DefaultRequestHeaders.Authorization = new("Bearer", key);

        var created = await management.CreateDatabaseAsync(database.Project, new TerraBaseDatabasePlacement(Name: "fromrpc"));
        using var refused = await plaintext.PostAsJsonAsync(new Uri($"/v1/projects/{database.Project}/databases", UriKind.Relative), new { name = "frompublic" });
        var error = JsonElement.Parse(await refused.Content.ReadAsStringAsync()).GetProperty("error");

        await Assert.That(created.Name).IsEqualTo("fromrpc");
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("tls_required");
    }

    private static SocketsHttpHandler From(IPAddress source) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                socket.Bind(new IPEndPoint(source, 0));
                await socket.ConnectAsync(IPAddress.Loopback, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    private static async Task<(string Code, string Message)> StateAsync(string host, int port, string key, SslMode ssl)
    {
        var settings = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Username = "wire",
            Password = key,
            Database = Faker.Database.Column(),
            SslMode = ssl,
            Pooling = false,
        };
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        var error = await Assert.ThrowsAsync<PostgresException>(async () => await connection.OpenAsync());
        return (error!.SqlState, error.MessageText);
    }

    private static IPAddress NonLoopbackAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(network => network.GetIPProperties().UnicastAddresses)
        .Select(unicast => unicast.Address)
        .First(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));
}
