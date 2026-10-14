using System.Diagnostics;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

[Category("restore-drill")]
public sealed class RestoreDrillTests
{
    [Test]
    public async Task Encrypted_backup_restores_identical_digests_after_the_live_database_is_wiped()
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var host = await BackupShippingHost.StartAsync();
        var faker = new Faker();
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < 24; index++)
        {
            var name = "document-" + index;
            expected.Add(name, faker.Lorem.Sentence());
            await host.PutAsync("people", name, expected[name], token);
        }

        var backupTimer = Stopwatch.StartNew();
        await host.ShipAsync(TimeSpan.FromSeconds(10), token);
        // Exercise replay as well as the initial full snapshot.
        expected["after-snapshot"] = faker.Lorem.Sentence();
        await host.PutAsync("people", "after-snapshot", expected["after-snapshot"], token);
        await host.ShipAsync(TimeSpan.FromSeconds(1), token);
        backupTimer.Stop();
        var at = host.Status().LastShippedAt!.Value;
        var tenant = new TenantContext(host.Database.Organization, PlanTier.Enterprise, host.Database.Project, host.Database.Id);
        var states = host.Api.Server.Services.GetRequiredService<IReplicaStates>();
        var original = await states.DigestAsync(tenant, null, token);
        await Assert.That(original).IsNotNull();
        var digest = original!.Buckets;
        await Assert.That(host.S3.Objects.Keys.Any(key => key.StartsWith(host.File + "/wal/", StringComparison.Ordinal))).IsTrue();

        var engine = host.Api.Server.Services.GetRequiredService<IStorageEngine>();
        var path = Path.Combine(host.Api.Server.DataDirectory, tenant.OrganizationId, tenant.ProjectId!, tenant.DatabaseId + ".db");
        await Assert.That(await engine.DeleteDatabaseAsync(tenant, token)).IsTrue();
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(await states.DigestAsync(tenant, null, token)).IsNull();

        var restoreTimer = Stopwatch.StartNew();
        await host.RestoreAsync(at, "recovered", token);
        restoreTimer.Stop();
        var restoredId = await host.DatabaseIdAsync("recovered", token);
        var restoredDigest = await states.DigestAsync(tenant with { DatabaseId = restoredId }, null, token);
        await Assert.That(restoredDigest!.Buckets).IsEquivalentTo(digest);
        await Assert.That(restoredDigest.Incarnation).IsNotEqualTo(original.Incarnation).Because("a restore is a new copy, whose change ids restart");
        foreach (var (name, text) in expected)
        {
            await Assert.That(await host.ReadAsync("recovered", "people", name, token)).IsEqualTo(text);
        }

        Console.WriteLine($"RESTORE_DRILL documents={expected.Count} backup_ms={backupTimer.Elapsed.TotalMilliseconds:F1} restore_ms={restoreTimer.Elapsed.TotalMilliseconds:F1} digest={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(digest))}");
    }

}
