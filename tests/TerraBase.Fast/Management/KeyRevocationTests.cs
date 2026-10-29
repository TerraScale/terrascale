using System.Data;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class KeyRevocationTests
{
    [Test]
    [Arguments("key_revoked")]
    [Arguments("key_expired")]
    public async Task The_same_bearer_stops_reading_when_its_key_stops_acting(string code)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(time: time);
        var database = await api.CreateManagedDatabaseAsync();
        var (key, token) = Grant(api, database);
        using var client = api.WithToken(token);
        var route = ManagementApi.Uri(database.Data + "/collections");
        using (var first = await client.GetAsync(route))
        {
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        if (code == "key_expired")
        {
            api.Issuer.ExpireClient(key);
        }
        else
        {
            api.Issuer.RevokeClient(key);
        }

        // Expire the cached authority without dispatching timers; this request must recheck the key itself.
        time.AdjustTime(time.GetUtcNow().AddSeconds(2));
        using var response = await client.GetAsync(route, TestContext.Current!.Execution.CancellationToken);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString()).IsEqualTo(code);
        await Assert.That(response.Headers.WwwAuthenticate.ToString()).Contains(code);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_established_http_feed_ends_when_its_last_key_authority_expires(bool unavailable, CancellationToken cancellationToken)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(time: time, cancellationToken: cancellationToken);
        var database = await api.CreateManagedDatabaseAsync();
        var (key, token) = Grant(api, database);
        using var client = api.WithToken(token);
        using var writer = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(writer, database, "watch", cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync(ManagementApi.Uri(database.Data + "/changes?cursor=now"),
            HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        async Task<bool> FrameAsync(string name)
        {
            var matched = false;
            while (await reader.ReadLineAsync(deadline.Token) is { } line && line.Length != 0)
            {
                matched |= line == "event: " + name;
            }

            return matched;
        }

        await Assert.That(await FrameAsync("cursor")).IsTrue();
        time.Advance(TimeSpan.FromMilliseconds(250));
        using (var written = await writer.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "watch", "active")), new { active = true }, deadline.Token))
        {
            await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
        }

        await Assert.That(await FrameAsync("change")).IsTrue();
        if (unavailable)
        {
            api.Issuer.StatusAvailable = false;
        }
        else
        {
            api.Issuer.RevokeClient(key);
        }

        time.Advance(TimeSpan.FromSeconds(2));
        try
        {
            while (await reader.ReadLineAsync(deadline.Token) is not null)
            {
            }
        }
        catch (IOException)
        {
        }

        await Assert.That(deadline.IsCancellationRequested).IsFalse();
    }

    [Test]
    public async Task Revocation_ends_an_open_postgres_session_and_refuses_new_connections()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(time: time);
        var database = await api.CreateManagedDatabaseAsync();
        var (key, _) = Grant(api, database);
        var settings = new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = api.Server.PostgresPort, Username = "wire", Password = key, Database = database.Name, SslMode = SslMode.Disable, Pooling = false };
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        await Assert.That(await command.ExecuteScalarAsync()).IsEqualTo(1L);
        api.Issuer.RevokeClient(key);
        // Let the next statement recheck the expired authority before any background watch runs.
        time.AdjustTime(time.GetUtcNow().AddSeconds(2));
        Exception? refused = null;
        try
        {
            await command.ExecuteScalarAsync(TestContext.Current!.Execution.CancellationToken);
        }
        catch (Exception error) when (error is NpgsqlException or InvalidOperationException)
        {
            refused = error;
        }

        await Assert.That(refused).IsNotNull();
        if (refused is PostgresException fatal)
        {
            await Assert.That(fatal.SqlState).IsEqualTo("28000");
            await Assert.That(fatal.MessageText).StartsWith("key_revoked:");
        }
        await Assert.That(connection.State).IsEqualTo(ConnectionState.Closed);
        await Assert.ThrowsAsync<InvalidOperationException>(command.ExecuteScalarAsync);
        await using var fresh = new NpgsqlConnection(settings.ConnectionString);
        var startup = await Assert.ThrowsAsync<PostgresException>(fresh.OpenAsync);
        await Assert.That(startup!.SqlState).IsEqualTo("28000");
        await Assert.That(startup.MessageText).StartsWith("key_revoked:");
    }

    private static (string Key, string Token) Grant(ManagementApi api, ManagedDatabaseRef database, bool mismatched = false, string scope = "documents:read databases:read")
    {
        var id = EntityIds.New().ToString();
        var key = id + "_" + new Faker().Random.String2(52, "0123456789abcdefghjkmnpqrstvwxyz");
        var token = api.Issuer.Mint(id, descriptor =>
        {
            descriptor.Claims["project_id"] = database.Project;
            descriptor.Claims["scope"] = scope;
            descriptor.Claims["key_id"] = mismatched ? EntityIds.New().ToString() : id;
            descriptor.Claims["key_gen"] = TestIssuer.KeyGeneration(key);
        });
        api.Issuer.GrantClient(key, () => token);
        return (key, token);
    }
}
