using System.Globalization;
using System.Net;
using System.Text;
using Bogus;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Sqlite;
using TerraBase.Fast.Documents;
using TerraBase.Fast.Storage;

namespace TerraBase.Fast.Replication;

public sealed class AntiEntropyOrderingTests
{
    [Test]
    [Timeout(60_000)]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Submitted_repair_restores_and_merges_before_queued_commit_and_disposal(
        bool raw, bool disposeWhileQueued, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = deadline.Token;
        await using var api = await DocumentApi.StartAsync(cancellationToken: token);
        var id = DocumentApi.NewId();
        var created = await api.PutAsync(id, new { name = new Faker().Name.FullName() }, cancellationToken: token);
        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.Created);
        var version = long.Parse(created.Json.GetProperty("offset").GetString()!, CultureInfo.InvariantCulture);
        var file = await api.DatabaseFileAsync();
        await using (var connection = new SqliteConnection(DocumentApi.ConnectionString(file)))
        {
            await connection.OpenAsync(token);
            using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE documents SET body = '{\"zzz\":true}' WHERE collection = 'items' AND id = $id;";
            corrupt.Parameters.AddWithValue("$id", id);
            await Assert.That(await corrupt.ExecuteNonQueryAsync(token)).IsEqualTo(1);
        }

        var database = await api.TenantOfAsync(cancellationToken: token);
        var native = api.Server.Services.GetRequiredService<SqliteStorageEngine>();
        var engine = raw ? native : api.Server.Services.GetRequiredService<IStorageEngine>();
        var store = (await engine.OpenDatabaseAsync(database, token))!;
        var nativeStore = (SqliteDatabaseStore)(await native.OpenDatabaseAsync(database, token))!;
        await using var transaction = await store.BeginDurableTransactionAsync(token);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new List<Task>();
        Exception? primaryFailure = null;
        native.Io.Admit();
        try
        {
            // Hold the real writer so repair, commit and disposal queue in a deterministic order.
            pending.Add(nativeStore.WriteLane.RunAsync(() =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(20), token))
                {
                    throw new TimeoutException("the repair-ordering test did not release its writer");
                }

                return 0;
            }, token));
            await entered.Task.WaitAsync(token);
            var repair = transaction.RepairReplicationStateAsync(
                new DocumentChange("items", id, version, Encoding.UTF8.GetBytes("{\"z\":true}")), token).AsTask();
            pending.Add(repair);
            var commit = transaction.CommitAsync(token).AsTask();
            pending.Add(commit);
            if (disposeWhileQueued)
            {
                var disposal = transaction.DisposeAsync().AsTask();
                pending.Add(disposal);
                await Assert.That(disposal.IsCompleted).IsFalse();
            }

            await Assert.That(repair.IsCompleted).IsFalse();
            await Assert.That(commit.IsCompleted).IsFalse();
            release.Set();
            await Task.WhenAll(pending).WaitAsync(token);
            await Assert.That(await repair).IsTrue();
            await Assert.That(await commit).IsGreaterThan(version);
            await transaction.DisposeAsync();

            var visible = await api.GetAsync(id, cancellationToken: token);
            await Assert.That(visible.Status).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(visible.Json.GetProperty("document").GetProperty("z").GetBoolean()).IsTrue();
            await using var connection = new SqliteConnection(DocumentApi.ConnectionString(file, readOnly: true));
            await connection.OpenAsync(token);
            using var state = connection.CreateCommand();
            state.CommandText = """
                SELECT version, body FROM documents WHERE collection = 'items' AND id = $id
                UNION ALL
                SELECT document_version, body FROM change_log WHERE sequence = (
                    SELECT sequence FROM change_log
                    WHERE collection = 'items' AND id = $id
                        AND generation = (SELECT generation FROM collections WHERE name = 'items')
                    ORDER BY version DESC, sequence DESC LIMIT 1);
                """;
            state.Parameters.AddWithValue("$id", id);
            await using var rows = await state.ExecuteReaderAsync(token);
            for (var row = 0; row < 2; row++)
            {
                await Assert.That(await rows.ReadAsync(token)).IsTrue();
                await Assert.That(rows.GetInt64(0)).IsEqualTo(version);
                await Assert.That(rows.GetString(1)).IsEqualTo("{\"z\":true}");
            }

            await Assert.That(await rows.ReadAsync(token)).IsFalse();
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            release.Set();
            deadline.Cancel();
            try
            {
                await StoragePoolTests.JoinCleanupAsync(pending, primaryFailure);
            }
            finally
            {
                native.Io.Release();
            }
        }
    }
}
