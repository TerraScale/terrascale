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
    public async Task The_same_bearer_stops_reading_when_its_key_stops_acting()
    {
        const string code = "key_revoked";
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(time: time);
        var database = await api.CreateManagedDatabaseAsync();
        var (key, token) = Grant(api, database);
        using var client = api.WithToken(token);
        using (var first = await ManagementApi.SqlAsync(client, database, "SELECT 1"))
        {
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        api.Issuer.RevokeClient(key);

        // Expire the cached authority without dispatching timers; this request must recheck the key itself.
        time.AdjustTime(time.GetUtcNow().AddSeconds(2));
        using var response = await ManagementApi.SqlAsync(client, database, "SELECT 1", TestContext.Current!.Execution.CancellationToken);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString()).IsEqualTo(code);
        await Assert.That(response.Headers.WwwAuthenticate.ToString()).Contains(code);
    }

    // A region's first question about a new key may pay a cold cross-region connection: one slow answer must not refuse it.
    [Test]
    public async Task A_new_key_is_admitted_when_the_first_status_answer_is_slow()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(time: time);
        var database = await api.CreateManagedDatabaseAsync();
        var (_, token) = Grant(api, database);
        var slow = 1;
        api.Issuer.BeforeStatus = () =>
        {
            if (Interlocked.Exchange(ref slow, 0) == 1)
            {
                time.AdjustTime(time.GetUtcNow().AddMilliseconds(1200));
            }
        };
        var before = api.Issuer.StatusRequests;
        using var client = api.WithToken(token);
        using var response = await ManagementApi.SqlAsync(client, database, "SELECT 1", TestContext.Current!.Execution.CancellationToken);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(api.Issuer.StatusRequests - before).IsEqualTo(2);
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
