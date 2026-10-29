using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TerraBase.Testing;
using static TerraBase.Testing.PostgresSql;

namespace TerraBase.Fast.Postgres;

public sealed class PostgresSavepointTests
{
    private const string Table = "CREATE TABLE items (id SERIAL PRIMARY KEY, label TEXT NOT NULL)";

    [Test]
    public async Task Nested_duplicate_names_restore_writes_and_generated_keys_and_preserve_visibility()
    {
        await using var api = await ManagementApi.StartAsync();
        await using var source = await SourceAsync(api);
        await using var connection = await source.OpenConnectionAsync();
        await using var observer = await source.OpenConnectionAsync();
        await ExecuteAsync(connection, Table);
        await using var transaction = await connection.BeginTransactionAsync();
        await transaction.SaveAsync("root");
        await ExecuteAsync(connection, "INSERT INTO items (label) VALUES ('kept')");
        var kept = await ScalarAsync(connection, "SELECT id FROM items");
        await transaction.SaveAsync("duplicate");
        await ExecuteAsync(connection, "INSERT INTO items (label) VALUES ('discarded')");
        await transaction.SaveAsync("duplicate");
        await ExecuteAsync(connection, "INSERT INTO items (label) VALUES ('leaf')");
        await transaction.RollbackAsync("duplicate");
        await Assert.That(await ScalarAsync(connection, "SELECT count(*) FROM items")).IsEqualTo("2");
        await transaction.ReleaseAsync("duplicate");
        await transaction.RollbackAsync("duplicate");
        await Assert.That(await ScalarAsync(connection, "SELECT id FROM items")).IsEqualTo(kept);
        await Assert.That(await ScalarAsync(observer, "SELECT count(*) FROM items")).IsEqualTo("0");
        await transaction.RollbackAsync("root");
        await ExecuteAsync(connection, "INSERT INTO items (label) VALUES ('final')");
        var final = await ScalarAsync(connection, "SELECT id FROM items");
        await Assert.That(final).IsNotEqualTo(kept);
        await transaction.CommitAsync();
        await Assert.That(await ScalarAsync(observer, "SELECT label FROM items")).IsEqualTo("final");
        await Assert.That(await ScalarAsync(observer, "SELECT id FROM items")).IsEqualTo(final);
    }

    [Test]
    public async Task Rollback_to_recovers_an_aborted_transaction_but_missing_markers_do_not()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(time: clock);
        await using var source = await SourceAsync(api);
        await using var connection = await source.OpenConnectionAsync();
        await ExecuteAsync(connection, Table);
        await StateAsync(connection, "SAVEPOINT absent", "25P01");
        await using var transaction = await connection.BeginTransactionAsync();
        await transaction.SaveAsync("recover");
        await StateAsync(connection, "INSERT INTO items (label) VALUES (NULL)", "23502");
        await StateAsync(connection, "RELEASE recover", "25P02");
        await StateAsync(connection, "ROLLBACK TO absent", "3B001");
        await StateAsync(connection, "SELECT 1", "25P02");
        clock.Advance(TimeSpan.FromSeconds(1));
        await transaction.RollbackAsync("recover");
        await ExecuteAsync(connection, "INSERT INTO items (label) VALUES ('recovered')");
        var recoveredKey = long.Parse((await ScalarAsync(connection, "SELECT id FROM items"))!, CultureInfo.InvariantCulture);
        var epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await Assert.That(recoveredKey / 4096).IsEqualTo((long)(clock.GetUtcNow() - epoch).TotalMilliseconds);
        await transaction.RollbackAsync("recover"); // The target survives the first rollback.
        await Assert.That(await ScalarAsync(connection, "SELECT count(*) FROM items")).IsEqualTo("0");
        await ExecuteAsync(connection, "INSERT INTO items (label) VALUES ('committed')");
        await transaction.CommitAsync();
        await Assert.That(await ScalarAsync(connection, "SELECT label FROM items")).IsEqualTo("committed");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Rollback_discards_only_reads_after_its_marker(bool readBefore)
    {
        await using var api = await ManagementApi.StartAsync();
        await using var source = await SourceAsync(api);
        await using var connection = await source.OpenConnectionAsync();
        await using var writer = await source.OpenConnectionAsync();
        await ExecuteAsync(connection, Table);
        await using var transaction = await connection.BeginTransactionAsync();
        if (readBefore)
        {
            await ScalarAsync(connection, "SELECT count(*) FROM items");
        }

        await transaction.SaveAsync("reads");
        await ScalarAsync(connection, "SELECT count(*) FROM items");
        await transaction.RollbackAsync("reads");
        await ExecuteAsync(writer, "INSERT INTO items (label) VALUES ('concurrent')");
        if (readBefore)
        {
            var conflict = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
            await Assert.That(conflict!.SqlState).IsEqualTo("40001");
        }
        else
        {
            await ExecuteAsync(connection, "INSERT INTO items (label) VALUES ('surviving')");
            await transaction.CommitAsync();
            await Assert.That(await ScalarAsync(writer, "SELECT count(*) FROM items")).IsEqualTo("2");
        }
    }

    [Test]
    public async Task Replay_conflict_preserves_a_savepoint_until_rollback_discards_the_conflicting_read()
    {
        await using var api = await ManagementApi.StartAsync();
        await using var source = await SourceAsync(api);
        await using var connection = await source.OpenConnectionAsync();
        await using var writer = await source.OpenConnectionAsync();
        await ExecuteAsync(connection, Table);
        await using var transaction = await connection.BeginTransactionAsync();
        await transaction.SaveAsync("recover");
        await ScalarAsync(connection, "SELECT count(*) FROM items");
        await ExecuteAsync(writer, "INSERT INTO items (label) VALUES ('concurrent')");
        await StateAsync(connection, "SELECT count(*) FROM items", "40001");
        await StateAsync(connection, "SELECT count(*) FROM items", "25P02"); // Refused at Parse, without replaying the read.
        await StateAsync(connection, "INSERT INTO items (label) VALUES ('refused')", "25P02");
        await transaction.RollbackAsync("recover");
        await Assert.That(await ScalarAsync(connection, "SELECT count(*) FROM items")).IsEqualTo("1");
        await ExecuteAsync(connection, "INSERT INTO items (label) VALUES ('recovered')");
        await Assert.That(await ScalarAsync(writer, "SELECT count(*) FROM items")).IsEqualTo("1");
        await transaction.CommitAsync();
        await Assert.That(await ScalarAsync(writer, "SELECT count(*) FROM items")).IsEqualTo("2");
    }

    [Test]
    public async Task Identifier_folding_quoting_and_release_remove_the_target_and_descendants()
    {
        await using var api = await ManagementApi.StartAsync();
        await using var source = await SourceAsync(api);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, "SAVEPOINT Mixed");
        await ExecuteAsync(connection, "SAVEPOINT \"Mixed\"");
        await ExecuteAsync(connection, "SAVEPOINT \"with\"\"quote\"");
        await ExecuteAsync(connection, "ROLLBACK WORK TO SAVEPOINT \"with\"\"quote\"");
        await ExecuteAsync(connection, "RELEASE SAVEPOINT mixed");
        await StateAsync(connection, "ROLLBACK TRANSACTION TO \"Mixed\"", "3B001");
        await transaction.RollbackAsync();
        await using var next = await connection.BeginTransactionAsync();
        await StateAsync(connection, "ROLLBACK TO mixed", "3B001");
        await next.RollbackAsync();
    }

    [Test]
    [Arguments(128, 1)]
    [Arguments(97, 63)]
    public async Task Savepoints_are_bounded_by_count_and_bytes_and_release_reclaims_capacity(int count, int length)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await ManagementApi.StartAsync(time: clock);
        await using var source = await SourceAsync(api);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var name = new string('s', length);
        await transaction.SaveAsync("root");
        for (var i = 1; i < count; i++)
        {
            // Npgsql's SaveAsync buffers markers until the next command. Round-trip each marker so advancing the clock
            // refills real admission between statements, without bypassing it or using real-time delays.
            clock.Advance(TimeSpan.FromSeconds(1));
            await ExecuteAsync(connection, $"SAVEPOINT {name}");
        }
        await StateAsync(connection, $"SAVEPOINT {name}", "54000");
        await transaction.RollbackAsync("root");
        for (var i = 1; i < count; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await ExecuteAsync(connection, $"SAVEPOINT {name}");
        }
        await transaction.ReleaseAsync("root");
        await transaction.SaveAsync("root");
        await transaction.CommitAsync();
    }
}
