using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Sqlite;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Tenancy;

/// <summary>HTTP writes, restarts and restores preserve encryption of every local database and its WAL.</summary>
public sealed class DatabaseEncryptionTests
{
    [Test]
    public async Task System_and_tenant_files_hide_plaintext_and_restart_reads_the_same_document()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-encryption-").FullName;
        var marker = "encrypted-" + EntityIds.New() + new Faker().Random.AlphaNumeric(32);
        try
        {
            Seed seed;
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                seed = await SeedAsync(api, marker);
                var databases = Directory.GetFiles(directory, "*.db", SearchOption.AllDirectories);
                await Assert.That(databases.Length).IsGreaterThanOrEqualTo(4);
                await Assert.That(databases.Any(path => File.Exists(path + "-wal"))).IsTrue();
                var records = new List<string>();
                foreach (var database in databases)
                {
                    var record = await File.ReadAllTextAsync(database + ".key");
                    await Assert.That(record.StartsWith("TERRABASE-DB-KEY-1\n", StringComparison.Ordinal)).IsTrue();
                    records.Add(record);
                    var bytes = await File.ReadAllBytesAsync(database);
                    await Assert.That(bytes.AsSpan().StartsWith("SQLite format 3\0"u8)).IsFalse();
                }

                await Assert.That(records.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(databases.Length);
                await AssertNoPlaintextAsync(directory, marker);
            }

            await using var restarted = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory);
            using var data = restarted.DataClient(seed.ApiKey);
            using var read = await data.GetAsync(seed.Document);
            await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await read.Content.ReadAsStringAsync()).IsEqualTo(seed.Response);
            await Assert.That(restarted.Server.Services.GetRequiredService<IStorageEngine>()
                .EnumerateDatabaseFiles().Any(file => file.Database is null)).IsFalse();
            await AssertNoPlaintextAsync(directory, marker);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Arguments("missing")]
    [Arguments("tampered")]
    [Arguments("wrong-master")]
    [Arguments("another-database")]
    public async Task An_unusable_system_wrapped_key_refuses_start_without_replacing_it(string problem)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-encryption-key-").FullName;
        try
        {
            Seed seed;
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory))
            {
                seed = await SeedAsync(api, "preserved-" + EntityIds.New());
            }

            var keyPath = Path.Combine(directory, "_system.db.key");
            var original = await File.ReadAllBytesAsync(keyPath);
            switch (problem)
            {
                case "missing":
                    File.Delete(keyPath);
                    break;
                case "tampered":
                    var altered = (byte[])original.Clone();
                    altered["TERRABASE-DB-KEY-1\n".Length + 8] ^= 1;
                    await File.WriteAllBytesAsync(keyPath, altered);
                    break;
                case "wrong-master":
                    var (key, wrapped) = await new TerraBase.Features.Backup.MasterKey(RandomNumberGenerator.GetBytes(32))
                        .GenerateDataKeyAsync(CancellationToken.None);
                    CryptographicOperations.ZeroMemory(key);
                    await File.WriteAllTextAsync(keyPath, "TERRABASE-DB-KEY-1\n" + wrapped + "\n");
                    break;
                default:
                    File.Copy(seed.DatabaseFile + ".key", keyPath, overwrite: true);
                    break;
            }

            var damaged = File.Exists(keyPath) ? await File.ReadAllBytesAsync(keyPath) : null;
            await Assert.That(async () =>
            {
                await using var refused = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory);
            }).ThrowsException();
            await Assert.That(File.Exists(keyPath)).IsEqualTo(damaged is not null);
            if (damaged is not null)
            {
                await Assert.That((await File.ReadAllBytesAsync(keyPath)).SequenceEqual(damaged)).IsTrue();
            }

            // Restoring the original wrapped key must recover the exact data, rather than a new empty catalog.
            await File.WriteAllBytesAsync(keyPath, original);
            await using var restored = await ManagementApi.StartAsync(settings => settings["data-directory"] = directory);
            using var data = restored.DataClient(seed.ApiKey);
            await Assert.That(await data.GetStringAsync(seed.Document)).IsEqualTo(seed.Response);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Encrypted_backup_restore_preserves_the_document_with_a_fresh_encrypted_target_key()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var host = await BackupShippingHost.StartAsync();
        var marker = "restore-" + EntityIds.New() + new Faker().Random.AlphaNumeric(32);
        var id = EntityIds.New().ToString();
        await host.PutAsync("people", id, marker, token);
        await host.ShipAsync(TimeSpan.FromSeconds(10), token);
        var at = host.Status().LastShippedAt!.Value;
        var source = Path.Combine(host.Api.Server.DataDirectory, host.Database.Organization, host.Database.Project, host.Database.Id + ".db");
        var originalKey = await File.ReadAllBytesAsync(source + ".key", token);
        foreach (var payload in host.S3.Objects.Values)
        {
            await Assert.That(payload.AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)) < 0).IsTrue();
        }

        var engine = host.Api.Server.Services.GetRequiredService<IStorageEngine>();
        var tenant = new TenantContext(host.Database.Organization, PlanTier.Enterprise, host.Database.Project, host.Database.Id);
        await Assert.That(await engine.DeleteDatabaseAsync(tenant, token)).IsTrue();
        await Assert.That(File.Exists(source + ".key")).IsFalse();
        await host.RestoreAsync(at, "recovered", token);
        var recovered = await host.DatabaseIdAsync("recovered", token);
        var target = Path.Combine(Path.GetDirectoryName(source)!, recovered + ".db");
        await Assert.That((await File.ReadAllBytesAsync(target + ".key", token)).SequenceEqual(originalKey)).IsFalse();
        await Assert.That(await host.ReadAsync("recovered", "people", id, token)).IsEqualTo(marker);
        await AssertNoPlaintextAsync(host.Api.Server.DataDirectory, marker);
    }

    private static async Task<Seed> SeedAsync(ManagementApi api, string marker)
    {
        var organization = await api.CreateOrganizationAsync(marker);
        var project = await api.CreateProjectAsync(organization, "Main");
        var apiKey = await api.Server.IssueAsync(organization, "documents:read", "documents:write");
        using var data = api.DataClient(apiKey);
        Seed? first = null;
        foreach (var name in new[] { "first", "second" })
        {
            var database = await api.CreateManagedDatabaseAsync(organization, project, name);
            await ManagementApi.CreateCollectionAsync(data, database, "items");
            var document = ManagementApi.Uri(ManagementApi.DocumentPath(database, "items", EntityIds.New().ToString()));
            using var written = await data.PutAsJsonAsync(document, new { marker });
            written.EnsureSuccessStatusCode();
            first ??= new Seed(document, apiKey, await data.GetStringAsync(document),
                Path.Combine(api.Server.DataDirectory, organization, project, database.Id + ".db"));
        }

        return first!;
    }

    private static async Task AssertNoPlaintextAsync(string directory, string marker)
    {
        var expected = Encoding.UTF8.GetBytes(marker);
        foreach (var path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(path) == SqliteStorageEngine.LockFileName)
            {
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(path);
            await Assert.That(bytes.AsSpan().IndexOf(expected) < 0).IsTrue().Because(Path.GetFileName(path));
        }
    }

    private sealed record Seed(Uri Document, string ApiKey, string Response, string DatabaseFile);
}
