using System.Data;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TerraBase.Engine;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Management;

public sealed class KeyRevocationTests
{
    [Test]
    [Arguments("key_revoked")]
    [Arguments("key_expired")]
    public async Task The_same_bearer_stops_reading_when_its_key_stops_acting(string code)
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
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

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            using var response = await client.GetAsync(route, deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
                await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token)).GetProperty("error").GetProperty("code").GetString()).IsEqualTo(code);
                await Assert.That(response.Headers.WwwAuthenticate.ToString()).Contains(code);
                break;
            }

            await Task.Delay(25, deadline.Token);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Unavailable_status_and_a_key_id_that_differs_from_sub_fail_closed(bool mismatched)
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        var (_, token) = Grant(api, database, mismatched);
        api.Issuer.StatusAvailable = false;
        using var client = api.WithToken(token);
        using var response = await client.GetAsync(ManagementApi.Uri(database.Data + "/collections"));
        await Assert.That(response.StatusCode).IsEqualTo(mismatched ? HttpStatusCode.Unauthorized : HttpStatusCode.ServiceUnavailable);
        if (!mismatched)
        {
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("auth_unavailable");
            await Assert.That(response.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
        }

        await Assert.That(api.Issuer.StatusRequests).IsEqualTo(mismatched ? 0 : 1);
    }

    [Test]
    public async Task A_key_answer_too_slow_for_new_work_to_outlast_refuses_it_before_it_starts(CancellationToken cancellationToken)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(time: time, cancellationToken: cancellationToken);
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        var (_, token) = Grant(api, database);
        using var client = api.WithToken(token);
        var route = ManagementApi.Uri(database.Data + "/collections");

        // ts-auth answers 1.5 s after the question: the yes holds 0.5 s more, too little for the request's watch to renew it.
        // The request is refused before it starts, retryable, instead of admitted and then reset mid-flight (#853).
        api.Issuer.BeforeStatus = () => time.Advance(TimeSpan.FromSeconds(1.5));
        using (var slow = await client.GetAsync(route, cancellationToken))
        {
            await Assert.That(slow.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That((await slow.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("auth_unavailable");
            await Assert.That(slow.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
        }

        // A prompt answer admits the retry: the late yes is too old to start on, so the request asks again.
        api.Issuer.BeforeStatus = null;
        using (var retried = await client.GetAsync(route, cancellationToken))
        {
            await Assert.That(retried.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        await Assert.That(api.Issuer.StatusRequests).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_established_http_feed_ends_when_its_last_key_authority_expires(bool unavailable, CancellationToken cancellationToken)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(time: time, cancellationToken: cancellationToken);
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
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

        // Drive the unchanged two-second freshness plus one poll bound without a wall-clock sleep.
        time.Advance(TimeSpan.FromSeconds(2));
        try
        {
            while (await reader.ReadLineAsync(deadline.Token) is not null)
            {
            }
        }
        catch (IOException)
        {
            // An aborted HTTP response can end with a transport error rather than EOF.
        }

        await Assert.That(deadline.IsCancellationRequested).IsFalse();
    }

    [Test]
    public async Task A_write_still_waiting_when_its_key_cannot_be_verified_answers_auth_unavailable_instead_of_a_reset(CancellationToken cancellationToken)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(time: time, cancellationToken: cancellationToken);
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        var (_, token) = Grant(api, database, scope: "documents:read documents:write");
        using var client = api.WithToken(token);
        var sql = ManagementApi.Uri(database.Data + "/sql/execute");
        var create = new { statements = new[] { new { sql = "CREATE TABLE waiting (id INTEGER PRIMARY KEY)" } } };
        var store = (await api.Server.Services.GetRequiredService<IStorageEngine>()
            .OpenDatabaseAsync(new TenantContext(database.Organization, PlanTier.Hobby, database.Project, database.Id), cancellationToken))!;
        var asked = api.Issuer.StatusRequests;
        HttpResponseMessage response;
        await using (var held = await store.BeginTransactionAsync(write: true, cancellationToken))
        {
            // Admitted with a fresh yes, the statement then waits for the writer this test holds: no response has started.
            var waiting = client.PostAsJsonAsync(sql, create, cancellationToken);
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(api.Issuer.StatusRequests > asked), TimeSpan.FromSeconds(10));
            api.Issuer.StatusAvailable = false;
            // Past the last yes plus one poll without a wall-clock sleep: each step gives the watch one tick.
            await TerraBaseTestServer.EventuallyAsync(_ =>
            {
                time.Advance(TimeSpan.FromMilliseconds(250));
                return Task.FromResult(waiting.IsCompleted);
            }, TimeSpan.FromSeconds(10));
            response = await waiting;
        }

        using (response)
        {
            // Answered like a new request, never an empty connection reset (an edge 502 with no body).
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("error").GetProperty("code").GetString())
                .IsEqualTo("auth_unavailable");
            await Assert.That(response.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
        }

        // The cut-off statement never ran: the same table can still be created.
        api.Issuer.StatusAvailable = true;
        using var retried = await client.PostAsJsonAsync(sql, create, cancellationToken);
        await Assert.That(retried.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Revocation_ends_an_open_postgres_session_and_refuses_new_connections()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        var (key, _) = Grant(api, database);
        var settings = new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = api.Server.PostgresPort, Username = "wire", Password = key, Database = database.Name, SslMode = SslMode.Disable, Pooling = false };
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        await Assert.That(await command.ExecuteScalarAsync()).IsEqualTo(1L);
        api.Issuer.RevokeClient(key);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Exception? refused = null;
        while (refused is null)
        {
            try
            {
                await command.ExecuteScalarAsync(deadline.Token);
                await Task.Delay(25, deadline.Token);
            }
            catch (Exception error) when (error is NpgsqlException or InvalidOperationException)
            {
                refused = error;
            }
        }

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

    [Test]
    public async Task An_older_ts_auth_without_key_status_keeps_keys_working_on_http_and_postgres()
    {
        // Deploy order must not matter: a ts-auth that predates key status answers 404 and issues tokens without key_id.
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        api.Issuer.StatusServed = false;
        var (_, token) = Grant(api, database);
        using var client = api.WithToken(token);
        using (var response = await client.GetAsync(ManagementApi.Uri(database.Data + "/collections")))
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }

        foreach (var claims in new[] { true, false })
        {
            var id = EntityIds.New().ToString();
            var key = id + "_" + new Faker().Random.String2(52, "0123456789abcdefghjkmnpqrstvwxyz");
            api.Issuer.GrantClient(key, () => api.Issuer.Mint(id, descriptor =>
            {
                descriptor.Claims["project_id"] = database.Project;
                descriptor.Claims["scope"] = "documents:read databases:read";
                if (claims)
                {
                    descriptor.Claims["key_id"] = id;
                    descriptor.Claims["key_gen"] = TestIssuer.KeyGeneration(key);
                }
            }));
            var settings = new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = api.Server.PostgresPort, Username = "wire", Password = key, Database = database.Name, SslMode = SslMode.Disable, Pooling = false };
            await using var connection = new NpgsqlConnection(settings.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await Assert.That(await command.ExecuteScalarAsync()).IsEqualTo(1L);
        }
    }

    [Test]
    public async Task A_token_issued_before_key_status_keeps_reading_without_a_status_check()
    {
        // Keys and tokens that predate key status keep working after TerraBase starts checking it.
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        var id = EntityIds.New().ToString();
        var token = api.Issuer.Mint(id, descriptor =>
        {
            descriptor.Claims["project_id"] = database.Project;
            descriptor.Claims["scope"] = "documents:read databases:read";
        });
        using var client = api.WithToken(token);
        using var response = await client.GetAsync(ManagementApi.Uri(database.Data + "/collections"));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(api.Issuer.StatusRequests).IsEqualTo(0);
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
