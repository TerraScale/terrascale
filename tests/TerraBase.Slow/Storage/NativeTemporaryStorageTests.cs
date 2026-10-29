using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Slow.Platform;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Slow.Storage;

public sealed class NativeTemporaryStorageTests
{
    [Test]
    public async Task Native_sorter_spills_use_the_owned_volume_and_initialization_cannot_redirect_it()
    {
        const string ChildFlag = "TERRABASE_TEST_NATIVE_TEMP_CHILD";
        if (Environment.GetEnvironmentVariable(ChildFlag) != "1")
        {
            await IsolatedTestChild.RunAsync(typeof(NativeTemporaryStorageTests),
                nameof(Native_sorter_spills_use_the_owned_volume_and_initialization_cannot_redirect_it), ChildFlag);
            return;
        }

        var directory = Directory.CreateTempSubdirectory("terrabase-native-temp-").FullName;
        try
        {
            await TerraBaseTestServer.InitializeDataDirectoryAsync(directory);
            var temporary = Directory.CreateDirectory(Path.Combine(directory, ".tmp")).FullName;
            var stale = Path.Combine(temporary, "stale");
            await File.WriteAllTextAsync(stale, "interrupted spill");
            // A rejected second host must leave the current owner's spill files alone.
            await using (var owner = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory))
            {
                await Assert.That(async () =>
                {
                    await using var refused = await StartProductionAsync();
                }).Throws<InvalidOperationException>();
                await Assert.That(File.Exists(stale)).IsTrue();
            }

            await using var server = await StartProductionAsync();
            using var health = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
            health.EnsureSuccessStatusCode();
            await Assert.That(File.Exists(stale)).IsFalse();
            await File.WriteAllTextAsync(stale, "active spill");
            server.Services.GetRequiredService<INativeTemporaryStorage>().Initialize();
            await Assert.That(File.Exists(stale)).IsTrue();
            await Assert.That(() => new NativeTemporaryStorage(new TerraBaseOptions { DataDirectory = directory + "-other", StorageThreads = 1, StorageQueue = 16, MemoryBudget = 64L << 20 }).Initialize())
                .Throws<TerraBaseConfigurationException>();

            // Memory databases use Turso's MemoryIO even for spill; use the filesystem backend being verified.
            using var connection = new SqliteConnection($"Data Source={Path.Combine(temporary, "sort.db")}");
            connection.Open();
            using var command = connection.CreateCommand();
            // Pinned Turso clamps cache_size to 200 pages (800 KiB); use 2 MiB to force file spill.
            command.CommandText = """
                PRAGMA temp_store=FILE; PRAGMA cache_size=1; CREATE TABLE spill(k BLOB);
                WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<512)
                INSERT INTO spill SELECT randomblob(4096) FROM n;
                """;
            command.ExecuteNonQuery();
            command.CommandText = "SELECT k FROM spill ORDER BY k;";
            using var reader = command.ExecuteReader();
            await Assert.That(reader.Read()).IsTrue();
            var files = Directory.GetFiles(temporary, "tursodb_temp_file", SearchOption.AllDirectories);
            await Assert.That(files.Length).IsGreaterThan(0);
            await Assert.That(files.Any(path => new FileInfo(path).Length > 0)).IsTrue();
            // /proc includes unlinked-but-open spill files, which directory enumeration cannot observe.
            var descriptors = Directory.EnumerateFiles("/proc/self/fd")
                .Select(path => new FileInfo(path).LinkTarget).Where(path => path?.Contains("tursodb_temp_file", StringComparison.Ordinal) == true).ToArray();
            await Assert.That(descriptors.Length).IsGreaterThan(0);
            await Assert.That(descriptors.All(path => path!.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.Ordinal))).IsTrue();

            Task<TerraBaseTestServer> StartProductionAsync()
            {
                return TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory,
                    services => services.AddSingleton<INativeTemporaryStorage, NativeTemporaryStorage>());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
