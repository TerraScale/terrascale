using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

public sealed class HealthyRewriteTests
{
    private const string Probe = "rewrite_probe";

    [Test]
    public async Task A_system_rewrite_removes_deleted_payloads_from_every_file_and_keeps_every_logical_state()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("terrabase-rewrite-").FullName;
        var server = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory);
        HealthyRewrite rewrite;
        var marker = "erased-" + EntityIds.New();
        try
        {
            var engine = server.Services.GetRequiredService<TursoStorageEngine>();
            var store = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
            await PutAsync(store, StorageFixture.Id("gone"), Payload(marker), token);
            await PutAsync(store, StorageFixture.Id("kept"), """{"name":"survivor"}""", token);
            CommitResult deletion;
            await using (var delete = await store.BeginTransactionAsync(write: true, token))
            {
                _ = await delete.DeleteAsync(Probe, StorageFixture.Id("gone"), token);
                deletion = await delete.CommitAsync(token);
            }
            await Assert.That(Holds(SystemFiles(directory), marker)).IsFalse();
            await Assert.That(CapturedMarker(store.FilePath, marker)).IsTrue();
            var wrappedKey = File.ReadAllBytes(store.FilePath + DatabaseKeys.Suffix);

            rewrite = await engine.RewriteAsync(null, deletion.Offset, token);

            await Assert.That(rewrite.Offset).IsGreaterThanOrEqualTo(deletion.Offset);
            await Assert.That(rewrite.TargetIncarnation).IsNotEqualTo(rewrite.SourceIncarnation);
            await Assert.That(ReferenceEquals(await engine.OpenSystemDatabaseAsync(token), store)).IsTrue();
            await Assert.That(store.Incarnation).IsEqualTo(rewrite.TargetIncarnation);
            await Assert.That(File.ReadAllBytes(store.FilePath + DatabaseKeys.Suffix).SequenceEqual(wrappedKey)).IsTrue();
            await Assert.That(Holds(SystemFiles(directory), marker)).IsFalse();
            await Assert.That(CapturedMarker(store.FilePath, marker)).IsFalse();
            await AssertRetainedAsync(store, token);
            await PutAsync(store, StorageFixture.Id("after"), """{"name":"later"}""", token);
        }
        finally
        {
            await server.DisposeAsync();
        }

        await Assert.That(Holds(SystemFiles(directory), marker)).IsFalse();
        var reopened = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory);
        try
        {
            var store = (TursoDatabaseStore)await reopened.Services.GetRequiredService<TursoStorageEngine>().OpenSystemDatabaseAsync(token);
            await Assert.That(store.Incarnation).IsEqualTo(rewrite.TargetIncarnation);
            await AssertRetainedAsync(store, token);
        }
        finally
        {
            await reopened.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }

        static async Task AssertRetainedAsync(TursoDatabaseStore store, CancellationToken token)
        {
            await using var read = await store.BeginTransactionAsync(write: false, token);
            await Assert.That(await read.GetIncarnationAsync(token)).IsEqualTo(store.Incarnation);
            await Assert.That(await read.GetAsync(Probe, StorageFixture.Id("gone"), token)).IsNull();
            await Assert.That(await read.GetAsync(Probe, StorageFixture.Id("kept"), token)).IsNotNull();
        }
    }

    private static async Task PutAsync(TursoDatabaseStore store, string id, string json, CancellationToken token)
    {
        await using var write = await store.BeginTransactionAsync(write: true, token);
        _ = await write.PutAsync(Probe, id, Encoding.UTF8.GetBytes(json), token);
        await write.CommitAsync(token);
    }

    private static string Payload(string marker) => $$"""{"secret":"{{string.Concat(Enumerable.Repeat(marker, 600))}}"}""";

    private static IEnumerable<string> SystemFiles(string directory) =>
        Directory.EnumerateFiles(directory, "_system.db*").Concat(Directory.EnumerateFiles(directory, "._system.*"));

    private static bool Holds(IEnumerable<string> files, string marker)
    {
        var needle = Encoding.UTF8.GetBytes(marker);
        return files.Any(file => File.ReadAllBytes(file).AsSpan().IndexOf(needle) >= 0);
    }

    private static bool CapturedMarker(string path, string marker)
    {
        using var connection = new Turso.TursoConnection(TestMasterKey.ConnectionString(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name = 'turso_cdc';";
        if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            return false;
        }

        command.CommandText = "SELECT after FROM turso_cdc WHERE after IS NOT NULL;";
        using var rows = command.ExecuteReader();
        var needle = Encoding.UTF8.GetBytes(marker);
        while (rows.Read())
        {
            if (((byte[])rows.GetValue(0)).AsSpan().IndexOf(needle) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
