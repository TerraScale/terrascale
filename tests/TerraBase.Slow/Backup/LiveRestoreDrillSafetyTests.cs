using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Turso;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Slow.Backup;

public sealed class LiveRestoreDrillSafetyTests
{
    [Test]
    public async Task Replay_preserves_captured_state_and_detects_damage(CancellationToken token)
    {
        await using var api = await DocumentApi.StartAsync(cancellationToken: token);
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var database = await api.TenantOfAsync(cancellationToken: token);
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, token))!;
        var scratch = Directory.CreateTempSubdirectory("terrabase-drill-checksum-").FullName;
        try
        {
            await using var tap = (await engine.OpenTapAsync(database, token))!;
            using var log = new MemoryStream();
            var replayed = Path.Combine(scratch, "replayed.db");
            var reference = Path.Combine(scratch, "reference.db");
            await tap.SnapshotAsync(log, replayed, token);
            log.SetLength(0);
            log.Position = 0;
            var written = await api.PutAsync(DocumentApi.NewId(), new { value = "captured" }, cancellationToken: token);
            await Assert.That((int)written.Status).IsBetween(200, 299);
            var latest = await tap.SnapshotAsync(log, reference, token);
            await Assert.That(latest.Read.Bytes).IsGreaterThan(0);
            log.Position = 0;
            await Assert.That(await engine.RollForwardAsync(replayed, SegmentsAsync(), token)).IsTrue();
            var expected = Convert.ToHexString(await engine.RestoreDrillChecksumAsync(reference, token));
            await Assert.That(Convert.ToHexString(await engine.RestoreDrillChecksumAsync(replayed, token))).IsEqualTo(expected);

            foreach (var mutation in new[]
            {
                "UPDATE _tb_state SET stamp = stamp + 1;",
                "UPDATE _tb_state SET h1 = h1 + 1;",
                "UPDATE commit_offset SET value = value + 1;",
            })
            {
                var damaged = Path.Combine(scratch, "damaged.db");
                File.Copy(replayed, damaged, overwrite: true);
                File.Copy(replayed + DatabaseKeys.Suffix, damaged + DatabaseKeys.Suffix, overwrite: true);
                Execute(damaged, mutation);
                await Assert.That(Convert.ToHexString(await engine.RestoreDrillChecksumAsync(damaged, token))).IsNotEqualTo(expected).Because(mutation);
            }

            async IAsyncEnumerable<Stream> SegmentsAsync()
            {
                yield return log;
                await Task.CompletedTask;
            }
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static void Execute(string file, string sql)
    {
        using var connection = new SqliteConnection(DocumentApi.ConnectionString(file));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
