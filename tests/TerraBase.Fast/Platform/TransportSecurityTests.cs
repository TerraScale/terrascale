using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Bogus;
using Npgsql;
using TerraBase.Primitives;
using TerraBase.Testing;

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

    private static async Task<(string Code, string Message)> StateAsync(string host, int port, string key, SslMode ssl)
    {
        var settings = new NpgsqlConnectionStringBuilder
        {
            Host = host, Port = port, Username = "wire", Password = key, Database = Faker.Database.Column(), SslMode = ssl, Pooling = false,
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
