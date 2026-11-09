using System.Text;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Engine.Turso;
using TerraBase.Features.Memory;
using TerraBase.Features.Replicas;
using TerraBase.Tenancy;

namespace TerraBase.Fast.Sync;

public sealed class RegionReplicaTests
{
    private readonly Faker _faker = new();

    [Test]
    public async Task A_delayed_home_answer_allows_local_writes_and_newer_pushes_without_regressing_the_revision()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var token = deadline.Token;
        await using var api = await ManagementApi.StartSharedAsync();
        var server = api.Server;
        var directory = Directory.CreateTempSubdirectory("terrabase-delayed-replica-").FullName;
        try
        {
            var database = await server.CreateSyncedDatabaseAsync();
            await server.ExecuteAsync(database, $"INSERT INTO items(id, tenant, v) VALUES ('home', '{database.DatabaseId}', 'initial')");
            var options = server.Services.GetRequiredService<TerraBaseOptions>() with { DataDirectory = directory };
            using var delayed = new DelayedAnswer();
            using var http = PeerTls.CreateClient(server.BaseAddress, delayed);
            await using var replicas = new TursoReplicas(options, new TestMasterKey(), server.Services.GetRequiredService<IMemoryBudget>(), NullLogger<TursoReplicas>.Instance);
            await replicas.OpenAsync(database, server.PullAddress(database), http, token);
            var held = replicas.HeldRevision(database)!.Value;
            await server.ExecuteAsync(database, "UPDATE items SET v = 'delayed' WHERE id = 'home'");
            var beforeRequest = replicas.Position;
            delayed.BlockNext = true;
            var fetching = replicas.PullAsync(database, fromHome: true, token).AsTask();
            try
            {
                await delayed.Captured.Task.WaitAsync(token);
                // Keep the same overlay contract as production: a pull restores the local commit after page apply.
                replicas.Overlay = async (_, _, writer, cancellation) =>
                {
                    _ = await writer.ExecuteAsync(Statement($"INSERT OR REPLACE INTO items(id, tenant, v) VALUES ('local', '{database.DatabaseId}', 'sjc')"), cancellation);
                    return true;
                };
                await using (var writer = (await replicas.BeginWriteAsync(database, token))!)
                {
                    _ = await writer.ExecuteAsync(Statement($"INSERT INTO items(id, tenant, v) VALUES ('local', '{database.DatabaseId}', 'sjc')"), token);
                    await writer.CommitAsync(token);
                }

                await server.ExecuteAsync(database, "UPDATE items SET v = 'newer' WHERE id = 'home'");
                using (var push = await RevisionPush.BuildAsync(server.Services.GetRequiredService<IDatabaseRevisions>(), server.Services.GetRequiredService<IMemoryBudget>(),
                    database, held.ToString(), 0, token))
                {
                    await Assert.That(RevisionPush.TryRead(push!.Payload.ToArray(), out var notice, out var answer)).IsTrue();
                    await Assert.That(DatabaseRevision.TryParse(notice!.Revision, out var to)).IsTrue();
                    await Assert.That(replicas.Offer(database, held, to, answer, notice.CdcPosition)).IsTrue();
                }

                await Assert.That((await replicas.PullAsync(database, fromHome: false, token))!.Changed).IsTrue();
                var newer = replicas.HeldRevision(database);
                var afterPush = replicas.Position;
                await Assert.That(fetching.IsCompleted).IsFalse();
                delayed.Release.TrySetResult();
                await Assert.That((await fetching)!.Changed).IsFalse();
                await Assert.That(replicas.HeldRevision(database)).IsEqualTo(newer);
                await Assert.That(await RowsAsync(replicas, database, token)).IsEquivalentTo(new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["home"] = "newer",
                    ["local"] = "sjc",
                });
                await using var certified = await replicas.BeginReadAsync(database, beforeRequest, token);
                await Assert.That(certified).IsNotNull();
                await Assert.That(await replicas.BeginReadAsync(database, afterPush, token)).IsNull();
            }
            finally
            {
                delayed.Release.TrySetResult();
                _ = await fetching;
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task A_local_replica_writer_drains_readers_and_cancelled_admission_releases_its_gate()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartSharedAsync();
        var server = api.Server;
        var directory = Directory.CreateTempSubdirectory("terrabase-replica-writer-").FullName;
        try
        {
            var database = await server.CreateSyncedDatabaseAsync();
            var options = server.Services.GetRequiredService<TerraBaseOptions>() with { DataDirectory = directory };
            using var http = PeerTls.CreateClient(server.BaseAddress);
            await using var replicas = new TursoReplicas(options, new TestMasterKey(), server.Services.GetRequiredService<IMemoryBudget>(), NullLogger<TursoReplicas>.Instance);
            await replicas.OpenAsync(database, server.PullAddress(database), http, token);
            await using (var reader = (await replicas.BeginReadAsync(database, token))!)
            {
                _ = await reader.ExecuteAsync(Statement("SELECT id FROM items"), token);
                using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
                cancelled.CancelAfter(TimeSpan.FromMilliseconds(50));
                IReplicaWriter? unexpected = null;
                try
                {
                    await Assert.ThrowsAsync<OperationCanceledException>(async () => { unexpected = await replicas.BeginWriteAsync(database, cancelled.Token); });
                }
                finally
                {
                    if (unexpected is not null)
                    {
                        await unexpected.DisposeAsync();
                    }
                }
            }

            // Cancellation must release the write gate; the next real transaction commits and becomes readable.
            await using (var writer = (await replicas.BeginWriteAsync(database, token))!)
            {
                _ = await writer.ExecuteAsync(Statement($"INSERT INTO items(id, tenant, v) VALUES ('local', '{database.DatabaseId}', 'committed')"), token);
                await writer.CommitAsync(token);
            }
            await Assert.That(await RowsAsync(replicas, database, token)).IsEquivalentTo(new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["local"] = "committed",
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class DelayedAnswer() : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        public bool BlockNext { get; set; }

        public TaskCompletionSource Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (!BlockNext)
            {
                return response;
            }

            BlockNext = false;
            try
            {
                await response.Content.LoadIntoBufferAsync(cancellationToken);
                Captured.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }

    [Test]
    public async Task AnEncryptedReplicaFollowsItsHomeThroughDmlDdlACheckpointAndAReopen()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var api = await ManagementApi.StartSharedAsync();
        var server = api.Server;
        var directory = Directory.CreateTempSubdirectory("terrabase-region-replica-").FullName;
        try
        {
            var database = await server.CreateSyncedDatabaseAsync();
            var marker = "plaintext-marker-" + _faker.Random.AlphaNumeric(24);
            var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var initial = new List<string>();
            for (var i = 0; i < 100; i++)
            {
                var id = $"r{i:D4}";
                expected[id] = marker + i;
                initial.Add($"('{id}', '{database.DatabaseId}', '{expected[id]}')");
            }

            await server.ExecuteAsync(database, "INSERT INTO items(id, tenant, v) VALUES " + string.Join(", ", initial));

            var options = server.Services.GetRequiredService<TerraBaseOptions>() with { DataDirectory = directory };
            using var http = PeerTls.CreateClient(server.BaseAddress);
            var replicas = new TursoReplicas(options, new TestMasterKey(), server.Services.GetRequiredService<IMemoryBudget>(), NullLogger<TursoReplicas>.Instance);
            await using (replicas)
            {
                await replicas.OpenAsync(database, server.PullAddress(database), http, token);
                await Assert.That(await RowsAsync(replicas, database, token)).IsEquivalentTo(expected);
                await Assert.That(await LeakingAsync(directory, marker, token)).IsEmpty();

                expected["r0000"] = "updated";
                expected.Remove("r0099");
                await server.ExecuteAsync(database, "UPDATE items SET v = 'updated' WHERE id = 'r0000'");
                await server.ExecuteAsync(database, "DELETE FROM items WHERE id = 'r0099'");
                await server.ExecuteAsync(database, "CREATE TABLE extra(k INTEGER PRIMARY KEY, w TEXT NOT NULL)");
                await server.ExecuteAsync(database, "INSERT INTO extra(k, w) VALUES (7, 'seven')");
                await Assert.That((await replicas.PullAsync(database, fromHome: true, token))!.Changed).IsTrue();
                await Assert.That(await RowsAsync(replicas, database, token)).IsEquivalentTo(expected);
                await Assert.That(await ScalarAsync(replicas, database, "SELECT w FROM extra WHERE k = 7", token)).IsEqualTo("seven");

                var held = replicas.HeldRevision(database)!.Value;
                expected["r0500"] = "pushed";
                await server.ExecuteAsync(database, $"INSERT INTO items(id, tenant, v) VALUES ('r0500', '{database.DatabaseId}', 'pushed')");
                using (var push = await RevisionPush.BuildAsync(server.Services.GetRequiredService<IDatabaseRevisions>(), server.Services.GetRequiredService<IMemoryBudget>(),
                    database, held.ToString(), 0, token))
                {
                    await Assert.That(RevisionPush.TryRead(push!.Payload.ToArray(), out var notice, out var answer)).IsTrue();
                    await Assert.That(notice!.Base).IsEqualTo(held.ToString());
                    await Assert.That(DatabaseRevision.TryParse(notice.Revision, out var to)).IsTrue();
                    await Assert.That(replicas.Offer(database, held, to, answer, notice.CdcPosition)).IsTrue();
                }

                var applied = (await replicas.PullAsync(database, fromHome: false, token))!;
                await Assert.That(applied.Pushed && applied.Changed).IsTrue();
                await Assert.That(await RowsAsync(replicas, database, token)).IsEquivalentTo(expected);

                await server.ExecuteAsync(database, "PRAGMA wal_checkpoint(TRUNCATE)");
                expected["r1000"] = "after-checkpoint";
                await server.ExecuteAsync(database, $"INSERT INTO items(id, tenant, v) VALUES ('r1000', '{database.DatabaseId}', 'after-checkpoint')");
                await Assert.That((await replicas.PullAsync(database, fromHome: true, token))!.Changed).IsTrue();
                await Assert.That(await RowsAsync(replicas, database, token)).IsEquivalentTo(expected);
                await Assert.That((await replicas.PullAsync(database, fromHome: true, token))!.Changed).IsFalse();

                await using var reader = (await replicas.BeginReadAsync(database, token))!;
                var refused = await Assert.That(async () => await reader.ExecuteAsync(Statement("INSERT INTO extra(k, w) VALUES (8, 'eight')"), token))
                    .Throws<SqlStatementException>();
                await Assert.That(refused!.Failure).IsEqualTo(SqlFailure.ReadOnly);
                await Assert.That(await LeakingAsync(directory, marker, token)).IsEmpty();
            }

            expected["r2000"] = "while-closed";
            await server.ExecuteAsync(database, $"INSERT INTO items(id, tenant, v) VALUES ('r2000', '{database.DatabaseId}', 'while-closed')");
            await using var reopened = new TursoReplicas(options, new TestMasterKey(), server.Services.GetRequiredService<IMemoryBudget>(), NullLogger<TursoReplicas>.Instance);
            await reopened.OpenAsync(database, server.PullAddress(database), http, token);
            await Assert.That((await RowsAsync(reopened, database, token)).ContainsKey("r2000")).IsFalse();
            await Assert.That((await reopened.PullAsync(database, fromHome: true, token))!.Changed).IsTrue();
            await Assert.That(await RowsAsync(reopened, database, token)).IsEquivalentTo(expected);
            await Assert.That(await LeakingAsync(directory, marker, token)).IsEmpty();
            await Assert.That(await reopened.DeleteAsync(database, token)).IsTrue();
            await Assert.That(await reopened.BeginReadAsync(database, token)).IsNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<List<string>> LeakingAsync(string directory, string marker, CancellationToken token)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList();
        await Assert.That(files.Any(file => file.EndsWith(".db", StringComparison.Ordinal))).IsTrue();
        var leaking = new List<string>();
        foreach (var file in files)
        {
            if (Encoding.ASCII.GetString(await File.ReadAllBytesAsync(file, token)).Contains(marker, StringComparison.Ordinal))
            {
                leaking.Add($"{Path.GetFileName(file)} ({new FileInfo(file).Length} bytes)");
            }
        }

        return leaking;
    }

    private static SqlStatement Statement(string sql) => new(sql, [], new Dictionary<string, object?>(), 10_000, 8 * 1024 * 1024, TimeSpan.FromSeconds(5));

    private static async Task<SortedDictionary<string, string>> RowsAsync(TursoReplicas replicas, TenantContext database, CancellationToken token)
    {
        await using var reader = (await replicas.BeginReadAsync(database, token))!;
        var result = await reader.ExecuteAsync(Statement("SELECT id, v FROM items ORDER BY id"), token);
        return new SortedDictionary<string, string>(result.Rows.ToDictionary(row => (string)row[0]!, row => (string)row[1]!), StringComparer.Ordinal);
    }

    private static async Task<object?> ScalarAsync(TursoReplicas replicas, TenantContext database, string sql, CancellationToken token)
    {
        await using var reader = (await replicas.BeginReadAsync(database, token))!;
        return (await reader.ExecuteAsync(Statement(sql), token)).Rows.Single()[0];
    }
}
