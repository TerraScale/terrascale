using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Bogus;
using TerraBase.Primitives;
using TerraBase.Testing;
using static TerraBase.Testing.ManagementApi;

namespace TerraBase.Fast.Management;

/// <summary>
/// API key IP allowlists (<c>key_cidrs</c>, <c>project_cidrs</c> on ts-auth's token): a request from outside any list the
/// token carries answers 403 ip_not_allowed, over HTTP and on the Postgres wire, where the edge names the client with a
/// PROXY protocol v2 header.
/// </summary>
public sealed class IpAllowlistTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Http_requests_must_come_from_every_allowlist_the_token_carries()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync("main");
        var collections = Uri(database.Data + "/collections");

        async Task<(HttpStatusCode Status, string? Code)> GetAsync(string? keyCidrs, string? projectCidrs, string? forwardedFor = null)
        {
            using var client = api.WithToken(api.Issuer.Mint(EntityIds.New().ToString(), token =>
            {
                token.Claims["project_id"] = database.Project;
                token.Claims["scope"] = "databases:read documents:read";
                if (keyCidrs is not null)
                {
                    token.Claims["key_cidrs"] = keyCidrs;
                }

                if (projectCidrs is not null)
                {
                    token.Claims["project_cidrs"] = projectCidrs;
                }
            }));
            using var request = new HttpRequestMessage(HttpMethod.Get, collections);
            if (forwardedFor is not null)
            {
                request.Headers.Add("X-Forwarded-For", forwardedFor);
            }

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, body.Length > 0 && JsonElement.Parse(body).TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null);
        }

        await Assert.That((await GetAsync(null, null)).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await GetAsync("127.0.0.0/8", "0.0.0.0/0 ::/0")).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await GetAsync("203.0.113.0/24", null)).IsEqualTo((HttpStatusCode.Forbidden, "ip_not_allowed"));
        await Assert.That(await GetAsync(null, "198.51.100.7/32")).IsEqualTo((HttpStatusCode.Forbidden, "ip_not_allowed"));
        // The edge (a loopback peer here) names the client in the rightmost X-Forwarded-For hop.
        await Assert.That((await GetAsync("203.0.113.0/24", null, "198.51.100.1, 203.0.113.9")).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await GetAsync("203.0.113.0/24", "198.51.100.0/24", "203.0.113.9")).IsEqualTo((HttpStatusCode.Forbidden, "ip_not_allowed"));
        // A malformed list fails authentication instead of allowing every address.
        await Assert.That((await GetAsync("not-a-cidr", null)).Status).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Postgres_connections_are_checked_against_the_client_the_proxy_header_names()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        var key = DashboardKey(api, database, "203.0.113.0/24");

        await Assert.That(await ConnectAsync(api.Server.PostgresPort, key, database.Name, null)).IsEqualTo("28000");
        await Assert.That(await ConnectAsync(api.Server.PostgresPort, key, database.Name, IPAddress.Parse("198.51.100.4"))).IsEqualTo("28000");
        await Assert.That(await ConnectAsync(api.Server.PostgresPort, key, database.Name, IPAddress.Parse("203.0.113.4"))).IsEqualTo("ok");
        await Assert.That(await ConnectAsync(api.Server.PostgresPort, DashboardKey(api, database, "2001:db8::/32"), database.Name, IPAddress.Parse("2001:db8::5"))).IsEqualTo("ok");
    }

    private static string DashboardKey(ManagementApi api, ManagedDatabaseRef database, string cidrs)
    {
        var id = EntityIds.New().ToString();
        var key = id + "_" + Faker.Random.String2(52, "0123456789abcdefghjkmnpqrstvwxyz");
        api.Issuer.GrantClient(key, () => api.Issuer.Mint(id, token =>
        {
            token.Claims["key_id"] = id;
            token.Claims["key_gen"] = TestIssuer.KeyGeneration(key);
            token.Claims["project_id"] = database.Project;
            token.Claims["scope"] = "documents:read";
            token.Claims["key_cidrs"] = cidrs;
        }));
        return key;
    }

    // Startup and password as psql sends them, behind an optional PROXY v2 header; "ok" or the error's SQLSTATE.
    private static async Task<string> ConnectAsync(int port, string key, string database, IPAddress? client)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        var stream = tcp.GetStream();
        if (client is not null)
        {
            var source = client.GetAddressBytes();
            byte[] header = [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A, 0x21, (byte)(source.Length == 4 ? 0x11 : 0x21), 0, (byte)(source.Length * 2 + 4)];
            await stream.WriteAsync(header);
            await stream.WriteAsync(source);
            await stream.WriteAsync(new byte[source.Length]);
            await stream.WriteAsync(new byte[] { 0xC0, 0x00, 0x15, 0x38 });
        }

        await SendAsync(stream, null, [0, 3, 0, 0, .. CString("user"), .. CString("wire"), .. CString("database"), .. CString(database), 0]);
        _ = await ReadAsync(stream); // AuthenticationCleartextPassword
        await SendAsync(stream, 'p', CString(key));
        var (type, body) = await ReadAsync(stream);
        return type == 'E' ? Encoding.UTF8.GetString(body).Split('\0').First(field => field.StartsWith('C'))[1..] : "ok";
    }

    private static byte[] CString(string value) => [.. Encoding.UTF8.GetBytes(value), 0];

    private static async Task SendAsync(NetworkStream stream, char? type, byte[] body)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, body.Length + 4);
        byte[] frame = type is { } t ? [(byte)t, .. length, .. body] : [.. length, .. body];
        await stream.WriteAsync(frame);
    }

    private static async Task<(char Type, byte[] Body)> ReadAsync(NetworkStream stream)
    {
        var head = new byte[5];
        await stream.ReadExactlyAsync(head);
        var body = new byte[BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(1)) - 4];
        await stream.ReadExactlyAsync(body);
        return ((char)head[0], body);
    }
}
