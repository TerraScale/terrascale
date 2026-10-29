using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Storage;

public sealed class StorageHistoryTests
{
    [Test]
    [Arguments("unicode-\uffff", "unicode-\U00010000")]
    [Arguments("unicode-\ud7ff", "unicode-\ue000")]
    [Arguments("unicode-\U00010000", "unicode-\U00010001")]
    [Arguments("unicode-\U0010ffff", "unicode.")]
    [Arguments("\U0010ffff", "\uffff")]
    public async Task System_collection_state_prefixes_follow_Unicode_scalar_boundaries(string prefix, string outside)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(cancellationToken: token);
        var system = await api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        var id = Ulid.NewUlid().ToString();
        await using (var writer = await system.BeginTransactionAsync(write: true, token))
        {
            await writer.PutAsync(outside, id, "{}"u8.ToArray(), token);
            await writer.CommitAsync(token);
        }

        await using (var reader = await system.BeginTransactionAsync(write: false, token))
        {
            await Assert.That(await reader.HasCollectionStateAsync(prefix, prefix: true, token)).IsFalse();
        }

        await using (var writer = await system.BeginTransactionAsync(write: true, token))
        {
            await writer.PutAsync(prefix + "/child", id, "{}"u8.ToArray(), token);
            await writer.CommitAsync(token);
        }

        await using (var reader = await system.BeginTransactionAsync(write: false, token))
        {
            await Assert.That(await reader.HasCollectionStateAsync(prefix, prefix: false, token)).IsFalse();
            await Assert.That(await reader.HasCollectionStateAsync(prefix, prefix: true, token)).IsTrue();
        }

        await using (var writer = await system.BeginTransactionAsync(write: true, token))
        {
            await writer.PutAsync(prefix, id, "{}"u8.ToArray(), token);
            await writer.CommitAsync(token);
        }

        await using var after = await system.BeginTransactionAsync(write: false, token);
        await Assert.That(await after.HasCollectionStateAsync(prefix, prefix: false, token)).IsTrue();
        await Assert.That(await after.HasCollectionStateAsync(prefix, prefix: true, token)).IsTrue();
    }

    [Test]
    [Arguments(false, "complete")]
    [Arguments(true, "complete")]
    [Arguments(false, "break")]
    [Arguments(true, "break")]
    [Arguments(false, "cancel")]
    [Arguments(true, "cancel")]
    [Arguments(false, "cancel-boundary")]
    [Arguments(true, "cancel-boundary")]
    public async Task An_active_query_stream_refuses_overlap_and_releases_its_transaction(bool native, string ending)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(settings => settings["storage-threads"] = "1", cancellationToken: token);
        var id = DocumentApi.NewId();
        (await api.PutAsync(id, new { n = 1 }, cancellationToken: token)).Status.ShouldBe(HttpStatusCode.Created);
        (await api.PutAsync(DocumentApi.NewId(), new { n = 2 }, cancellationToken: token)).Status.ShouldBe(HttpStatusCode.Created);
        var engine = native ? api.Server.Services.GetRequiredService<TursoStorageEngine>()
            : api.Server.Services.GetRequiredService<IStorageEngine>();
        var tenant = await api.TenantOfAsync(cancellationToken: token);
        var store = (await engine.OpenDatabaseAsync(tenant, token))!;
        await using var transaction = await store.BeginTransactionAsync(write: ending == "cancel", token);
        var offset = await transaction.GetOffsetAsync(token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using (var changes = transaction.QuerySqlAsync(new SqlQuery(DocumentApi.Collection, null, [], 100), cancellation.Token).GetAsyncEnumerator())
        {
            await Assert.That(await changes.MoveNextAsync()).IsTrue();
            await Assert.That(changes.Current.Id).IsEqualTo(id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.GetOffsetAsync(token).AsTask().WaitAsync(token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.DisposeAsync().AsTask().WaitAsync(token));
            await using (var overlapping = transaction.QuerySqlAsync(new SqlQuery(DocumentApi.Collection, null, [], 100), token).GetAsyncEnumerator())
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => overlapping.MoveNextAsync().AsTask().WaitAsync(token));
            }
            if (ending == "complete")
            {
                await Assert.That(await changes.MoveNextAsync()).IsTrue();
                await Assert.That(await changes.MoveNextAsync()).IsFalse();
                await Assert.That(await transaction.GetOffsetAsync(token)).IsEqualTo(offset);
            }
            else if (ending == "cancel")
            {
                // Cancel inside native execution: a failed scan must still end Turso's transaction.
                var nativeStore = (TursoDatabaseStore)(await api.Server.Services.GetRequiredService<TursoStorageEngine>()
                    .OpenDatabaseAsync(tenant, token))!;
                await nativeStore.WriteLane.RunAsync(() => nativeStore.CurrentWriter()!.StopWhen(() =>
                {
                    cancellation.Cancel();
                    return true;
                }), token);
                await Assert.ThrowsAsync<OperationCanceledException>(() => changes.MoveNextAsync().AsTask().WaitAsync(token));
                await Assert.That(cancellation.IsCancellationRequested).IsTrue();
            }
            else if (ending == "cancel-boundary")
            {
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => changes.MoveNextAsync().AsTask().WaitAsync(token));
            }
        }

        if (ending == "cancel")
        {
            // Turso ended the transaction with the cancelled read: it refuses to read outside its snapshot.
            await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.GetOffsetAsync(token).AsTask());
        }
        else
        {
            await Assert.That(await transaction.GetOffsetAsync(token)).IsEqualTo(offset);
        }

        await transaction.DisposeAsync();
        await using var reused = await store.BeginTransactionAsync(write: false, token);
        await Assert.That(await reused.GetAsync(DocumentApi.Collection, id, token)).IsNotNull();
        (await api.GetAsync(id, cancellationToken: token)).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_write_refused_during_a_query_stream_cannot_commit_earlier_writes(bool native)
    {
        var token = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
        await using var api = await DocumentApi.StartAsync(cancellationToken: token);
        var seed = DocumentApi.NewId();
        (await api.PutAsync(seed, new { n = 1 }, cancellationToken: token)).Status.ShouldBe(HttpStatusCode.Created);
        var engine = native ? api.Server.Services.GetRequiredService<TursoStorageEngine>()
            : api.Server.Services.GetRequiredService<IStorageEngine>();
        var store = (await engine.OpenDatabaseAsync(await api.TenantOfAsync(cancellationToken: token), token))!;
        var idA = DocumentApi.NewId();
        var idB = DocumentApi.NewId();
        long offset;
        await using (var writer = await store.BeginTransactionAsync(write: true, token))
        {
            offset = await writer.GetOffsetAsync(token);
            await writer.PutAsync(DocumentApi.Collection, idA, Encoding.UTF8.GetBytes("{}"), token);
            await using (var changes = writer.QuerySqlAsync(new SqlQuery(DocumentApi.Collection, null, [], 100), token).GetAsyncEnumerator())
            {
                await Assert.That(await changes.MoveNextAsync()).IsTrue();
                await Assert.ThrowsAsync<InvalidOperationException>(() => writer.PutAsync(DocumentApi.Collection, idB, Encoding.UTF8.GetBytes("{}"), token).AsTask().WaitAsync(token));
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CommitAsync(token).AsTask());
        }
        await using var reader = await store.BeginTransactionAsync(write: false, token);
        await Assert.That(await reader.GetOffsetAsync(token)).IsEqualTo(offset);
        await Assert.That(await reader.GetAsync(DocumentApi.Collection, idA, token)).IsNull();
        await Assert.That(await reader.GetAsync(DocumentApi.Collection, idB, token)).IsNull();
        (await api.GetAsync(seed, cancellationToken: token)).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Storage_refuses_an_empty_document_id()
    {
        // List pages resume strictly after an id; the empty id would have none before it, so no writer may store it.
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api);
        await using var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.PutAsync(DocumentApi.Collection, "", Encoding.UTF8.GetBytes("{}"), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CommitAsync(CancellationToken.None).AsTask());
    }

    [Test]
    public async Task A_write_below_the_documents_api_creates_missing_collection_metadata()
    {
        // Engine callers may write to a collection they never created: its metadata is created with the write.
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api);
        var id = DocumentApi.NewId();
        await using (var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await writer.PutAsync("unlisted", id, Encoding.UTF8.GetBytes("{}"), CancellationToken.None);
            await writer.CommitAsync(CancellationToken.None);
        }

        await using var reader = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await reader.GetCollectionAsync("unlisted", CancellationToken.None)).IsNotNull();
        await Assert.That(await reader.GetAsync("unlisted", id, CancellationToken.None)).IsNotNull();
    }

    [Test]
    public async Task The_system_database_has_no_per_commit_write_count_limit()
    {
        // Clients cannot name _system, so its writes (API keys, Management) carry no per-commit client limits;
        // Management cleanup may delete more than 1024 entries in one transaction.
        await using var api = await DocumentApi.StartIsolatedAsync();
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var system = await engine.OpenSystemDatabaseAsync(CancellationToken.None);
        await using (var writer = await system.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            foreach (var id in Enumerable.Range(0, 1100))
            {
                await writer.PutAsync("bulk", id.ToString(CultureInfo.InvariantCulture), Encoding.UTF8.GetBytes("{}"), CancellationToken.None);
            }

            await writer.CommitAsync(CancellationToken.None);
        }

        await using var reader = await system.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await reader.GetAsync("bulk", "1099", CancellationToken.None)).IsNotNull();
    }

    [Test]
    public async Task Assertion_only_transactions_do_not_notify_commit_waiters()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api);
        var sequence = store.CommitSequence;
        using var cancelled = new CancellationTokenSource();
        var waiting = store.WaitForCommitAsync(sequence, cancelled.Token);
        var id = DocumentApi.NewId();
        var asserted = await api.TransactAsync(new { op = "assert", collection = DocumentApi.Collection, id, absent = true });
        await Assert.That(asserted.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(store.CommitSequence).IsEqualTo(sequence);
        await Assert.That(waiting.IsCompleted).IsFalse();

        await api.PutAsync(id, new { value = 1 });
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(store.CommitSequence).IsEqualTo(sequence + 1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Storage_commit_limits_bound_distinct_states_and_count_repeated_writes_with_framing(bool repeated)
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api);
        var ids = Enumerable.Range(0, 7).Select(_ => DocumentApi.NewId()).ToArray();
        var document = Encoding.UTF8.GetBytes("{\"v\":\"" + new string('x', (1 << 20) - 8) + "\"}");
        await Assert.That(document.Length).IsEqualTo(1 << 20);
        var sequence = store.CommitSequence;
        using var cancelled = new CancellationTokenSource();
        var waiting = store.WaitForCommitAsync(sequence, cancelled.Token);

        await using (var rejected = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            for (var i = 0; i < 6; i++)
            {
                await rejected.PutAsync(DocumentApi.Collection, ids[repeated ? 0 : i], document, CancellationToken.None);
            }

            // Seven raw 1 MiB writes, of one key or of seven, exceed the per-commit byte limit with framing.
            await Assert.ThrowsAsync<StorageWriteLimitException>(() => rejected.PutAsync(DocumentApi.Collection, ids[repeated ? 0 : 6], document, CancellationToken.None).AsTask());

            await Assert.ThrowsAsync<InvalidOperationException>(() => rejected.CommitAsync(CancellationToken.None).AsTask());
        }

        await Assert.That((await api.GetAsync(ids[0])).Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await api.GetAsync(ids[1])).Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(store.CommitSequence).IsEqualTo(sequence);
        await Assert.That(waiting.IsCompleted).IsFalse();

        // Six smaller distinct documents fit; six full-size writes of one key retain only its final state.
        var acceptedDocument = repeated ? document : Encoding.UTF8.GetBytes("{\"v\":\"" + new string('x', 300 * 1024) + "\"}");
        await using (var accepted = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            for (var i = 0; i < 6; i++)
            {
                await accepted.PutAsync(DocumentApi.Collection, ids[repeated ? 0 : i], acceptedDocument, CancellationToken.None);
            }

            await accepted.CommitAsync(CancellationToken.None);
        }

        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(store.CommitSequence).IsEqualTo(sequence + 1);
        foreach (var id in repeated ? ids[..1] : ids[..6])
        {
            await Assert.That((await api.GetAsync(id)).Status).IsEqualTo(HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task Storage_write_count_and_document_size_failures_cannot_commit()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api);
        var id = DocumentApi.NewId();
        var small = Encoding.UTF8.GetBytes("{}");
        await using (var repeated = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            for (var i = 0; i < 512; i++)
            {
                await repeated.PutAsync(DocumentApi.Collection, id, small, CancellationToken.None);
                await repeated.TombstoneAsync(DocumentApi.Collection, id, CancellationToken.None);
            }

            await Assert.ThrowsAsync<StorageWriteLimitException>(() => repeated.PutAsync(DocumentApi.Collection, id, small, CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => repeated.CommitAsync(CancellationToken.None).AsTask());
        }

        await using (var oversized = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await oversized.PutAsync(DocumentApi.Collection, id, small, CancellationToken.None);
            var large = Encoding.UTF8.GetBytes("{\"v\":\"" + new string('x', 1 << 20) + "\"}");
            var error = await Assert.ThrowsAsync<DocumentValidationException>(() => oversized.PutAsync(DocumentApi.Collection, id, large, CancellationToken.None).AsTask());
            await Assert.That(error!.TooLarge).IsTrue();
            await Assert.ThrowsAsync<InvalidOperationException>(() => oversized.CommitAsync(CancellationToken.None).AsTask());
        }

        await Assert.That((await api.GetAsync(id)).Status).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Invalid_utf8_is_rejected_before_lossy_expansion_and_cannot_commit()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var store = await StoreAsync(api);
        var id = DocumentApi.NewId();
        var invalid = new byte[1 << 20];
        invalid.AsSpan().Fill(0xff);
        Encoding.UTF8.GetBytes("{\"v\":\"").CopyTo(invalid, 0);
        invalid[^2] = (byte)'"';
        invalid[^1] = (byte)'}';
        await using (var writer = await store.BeginTransactionAsync(write: true, CancellationToken.None))
        {
            await writer.PutAsync(DocumentApi.Collection, id, Encoding.UTF8.GetBytes("{}"), CancellationToken.None);
            // A lossy decoder triples these bytes, bypassing the admitted body size on repeated writes.
            await Assert.ThrowsAsync<DocumentValidationException>(() => writer.PutAsync(DocumentApi.Collection, id, invalid, CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.PutAsync(DocumentApi.Collection, id, invalid, CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CommitAsync(CancellationToken.None).AsTask());
        }

        await Assert.That((await api.GetAsync(id)).Status).IsEqualTo(HttpStatusCode.NotFound);
    }

    private static async Task<IDatabaseStore> StoreAsync(DocumentApi api, CancellationToken cancellationToken = default)
    {
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        return (await engine.OpenDatabaseAsync(await api.TenantOfAsync(cancellationToken: cancellationToken), cancellationToken))!;
    }
}
