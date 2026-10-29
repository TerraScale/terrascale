using System.Diagnostics;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using System.Security.Cryptography;
using System.Text;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

[Category("restore-drill")]
public sealed class RestoreDrillTests
{
    [Test]
    public async Task Encrypted_backup_restores_identical_documents_and_versions_after_the_live_database_is_wiped()
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
        expected["after-snapshot"] = faker.Lorem.Sentence();
        await host.PutAsync("people", "after-snapshot", expected["after-snapshot"], token);
        await host.ShipAsync(TimeSpan.FromSeconds(1), token);
        backupTimer.Stop();
        var at = host.Status().LastShippedAt!.Value;
        var tenant = new TenantContext(host.Database.Organization, PlanTier.Enterprise, host.Database.Project, host.Database.Id);
        var engine = host.Api.Server.Services.GetRequiredService<IStorageEngine>();
        var original = (await engine.OpenDatabaseAsync(tenant, token))!;
        var incarnation = original.Incarnation;
        var digest = await DigestAsync(original, token);
        await Assert.That(host.S3.Objects.Keys.Any(key => key.StartsWith(host.File + "/wal/", StringComparison.Ordinal))).IsTrue();

        var path = Path.Combine(host.Api.Server.DataDirectory, tenant.OrganizationId, tenant.ProjectId!, tenant.DatabaseId + ".db");
        await Assert.That(await engine.DeleteDatabaseAsync(tenant, token)).IsTrue();
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(await engine.OpenDatabaseAsync(tenant, token)).IsNull();

        var restoreTimer = Stopwatch.StartNew();
        await host.RestoreAsync(at, "recovered", token);
        restoreTimer.Stop();
        var restoredId = await host.DatabaseIdAsync("recovered", token);
        var restored = (await engine.OpenDatabaseAsync(tenant with { DatabaseId = restoredId }, token))!;
        await Assert.That(await DigestAsync(restored, token)).IsEquivalentTo(digest);
        await Assert.That(restored.Incarnation).IsNotEqualTo(incarnation);
        foreach (var (name, text) in expected)
        {
            await Assert.That(await host.ReadAsync("recovered", "people", name, token)).IsEqualTo(text);
        }

        Console.WriteLine($"RESTORE_DRILL documents={expected.Count} backup_ms={backupTimer.Elapsed.TotalMilliseconds:F1} restore_ms={restoreTimer.Elapsed.TotalMilliseconds:F1} digest={Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(digest))}");
    }

    private static async Task<byte[]> DigestAsync(IDatabaseStore store, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var read = await store.BeginTransactionAsync(write: false, token);
        string? after = null;
        do
        {
            var page = await read.ListAsync("people", after, 100, 1024 * 1024, token);
            foreach (var document in page.Documents)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(document.Id));
                hash.AppendData([0]);
                hash.AppendData(Encoding.UTF8.GetBytes(document.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                hash.AppendData([0]);
                hash.AppendData(document.Json.Span);
                hash.AppendData([0]);
            }
            if (!page.HasMore)
            {
                return hash.GetHashAndReset();
            }
            after = page.Documents[^1].Id;
        }
        while (true);
    }
}
