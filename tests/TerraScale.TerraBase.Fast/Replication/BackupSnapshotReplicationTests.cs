using System.Globalization;
using System.Net;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;
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
