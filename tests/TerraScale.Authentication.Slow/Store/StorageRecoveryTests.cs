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
/// clean shutdown and idle crash keep existing keys. A copy faulted by an unclean exit keeps auth unready.
/// Each test owns its TerraBase
/// process (the one built from this commit), so it can stop it.
/// </summary>
public sealed class StorageRecoveryTests
{
    // A ready replica turns not ready after three consecutive failed passes, about 15 s, plus slack.
    private static readonly TimeSpan Recovery = TimeSpan.FromSeconds(45);

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
    public async Task A_terrabase_crash_that_faults_the_auth_copy_keeps_auth_unready_and_refuses_writes()
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
        await LeaveFaultingMarkAsync(terrabase, tenant.Organization.Id, tenant.Project.Id, created.Id);
        await Assert.That(await ReadinessWait.StatusWithinAsync(auth.Client, "not_ready", Recovery)).IsTrue();
        await terrabase.RestartAsync();

        await Assert.That(await ReadinessWait.ReadyWithinAsync(auth.Client, Recovery)).IsFalse();
        using var ready = await auth.Client.GetAsync("/readyz");
        await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var refusal = await Assert.ThrowsAsync<TerraBaseUnavailableException>(async () =>
            await database.CreateCollectionAsync("after_crash"))
            ?? throw new InvalidOperationException("The faulted copy accepted an ordinary write.");
        await Assert.That(refusal.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(refusal.ErrorCode).IsEqualTo("copy_faulted");
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
