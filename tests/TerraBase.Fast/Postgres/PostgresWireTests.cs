using Bogus;
using Npgsql;
using TerraBase.Primitives;
using TerraBase.Testing;
using static TerraBase.Testing.PostgresSql;

namespace TerraBase.Fast.Postgres;

public sealed class PostgresWireTests
{
    private const string People = "CREATE TABLE people (id SERIAL PRIMARY KEY, name TEXT NOT NULL, email VARCHAR(80) UNIQUE, team TEXT)";

    // psql 17's \dt and \d people (src/bin/psql/describe.c) against a server reporting 16.0, as ECHO_HIDDEN prints them.
    private const string ListTables = """
        SELECT n.nspname as "Schema",
          c.relname as "Name",
          CASE c.relkind WHEN 'r' THEN 'table' WHEN 'v' THEN 'view' WHEN 'm' THEN 'materialized view' WHEN 'i' THEN 'index' WHEN 'S' THEN 'sequence' WHEN 't' THEN 'TOAST table' WHEN 'f' THEN 'foreign table' WHEN 'p' THEN 'partitioned table' WHEN 'I' THEN 'partitioned index' END as "Type",
          pg_catalog.pg_get_userbyid(c.relowner) as "Owner"
        FROM pg_catalog.pg_class c
             LEFT JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
             LEFT JOIN pg_catalog.pg_am am ON am.oid = c.relam
        WHERE c.relkind IN ('r','p','')
              AND n.nspname <> 'pg_catalog'
              AND n.nspname !~ '^pg_toast'
              AND n.nspname <> 'information_schema'
          AND pg_catalog.pg_table_is_visible(c.oid)
        ORDER BY 1,2;
        """;

    private const string FindTable = """
        SELECT c.oid,
          n.nspname,
          c.relname
        FROM pg_catalog.pg_class c
             LEFT JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relname OPERATOR(pg_catalog.~) '^(people)$' COLLATE pg_catalog.default
          AND pg_catalog.pg_table_is_visible(c.oid)
        ORDER BY 2, 3;
        """;

    private const string TableColumns = """
        SELECT a.attname,
          pg_catalog.format_type(a.atttypid, a.atttypmod),
          (SELECT pg_catalog.pg_get_expr(d.adbin, d.adrelid, true)
           FROM pg_catalog.pg_attrdef d
           WHERE d.adrelid = a.attrelid AND d.adnum = a.attnum AND a.atthasdef),
          a.attnotnull,
          (SELECT c.collname FROM pg_catalog.pg_collation c, pg_catalog.pg_type t
           WHERE c.oid = a.attcollation AND t.oid = a.atttypid AND a.attcollation <> t.typcollation) AS attcollation,
          a.attidentity,
          a.attgenerated
        FROM pg_catalog.pg_attribute a
        WHERE a.attrelid = 'OID' AND a.attnum > 0 AND NOT a.attisdropped
        ORDER BY a.attnum;
        """;

    private static readonly Faker Faker = new();

    [Test]
    public async Task Npgsql_with_default_settings_creates_tables_and_answers_session_queries()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key) = await DatabaseAsync(api, "documents:read", "documents:write");
        await using var source = Source(api, key, database.Name);
        await using var connection = await source.OpenConnectionAsync();

        await ExecuteAsync(connection, People);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, People));
        var session = await ReadAsync(connection, "SELECT current_database(), current_schema(), current_user, version()");
        await ExecuteAsync(connection, "SET application_name = 'wire test'");
        var shown = await ReadAsync(connection, "SHOW application_name");
        var unsupported = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, "SELECT relname FROM pg_catalog.pg_class"));
        var values = await ReadAsync(connection, "SELECT 40 + 2, '2.5'::float8, upper('xy'), count(*) FROM people WHERE name ILIKE 'A%'");
        var missing = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, "SELECT * FROM nowhere"));

        await Assert.That(connection.PostgreSqlVersion).IsEqualTo(new Version(16, 0));
        await Assert.That(duplicate!.SqlState).IsEqualTo("42P07");
        await Assert.That(session[0]).IsEquivalentTo([database.Name, "public", "wire", "PostgreSQL 16.0 (TerraBase)"]);
        await Assert.That(shown[0][0]).IsEqualTo("wire test");
        await Assert.That(unsupported!.SqlState).IsEqualTo("0A000");
        await Assert.That(values[0]).IsEquivalentTo(["42", "2.5", "XY", "0"]);
        await Assert.That(missing!.SqlState).IsEqualTo("42P01");
    }

    [Test]
    public async Task Tables_named_pg_something_are_ordinary_tables_and_postgres_only_commands_answer_0A000()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key) = await DatabaseAsync(api, "documents:read", "documents:write");
        await using var source = Source(api, key, database.Name);
        await using var connection = await source.OpenConnectionAsync();

        await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS pg_orders (id INTEGER PRIMARY KEY, label TEXT)");
        await ExecuteAsync(connection, "INSERT INTO pg_orders (id, label) VALUES ($1, $2)", 7, "seven");
        var rows = await ReadAsync(connection, "SELECT o.id, o.label FROM pg_orders o JOIN pg_orders p ON p.id = o.id");
        var catalog = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, "SELECT typname FROM pg_type"));
        var listen = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, "LISTEN ch"));
        var extension = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, "CREATE EXTENSION IF NOT EXISTS pgcrypto"));

        await Assert.That(rows[0]).IsEquivalentTo(["7", "seven"]);
        await Assert.That(catalog!.SqlState).IsEqualTo("0A000");
        await Assert.That(listen!.SqlState).IsEqualTo("0A000");
        await Assert.That(extension!.SqlState).IsEqualTo("0A000");
    }

    [Test]
    public async Task Index_statements_run_and_a_unique_index_answers_unique_violation()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key) = await DatabaseAsync(api, "documents:read", "documents:write");
        await using var source = Source(api, key, database.Name);
        await using var connection = await source.OpenConnectionAsync();
        await ExecuteAsync(connection, People);

        await ExecuteAsync(connection, "CREATE INDEX people_team ON people (team)");
        await ExecuteAsync(connection, "CREATE UNIQUE INDEX people_name ON people (name)");
        await ExecuteAsync(connection, "INSERT INTO people (name) VALUES ($1)", "ada");
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, "INSERT INTO people (name) VALUES ($1)", "ada"));
        await ExecuteAsync(connection, "DROP INDEX people_name");
        await ExecuteAsync(connection, "DROP INDEX IF EXISTS people_team");
        await ExecuteAsync(connection, "INSERT INTO people (name) VALUES ($1)", "ada");

        await Assert.That(duplicate!.SqlState).IsEqualTo("23505");
        await Assert.That((await ReadAsync(connection, "SELECT count(*) FROM people"))[0][0]).IsEqualTo("2");
    }

    [Test]
    public async Task Read_only_keys_cannot_write()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key) = await DatabaseAsync(api, "documents:read");
        await using var source = Source(api, key, database.Name);
        await using var connection = await source.OpenConnectionAsync();

        var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, People));

        await Assert.That(refused!.SqlState).IsEqualTo("42501");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Dashboard_database_restrictions_can_connect_by_id_or_name(bool idRoute)
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        var key = DashboardKey(api, database, "documents:read documents:write", database.Id);
        await using var source = Source(api, key, idRoute ? database.Id : database.Name);
        await using var connection = await source.OpenConnectionAsync();
        await ExecuteAsync(connection, People);
        await ExecuteAsync(connection, "INSERT INTO people (name) VALUES ($1)", Faker.Name.FullName());
        await Assert.That((await ReadAsync(connection, "SELECT count(*) FROM people"))[0][0]).IsEqualTo("1");
    }

    [Test]
    public async Task Dashboard_api_keys_are_passwords_within_their_scopes_and_database()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        var writer = DashboardKey(api, database, "documents:read documents:write", database.Id);
        var reader = DashboardKey(api, database, "documents:read", database.Id);
        var elsewhere = DashboardKey(api, database, "documents:read documents:write", EntityIds.New().ToString());
        var unknown = EntityIds.New() + "_" + Faker.Random.String2(52, "0123456789abcdefghjkmnpqrstvwxyz");

        await using (var source = Source(api, writer, database.Name))
        {
            await using var connection = await source.OpenConnectionAsync();
            await ExecuteAsync(connection, People);
            await ExecuteAsync(connection, "INSERT INTO people (name) VALUES ($1)", Faker.Name.FullName());
        }

        var exchanged = api.Issuer.TokenRequests;
        await using var again = Source(api, writer, $"{database.Project}/{database.Name}");
        await using var reused = await again.OpenConnectionAsync();
        var count = await ReadAsync(reused, "SELECT count(*) FROM people");
        var reExchanged = api.Issuer.TokenRequests - exchanged;
        await using var readOnly = Source(api, reader, database.Name);
        await using var reading = await readOnly.OpenConnectionAsync();
        var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(reading, "DELETE FROM people"));
        var wrongDatabase = await Assert.ThrowsAsync<PostgresException>(async () => await (await Source(api, elsewhere, database.Name).OpenConnectionAsync()).DisposeAsync());
        var wrongKey = await Assert.ThrowsAsync<PostgresException>(async () => await (await Source(api, unknown, database.Name).OpenConnectionAsync()).DisposeAsync());

        await Assert.That(count[0][0]).IsEqualTo("1");
        await Assert.That(refused!.SqlState).IsEqualTo("42501");
        await Assert.That(wrongDatabase!.SqlState).IsEqualTo("28P01");
        await Assert.That(wrongKey!.SqlState).IsEqualTo("28P01");
        // Later connections and statements reuse the writer's grant until its token nears expiry.
        await Assert.That(reExchanged).IsEqualTo(0);
    }

    [Test]
    public async Task Psql_runs_statements_in_order_and_describes_tables()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key) = await DatabaseAsync(api, "documents:read", "documents:write");
        await using var psql = await PostgresWireClient.ConnectAsync(api.Server.PostgresPort, key, database.Name);

        var script = await psql.QueryAsync($"{People}; CREATE TABLE people (id INTEGER PRIMARY KEY); CREATE TABLE never (id INTEGER PRIMARY KEY)");
        var deallocated = await psql.QueryAsync("DEALLOCATE ALL");
        var tables = await psql.QueryAsync(ListTables);
        var found = await psql.QueryAsync(FindTable);
        var oid = found.Single(message => message.Type == 'D').Fields[0]!;
        var columns = await psql.QueryAsync(TableColumns.Replace("OID", oid, StringComparison.Ordinal));

        await Assert.That(script.Select(message => $"{message.Type}:{message.Fields[0]}")).IsEquivalentTo(["C:CREATE TABLE", "E:42P07", "Z:I"]);
        await Assert.That(deallocated.Select(message => message.Fields[0]!)).IsEquivalentTo(["DEALLOCATE ALL", "I"]);
        await Assert.That(tables.Where(message => message.Type == 'D').Select(message => string.Join(',', message.Fields))).IsEquivalentTo(["public,people,table,wire"]);
        await Assert.That(columns.Where(message => message.Type == 'D').Select(message => string.Join(',', message.Fields.Take(4)))).IsEquivalentTo(
            ["id,bigint,,t", "name,text,,t", "email,character varying,,f", "team,text,,f"]);
    }

    [Test]
    public async Task An_aborted_transaction_refuses_every_portal_and_rollback_discards_reads_run_at_describe()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key) = await DatabaseAsync(api, "documents:read", "documents:write");
        await using var wire = await PostgresWireClient.ConnectAsync(api.Server.PostgresPort, key, database.Name);
        await wire.QueryAsync(People);
        await wire.SyncAsync(PostgresWireClient.Parse("described", "SELECT name FROM people"), PostgresWireClient.Describe('S', "described"),
            PostgresWireClient.Parse("plain", "SELECT name FROM people"));
        await wire.QueryAsync("BEGIN; SAVEPOINT s; INSERT INTO people (name) VALUES ('ghost')");
        // "inherited" takes its statement's columns; Describe runs the reads of "cached" and "ghost" ahead of Execute.
        var bound = await wire.SyncAsync(PostgresWireClient.Bind("inherited", "described"), PostgresWireClient.Bind("fresh", "plain"),
            PostgresWireClient.Bind("cached", "plain"), PostgresWireClient.Describe('P', "cached"), PostgresWireClient.Bind("ghost", "plain"), PostgresWireClient.Describe('P', "ghost"));
        var failed = await wire.QueryAsync("INSERT INTO people (name) VALUES (NULL)");
        List<string> refused = [Tags(await wire.QueryAsync("SELECT oid FROM pg_type AS typ JOIN pg_namespace ns ON ns.oid = typnamespace"))];
        foreach (var message in new (char, byte[])[] { PostgresWireClient.Parse("again", "SELECT 1"), PostgresWireClient.Bind("rebound", "plain"), PostgresWireClient.Describe('S', "described"),
            PostgresWireClient.Describe('S', "plain"), PostgresWireClient.Describe('P', "inherited"), PostgresWireClient.Describe('P', "cached"), PostgresWireClient.Describe('P', "fresh"),
            PostgresWireClient.Execute("cached"), ('E', [.. "cached\0"u8, 0, 0, 0, 1]) }) // The last asks for one row.
        {
            refused.Add(Tags(await wire.SyncAsync(message)));
        }

        await wire.QueryAsync("ROLLBACK TO s");
        var recovered = await wire.SyncAsync(PostgresWireClient.Execute("ghost"));
        await wire.QueryAsync("INSERT INTO people (name) VALUES ('kept'); SAVEPOINT t; INSERT INTO people (name) VALUES ('late')");
        await wire.SyncAsync(PostgresWireClient.Bind("late", "plain"), PostgresWireClient.Describe('P', "late"));
        await wire.QueryAsync("ROLLBACK TO t");
        var kept = await wire.SyncAsync(PostgresWireClient.Execute("late"));
        await wire.SyncAsync(PostgresWireClient.Bind("ended", "plain"), PostgresWireClient.Describe('P', "ended"));
        await wire.QueryAsync("ROLLBACK");
        var ended = await wire.SyncAsync(PostgresWireClient.Execute("ended"));

        await Assert.That(Tags(bound)).IsEqualTo("Z:T");
        await Assert.That(Tags(failed)).IsEqualTo("E:23502,Z:E");
        await Assert.That(refused).IsEquivalentTo(Enumerable.Repeat("E:25P02,Z:E", 10));
        await Assert.That(Tags(recovered)).IsEqualTo("C:SELECT 0,Z:T");
        await Assert.That(Tags(kept)).IsEqualTo("D:kept,C:SELECT 1,Z:T");
        await Assert.That(Tags(ended)).IsEqualTo("C:SELECT 0,Z:I");
    }

    // Needs SQ1-c (SELECT with joins) and SQ1-d (INSERT/UPDATE/DELETE).
    [Test]
    public async Task Rows_are_written_with_parameters_and_read_with_joins()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key) = await DatabaseAsync(api, "documents:read", "documents:write");
        await using var source = Source(api, key, database.Name);
        await using var connection = await source.OpenConnectionAsync();
        await ExecuteAsync(connection, People);
        await ExecuteAsync(connection, "CREATE TABLE teams (code TEXT PRIMARY KEY, label TEXT NOT NULL)");
        var (ann, bob) = (Faker.Name.FullName(), Faker.Name.FullName() + " O'Brien");

        var teams = await ExecuteAsync(connection, "INSERT INTO teams (code, label) VALUES ($1, $2), ($3, $4)", "core", "Core", "edge", "Edge");
        var people = await ExecuteAsync(connection, "INSERT INTO people (name, email, team) VALUES ($1, $2, $3), ($4, $5, $6)",
            ann, Faker.Internet.Email(), "core", bob, Faker.Internet.Email(), "edge");
        var updated = await ExecuteAsync(connection, "UPDATE people SET team = $1 WHERE name ILIKE $2", "core", bob.ToUpperInvariant());
        var joined = await ReadAsync(connection, "SELECT p.name, t.label, p.id::text FROM people p JOIN teams t ON t.code = p.team ORDER BY p.name");
        var deleted = await ExecuteAsync(connection, "DELETE FROM people WHERE name = $1", ann);

        await Assert.That((teams, people, updated, deleted)).IsEqualTo((2, 2, 1, 1));
        await Assert.That(joined.Select(row => $"{row[0]}|{row[1]}")).IsEquivalentTo(new[] { $"{ann}|Core", $"{bob}|Core" }.Order(StringComparer.Ordinal));
    }

    // Needs SQ1-d (INSERT and unique constraints).
    [Test]
    public async Task Prepared_statements_run_repeatedly_and_unique_violations_are_23505()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key) = await DatabaseAsync(api, "documents:read", "documents:write");
        await using var source = Source(api, key, database.Name);
        await using var connection = await source.OpenConnectionAsync();
        await ExecuteAsync(connection, People);
        string[] emails = [.. Enumerable.Range(0, 3).Select(i => $"{i}{Faker.Internet.Email()}")];

        await using var insert = new NpgsqlCommand("INSERT INTO people (name, email) VALUES ($1, $2)", connection)
        {
            Parameters = { new() { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text }, new() { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text } },
        };
        await insert.PrepareAsync();
        var inserted = 0;
        foreach (var email in emails)
        {
            (insert.Parameters[0].Value, insert.Parameters[1].Value) = (Faker.Name.FullName(), email);
            inserted += await insert.ExecuteNonQueryAsync();
        }

        insert.Parameters[1].Value = emails[0];
        var conflict = await Assert.ThrowsAsync<PostgresException>(insert.ExecuteNonQueryAsync);
        var count = await ReadAsync(connection, "SELECT count(*) FROM people");

        await Assert.That(inserted).IsEqualTo(3);
        await Assert.That(conflict!.SqlState).IsEqualTo("23505");
        await Assert.That(count[0][0]).IsEqualTo("3");
    }

    [Test]
    public async Task Alter_add_column_refuses_a_prepared_result_whose_width_changed()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key) = await DatabaseAsync(api, "documents:read", "documents:write");
        await using var source = Source(api, key, database.Name);
        await using var connection = await source.OpenConnectionAsync();
        await ExecuteAsync(connection, "CREATE TABLE t (id INTEGER PRIMARY KEY)");
        await ExecuteAsync(connection, "INSERT INTO t VALUES (1)");
        await using var select = new NpgsqlCommand("SELECT * FROM t", connection);
        await select.PrepareAsync();
        await ExecuteAsync(connection, "ALTER TABLE t ADD extra TEXT");
        var changed = await Assert.ThrowsAsync<PostgresException>(select.ExecuteScalarAsync);
        await Assert.That(changed!.SqlState).IsEqualTo("0A000");
        await using var current = new NpgsqlCommand("SELECT id, extra FROM t", connection);
        await using var row = await current.ExecuteReaderAsync();
        await Assert.That(await row.ReadAsync()).IsTrue();
        await Assert.That(row.GetInt64(0)).IsEqualTo(1);
        await Assert.That(row.IsDBNull(1)).IsTrue();
    }

    // Needs SQ1-d (writes) and SQ1-e (transactions).
    [Test]
    public async Task Transactions_commit_roll_back_and_abort_after_an_error()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key) = await DatabaseAsync(api, "documents:read", "documents:write");
        await using var source = Source(api, key, database.Name);
        await using var connection = await source.OpenConnectionAsync();
        await ExecuteAsync(connection, People);
        var (kept, discarded) = (Faker.Name.FullName(), Faker.Name.FullName());

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, "INSERT INTO people (name) VALUES ($1)", kept);
            await transaction.CommitAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, "INSERT INTO people (name) VALUES ($1)", discarded);
            await transaction.RollbackAsync();
        }

        PostgresException? failed, aborted;
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            failed = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, "INSERT INTO people (name) VALUES (NULL)"));
            aborted = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, "SELECT 1"));
            await transaction.RollbackAsync();
        }

        var names = await ReadAsync(connection, "SELECT name FROM people");

        await Assert.That(failed!.SqlState).IsEqualTo("23502");
        await Assert.That(aborted!.SqlState).IsEqualTo("25P02");
        await Assert.That(names.Select(row => row[0])).IsEquivalentTo([kept]);
    }

    [Test]
    public async Task Wrong_keys_unknown_databases_and_unreached_databases_fail_alike()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key) = await DatabaseAsync(api, "documents:read");
        var other = await api.CreateManagedDatabaseAsync();
        var otherKey = await api.Server.IssueNarrowedAsync(other.Organization, other.Project, null, "documents:read");

        PostgresException?[] failures =
        [
            await OpenFailureAsync(api, "tb_" + Faker.Random.AlphaNumeric(40), database.Name),
            await OpenFailureAsync(api, key, "missing"),
            await OpenFailureAsync(api, otherKey, $"{database.Project}/{database.Name}"),
        ];

        foreach (var failure in failures)
        {
            await Assert.That(failure!.SqlState).IsEqualTo("28P01");
            await Assert.That(failure.MessageText).IsEqualTo("password authentication failed");
        }
    }

    [Test]
    public async Task Driver_startup_settings_and_returning_work_without_client_overrides()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key) = await DatabaseAsync(api, "documents:read", "documents:write");
        await using var source = Source(api, key, database.Name);
        await using var connection = await source.OpenConnectionAsync();

        // SQLAlchemy's default dialect initialization and pgcli's timezone startup query.
        var isolation = await ReadAsync(connection, "show transaction isolation level");
        var spaced = await ReadAsync(connection, "SHOW TRANSACTION\tISOLATION  LEVEL");
        var zone = await ReadAsync(connection, "show time zone");
        var conforming = await ReadAsync(connection, "show standard_conforming_strings");
        await Assert.That(isolation[0][0]).IsEqualTo("serializable");
        await Assert.That(spaced[0][0]).IsEqualTo("serializable");
        await Assert.That(zone[0][0]).IsEqualTo("UTC");
        await Assert.That(conforming[0][0]).IsEqualTo("on");

        await ExecuteAsync(connection, "CREATE TABLE driver_rows (id TEXT PRIMARY KEY, value TEXT NOT NULL)");
        await using var existence = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM driver_rows) AS found", connection);
        await existence.PrepareAsync();
        await Assert.That((bool)(await existence.ExecuteScalarAsync())!).IsFalse();
        await using var insert = new NpgsqlCommand("INSERT INTO driver_rows (id, value) VALUES ($1, $2) RETURNING id, value", connection);
        insert.Parameters.AddWithValue("a");
        insert.Parameters.AddWithValue("before");
        await insert.PrepareAsync();
        await using (var reader = await insert.ExecuteReaderAsync())
        {
            await Assert.That(await reader.ReadAsync()).IsTrue();
            await Assert.That(reader.GetString(0)).IsEqualTo("a");
            await Assert.That(reader.GetString(1)).IsEqualTo("before");
            await Assert.That(await reader.ReadAsync()).IsFalse();
        }

        await ExecuteAsync(connection, "BEGIN");
        await Assert.That((bool)(await existence.ExecuteScalarAsync())!).IsTrue();
        var updated = await ReadAsync(connection, "UPDATE driver_rows SET value = 'after' WHERE id = 'a' RETURNING value");
        await Assert.That(updated[0][0]).IsEqualTo("after");
        await ExecuteAsync(connection, "ROLLBACK");
        var persisted = await ReadAsync(connection, "SELECT value FROM driver_rows WHERE id = 'a'");
        await Assert.That(persisted[0][0]).IsEqualTo("before");
        var deleted = await ReadAsync(connection, "DELETE FROM driver_rows WHERE id = 'a' RETURNING id");
        await Assert.That(deleted[0][0]).IsEqualTo("a");

        await ExecuteAsync(connection, "CREATE TABLE bad_dates (v DATE)");
        await ExecuteAsync(connection, "INSERT INTO bad_dates VALUES ('2023-02-29')");
        await using var simple = await PostgresWireClient.ConnectAsync(api.Server.PostgresPort, key, database.Name);
        var invalid = await simple.QueryAsync("SELECT v FROM bad_dates");
        await Assert.That(invalid.Single(message => message.Type == 'E').Fields[0]).IsEqualTo("22P02");
        var recovery = await simple.QueryAsync("SELECT 1");
        await Assert.That(recovery.Single(message => message.Type == 'D').Fields[0]).IsEqualTo("1");
    }
    private static string Tags(List<(char Type, string?[] Fields)> messages) => string.Join(',', messages.Select(message => $"{message.Type}:{message.Fields[0]}"));

    [Test]
    [Arguments("main@")]
    [Arguments("main@@gru")]
    [Arguments("main@GRU")]
    public async Task Malformed_database_pins_fail_closed_before_key_lookup(string database)
    {
        await using var api = await ManagementApi.StartAsync();
        var failure = await OpenFailureAsync(api, "invalid", database);
        await Assert.That(failure!.SqlState).IsEqualTo("08004");
        await Assert.That(failure.MessageText).StartsWith("misdirected_request:");
        await Assert.That(failure.MessageText).DoesNotContain("database regions:");
    }

    private static async Task<PostgresException?> OpenFailureAsync(ManagementApi api, string key, string database)
    {
        await using var source = Source(api, key, database);
        return await Assert.ThrowsAsync<PostgresException>(async () => await source.OpenConnectionAsync());
    }

    private static async Task<List<string[]>> ReadAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string[]>();
        while (await reader.ReadAsync())
        {
            rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!)]);
        }

        return rows;
    }

    // A ts-auth project API key, as the dashboard issues it, restricted to one database.
    private static string DashboardKey(ManagementApi api, ManagedDatabaseRef database, string scope, string restricted)
    {
        var id = EntityIds.New().ToString();
        var key = id + "_" + Faker.Random.String2(52, "0123456789abcdefghjkmnpqrstvwxyz");
        api.Issuer.GrantClient(key, () => api.Issuer.Mint(id, token =>
        {
            token.Claims["key_id"] = id;
            token.Claims["key_gen"] = TestIssuer.KeyGeneration(key);
            token.Claims["project_id"] = database.Project;
            token.Claims["database"] = restricted;
            token.Claims["scope"] = scope;
        }));
        return key;
    }

    private static async Task<(ManagedDatabaseRef Database, string Key)> DatabaseAsync(ManagementApi api, params string[] scopes)
    {
        var database = await api.CreateManagedDatabaseAsync();
        return (database, await api.Server.IssueNarrowedAsync(database.Organization, database.Project, null, scopes));
    }
}
