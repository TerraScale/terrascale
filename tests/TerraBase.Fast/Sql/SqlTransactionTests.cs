using System.Net;
using TerraBase.Fast.Documents;
using TerraBase.Features.Sql;

namespace TerraBase.Fast.Sql;

public sealed class SqlTransactionTests
{
    private static async Task<SqlOutcome> RunAsync(ISqlSession session, string sql) =>
        await session.ExecuteAsync(SqlHarness.Command(sql), CancellationToken.None);

    private static async Task<string> ScalarAsync(ISqlSession session, string sql) => await RunAsync(session, sql) switch
    {
        SqlResult { Rows: [[var value, ..]] } => value.Kind == SqlValueKind.Text ? value.Text! : value.Integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
        SqlResult => "",
        SqlError error => error.Code,
    };

    [Test]
    public async Task A_transaction_sees_its_own_writes_and_rollback_discards_them()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT)");
        await using var session = SqlHarness.Open(api, canWrite: true);

        var begun = await RunAsync(session, "BEGIN IMMEDIATE");
        var inTransaction = !session.IsAutocommit;
        await RunAsync(session, "INSERT INTO t VALUES (1, 'a')");
        var own = await ScalarAsync(session, "SELECT count(*) FROM t");
        var nested = await RunAsync(session, "BEGIN");
        await RunAsync(session, "ROLLBACK");
        var after = await ScalarAsync(session, "SELECT count(*) FROM t");
        var commitAlone = await RunAsync(session, "COMMIT");

        await Assert.That(begun is SqlResult && inTransaction).IsTrue();
        await Assert.That(own).IsEqualTo("1");
        await Assert.That(SqlHarness.Code(nested)).IsEqualTo("SQLITE_ERROR");
        await Assert.That(after).IsEqualTo("0");
        await Assert.That(session.IsAutocommit).IsTrue();
        await Assert.That(SqlHarness.Code(commitAlone)).IsEqualTo("SQLITE_ERROR");
    }

    [Test]
    public async Task A_transaction_spanning_requests_replays_with_the_same_keys_and_commits()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE gen (id INTEGER PRIMARY KEY, name TEXT)");
        await using var session = SqlHarness.Open(api, canWrite: true);
        await using var other = SqlHarness.Open(api, canWrite: false);

        await RunAsync(session, "BEGIN");
        var inserted = await RunAsync(session, "INSERT INTO gen (name) VALUES ('x')") is SqlResult result ? result : throw new InvalidOperationException("insert failed");
        await session.EndRequestAsync();
        var uncommitted = await ScalarAsync(other, "SELECT count(*) FROM gen");
        // The next request replays the insert: the generated key is the logged one.
        var replayed = await ScalarAsync(session, "SELECT id FROM gen");
        var committed = await RunAsync(session, "COMMIT");
        await session.EndRequestAsync();

        await Assert.That(uncommitted).IsEqualTo("0");
        await Assert.That(replayed).IsEqualTo(inserted.LastInsertRowid!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(committed is SqlResult).IsTrue();
        await Assert.That(await ScalarAsync(other, "SELECT id FROM gen")).IsEqualTo(replayed);
    }

    [Test]
    public async Task A_change_since_the_transaction_read_is_a_conflict_and_nothing_commits()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE counters (id INTEGER PRIMARY KEY, n INTEGER)", "INSERT INTO counters VALUES (1, 0)");
        await using var session = SqlHarness.Open(api, canWrite: true);
        await using var other = SqlHarness.Open(api, canWrite: true);

        await RunAsync(session, "BEGIN");
        var read = await ScalarAsync(session, "SELECT n FROM counters WHERE id = 1");
        await session.EndRequestAsync();
        await RunAsync(other, "UPDATE counters SET n = n + 1 WHERE id = 1");
        var write = await RunAsync(session, "UPDATE counters SET n = 100 WHERE id = 1");
        await session.EndRequestAsync();

        await Assert.That(read).IsEqualTo("0");
        await Assert.That(write is SqlError { Code: "SQLITE_BUSY" } error && error.Message.Contains("conflict", StringComparison.Ordinal)).IsTrue();
        await Assert.That(session.IsAutocommit).IsTrue();
        await Assert.That(await ScalarAsync(other, "SELECT n FROM counters WHERE id = 1")).IsEqualTo("1");
    }

    [Test]
    public async Task Readonly_transactions_refuse_writes_and_transactions_hold_at_most_256_statements()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (id INTEGER PRIMARY KEY)");
        await using var session = SqlHarness.Open(api, canWrite: true);

        await RunAsync(session, "BEGIN TRANSACTION READONLY");
        var write = await RunAsync(session, "INSERT INTO t VALUES (1)");
        var read = await ScalarAsync(session, "SELECT count(*) FROM t");
        var commit = await RunAsync(session, "COMMIT");
        await RunAsync(session, "BEGIN READONLY");
        SqlOutcome last = new SqlResult([], [], 0, null);
        for (var i = 0; i < 257 && last is SqlResult; i++)
        {
            last = await RunAsync(session, "SELECT 1");
        }

        await Assert.That(SqlHarness.Code(write)).IsEqualTo("SQLITE_READONLY");
        await Assert.That(read).IsEqualTo("0");
        await Assert.That(commit is SqlResult).IsTrue();
        await Assert.That(SqlHarness.Code(last)).IsEqualTo("SQLITE_TOOBIG");
        await Assert.That(session.IsAutocommit).IsFalse();
        await Assert.That(await RunAsync(session, "ROLLBACK") is SqlResult && session.IsAutocommit).IsTrue();
    }

    [Test]
    public async Task A_failed_statement_undoes_only_itself_and_a_failed_commit_returns_to_autocommit()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (id INTEGER PRIMARY KEY, email TEXT UNIQUE)", "INSERT INTO t VALUES (9, 'taken')");
        await using var session = SqlHarness.Open(api, canWrite: true);
        await using var other = SqlHarness.Open(api, canWrite: true);

        await RunAsync(session, "BEGIN");
        await RunAsync(session, "INSERT INTO t VALUES (1, 'a')");
        var duplicate = await RunAsync(session, "INSERT INTO t VALUES (2, 'taken')");
        var open = !session.IsAutocommit;
        await RunAsync(session, "INSERT INTO t VALUES (3, 'c')");
        var committed = await RunAsync(session, "COMMIT");
        var rows = await ScalarAsync(other, "SELECT group_concat(id) FROM (SELECT id FROM t ORDER BY id)");

        await RunAsync(session, "BEGIN");
        await RunAsync(session, "SELECT count(*) FROM t");
        await session.EndRequestAsync();
        await RunAsync(other, "INSERT INTO t VALUES (4, 'd')");
        var conflict = await RunAsync(session, "COMMIT");
        var afterConflict = session.IsAutocommit;
        var rollback = await RunAsync(session, "ROLLBACK");

        await Assert.That(duplicate is SqlError { Code: "SQLITE_CONSTRAINT_UNIQUE", Kind: "constraint" }).IsTrue();
        await Assert.That(open).IsTrue();
        await Assert.That(committed is SqlResult).IsTrue();
        await Assert.That(rows).IsEqualTo("1,3,9");
        await Assert.That(conflict is SqlError { Code: "SQLITE_BUSY", Kind: "conflict" }).IsTrue();
        await Assert.That(afterConflict).IsTrue();
        await Assert.That(rollback is SqlResult).IsTrue();
    }

    [Test]
    public async Task Execute_is_all_or_nothing_and_scripts_stop_at_the_first_error()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (id INTEGER PRIMARY KEY)");

        var failed = await SqlApi.ExecuteAsync(api, "INSERT INTO t VALUES (1)", "INSERT INTO t VALUES (1)");
        var control = await SqlApi.ExecuteAsync(api, "SELECT 1", "COMMIT");
        await using var session = SqlHarness.Open(api, canWrite: true);
        var script = await session.ExecuteScriptAsync("INSERT INTO t VALUES (2); INSERT INTO t VALUES (3); SELECT count(*) FROM t", CancellationToken.None);
        var broken = await session.ExecuteScriptAsync("INSERT INTO t VALUES (4); INSERT INTO t VALUES (4); INSERT INTO t VALUES (5)", CancellationToken.None);

        await Assert.That(SqlApi.Error(failed)).IsEqualTo((HttpStatusCode.BadRequest, "sql_error", "SQLITE_CONSTRAINT_PRIMARYKEY", 1));
        await Assert.That((control.Status, control.Code, control.Op)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_request", 1));
        await Assert.That(script is SqlResult { Rows: [[{ Integer: 2 }]] }).IsTrue();
        await Assert.That(SqlHarness.Code(broken)).IsEqualTo("SQLITE_CONSTRAINT_PRIMARYKEY");
        await Assert.That(await ScalarAsync(session, "SELECT group_concat(id) FROM t")).IsEqualTo("2,3,4");
    }
}
