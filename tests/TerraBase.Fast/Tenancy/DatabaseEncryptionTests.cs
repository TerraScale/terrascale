using System.Net;
using System.Text;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Primitives;
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

    private static async Task<Seed> SeedAsync(ManagementApi api, string marker)
    {
        var organization = await api.CreateOrganizationAsync(marker);
        var project = await api.CreateProjectAsync(organization, "Main");
        var apiKey = await api.Server.IssueAsync(organization, "documents:read", "documents:write");
        using var data = api.DataClient(apiKey);
        Seed? first = null;
        foreach (var name in (string[])["first", "second"])
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
        // Logical backup .frames spools can contain plaintext; this proof covers encrypted database pages and wrapped keys.
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

    private sealed record Seed(Uri Document, string ApiKey, string Response, string DatabaseFile);
}
