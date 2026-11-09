using System.Text;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Primitives;

namespace TerraBase.Slow.Tenancy;

public sealed class DatabaseEncryptionTests
{
    [Test]
    public async Task System_and_tenant_files_hide_plaintext_and_restart_reads_the_same_row()
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
            var read = await ManagementApi.RowsAsync(data, seed.Database, "SELECT marker FROM items");
            await Assert.That(read.Single()[0].GetString()).IsEqualTo(marker);
            await Assert.That(restarted.Server.Services.GetRequiredService<IStorageEngine>()
                .EnumerateDatabaseFiles().Any(file => file.Database is null)).IsFalse();
            await AssertNoPlaintextAsync(directory, marker);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<Seed> SeedAsync(ManagementApi api, string marker)
    {
        var organization = await api.CreateOrganizationAsync(marker);
        var project = await api.CreateProjectAsync(organization, "Main");
        var apiKey = await api.Server.IssueAsync(organization, "sql:read", "sql:write");
        using var data = api.DataClient(apiKey);
        Seed? first = null;
        foreach (var name in (string[])["first", "second"])
        {
            var database = await api.CreateManagedDatabaseAsync(organization, project, name);
            (await ManagementApi.SqlAsync(data, database, "CREATE TABLE items (id INTEGER PRIMARY KEY, marker TEXT NOT NULL)")).EnsureSuccessStatusCode();
            (await ManagementApi.SqlAsync(data, database, $"INSERT INTO items (marker) VALUES ('{marker}')")).EnsureSuccessStatusCode();
            first ??= new Seed(database.Data, apiKey, Path.Combine(api.Server.DataDirectory, organization, project, database.Id + ".db"));
        }

        return first!;
    }

    private static async Task AssertNoPlaintextAsync(string directory, string marker)
    {
        var expected = Encoding.UTF8.GetBytes(marker);
        var paths = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".db" or ".key" or ".creating" or ".rebuild"
                || path.EndsWith("-wal", StringComparison.Ordinal)
                || path.EndsWith("-shm", StringComparison.Ordinal)
                || path.EndsWith("-journal", StringComparison.Ordinal)).ToArray();
        await Assert.That(paths.Any(path => Path.GetExtension(path) == ".db")).IsTrue();
        foreach (var path in paths)
        {
            var bytes = await File.ReadAllBytesAsync(path);
            await Assert.That(bytes.AsSpan().IndexOf(expected) < 0).IsTrue().Because(Path.GetFileName(path));
        }
    }

    private sealed record Seed(string Database, string ApiKey, string DatabaseFile);
}
