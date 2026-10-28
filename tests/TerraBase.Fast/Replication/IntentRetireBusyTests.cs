using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using TerraBase.Engine.Turso;
using TerraBase.Features.Memory;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class IntentRetireBusyTests
{
    [Test]
    public async Task A_busy_intent_retirement_keeps_the_intent_and_the_node_writable()
    {
        using var directory = new TempDirectory();
        using var meter = new Meter("intent-retire-busy");
        var budget = new SwitchedBudget();
        await using var engine = new TursoStorageEngine(directory.Root, 1, 64, 64 * 1024 * 1024, meter,
            NullLogger<TursoStorageEngine>.Instance, new TestMasterKey(), TimeSpan.FromSeconds(5), schemaMemory: budget);
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        await Assert.That(await engine.CreateDatabaseAsync(database, CancellationToken.None)).IsTrue();
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the database is missing"));

        await using (var write = await store.BeginDurableTransactionAsync(CancellationToken.None))
        {
            _ = await write.PutAsync("items", "first", "{\"n\":1}"u8.ToArray(), CancellationToken.None);
            _ = await write.CommitAsync(CancellationToken.None);
            // The ledger's journal reservation is refused only as the operation ends, when its intent is retired.
            budget.Refuse = true;
        }

        budget.Refuse = false;
        await Assert.That(engine.Ledger.FailedClosed).IsNull().Because("a busy retirement is transient, not a storage fault");
        await Assert.That(await IntentAsync(engine, database)).IsTrue().Because("the intent stays until an operation retires it");

        await using (var write = await store.BeginDurableTransactionAsync(CancellationToken.None))
        {
            _ = await write.PutAsync("items", "second", "{\"n\":2}"u8.ToArray(), CancellationToken.None);
            _ = await write.CommitAsync(CancellationToken.None);
        }

        await Assert.That(await IntentAsync(engine, database)).IsFalse().Because("the next operation's end retires the kept intent");
        await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        await Assert.That(await read.GetAsync("items", "first", CancellationToken.None)).IsNotNull();
    }

    private static Task<bool> IntentAsync(TursoStorageEngine engine, TenantContext database) =>
        engine.IntentStore!.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM _tb_write_intent WHERE database = $database;";
            command.Parameters.AddWithValue("$database", database.DatabaseId);
            return command.ExecuteScalar() is not null;
        }, "checking the intent", CancellationToken.None);

    private sealed class TempDirectory : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("intent-retire-busy-").FullName;
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    // Grants everything, except the system work the rebuild ledger reserves (the bulk lane, no tenant) while Refuse is set.
    private sealed class SwitchedBudget : IMemoryBudget
    {
        public volatile bool Refuse;

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes) => new Reservation(bytes);

        public IMemoryReservation? TryReserve(MemoryLane lane, long bytes, TenantContext? tenant) =>
            Refuse && lane == MemoryLane.Bulk && tenant is null ? null : new Reservation(bytes);

        private sealed class Reservation(long bytes) : IMemoryReservation
        {
            public long Bytes { get; } = bytes;

            public void Dispose()
            {
            }
        }
    }
}
