using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine.Turso;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// Acks and retention read the change log's bounds under the writer. Turso seeks the rowid only for a bare min() or max();
/// wrapped in coalesce or arithmetic it read every turso_cdc row, so on gru (2026-10-27) a 700k-row backlog behind a silent
/// peer held each database's writer 1-2 s per ack and every write waited behind it.
/// </summary>
public sealed class ChangeLogBoundsTests
{
    private static readonly string[] Bounds = [TursoStatements.NewestChange, TursoStatements.OldestChange, TursoStatements.ChangeRows];

    [Test]
    public async Task Each_change_log_bound_reads_one_row()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var server = await TerraBaseTestServer.StartAsync();
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        await engine.CreateDatabaseAsync(database, token);
        var store = (TursoDatabaseStore)(await engine.OpenDatabaseAsync(database, token))!;
        for (var write = 0; write < 3; write++)
        {
            await using var transaction = await store.BeginTransactionAsync(write: true, token);
            _ = await transaction.PutAsync("items", EntityIds.New().ToString(), Encoding.UTF8.GetBytes("""{"n":1}"""), token);
            await transaction.CommitAsync(token);
        }

        var (newest, oldest, rows) = await store.WithWriterAsync(session =>
        {
            long Scalar(string sql)
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = sql;
                return (long)command.ExecuteScalar()!;
            }

            return (Scalar(TursoStatements.NewestChange), Scalar(TursoStatements.OldestChange), Scalar(TursoStatements.ChangeRows));
        }, "a test reading the change log's bounds", token);
        await Assert.That(newest).IsGreaterThan(oldest).Because("the writes were captured");
        await Assert.That(rows).IsEqualTo(newest - oldest + 1);

        foreach (var sql in Bounds)
        {
            var program = await store.WithWriterAsync(session =>
            {
                using var command = session.Connection.CreateCommand();
                command.CommandText = "EXPLAIN " + sql;
                using var reader = command.ExecuteReader();
                List<string> opcodes = [];
                while (reader.Read())
                {
                    opcodes.Add(reader.GetString(1));
                }

                return opcodes;
            }, "a test explaining a change log bound", token);
            var steps = program.Select((opcode, at) => (opcode, at)).Where(step => step.opcode == "AggStep").ToList();
            await Assert.That(steps).IsNotEmpty().Because(sql);
            // A seek leaves its loop after the first row (Goto); a scan steps to the next one (Next).
            foreach (var (_, at) in steps)
            {
                await Assert.That(program[at + 1]).IsEqualTo("Goto").Because($"{sql}: {string.Join(' ', program)}");
            }
        }
    }
}
