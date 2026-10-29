using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Store;
using TerraScale.Authentication.Tests;
using TerraScale.DotNet.TerraBase;
using Turso.Data.Sqlite;

namespace TerraScale.Authentication.Slow.Store;

/// <summary>
/// No startup order between ts-auth and TerraBase: ts-auth starts without TerraBase, answers 503 on <c>/readyz</c>, and
/// becomes ready by itself once TerraBase serves and startup provisions the issuer's first signing key; a TerraBase
/// clean shutdown and idle crash keep existing keys. An uncertain copy awaits certified recovery and keeps auth unready.
/// Each test owns its TerraBase
/// process (the one built from this commit), so it can stop it.
/// </summary>
public sealed class StorageRecoveryTests
{
    // A ready replica turns not ready after three consecutive failed passes, about 15 s, plus slack.
    private static readonly TimeSpan Recovery = TimeSpan.FromSeconds(45);

    [Test]
    public async Task Ts_auth_started_before_terrabase_becomes_ready_once_terrabase_serves()
    {
        var (running, unavailable) = await TerraBaseTestServer.StartAsync();
        await using var terrabase = running ?? throw new InvalidOperationException(unavailable);
        var tenant = await terrabase.CreateTenantAsync();
        await terrabase.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth").CreateAsync();
        await terrabase.StopAsync();

        // A configured client is registered by the storage bootstrap too, so it cannot stop startup either.
        var client = AuthStore.NewId();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var auth = await StartAuthAsync(terrabase, tenant, $"{client}:{secret}");
        await Task.Delay(ReadinessWait.NextPass);
        using (var down = await auth.Client.GetAsync("/readyz"))
        {
            await Assert.That(down.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        }

        await terrabase.RestartAsync();
        var database = terrabase.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth");
        await Assert.That(await ReadinessWait.ReadyWithinAsync(auth.Client, Recovery)).IsTrue();
        await Assert.That((await database.ListCollectionsAsync(limit: 200)).Items.Count).IsEqualTo(AuthStore.RequiredCollections.Count);
        await Assert.That((await database.ListDocumentsAsync(AuthStore.SigningKeyCollection)).Items.Count).IsEqualTo(1);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/{OidcIssuer.Slug}/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{client}:{secret}")));
        using var token = await auth.Client.SendAsync(request);
        await Assert.That(token.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()).IsNotNull();
    }

    [Test]
    public async Task A_clean_terrabase_shutdown_while_ts_auth_runs_is_not_ready_then_recovers_with_the_same_key()
    {
        var (running, unavailable) = await TerraBaseTestServer.StartAsync();
        await using var terrabase = running ?? throw new InvalidOperationException(unavailable);
        var tenant = await terrabase.CreateTenantAsync();
        await terrabase.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth").CreateAsync();
        await using var auth = await StartAuthAsync(terrabase, tenant, provision: true);
        await Assert.That(await ReadinessWait.ReadyWithinAsync(auth.Client, Recovery)).IsTrue();
        var before = await KeyIdAsync(auth);
        var front = (terrabase.ClientAddress, terrabase.InternalAddress);

        await terrabase.StopCleanlyAsync();
        await Assert.That(await ReadinessWait.StatusWithinAsync(auth.Client, "not_ready", Recovery)).IsTrue();
        using (var down = await auth.Client.GetAsync("/readyz"))
        {
            await Assert.That(down.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        }

        var probes = new[] { front.ClientAddress.Port, front.InternalAddress.Port }
            .Select(port => new TcpListener(IPAddress.Loopback, port)).ToArray();
        try
        {
            // Front relays retain exclusive ownership; released backend ports may be reused by parallel tests.
            foreach (var listener in probes)
            {
                var error = Assert.Throws<SocketException>(listener.Start)
                    ?? throw new InvalidOperationException("The fixture released its front listener during the outage.");
                await Assert.That(error.SocketErrorCode).IsEqualTo(SocketError.AddressAlreadyInUse);
            }

            await terrabase.RestartAsync();
            await Assert.That((terrabase.ClientAddress, terrabase.InternalAddress)).IsEqualTo(front);
            await Assert.That(await ReadinessWait.ReadyWithinAsync(auth.Client, Recovery)).IsTrue();
            await Assert.That(await KeyIdAsync(auth)).IsEqualTo(before);
            var keys = await terrabase.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth").ListDocumentsAsync(AuthStore.SigningKeyCollection);
            await Assert.That(keys.Items.Count).IsEqualTo(1);
        }
        finally
        {
            foreach (var listener in probes)
            {
                listener.Stop();
            }
        }
    }

    [Test]
    public async Task A_terrabase_crash_keeps_auth_unready_until_the_uncertain_copy_can_be_certified()
    {
        var (running, unavailable) = await TerraBaseTestServer.StartAsync();
        await using var terrabase = running ?? throw new InvalidOperationException(unavailable);
        var tenant = await terrabase.CreateTenantAsync();
        var database = terrabase.Documents(tenant.ApiKey).Database(tenant.Project.Id, "auth");
        var created = await database.CreateAsync();
        await using var auth = await StartAuthAsync(terrabase, tenant, provision: true);
        await Assert.That(await ReadinessWait.ReadyWithinAsync(auth.Client, Recovery)).IsTrue();
        var keys = await database.ListDocumentsAsync(AuthStore.SigningKeyCollection);
        await Assert.That(keys.Items.Count).IsEqualTo(1);

        await terrabase.StopAsync();
        // Model death after a fault of the copy was noted, before its record: its intent is marked Faulting (decision 0013c).
        await LeaveFaultingMarkAsync(terrabase, tenant.Organization.Id, tenant.Project.Id, created.Id);
        var image = await terrabase.SnapshotDatabaseFilesAsync(tenant.Organization.Id, tenant.Project.Id, created.Id);
        await Assert.That(await ReadinessWait.StatusWithinAsync(auth.Client, "not_ready", Recovery)).IsTrue();
        await terrabase.RestartAsync();
        await Assert.That(await ReadinessWait.UntilAsync(
            () => terrabase.RetainedDatabaseMatchesAsync(tenant.Organization.Id, tenant.Project.Id, created.Id, image), Recovery)).IsTrue();

        // Without an intact peer, a restarted uncertain copy cannot authorize auth readiness.
        await Assert.That(await ReadinessWait.ReadyWithinAsync(auth.Client, Recovery)).IsFalse();
        using var ready = await auth.Client.GetAsync("/readyz");
        await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var refusal = await Assert.ThrowsAsync<TerraBaseUnavailableException>(async () =>
            await database.CreateCollectionAsync("after_crash"))
            ?? throw new InvalidOperationException("The refused copy accepted an ordinary write.");
        await Assert.That(refusal.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(refusal.ErrorCode).IsEqualTo("copy_faulted");
        await Assert.That(await terrabase.RetainedDatabaseMatchesAsync(
            tenant.Organization.Id, tenant.Project.Id, created.Id, image)).IsTrue();
    }

    [Test]
    public async Task Ts_auth_stores_in_the_platform_database_terrabase_seeds_with_no_configuration()
    {
        var (running, unavailable) = await TerraBaseTestServer.StartAsync();
        await using var terrabase = running ?? throw new InvalidOperationException(unavailable);
        // ts-auth's constants (App.StorageProject, App.StorageDatabase) name what TerraBase seeds on its own.
        var database = terrabase.InternalDocuments().Database(App.StorageProject, App.StorageDatabase);
        await Assert.That(await ReadinessWait.UntilAsync(async () =>
        {
            try
            {
                await database.ListCollectionsAsync();
                return true;
            }
            catch (TerraBaseException)
            {
                return false;
            }
        }, Recovery)).IsTrue();

        await using var auth = await AuthTestServer.StartAsync(services: services =>
        {
            services.AddSingleton(terrabase.Http);
            services.AddSingleton(_ => terrabase.InternalDocuments().Database(App.StorageProject, App.StorageDatabase));
        }, beforeStart: services => SigningKeyProvisioning.ProvisionAsync(services, CancellationToken.None));

        await Assert.That(await ReadinessWait.ReadyWithinAsync(auth.Client, Recovery)).IsTrue();
        await Assert.That((await database.ListCollectionsAsync(limit: 200)).Items.Count).IsEqualTo(AuthStore.RequiredCollections.Count);
    }

    private static async Task<string> KeyIdAsync(AuthTestServer auth)
    {
        var jwks = await auth.Client.GetFromJsonAsync<JsonElement>($"/{OidcIssuer.Slug}/.well-known/jwks.json");
        return jwks.GetProperty("keys").EnumerateArray().Single().GetProperty("kid").GetString()!;
    }

    private static async Task LeaveFaultingMarkAsync(TerraBaseTestServer server, string organization, string project, string database)
    {
        var root = server.StoppedDataDirectory;
        await using var tenant = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, organization, project, database + ".db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            EncryptionCipher = "aegis256",
            EncryptionKey = server.StoppedDatabaseKey(organization, project, database + ".db"),
        }.ToString());
        await tenant.OpenAsync();
        await using var read = tenant.CreateCommand();
        read.CommandText = "SELECT incarnation FROM commit_offset WHERE id = 1;";
        var incarnation = (string)(await read.ExecuteScalarAsync() ?? throw new InvalidOperationException("The fixture copy has no incarnation."));

        await using var journal = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "_intents.db"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            EncryptionCipher = "aegis256",
            EncryptionKey = server.StoppedDatabaseKey("_intents.db"),
        }.ToString());
        await journal.OpenAsync();
        await using var write = journal.CreateCommand();
        write.CommandText = "PRAGMA synchronous = FULL;";
        _ = await write.ExecuteNonQueryAsync();
        write.CommandText = """
            INSERT INTO _tb_write_intent (organization, project, database, incarnation, replica)
            VALUES ($organization, $project, $database, $incarnation, $replica)
            ON CONFLICT (organization, project, database) DO UPDATE
                SET incarnation = excluded.incarnation, replica = excluded.replica;
            """;
        write.Parameters.AddWithValue("$organization", organization);
        write.Parameters.AddWithValue("$project", project);
        write.Parameters.AddWithValue("$database", database);
        write.Parameters.AddWithValue("$incarnation", incarnation);
        // Replica 0, which no replica id uses, is the Faulting mark: the next start faults the copy instead of proving it.
        write.Parameters.AddWithValue("$replica", 0L);
        _ = await write.ExecuteNonQueryAsync();
    }

    private static Task<AuthTestServer> StartAuthAsync(TerraBaseTestServer terrabase, TerraBaseTestTenant tenant, string? serviceClients = null, bool provision = false) =>
        AuthTestServer.StartAsync(settings: values =>
        {
            if (serviceClients is not null)
            {
                values["service-clients"] = serviceClients;
            }
        }, services: services =>
        {
            services.AddSingleton(terrabase.Http);
            services.AddSingleton(_ => terrabase.InternalDocuments().Database(tenant.Project.Id, "auth"));
        }, beforeStart: provision ? services => SigningKeyProvisioning.ProvisionAsync(services, CancellationToken.None) : null);
}
