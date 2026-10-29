using System.Buffers.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Fast.Documents;
using TerraBase.Features.Documents;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Shapes;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Shapes;

public sealed class ShapeSealTests
{
    private const string Collection = DocumentApi.Collection;
    private const int MiB = 1024 * 1024;

    [Test]
    public async Task Chunks_hold_whole_commits_and_close_at_4096_rows()
    {
        await using var api = await DocumentApi.StartAsync();
        var db = await Database.OpenAsync(api.Server, await api.TenantOfAsync());
        var faker = new Faker();
        var start = await db.HeadAsync();
        List<long> commits = [];
        for (var commit = 0; commit < 5; commit++)
        {
            commits.Add(await db.CommitAsync(async transaction =>
            {
                for (var n = 0; n < 1024; n++)
                {
                    _ = await transaction.PutAsync(Collection, DocumentApi.NewId(), Json(new { n, name = faker.Name.FullName() }), CancellationToken.None);
                }
            }));
        }

        var shape = await ShapeAsync(db);
        await Assert.That(await db.SealAsync(shape, start)).IsEqualTo(commits[^1]);
        var chunks = await db.ReadAllAsync(shape.Handle, start, commits[^1]);
        await Assert.That(chunks.Select(chunk => chunk.EndOffset).ToArray()).IsEquivalentTo([commits[3], commits[4]], TUnit.Assertions.Enums.CollectionOrdering.Matching)
            .Because("four commits of 1024 rows fill 4096 exactly; the fifth would exceed it");
        foreach (var chunk in chunks)
        {
            var changes = chunk.Ops.GroupBy(op => op.Commit).ToArray();
            await Assert.That(changes.All(change => change.Count() == 1024)).IsTrue().Because("a commit is never split across chunks");
            await Assert.That(changes.Select(change => change.Key).All(commits.Contains)).IsTrue().Because("a change's offset is its commit_seq");
        }

        await Assert.That(chunks[0].Ops.Length).IsEqualTo(4096);
        await Assert.That(async () => await db.SealAsync(await ShapeAsync(db), commits[0] - 1)).Throws<ShapeSealOffsetException>()
            .Because("an offset inside a commit is no commit boundary");
    }

    [Test]
    public async Task Chunks_close_before_1_MiB_of_body_and_a_larger_commit_travels_alone()
    {
        await using var api = await DocumentApi.StartAsync();
        var db = await Database.OpenAsync(api.Server, await api.TenantOfAsync());
        var faker = new Faker();
        var start = await db.HeadAsync();
        List<long> commits = [];
        for (var n = 0; n < 4; n++)
        {
            commits.Add(await db.PutAsync(DocumentApi.NewId(), new { pad = faker.Random.String2(300_000) }));
        }

        commits.Add(await db.CommitAsync(async transaction =>
        {
            for (var n = 0; n < 3; n++)
            {
                _ = await transaction.PutAsync(Collection, DocumentApi.NewId(), Json(new { pad = faker.Random.String2(600_000) }), CancellationToken.None);
            }
        }));
        commits.Add(await db.PutAsync(DocumentApi.NewId(), new { n = 1 }));

        var shape = await ShapeAsync(db);
        await Assert.That(await db.SealAsync(shape, start)).IsEqualTo(commits[^1]);
        var chunks = await db.ReadAllAsync(shape.Handle, start, commits[^1]);
        await Assert.That(chunks.Select(chunk => chunk.EndOffset).ToArray())
            .IsEquivalentTo([commits[2], commits[3], commits[4], commits[5]], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var single = chunks[2];
        await Assert.That(single.Body.Length).IsGreaterThan(MiB).And.IsLessThanOrEqualTo(8 * MiB);
        await Assert.That(single.Ops.Length).IsEqualTo(3).Because("the singleton chunk holds its whole commit");
        await Assert.That(chunks.Where(chunk => chunk != single).All(chunk => chunk.Body.Length <= MiB)).IsTrue();
    }

    [Test]
    public async Task A_commit_materializing_past_8_MiB_is_refused_until_it_fits()
    {
        await using var api = await DocumentApi.StartAsync();
        var db = await Database.OpenAsync(api.Server, await api.TenantOfAsync());
        var faker = new Faker();
        var start = await db.HeadAsync();
        var ids = Enumerable.Range(0, 10).Select(_ => DocumentApi.NewId()).ToArray();
        var a = await db.CommitAsync(async transaction =>
        {
            foreach (var id in ids)
            {
                _ = await transaction.PutAsync(Collection, id, Json(new { n = 1 }), CancellationToken.None);
            }
        });
        foreach (var id in ids)
        {
            _ = await db.PutAsync(id, new { pad = faker.Random.String2(900_000) });
        }

        var shape = await ShapeAsync(db);
        await Assert.That(async () => await db.SealAsync(shape, start)).Throws<ShapeTooLargeException>();
        await Assert.That(await db.ReadAsync(shape.Handle, start)).IsNull().Because("a refused commit seals nothing");

        _ = await db.CommitAsync(async transaction =>
        {
            foreach (var id in ids[5..])
            {
                _ = await transaction.DeleteAsync(Collection, id, CancellationToken.None);
            }
        });
        _ = await db.SealAsync(shape, start);
        var first = (await db.ReadAllAsync(shape.Handle, start, a)).Single();
        await Assert.That(first.EndOffset).IsEqualTo(a);
        await Assert.That(first.Body.Length).IsGreaterThan(MiB);
        await Assert.That(first.Ops.Count(op => op is { Kind: "upsert", Document.Length: > 900_000 })).IsEqualTo(5);
        await Assert.That(first.Ops.Count(op => op.Kind == "delete")).IsEqualTo(5);
    }

    [Test]
    public async Task Ops_carry_current_stored_documents_and_deletes_for_absent_or_nonmatching_ones()
    {
        await using var api = await DocumentApi.StartAsync();
        var db = await Database.OpenAsync(api.Server, await api.TenantOfAsync());
        var faker = new Faker();
        var start = await db.HeadAsync();
        var (a, b, f, never) = (DocumentApi.NewId(), DocumentApi.NewId(), DocumentApi.NewId(), DocumentApi.NewId());
        var excluded = DocumentApi.NewId();
        _ = await db.CommitAsync(async transaction =>
        {
            _ = await transaction.PutAsync(Collection, a, Json(new { keep = true, v = 1 }), CancellationToken.None);
            _ = await transaction.PutAsync(Collection, b, Json(new { keep = true }), CancellationToken.None);
            _ = await transaction.PutAsync(Collection, f, Json(new { keep = true }), CancellationToken.None);
            _ = await transaction.PutAsync(Collection, never, Json(new { keep = false }), CancellationToken.None);
            _ = await transaction.PutAsync(Collection, excluded, Json(new { keep = true }), CancellationToken.None);
        });
        (await api.PutAsync(a, new { keep = true, v = 2, text = faker.Lorem.Sentence() })).Status.ShouldBe(System.Net.HttpStatusCode.OK);
        var head = await db.CommitAsync(async transaction =>
        {
            _ = await transaction.DeleteAsync(Collection, b, CancellationToken.None);
            _ = await transaction.PutAsync(Collection, f, Json(new { keep = false }), CancellationToken.None);
        });

        var shape = await ShapeAsync(db, document => document.Id != excluded && document.Json.Span.IndexOf("\"keep\":true"u8) >= 0);
        _ = await db.SealAsync(shape, start);
        var chunks = await db.ReadAllAsync(shape.Handle, start, head);
        var ops = chunks.SelectMany(chunk => chunk.Ops).ToArray();
        await Assert.That(chunks.All(chunk => chunk.ValuesAt >= head && chunk.ValuesAt >= chunk.EndOffset)).IsTrue();

        var stored = await db.StoredAsync(a);
        var version = (await api.GetAsync(a)).Version;
        await Assert.That(ops.Count(op => op.Id == a)).IsEqualTo(2);
        await Assert.That(ops.Where(op => op.Id == a).All(op => op.Kind == "upsert" && op.Document == stored && op.Version == version)).IsTrue()
            .Because("a's first event carries the newer value, as stored bytes with the version a read returns");
        foreach (var id in new[] { b, f, never, excluded })
        {
            await Assert.That(ops.Where(op => op.Id == id).All(op => op is { Kind: "delete", Version: null, Document: null })).IsTrue();
        }

        await Assert.That(ops.Count(op => op.Id == b)).IsEqualTo(2);
        await Assert.That(ops.Count(op => op.Id == f)).IsEqualTo(2);
        await Assert.That(ops.Count(op => op.Id == excluded)).IsEqualTo(1)
            .Because("the predicate can reject a document by id even when its JSON matches");
        foreach (var change in ops.GroupBy(op => op.Commit))
        {
            await Assert.That(change.Select(op => op.Id).SequenceEqual(change.Select(op => op.Id).Order(StringComparer.Ordinal))).IsTrue()
                .Because("a change lists its documents once, in ordinal id order");
        }
    }

    [Test]
    public async Task A_deleted_then_recreated_document_is_an_upsert_in_both_changes_at_values_at()
    {
        await using var api = await DocumentApi.StartAsync();
        var db = await Database.OpenAsync(api.Server, await api.TenantOfAsync());
        var id = DocumentApi.NewId();
        var start = await db.PutAsync(id, new { v = 1 });
        var deleted = await db.CommitAsync(async transaction =>
            _ = await transaction.DeleteAsync(Collection, id, CancellationToken.None));
        var recreated = await db.PutAsync(id, new { v = 2 });
        var shape = await ShapeAsync(db);

        await Assert.That(await db.SealAsync(shape, start)).IsEqualTo(recreated);
        var chunks = await db.ReadAllAsync(shape.Handle, start, recreated);
        var ops = chunks.SelectMany(chunk => chunk.Ops).ToArray();
        var stored = await db.StoredAsync(id);
        var version = (await api.GetAsync(id)).Version;
        await Assert.That(ops.Select(op => op.Commit).ToArray()).IsEquivalentTo([deleted, recreated],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(ops.All(op => op.Id == id && op.Kind == "upsert" && op.Document == stored && op.Version == version)).IsTrue()
            .Because("both events carry the document that exists at values_at, including the earlier delete event");
        await Assert.That(chunks.All(chunk => chunk.ValuesAt == recreated)).IsTrue();
    }

    [Test]
    public async Task A_stale_collection_generation_is_refused_without_sealing_a_chunk()
    {
        await using var api = await DocumentApi.StartAsync();
        var db = await Database.OpenAsync(api.Server, await api.TenantOfAsync());
        var stale = await ShapeAsync(db);
        var start = await db.HeadAsync();
        _ = await db.CommitAsync(async transaction =>
        {
            await Assert.That(await transaction.DropCollectionAsync(Collection, CancellationToken.None)).IsTrue();
        });
        _ = await db.CommitAsync(async transaction =>
        {
            await Assert.That(await transaction.CreateCollectionAsync(new CollectionInfo(Collection, 1), CancellationToken.None)).IsTrue();
        });
        var current = await ShapeAsync(db);
        await Assert.That(current.CollectionGeneration).IsNotEqualTo(stale.CollectionGeneration);
        var head = await db.PutAsync(DocumentApi.NewId(), new { v = 2 });

        await Assert.That(async () => await db.SealAsync(stale, start)).Throws<ShapeSealOffsetException>();
        await Assert.That(await db.ReadAsync(stale.Handle, start)).IsNull();
        await Assert.That(await db.SealAsync(current, start)).IsEqualTo(head)
            .Because("only the old generation's handle is refused");
    }

    [Test]
    public async Task Retention_cutting_inside_a_commit_refuses_the_first_retained_rows_predecessor()
    {
        await using var api = await DocumentApi.StartAsync();
        var db = await Database.OpenAsync(api.Server, await api.TenantOfAsync());
        var start = await db.HeadAsync();
        var head = await db.CommitAsync(async transaction =>
        {
            _ = await transaction.PutAsync(Collection, DocumentApi.NewId(), Json(new { n = 1 }), CancellationToken.None);
            _ = await transaction.PutAsync(Collection, DocumentApi.NewId(), Json(new { n = 2 }), CancellationToken.None);
        });
        await Assert.That(head).IsEqualTo(start + 2);
        await db.RawWriteAsync($"DELETE FROM _tb_feed WHERE seq <= {start + 1};");
        var min = await db.ScalarAsync("SELECT MIN(seq) FROM _tb_feed;");
        await Assert.That(min).IsEqualTo(head);
        var shape = await ShapeAsync(db);

        await Assert.That(async () => await db.SealAsync(shape, min - 1)).Throws<ShapeSealOffsetException>();
        await Assert.That(await db.ReadAsync(shape.Handle, min - 1)).IsNull()
            .Because("the retained suffix of a commit cannot become an immutable partial chunk");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_missing_commit_tail_cannot_publish_an_oversized_partial_chunk(bool laterCommit)
    {
        await using var api = await DocumentApi.StartAsync();
        var db = await Database.OpenAsync(api.Server, await api.TenantOfAsync());
        _ = await db.CapturedHeadAsync();
        var start = await db.HeadAsync();
        var faker = new Faker();
        var tail = await db.CommitAsync(async transaction =>
        {
            for (var n = 0; n < 2; n++)
            {
                _ = await transaction.PutAsync(Collection, DocumentApi.NewId(), Json(new { pad = faker.Random.String2(600_000) }), CancellationToken.None);
            }
            _ = await transaction.PutAsync(Collection, DocumentApi.NewId(), Json(new { n = 3 }), CancellationToken.None);
        });
        await Assert.That(tail).IsEqualTo(start + 3);
        if (laterCommit)
        {
            _ = await db.PutAsync(DocumentApi.NewId(), new { n = 4 });
        }
        await db.RawWriteAsync($"DELETE FROM _tb_feed WHERE seq = {tail};");
        var shape = await ShapeAsync(db);

        await Assert.That(async () => await db.SealAsync(shape, start)).Throws<ShapeSealOffsetException>();
        await Assert.That(await db.ReadAsync(shape.Handle, start)).IsNull();
        await Assert.That(await db.ScalarAsync("SELECT COUNT(*) FROM _tb_shape_chunk;")).IsEqualTo(0L)
            .Because("the two retained 600 kB documents must not close a singleton before the missing tail is detected");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Zero_feed_stamps_in_the_range_or_older_resume_prefix_refuse_sealing(bool olderPrefix)
    {
        await using var api = await DocumentApi.StartAsync();
        var db = await Database.OpenAsync(api.Server, await api.TenantOfAsync());
        var start = await db.HeadAsync();
        var damaged = await db.PutAsync(DocumentApi.NewId(), new { n = 1 });
        if (olderPrefix)
        {
            start = damaged;
            _ = await db.PutAsync(DocumentApi.NewId(), new { n = 2 });
        }
        await db.RawWriteAsync($"UPDATE _tb_feed SET stamp = 0 WHERE seq = {damaged};");
        var shape = await ShapeAsync(db);

        await Assert.That(async () => await db.SealAsync(shape, start)).Throws<InvalidDataException>();
        await Assert.That(await db.ReadAsync(shape.Handle, start)).IsNull();
        await Assert.That(await db.ScalarAsync("SELECT COUNT(*) FROM _tb_shape_chunk;")).IsEqualTo(0L);
    }

    [Test]
    public async Task A_feed_row_whose_stamp_differs_from_the_document_stamp_still_carries_the_current_document()
    {
        await using var api = await DocumentApi.StartAsync();
        var db = await Database.OpenAsync(api.Server, await api.TenantOfAsync());
        var start = await db.HeadAsync();
        var id = DocumentApi.NewId();
        var head = await db.PutAsync(id, new { n = 1 });
        await db.RawWriteAsync($"UPDATE _tb_feed SET stamp = stamp + 65536 WHERE seq = {head};");

        var shape = await ShapeAsync(db);
        _ = await db.SealAsync(shape, start);
        var op = (await db.ReadAllAsync(shape.Handle, start, head)).Single().Ops.Single();
        await Assert.That(op.Kind).IsEqualTo("upsert");
        await Assert.That(op.Document).IsEqualTo(await db.StoredAsync(id));
        await Assert.That(op.Version).IsEqualTo((await api.GetAsync(id)).Version);
    }

    [Test]
    public async Task A_sealed_chunk_keeps_its_bytes_after_later_writes_and_is_never_captured()
    {
        await using var api = await DocumentApi.StartAsync();
        var db = await Database.OpenAsync(api.Server, await api.TenantOfAsync());
        var start = await db.HeadAsync();
        var x = DocumentApi.NewId();
        _ = await db.PutAsync(x, new { v = 1 });
        var first = await db.PutAsync(DocumentApi.NewId(), new { v = 1 });
        var shape = await ShapeAsync(db);
        await Assert.That(await db.SealAsync(shape, start)).IsEqualTo(first);
        var before = await db.ReadAllAsync(shape.Handle, start, first);

        (await api.PutAsync(x, new { v = 2 })).Status.ShouldBe(System.Net.HttpStatusCode.OK);
        var second = await db.PutAsync(DocumentApi.NewId(), new { v = 2 });
        await Assert.That(await db.SealAsync(shape, start)).IsEqualTo(second).Because("resealing from a sealed offset continues past it");
        var after = await db.ReadAllAsync(shape.Handle, start, second);

        await Assert.That(after.Length).IsEqualTo(before.Length + 1);
        for (var n = 0; n < before.Length; n++)
        {
            await Assert.That(after[n].Body.AsSpan().SequenceEqual(before[n].Body)).IsTrue().Because("sealed bytes are never recomputed");
            await Assert.That(after[n].ValuesAt).IsEqualTo(before[n].ValuesAt);
        }

        var stored = await db.StoredAsync(x);
        await Assert.That(after[^1].Ops.Single(op => op.Id == x).Document).IsEqualTo(stored);
        await Assert.That(await db.ScalarAsync("SELECT COUNT(*) FROM turso_cdc WHERE table_name = '_tb_shape_chunk';")).IsEqualTo(0L);
        await Assert.That(await db.ReadAsync(shape.Handle, second)).IsNull().Because("nothing past the head is sealed");
        await Assert.That(await db.ReadAsync((await ShapeAsync(db)).Handle, start)).IsNull().Because("an unknown handle has no chunk");
    }

    [Test]
    public async Task A_refused_memory_reservation_seals_nothing()
    {
        await using var api = await DocumentApi.StartAsync();
        var db = await Database.OpenAsync(api.Server, await api.TenantOfAsync());
        var start = await db.HeadAsync();
        var head = await db.PutAsync(DocumentApi.NewId(), new { n = 1 });
        var shape = await ShapeAsync(db);

        var refusing = new ShapeSealer(new RefusingBudget(), NullLogger<ShapeSealer>.Instance, db.Storage);
        await Assert.That(async () => await refusing.SealUpToHeadAsync(db.Store, shape, start, CancellationToken.None)).Throws<QosMemoryException>();
        await Assert.That(await db.ReadAsync(shape.Handle, start)).IsNull();
        await Assert.That(await db.SealAsync(shape, start)).IsEqualTo(head).Because("admission refusal leaves the range sealable");
    }

    [Test]
    public async Task A_sealed_chunk_is_published_from_its_committed_bytes_without_rereading_them()
    {
        await using var api = await DocumentApi.StartAsync();
        var db = await Database.OpenAsync(api.Server, await api.TenantOfAsync());
        var start = await db.HeadAsync();
        var head = await db.PutAsync(DocumentApi.NewId(), new { n = new Faker().Random.Int() });
        var shape = await ShapeAsync(db);
        var memory = new ObservedBudget(api.Server.Services.GetRequiredService<IMemoryBudget>());
        var storage = new ObservedShapeStorage(db.Storage);
        var published = new List<(long Offset, long EndOffset, byte[] Body, long SealedAt, long Admitted)>();
        var sealer = new ShapeSealer(memory, NullLogger<ShapeSealer>.Instance, storage,
            onSealed: (_, _, chunk) => published.Add((chunk.Offset, chunk.EndOffset, chunk.Body.ToArray(), chunk.SealedAt, memory.ReservedBytes)));

        await Assert.That(await sealer.SealUpToHeadAsync(db.Store, shape, start, CancellationToken.None)).IsEqualTo(head);
        await Assert.That(storage.ReadTransactions).IsEqualTo(0).Because("publication reuses the committed bytes instead of a body SELECT");
        await Assert.That(memory.ReservedBytes).IsEqualTo(0L).Because("the retained admission ends with the callback");
        var (offset, endOffset, body, sealedAt, admitted) = published.Single();
        await Assert.That(admitted).IsGreaterThanOrEqualTo((long)body.Length).Because("the bytes stay admitted through the callback");
        using var stored = (await sealer.ReadChunkAsync(db.Store, shape.Handle, start, CancellationToken.None))!;
        await Assert.That(offset).IsEqualTo(start);
        await Assert.That(endOffset).IsEqualTo(stored.EndOffset);
        await Assert.That(sealedAt).IsEqualTo(stored.SealedAt);
        await Assert.That(body.AsSpan().SequenceEqual(stored.Body.Span)).IsTrue();
    }

    [Test]
    public async Task Escaped_ids_are_admitted_for_the_simultaneously_live_output_arrays()
    {
        await using var api = await DocumentApi.StartAsync();
        var db = await Database.OpenAsync(api.Server, await api.TenantOfAsync());
        var start = await db.HeadAsync();
        var head = await db.CommitAsync(async transaction =>
        {
            for (var n = 0; n < 1024; n++)
            {
                var id = new string('<', 250) + n.ToString("D6", CultureInfo.InvariantCulture);
                _ = await transaction.PutAsync(Collection, id, "{}"u8.ToArray(), CancellationToken.None);
            }
        });
        var memory = new ObservedBudget(api.Server.Services.GetRequiredService<IMemoryBudget>());
        var materializationBytes = long.MaxValue;
        var materialized = 0;
        var shape = await ShapeAsync(db, _ =>
        {
            materializationBytes = Math.Min(materializationBytes, memory.ReservedBytes);
            materialized++;
            return true;
        });
        var sealer = new ShapeSealer(memory, NullLogger<ShapeSealer>.Instance, db.Storage);

        await Assert.That(await sealer.SealUpToHeadAsync(db.Store, shape, start, CancellationToken.None)).IsEqualTo(head);
        var chunk = (await db.ReadAllAsync(shape.Handle, start, head)).Single();
        await Assert.That(materialized).IsEqualTo(1024);
        await Assert.That(chunk.Ops.Length).IsEqualTo(1024);
        await Assert.That(chunk.Body.Length).IsGreaterThan(MiB);
        await Assert.That(materializationBytes).IsGreaterThanOrEqualTo(3L * chunk.Body.Length)
            .Because("the changes buffer, envelope buffer and returned body coexist, including escaped ids");
        await Assert.That(memory.ReservedBytes).IsEqualTo(0L);
        await Assert.That(memory.ActiveReservations).IsEqualTo(0);
    }

    [Test]
    public async Task Worker_keeps_active_frontiers_and_releases_memory_when_requests_leave_or_it_stops()
    {
        await using var api = await DocumentApi.StartAsync();
        var db = await Database.OpenAsync(api.Server, await api.TenantOfAsync());
        var start = await db.HeadAsync();
        var first = await db.PutAsync(DocumentApi.NewId(), new { n = 1 });
        var shape = await ShapeAsync(db);
        var memory = new ObservedBudget(api.Server.Services.GetRequiredService<IMemoryBudget>());
        var storage = new ObservedShapeStorage(db.Storage);
        var sealer = new ShapeSealer(memory, NullLogger<ShapeSealer>.Instance, storage);
        ShapeSealRequest[] active = [new(shape, start)];
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var worker = sealer.RunDatabaseAsync(db.Store, () => Volatile.Read(ref active), stop.Token);
        try
        {
            await TerraBaseTestServer.EventuallyAsync(async _ => await db.ReadAsync(shape.Handle, start) is not null
                && memory.ActiveReservations == 1);
            var activeBytes = memory.ReservedBytes;
            await Assert.That(activeBytes).IsGreaterThan(0L);
            var second = await db.PutAsync(DocumentApi.NewId(), new { n = 2 });
            await TerraBaseTestServer.EventuallyAsync(async _ => await db.ReadAsync(shape.Handle, first) is not null
                && memory.ActiveReservations == 1);
            await Assert.That(storage.WriteTransactions).IsEqualTo(2)
                .Because("the second pass starts at the cached boundary instead of rewalking the first chunk");

            Volatile.Write(ref active, []);
            _ = await db.PutAsync(DocumentApi.NewId(), new { n = 3 });
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(memory.ActiveReservations == 1
                && memory.ReservedBytes < activeBytes));
            await Assert.That(storage.WriteTransactions).IsEqualTo(2);

            Volatile.Write(ref active, [new ShapeSealRequest(shape, start)]);
            var head = await db.PutAsync(DocumentApi.NewId(), new { n = 4 });
            await TerraBaseTestServer.EventuallyAsync(async _ => await db.ReadAsync(shape.Handle, second) is { } chunk
                && chunk.EndOffset == head && memory.ActiveReservations == 1);
            await Assert.That(storage.WriteTransactions).IsEqualTo(5)
                .Because("a reactivated request rebuilds its frontier after its old cache entry was released");
        }
        finally
        {
            await stop.CancelAsync();
            await Assert.That(async () => await worker).Throws<OperationCanceledException>();
        }
        await Assert.That(memory.ActiveReservations).IsEqualTo(0);
        await Assert.That(memory.ReservedBytes).IsEqualTo(0L);
    }

    [Test]
    [Arguments("truncated-state-image")]
    [Arguments("unknown-change-type-on-state-row")]
    [Arguments("oversized-table-name")]
    [Arguments("oversized-state-header")]
    public async Task Damaged_cdc_refuses_its_database_and_does_not_hold_back_another(string fixture)
    {
        await using var api = await DocumentApi.StartAsync();
        (await api.SendAsync(HttpMethod.Put, api.DatabasePath("other"))).Status.ShouldBe(System.Net.HttpStatusCode.Created);
        (await api.SendAsync(HttpMethod.Post, $"{api.DatabasePath("other")}/collections", new { name = Collection }))
            .Status.ShouldBe(System.Net.HttpStatusCode.Created);
        var damaged = await Database.OpenAsync(api.Server, await api.TenantOfAsync());
        var healthy = await Database.OpenAsync(api.Server, await api.TenantOfAsync("other"));
        var (damagedStart, healthyStart) = (await damaged.HeadAsync(), await healthy.HeadAsync());

        var change = await damaged.CapturedHeadAsync();
        _ = await damaged.PutAsync(DocumentApi.NewId(), new { n = 1 });
        await damaged.DamageAsync(change, fixture);
        _ = await healthy.PutAsync(DocumentApi.NewId(), new { n = 2 });

        var (shapeDamaged, shapeHealthy) = (await ShapeAsync(damaged), await ShapeAsync(healthy));
        var error = await Assert.That(async () => await damaged.SealAsync(shapeDamaged, damagedStart)).Throws<InvalidDataException>();
        if (fixture == "oversized-table-name")
        {
            await Assert.That(error!.Message).Contains("table name exceeds");
        }
        if (fixture == "oversized-state-header")
        {
            await Assert.That(error!.Message).Contains("column bound").Because("the record is refused before the generic decoder expands its NULL columns");
        }
        await Assert.That(await damaged.ReadAsync(shapeDamaged.Handle, damagedStart)).IsNull();

        using var stop = new CancellationTokenSource();
        var damagedWorker = damaged.Sealer.RunDatabaseAsync(damaged.Store, () => [new ShapeSealRequest(shapeDamaged, damagedStart)], stop.Token);
        var healthyWorker = healthy.Sealer.RunDatabaseAsync(healthy.Store, () => [new ShapeSealRequest(shapeHealthy, healthyStart)], stop.Token);
        await TerraBaseTestServer.EventuallyAsync(async _ => await healthy.ReadAsync(shapeHealthy.Handle, healthyStart) is not null);
        await Assert.That(damagedWorker.IsCompleted).IsFalse().Because("a refusal holds the database; its worker waits for the next commit");
        await Assert.That(await damaged.ReadAsync(shapeDamaged.Handle, damagedStart)).IsNull();

        await stop.CancelAsync();
        await Assert.That(async () => await damagedWorker).Throws<OperationCanceledException>();
        await Assert.That(async () => await healthyWorker).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Captured_unique_invalid_identifiers_are_bookkeeping_not_document_json()
    {
        await using var api = await DocumentApi.StartAsync();
        var db = await Database.OpenAsync(api.Server, await api.TenantOfAsync());
        var change = await db.CapturedHeadAsync();
        var start = await db.HeadAsync();
        var id = DocumentApi.NewId();
        var head = await db.PutAsync(id, new { a = 0 });
        var image = SqliteTextRecord(Collection, id);
        await db.RawWriteAsync($"""
            UPDATE turso_cdc SET table_name = '_tb_unique_invalid', after = X'{Convert.ToHexString(image)}'
            WHERE change_id = (SELECT MIN(change_id) FROM turso_cdc
                WHERE change_id > {change} AND table_name = '_tb_feed' AND change_type IN (0, 1));
            """);
        await Assert.That(await db.ScalarAsync($"SELECT COUNT(*) FROM turso_cdc WHERE change_id > {change} AND table_name = '_tb_unique_invalid';"))
            .IsEqualTo(1L);
        var shape = await ShapeAsync(db);

        await Assert.That(await db.SealAsync(shape, start)).IsEqualTo(head)
            .Because("the two TEXT metadata columns are collection and document identifiers, not JSON");
        var op = (await db.ReadAllAsync(shape.Handle, start, head)).Single().Ops.Single();
        await Assert.That(op.Id).IsEqualTo(id);
        await Assert.That(op.Document).IsEqualTo(await db.StoredAsync(id));
    }

    [Test]
    [Arguments(Collection, Collection)]
    [Arguments("documents", "documents")]
    [Arguments(Collection, "_tb_unique_invalid")]
    public async Task Captured_json_token_amplification_is_refused_before_decode_and_releases_admission(string collection, string capturedTable)
    {
        const int values = 500_000;
        await using var api = await DocumentApi.StartAsync();
        if (collection != Collection)
        {
            (await api.SendAsync(HttpMethod.Post, api.Collections, new { name = collection }))
                .Status.ShouldBe(System.Net.HttpStatusCode.Created);
        }
        (await api.SendAsync(HttpMethod.Put, api.DatabasePath("other"))).Status.ShouldBe(System.Net.HttpStatusCode.Created);
        (await api.SendAsync(HttpMethod.Post, $"{api.DatabasePath("other")}/collections", new { name = Collection }))
            .Status.ShouldBe(System.Net.HttpStatusCode.Created);
        var damaged = await Database.OpenAsync(api.Server, await api.TenantOfAsync(), collection);
        var healthy = await Database.OpenAsync(api.Server, await api.TenantOfAsync("other"));
        var (damagedStart, healthyStart) = (await damaged.HeadAsync(), await healthy.HeadAsync());
        var change = await damaged.CapturedHeadAsync();
        _ = await damaged.PutAsync(DocumentApi.NewId(), new { a = 0 });

        var document = new StringBuilder(2 * values + 7).Append("{\"a\":[");
        for (var n = 1; n < values; n++)
        {
            document.Append("0,");
        }
        document.Append("0]}");
        await Assert.That(document.Length).IsLessThan(MiB);
        await damaged.ReplaceCapturedDocumentAsync(change, document.ToString(), capturedTable);
        var healthyHead = await healthy.PutAsync(DocumentApi.NewId(), new { a = 0 });
        var (shapeDamaged, shapeHealthy) = (await ShapeAsync(damaged), await ShapeAsync(healthy));
        var memory = new ObservedBudget(api.Server.Services.GetRequiredService<IMemoryBudget>(), 20 * MiB);
        var sealer = new ShapeSealer(memory, NullLogger<ShapeSealer>.Instance, damaged.Storage);

        await Assert.That(async () => await sealer.SealUpToHeadAsync(damaged.Store, shapeDamaged, damagedStart, CancellationToken.None))
            .Throws<QosMemoryException>();
        await Assert.That(memory.LastRefusedBytes).IsGreaterThanOrEqualTo(36L * values)
            .Because("every sender-decodable document shape is admitted before decode, after the smaller image allowance succeeds");
        await Assert.That(memory.ReservedBytes).IsEqualTo(0L);
        await Assert.That(memory.ActiveReservations).IsEqualTo(0);
        await Assert.That(await damaged.ReadAsync(shapeDamaged.Handle, damagedStart)).IsNull();
        await Assert.That(await damaged.ScalarAsync("SELECT COUNT(*) FROM _tb_shape_chunk;")).IsEqualTo(0L);

        var healthySealer = new ShapeSealer(memory, NullLogger<ShapeSealer>.Instance, healthy.Storage);
        await Assert.That(await healthySealer.SealUpToHeadAsync(healthy.Store, shapeHealthy, healthyStart, CancellationToken.None))
            .IsEqualTo(healthyHead).Because("the refused database does not retain admission or stop another database from sealing");
        await Assert.That(await healthy.ReadAsync(shapeHealthy.Handle, healthyStart)).IsNotNull();
        await Assert.That(memory.ReservedBytes).IsEqualTo(0L);
        await Assert.That(memory.ActiveReservations).IsEqualTo(0);
    }

    private static async Task<ShapeSealDefinition> ShapeAsync(Database db, Func<StoredDocument, bool>? predicate = null)
    {
        await using var transaction = await db.Store.BeginTransactionAsync(write: false, CancellationToken.None);
        var collection = await transaction.GetCollectionAsync(db.CollectionName, CancellationToken.None)
            ?? throw new InvalidOperationException("the collection is missing");
        return new ShapeSealDefinition(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            "h1-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), db.CollectionName, collection.Id,
            RandomNumberGenerator.GetBytes(16), db.Store.Incarnation, predicate);
    }

    private static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);

    private static byte[] SqliteTextRecord(string id, string document)
    {
        var idBytes = Encoding.UTF8.GetBytes(id);
        var documentBytes = Encoding.UTF8.GetBytes(document);
        var idType = SqliteVarint(13 + 2 * idBytes.Length);
        var documentType = SqliteVarint(13 + 2 * documentBytes.Length);
        return [(byte)(1 + idType.Length + documentType.Length), .. idType, .. documentType, .. idBytes, .. documentBytes];
    }

    private static byte[] SqliteVarint(int value)
    {
        Span<byte> bytes = stackalloc byte[5];
        var start = bytes.Length;
        bytes[--start] = (byte)(value & 0x7f);
        while ((value >>= 7) != 0)
        {
            bytes[--start] = (byte)((value & 0x7f) | 0x80);
        }
        return bytes[start..].ToArray();
    }

    private static string FixtureDirectory([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(source))!, "Fixtures", "CorruptCdc");
    private sealed record Sealed(long Offset, long EndOffset, long ValuesAt, byte[] Body)
    {
        public Op[] Ops { get; } = Parse(Body);

        private static Op[] Parse(byte[] body)
        {
            using var json = JsonDocument.Parse(body);
            return [.. json.RootElement.GetProperty("changes").EnumerateArray().SelectMany(change => change.GetProperty("ops").EnumerateArray().Select(op => new Op(
                long.Parse(change.GetProperty("offset").GetString()!, CultureInfo.InvariantCulture),
                op.GetProperty("op").GetString()!,
                op.GetProperty("id").GetString()!,
                op.TryGetProperty("version", out var version) ? version.GetString() : null,
                op.TryGetProperty("document", out var document) ? document.GetRawText() : null)))];
        }
    }

    private sealed record Op(long Commit, string Kind, string Id, string? Version, string? Document);
    private sealed class Database(TerraBaseTestServer server, IDatabaseStore store, TursoDatabaseStore turso, string collectionName)
    {
        public string CollectionName { get; } = collectionName;

        public IDatabaseStore Store { get; } = store;

        public TursoShapeStorage Storage { get; } = new(server.Services.GetRequiredService<TursoStorageEngine>());

        public ShapeSealer Sealer => new(server.Services.GetRequiredService<IMemoryBudget>(), NullLogger<ShapeSealer>.Instance, Storage);

        public static async Task<Database> OpenAsync(TerraBaseTestServer server, TenantContext tenant, string collection = Collection)
        {
            var store = await server.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(tenant, CancellationToken.None)
                ?? throw new InvalidOperationException("the database is missing");
            var turso = (TursoDatabaseStore)(await server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(tenant, CancellationToken.None))!;
            return new Database(server, store, turso, collection);
        }

        public async Task<long> SealAsync(ShapeSealDefinition shape, long offset) =>
            await Sealer.SealUpToHeadAsync(Store, shape, offset, CancellationToken.None);

        public async Task<Sealed?> ReadAsync(string handle, long offset)
        {
            using var chunk = await Sealer.ReadChunkAsync(Store, handle, offset, CancellationToken.None);
            if (chunk is null)
            {
                return null;
            }
            using var json = JsonDocument.Parse(chunk.Body);
            var resume = Base64Url.DecodeFromChars(json.RootElement.GetProperty("cursor_resume").GetString()!);
            await Assert.That(resume.Length).IsGreaterThan(17);
            await Assert.That(resume[0]).IsEqualTo((byte)1);
            var r8 = Base64Url.EncodeToString(resume.AsSpan(17));
            var cursor = ChangeCursor.Decode(r8);
            await Assert.That(cursor).IsNotNull().Because("every healthy chunk carries an R8-decodable resume cursor");
            await Assert.That(ChangeCursor.Encode(cursor!)).IsEqualTo(r8);
            return new Sealed(chunk.Offset, chunk.EndOffset, chunk.ValuesAt, chunk.Body.ToArray());
        }
        public async Task<Sealed[]> ReadAllAsync(string handle, long from, long to)
        {
            List<Sealed> chunks = [];
            while (from < to)
            {
                var chunk = await ReadAsync(handle, from) ?? throw new InvalidOperationException($"no chunk is sealed at {from}");
                await Assert.That(chunk.Offset).IsEqualTo(from);
                chunks.Add(chunk);
                from = chunk.EndOffset;
            }

            await Assert.That(from).IsEqualTo(to).Because("chunks end on the sealed boundary");
            return [.. chunks];
        }
        public async Task<long> CommitAsync(Func<IStorageTransaction, Task> work)
        {
            await using (var transaction = await Store.BeginTransactionAsync(write: true, CancellationToken.None))
            {
                await work(transaction);
                _ = await transaction.CommitAsync(CancellationToken.None);
            }

            return await HeadAsync();
        }

        public Task<long> PutAsync(string id, object document) =>
            CommitAsync(async transaction => _ = await transaction.PutAsync(CollectionName, id, Json(document), CancellationToken.None));

        public Task<long> HeadAsync() => ScalarAsync("SELECT seq FROM _tb_feed_sequence WHERE id = 1;");

        public async Task<string> StoredAsync(string id)
        {
            await using var transaction = await Store.BeginTransactionAsync(write: false, CancellationToken.None);
            var document = await transaction.GetAsync(CollectionName, id, CancellationToken.None) ?? throw new InvalidOperationException("the document is missing");
            return Encoding.UTF8.GetString(document.Json.Span);
        }

        public Task<long> ScalarAsync(string sql) => turso.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = sql;
            return (long)command.ExecuteScalar()!;
        }, "a test reading a scalar", CancellationToken.None);
        public Task<bool> RawWriteAsync(string sql) => turso.WithWriterAsync(session =>
        {
            using (var off = session.Connection.CreateCommand())
            {
                off.CommandText = TursoSession.CaptureOff;
                off.ExecuteNonQuery();
            }

            try
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
            finally
            {
                using var on = session.Connection.CreateCommand();
                on.CommandText = TursoSession.CaptureOn;
                on.ExecuteNonQuery();
            }

            return true;
        }, "a test writing feed rows uncaptured", CancellationToken.None);
        public Task<long> CapturedHeadAsync() => ScalarAsync("SELECT COALESCE(MAX(change_id), 0) FROM turso_cdc;");

        public async Task ReplaceCapturedDocumentAsync(long after, string document, string table)
        {
            var (changeId, capturedId, capturedDocument) = await turso.WithWriterAsync(session =>
            {
                using var read = session.Connection.CreateCommand();
                read.CommandText = "SELECT change_id, after FROM turso_cdc WHERE change_id > $after AND table_name = $collection AND change_type IN (0, 1) ORDER BY change_id LIMIT 1;";
                read.Parameters.AddWithValue("$after", after);
                read.Parameters.AddWithValue("$collection", CollectionName);
                using var reader = read.ExecuteReader();
                if (!reader.Read() || SqliteRecord.Decode(TursoSession.Bytes(reader, 1)) is not [string id, string body])
                {
                    throw new InvalidDataException("the captured document row is missing");
                }
                return (ChangeId: reader.GetInt64(0), Id: id, Document: body);
            }, "a test reading a captured document", CancellationToken.None);
            await Assert.That(capturedDocument).IsEqualTo("{\"a\":0}");
            var image = SqliteTextRecord(capturedId, document);
            await RawWriteAsync($"UPDATE turso_cdc SET table_name = '{table}', after = X'{Convert.ToHexString(image)}' WHERE change_id = {changeId};");
            if (table == CollectionName)
            {
                return;
            }

            var (stateChangeId, stateImage) = await turso.WithWriterAsync(session =>
            {
                using var read = session.Connection.CreateCommand();
                read.CommandText = "SELECT change_id, after FROM turso_cdc WHERE change_id > $after AND table_name = '_tb_state' AND change_type IN (0, 1) ORDER BY change_id;";
                read.Parameters.AddWithValue("$after", after);
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    var candidate = TursoSession.Bytes(reader, 1);
                    if (SqliteRecord.Decode(candidate) is [string collection, string id, string, long, long, 1L, _, _]
                        && collection == CollectionName && id == capturedId)
                    {
                        return (ChangeId: reader.GetInt64(0), Image: candidate);
                    }
                }
                throw new InvalidDataException("the captured local live state row is missing");
            }, "a test reading a captured state", CancellationToken.None);
            var collectionBytes = Encoding.UTF8.GetBytes(CollectionName);
            var tableBytes = Encoding.UTF8.GetBytes(table);
            await Assert.That(stateImage[0]).IsLessThan((byte)128);
            await Assert.That(stateImage[1]).IsEqualTo((byte)(13 + 2 * collectionBytes.Length));
            await Assert.That(13 + 2 * tableBytes.Length).IsLessThan(128);
            image = [stateImage[0], (byte)(13 + 2 * tableBytes.Length), .. stateImage[2..stateImage[0]],
                .. tableBytes, .. stateImage[(stateImage[0] + collectionBytes.Length)..]];
            await Assert.That(SqliteRecord.Decode(image)[0]).IsEqualTo(table);
            await Assert.That(SqliteRecord.Decode(image).Skip(1).SequenceEqual(SqliteRecord.Decode(stateImage).Skip(1))).IsTrue()
                .Because("only the collection reference changes; id, live, state, stamp and origin stay intact");
            await RawWriteAsync($"UPDATE turso_cdc SET after = X'{Convert.ToHexString(image)}' WHERE change_id = {stateChangeId};");
        }
        public async Task DamageAsync(long after, string fixture)
        {
            if (fixture is "oversized-table-name" or "oversized-state-header")
            {
                var id = await ScalarAsync($"SELECT MIN(change_id) FROM turso_cdc WHERE change_id > {after} AND table_name = '_tb_state' AND change_type IN (0, 1);");
                await Assert.That(id).IsGreaterThan(after);
                var assignment = fixture == "oversized-table-name"
                    ? $"table_name = 'sqlite_{new string('x', MiB)}'"
                    : $"after = X'868d23{new string('0', 200_000)}'";
                await RawWriteAsync($"UPDATE turso_cdc SET {assignment} WHERE change_id = {id};");
                return;
            }
            var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(FixtureDirectory(), fixture + ".json")))!.AsObject();
            await Assert.That(json["expect"]!.GetValue<string>()).IsEqualTo("refused");
            await Assert.That(json["row"]!.GetValue<string>()).IsEqualTo("state");
            var mutation = json["mutation"]!.AsObject();
            var damaged = await turso.WithWriterAsync(session =>
            {
                long id = 0;
                byte[]? image = null;
                using (var read = session.Connection.CreateCommand())
                {
                    read.CommandText = "SELECT change_id, after FROM turso_cdc WHERE change_id > $after AND table_name = '_tb_state' AND change_type IN (0, 1) ORDER BY change_id;";
                    read.Parameters.AddWithValue("$after", after);
                    using var reader = read.ExecuteReader();
                    while (reader.Read())
                    {
                        var candidate = TursoSession.Bytes(reader, 1);
                        if (SqliteRecord.Decode(candidate) is [string collection, ..] && collection == Collection)
                        {
                            (id, image) = (reader.GetInt64(0), candidate);
                            break;
                        }
                    }
                }

                using var write = session.Connection.CreateCommand();
                write.Parameters.AddWithValue("$id", id);
                switch (mutation["kind"]!.GetValue<string>())
                {
                    case "truncate":
                        write.CommandText = "UPDATE turso_cdc SET after = $image WHERE change_id = $id;";
                        write.Parameters.AddWithValue("$image", image![..(image!.Length / 2)]);
                        break;
                    case "change_type":
                        write.CommandText = "UPDATE turso_cdc SET change_type = $type WHERE change_id = $id;";
                        write.Parameters.AddWithValue("$type", mutation["value"]!.GetValue<long>());
                        break;
                    default:
                        throw new InvalidDataException("this test applies truncate and change_type fixtures");
                }

                return write.ExecuteNonQuery();
            }, "a test damaging a CDC row", CancellationToken.None);
            await Assert.That(damaged).IsEqualTo(1);
        }
    }

    private sealed class ObservedShapeStorage(IShapeStorage inner) : IShapeStorage
    {
        private int _writeTransactions;
        private int _readTransactions;

        public int WriteTransactions => Volatile.Read(ref _writeTransactions);

        public int ReadTransactions => Volatile.Read(ref _readTransactions);

        public ValueTask<IStorageTransaction> BeginTransactionAsync(IDatabaseStore store, bool write, CancellationToken cancellationToken)
        {
            if (write)
            {
                Interlocked.Increment(ref _writeTransactions);
            }
            else
            {
                Interlocked.Increment(ref _readTransactions);
            }
            return inner.BeginTransactionAsync(store, write, cancellationToken);
        }
    }

    private sealed class ObservedBudget(IMemoryBudget inner, long limit = long.MaxValue) : IMemoryBudget
    {
        private readonly Lock _gate = new();
        private int _activeReservations;
        private long _reservedBytes;
        private long _lastRefusedBytes;

        public int ActiveReservations => Volatile.Read(ref _activeReservations);

        public long ReservedBytes => Interlocked.Read(ref _reservedBytes);

        public long LastRefusedBytes => Interlocked.Read(ref _lastRefusedBytes);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes) => TryReserve(lane, bytes, null);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant)
        {
            lock (_gate)
            {
                if (bytes > limit - _reservedBytes || inner.TryReserve(lane, bytes, tenant) is not { } reservation)
                {
                    Interlocked.Exchange(ref _lastRefusedBytes, bytes);
                    return null;
                }
                Interlocked.Increment(ref _activeReservations);
                Interlocked.Add(ref _reservedBytes, reservation.Bytes);
                return new Reservation(this, reservation);
            }
        }

        private sealed class Reservation(ObservedBudget owner, IMemoryReservation inner) : IMemoryReservation
        {
            private IMemoryReservation? _inner = inner;

            public long Bytes { get; } = inner.Bytes;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _inner, null) is { } reservation)
                {
                    lock (owner._gate)
                    {
                        reservation.Dispose();
                        Interlocked.Add(ref owner._reservedBytes, -Bytes);
                        Interlocked.Decrement(ref owner._activeReservations);
                    }
                }
            }
        }
    }

    private sealed class RefusingBudget : IMemoryBudget
    {
        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes) => TryReserve(lane, bytes, null);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant) => bytes == 0 ? new Empty() : null;

        private sealed class Empty : IMemoryReservation
        {
            public long Bytes => 0;

            public void Dispose()
            {
            }
        }
    }
}
