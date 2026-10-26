using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Fast.Documents;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Fast.Storage;

public sealed class HealthyRewriteConcurrentTests
{
    [Test]
    public async Task A_system_rewrite_preserves_catalog_and_housekeeping_commits_including_an_existing_uncaptured_writer()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync();
        var engine = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var system = (TursoDatabaseStore)await engine.OpenSystemDatabaseAsync(token);
        var database = await api.TenantOfAsync();
        var tenant = (await engine.OpenDatabaseAsync(database, token))!;
        var peer = new ReplicaId(123);
        var stable = new Hlc(456);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        system.RewriteWaiting = () => waiting.TrySetResult();
        // Keep the final cut waiting while real catalog and ledger transactions commit after the pinned snapshot.
        var reader = await system.BeginTransactionAsync(write: false, token);
        Task<HealthyRewrite> rewrite;
        await using (var retention = (TursoStorageTransaction)await system.BeginTransactionAsync(write: true, token))
        {
            await retention.RecordStableAsync(peer, stable, DateTimeOffset.UtcNow, token);
            await retention.TrimAsync(system.BackupAnchor, DateTimeOffset.UtcNow, engine.ReplicaId, new HashSet<long>(), token);
            rewrite = engine.RewriteAsync(null, 0, token).AsTask();
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(system.Rewriting));
            await retention.CommitAsync(token);
        }

        var organizations = new List<string>();
        var delta = Enumerable.Range(0, 512).Select(_ => EntityIds.New().ToString()).ToArray();
        var ledger = (RebuildLedger)api.Server.Services.GetRequiredService<IRebuildLedger>();
        const string reason = "preserve the concurrent rebuild record";
        try
        {
            var ready = await Task.WhenAny(waiting.Task, rewrite).WaitAsync(TimeSpan.FromSeconds(30), token);
            await ready;
            for (var i = 0; i < 5; i++)
            {
                organizations.Add(await api.Management.CreateOrganizationAsync("During rewrite " + EntityIds.New()));
            }
            long before;
            await using (var writes = (TursoStorageTransaction)await system.BeginTransactionAsync(write: true, token))
            {
                before = await writes.GetHeadAsync(token);
                foreach (var id in delta)
                {
                    await writes.PutAsync("rewrite_delta", id, "{}"u8.ToArray(), token);
                }
                await writes.CommitAsync(token);
            }
            await using (var head = (TursoStorageTransaction)await system.BeginTransactionAsync(write: false, token))
            {
                await Assert.That(await head.GetHeadAsync(token) - before).IsGreaterThan(1024);
            }
            await ledger.RecordAsync(database, tenant.Incarnation, reason, token);
        }
        finally
        {
            await reader.DisposeAsync();
        }

        var result = await rewrite;
        await Assert.That(system.Incarnation).IsEqualTo(result.TargetIncarnation);
        await using var read = (TursoStorageTransaction)await system.BeginTransactionAsync(write: false, token);
        var recorded = await read.LedgerAsync(command =>
        {
            using var query = command("SELECT reason FROM _tb_rebuild WHERE database = $database;");
            query.Parameters.AddWithValue("$database", database.DatabaseId!);
            return query.ExecuteScalar() as string;
        }, token);
        await Assert.That(recorded).IsEqualTo(reason);
        foreach (var organization in organizations)
        {
            await Assert.That(await read.GetAsync("organizations", organization, token)).IsNotNull();
        }
        foreach (var id in delta)
        {
            await Assert.That(await read.GetAsync("rewrite_delta", id, token)).IsNotNull();
        }
        var progress = await read.LedgerAsync(command =>
        {
            using var query = command("SELECT stable FROM _tb_peers WHERE node = 123;");
            return (long)query.ExecuteScalar()!;
        }, token);
        await Assert.That(progress).IsEqualTo(456);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_large_rewrite_keeps_committing_and_preserves_every_write_with_a_short_writer_hold(bool system)
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        // The harness freezes query/writer timing by default; this regression measures real elapsed time.
        await using var api = await DocumentApi.StartAsync(services: services =>
            services.AddKeyedSingleton<TimeProvider>(EngineExtensions.QueryDeadlineClock, TimeProvider.System));
        var server = api.Server;
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var database = system ? null : await api.TenantOfAsync();
        var store = (TursoDatabaseStore)(system ? await engine.OpenSystemDatabaseAsync(token) : (await engine.OpenDatabaseAsync(database!, token))!);
        var payload = Encoding.UTF8.GetBytes("{\"body\":\"" + new Faker().Random.AlphaNumeric(100 * 1024) + "\"}");
        var seeded = Enumerable.Range(0, 256).Select(_ => EntityIds.New().ToString()).ToArray();
        foreach (var id in seeded)
        {
            await PutAsync(store, id, payload, token);
        }

        store.RewriteStepMeasured = (step, seconds) => Console.WriteLine($"REWRITE {system} {step}: {seconds * 1000:F3} ms");
        using var holds = new WriterHoldTests.WriterHolds(server);
        var pinned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long sourceBytes = 0;
        store.RewriteSnapshotPinned = _ =>
        {
            sourceBytes = Directory.EnumerateFiles(Path.GetDirectoryName(store.FilePath)!, Path.GetFileName(store.FilePath) + "*").Sum(path => new FileInfo(path).Length);
            pinned.TrySetResult();
        };
        var old = store.Incarnation;
        var started = Stopwatch.GetTimestamp();
        var rewrite = engine.RewriteAsync(database, 0, token).AsTask();
        await pinned.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        if (!system)
        {
            await using var proof = (TursoStorageTransaction)await store.BeginTransactionAsync(write: true, token);
            await proof.AdvanceShapeFrontierAsync(engine.ReplicaId, null, new HashSet<ReplicaId>(), token);
            await proof.CommitAsync(token);
        }
        List<string> committed = [];
        var duringCopy = 0;
        while (!rewrite.IsCompleted)
        {
            var id = EntityIds.New().ToString();
            await PutAsync(store, id, "{\"during\":true}"u8.ToArray(), token);
            committed.Add(id);
            if (store.Incarnation == old)
            {
                duringCopy++;
            }
        }

        var result = await rewrite;
        var measured = holds.Measured("a healthy rewrite");
        Console.WriteLine($"Rewrite ({system}): {Stopwatch.GetElapsedTime(started).TotalSeconds:F3} s; writer {measured.DefaultIfEmpty().Max():F3} s; {duringCopy} concurrent commits; source {sourceBytes} bytes");
        await Assert.That(sourceBytes).IsGreaterThan(45L * 1024 * 1024);
        await Assert.That(duringCopy).IsGreaterThan(0);
        await Assert.That(result.TargetIncarnation).IsNotEqualTo(old);
        await Assert.That(measured.Count).IsEqualTo(1);
        await Assert.That(measured.Max()).IsLessThan(1);
        await using var read = await store.BeginTransactionAsync(write: false, token);
        await Assert.That(await read.GetIncarnationAsync(token)).IsEqualTo(result.TargetIncarnation);
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var actual = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var id in seeded.Concat(committed))
        {
            var bytes = seeded.Contains(id) ? payload : "{\"during\":true}"u8.ToArray();
            var document = await read.GetAsync("items", id, token);
            await Assert.That(document).IsNotNull();
            expected.AppendData(bytes);
            actual.AppendData(document!.Json.Span);
        }

        await Assert.That(actual.GetHashAndReset().SequenceEqual(expected.GetHashAndReset())).IsTrue();
    }

    private static async Task PutAsync(TursoDatabaseStore store, string id, byte[] json, CancellationToken token)
    {
        await using var write = await store.BeginTransactionAsync(write: true, token);
        await write.PutAsync("items", id, json, token);
        await write.CommitAsync(token);
    }
}
