using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Testing;

namespace TerraBase.Fast.Backup;

public sealed class CatalogImportReservationTests
{
    // Above a result owner's 16,384 leases, which one reservation per state read exhausted below the byte limit.
    private const int Documents = 20_000;

    // A restarted node re-imports a catalog it already holds: each held state it reads is admitted until the single import
    // transaction commits. The import must stay within the byte budget without one live reservation per state.
    [Test]
    public async Task A_catalog_import_reading_more_states_than_the_reservation_table_holds_commits()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var source = await ManagementApi.StartAsync();
        await using var target = await ManagementApi.StartAsync();
        var sourceStore = (TursoDatabaseStore)await source.Server.Services.GetRequiredService<TursoStorageEngine>().OpenSystemDatabaseAsync(token);
        var targetStore = await target.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        var body = Encoding.UTF8.GetBytes("""{"name":"catalog"}""");
        var results = new (long, DocumentVersion?)[1000];
        for (var written = 0; written < Documents; written += results.Length)
        {
            await using var write = (TursoStorageTransaction)await sourceStore.BeginDurableTransactionAsync(token);
            await write.PutBatchAsync([.. Enumerable.Range(written, results.Length)
                .Select(i => ("bulk_catalog", $"doc-{i:D6}", (ReadOnlyMemory<byte>)body))], results, token);
            _ = await write.CommitAsync(token);
        }

        var exporter = source.Server.Services.GetRequiredService<IReplicaSnapshotSource>();
        var importer = target.Server.Services.GetRequiredService<IReplicaSnapshotImporter>();
        var meter = target.Server.Services.GetRequiredService<IMeterFactory>().Create(Telemetry.MeterName);
        var timings = new ConcurrentQueue<(string Phase, double Seconds)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "terrabase.replication.snapshot_import")
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, seconds, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "phase" && timings.Count < 64)
                {
                    timings.Enqueue(((string)tag.Value!, seconds));
                }
            }
        });
        listener.Start();
        var origins = new HashSet<ulong> { source.Server.Services.GetRequiredService<LocalReplica>().Id.Value };
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await Assert.That(await importer.ImportAsync(ChangeGroups.System, targetStore.Incarnation,
                    exporter.SnapshotAsync(ChangeGroups.System, sourceStore.Incarnation, token), origins, token)).IsTrue();
            }
        }
        finally
        {
            foreach (var (phase, seconds) in timings)
            {
                TestContext.Current!.OutputWriter.WriteLine($"SnapshotPhase states={Documents} phase={phase} seconds={seconds:F6}");
            }
        }

        await using var read = await targetStore.BeginTransactionAsync(write: false, token);
        await Assert.That(await read.GetAsync("bulk_catalog", $"doc-{Documents - 1:D6}", token) is { Deleted: false }).IsTrue();
    }

    // A catalog's reads, charged 16 times their bytes, can outgrow the whole process budget while every page fits in it:
    // sjc's 386 MB SYSTEM file was refused after 108-158 s of writer hold, on every retry (2026-10-28). The import keeps
    // no earlier page's reads, so each page's admission ends before the next page is read.
    [Test]
    public async Task A_catalog_import_whose_reads_outgrow_the_memory_budget_commits_page_by_page()
    {
        const int States = 8_000;
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var source = await ManagementApi.StartAsync();
        // The import's own 64 MiB transfer admission plus one page's reads fit; all pages' reads (~170 MiB) do not.
        await using var target = await ManagementApi.StartAsync(settings => settings["memory-budget"] = (128L * 1024 * 1024).ToString(CultureInfo.InvariantCulture));
        var sourceStore = (TursoDatabaseStore)await source.Server.Services.GetRequiredService<TursoStorageEngine>().OpenSystemDatabaseAsync(token);
        var targetStore = await target.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(token);
        var body = Encoding.UTF8.GetBytes($$"""{"name":"{{new string('c', 512)}}"}""");
        var results = new (long, DocumentVersion?)[1000];
        for (var written = 0; written < States; written += results.Length)
        {
            await using var write = (TursoStorageTransaction)await sourceStore.BeginDurableTransactionAsync(token);
            await write.PutBatchAsync([.. Enumerable.Range(written, results.Length)
                .Select(i => ("large_catalog", $"doc-{i:D6}", (ReadOnlyMemory<byte>)body))], results, token);
            _ = await write.CommitAsync(token);
        }

        var exporter = source.Server.Services.GetRequiredService<IReplicaSnapshotSource>();
        var importer = target.Server.Services.GetRequiredService<IReplicaSnapshotImporter>();
        var origins = new HashSet<ulong> { source.Server.Services.GetRequiredService<LocalReplica>().Id.Value };
        // The second import reads every state the first wrote, as a restarted machine's re-import does.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.That(await importer.ImportAsync(ChangeGroups.System, targetStore.Incarnation,
                exporter.SnapshotAsync(ChangeGroups.System, sourceStore.Incarnation, token), origins, token)).IsTrue();
        }

        await using var read = await targetStore.BeginTransactionAsync(write: false, token);
        await Assert.That(await read.GetAsync("large_catalog", $"doc-{States - 1:D6}", token) is { Deleted: false }).IsTrue();
    }
}
