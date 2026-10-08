using System.Globalization;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;
using TerraBase.Fast.Management;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Backups;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>A member sends only databases its catalog registers: backup snapshots stay on the primary (TS-660).</summary>
public sealed class BackupSnapshotReplicationTests
{
    private static readonly Faker Faker = new();
    private static readonly string[] Scopes = [ApiKeyScopes.DocumentsRead, ApiKeyScopes.DocumentsWrite];

    [Test]
    public async Task A_persisted_backup_is_not_replicated_after_restart_but_its_restored_database_is()
    {
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var token = deadline.Token;
        await using var receiver = await TerraBaseTestServer.StartAsync(settings =>
        {
            peers.Apply(settings);
            tls.Apply(settings);
        });
        var directory = Directory.CreateTempSubdirectory("terrabase-backup-restart-").FullName;
        try
        {
            ReplicationTenant tenant;
            DatabaseBackup backup;
            var documents = new[] { (Id: "first", Name: Faker.Name.FullName()), (Id: "second", Name: Faker.Name.FullName()) };
            await using (var seed = await TerraBaseTestServer.StartAsync(settings =>
            {
                tls.Apply(settings);
                settings["data-directory"] = directory;
            }))
            {
                tenant = await ReplicationTenant.CreateAsync(seed, "orders");
                using var writer = tls.CreateClient(seed.ClientAddress!, await tenant.KeyAsync(seed, ApiKeyScopes.DocumentsWrite));
                using var collection = await writer.PostAsJsonAsync(
                    new Uri($"{tenant.Path("orders")}/collections", UriKind.Relative), new { name = "items" }, token);
                await Assert.That(collection.StatusCode).IsEqualTo(HttpStatusCode.Created);
                foreach (var (id, name) in documents)
                {
                    using var written = await writer.PutAsJsonAsync(Document("orders", id), new { name }, token);
                    await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
                }

                backup = await seed.Services.GetRequiredService<IManagedBackupService>().CreateBackupAsync(
                    tenant.UserId, tenant.OrganizationId, tenant.Database("orders"), token) switch
                {
                    DatabaseBackup completed => completed,
                    _ => throw new InvalidOperationException("the backup did not complete"),
                };
                // Bind only the live catalog rows: the receiver has no registration for the snapshot.
                await tenant.BindAsync(receiver);
            }

            var snapshot = Path.Combine(directory, tenant.OrganizationId, tenant.ProjectId, backup.Id + ".db");
            await Assert.That(File.Exists(snapshot)).IsTrue();
            var hook = new UsageCommitHook();
            await using var api = await ManagementApi.StartAsync(settings =>
            {
                peers.Apply(settings);
                settings["data-directory"] = directory;
                settings["peer-addresses"] = receiver.PeerAddress!.ToString();
            }, services: hook.Install);
            var sender = api.Server;
            using var reader = tls.CreateClient(receiver.ClientAddress!, await tenant.KeyAsync(receiver, ApiKeyScopes.DocumentsRead));
            await TerraBaseTestServer.ReplicatedAsync(sender);
            await Assert.That(await sender.MetricAsync("terrabase_replication_sender_initialized", "", token)).IsEqualTo(1);
            await Assert.That(await sender.MetricAsync("terrabase_replication_sender_durable_lag", "", token)).IsEqualTo(0);
            foreach (var (id, name) in documents)
            {
                using var read = await reader.GetAsync(Document("orders", id), token);
                await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
                using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync(token));
                await Assert.That(body.RootElement.GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
            }

            await Assert.That(File.Exists(Path.Combine(receiver.DataDirectory, tenant.OrganizationId, tenant.ProjectId, backup.Id + ".db"))).IsFalse();
            await Assert.That(File.Exists(snapshot)).IsTrue();

            using var management = api.As(tenant.UserId);
            var backups = $"/v1/management/organizations/{tenant.OrganizationId}/projects/{tenant.ProjectId}/databases/{tenant.Database("orders").DatabaseId}/backups";
            // Copy initialization queues a baseline before the backup request returns. A missing snapshot open must
            // not bypass the catalog exclusion and retain that queued head as permanent durable lag (TS-644).
            hook.Hidden = storage => storage.DatabaseId != tenant.Database("orders").DatabaseId;
            using (var queued = await management.PostAsync(new Uri(backups, UriKind.Relative), content: null, token))
            {
                await Assert.That(queued.StatusCode).IsEqualTo(HttpStatusCode.Created);
                await TerraBaseTestServer.ReplicatedAsync(sender);
            }

            hook.Hidden = null;
            var restore = new Uri($"{backups}/{backup.Id}/restore", UriKind.Relative);
            using var restored = await management.PostAsJsonAsync(restore, new { name = "restored" }, token);
            await Assert.That(restored.StatusCode).IsEqualTo(HttpStatusCode.Created);
            await tenant.CaptureDatabaseAsync(sender, "restored");
            await tenant.BindAsync(receiver);
            await TerraBaseTestServer.ReplicatedAsync(sender);
            foreach (var (id, name) in documents)
            {
                using var read = await reader.GetAsync(Document("restored", id), token);
                await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
                using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync(token));
                await Assert.That(body.RootElement.GetProperty("document").GetProperty("name").GetString()).IsEqualTo(name);
            }

            await Assert.That(File.Exists(snapshot)).IsTrue();
            await Assert.That(File.Exists(Path.Combine(receiver.DataDirectory, tenant.OrganizationId, tenant.ProjectId, backup.Id + ".db"))).IsFalse();

            Uri Document(string database, string id)
            {
                return new($"{tenant.Path(database)}/collections/items/documents/{ReplicationDocuments.Id(id)}", UriKind.Relative);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_backup_snapshot_is_not_sent_to_peers_and_leaves_no_durable_lag()
    {
        using var peers = new PeerTls();
        using var tls = new ClientTls();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        var token = deadline.Token;
        var (a, b) = await TerraBaseTestServer.StartPairAsync((settings, own, other) =>
        {
            peers.Apply(settings);
            tls.Apply(settings);
            settings["peer-port"] = own.ToString(CultureInfo.InvariantCulture);
            var (first, second) = own < other ? (own, other) : (other, own);
            settings["peers"] = $"member-a=localhost:{first},member-b=localhost:{second}";
            settings["node-name"] = own == first ? "member-a" : "member-b";
            settings["catalog-primary"] = "member-a";
            settings["write-wait-ms"] = "30000";
        });
        await using (a)
        await using (b)
        {
            var (primary, follower) = a.Services.GetRequiredService<TerraBaseOptions>().CatalogPrimary is null ? (a, b) : (b, a);
            var user = Guid.NewGuid().ToString("D");
            var management = primary.Services.GetRequiredService<IManagementService>();
            var organization = (await management.CreateOrganizationAsync(user, "backups", token)).Value!;
            var project = (await management.CreateProjectAsync(user, organization.Id, "main", token)).Value!;
            var database = (await primary.Services.GetRequiredService<ITenantDirectory>().CreateDatabaseAsync(organization.Id, project.Id, "orders", token))!.Value.Database;
            var key = await primary.IssueAsync(organization.Id, Scopes);
            using var writer = tls.CreateClient(primary.ClientAddress!, key);
            using var reader = tls.CreateClient(follower.ClientAddress!, key);
            var collection = $"/v1/projects/{project.Id}/databases/orders/collections";
            using (var created = await writer.PostAsJsonAsync(new Uri(collection, UriKind.Relative), new { name = "items" }, token))
            {
                await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            }

            var before = new Uri($"{collection}/items/documents/{Guid.NewGuid():D}", UriKind.Relative);
            using (var written = await writer.PutAsJsonAsync(before, new { name = Faker.Name.FullName() }, token))
            {
                await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
            }

            // Once the follower's catalog has the key, it serves the replicated document.
            await TerraBaseTestServer.EventuallyAsync(async probe =>
            {
                using var read = await reader.GetAsync(before, probe);
                return read.StatusCode == HttpStatusCode.OK;
            });

            // The snapshot is an engine database next to its source, registered only as a backup of the organization.
            var backup = await primary.Services.GetRequiredService<IManagedBackupService>().CreateBackupAsync(user, organization.Id, database, token) switch
            {
                DatabaseBackup completed => completed,
                _ => throw new InvalidOperationException("the backup did not complete"),
            };
            await Assert.That(File.Exists(Path.Combine(primary.DataDirectory, organization.Id, project.Id, backup.Id + ".db"))).IsTrue();

            await TerraBaseTestServer.ReplicatedAsync(primary);

            var after = new Uri($"{collection}/items/documents/{Guid.NewGuid():D}", UriKind.Relative);
            using (var written = await writer.PutAsJsonAsync(after, new { name = Faker.Name.FullName() }, token))
            {
                await Assert.That(written.StatusCode).IsEqualTo(HttpStatusCode.Created);
            }

            using var replicated = await reader.GetAsync(after, token);
            await Assert.That(replicated.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await TerraBaseTestServer.ReplicatedAsync(primary);
        }
    }
}
