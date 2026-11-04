using Bogus;
using Npgsql;
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
        await using var api = await ManagementApi.StartSharedAsync();
        var (database, key) = await DatabaseAsync(api, "sql:read", "sql:write");
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
    public async Task Psql_runs_statements_in_order_and_describes_tables()
    {
        await using var api = await ManagementApi.StartSharedAsync();
        var (database, key) = await DatabaseAsync(api, "sql:read", "sql:write");
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
            ["id,integer,,t", "name,text,,t", "email,character varying,,f", "team,text,,f"]);
    }

    // Needs SQ1-d (writes) and SQ1-e (transactions).
    [Test]
    public async Task Transactions_commit_roll_back_and_abort_after_an_error()
    {
        await using var api = await ManagementApi.StartSharedAsync();
        var (database, key) = await DatabaseAsync(api, "sql:read", "sql:write");
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
        await using var api = await ManagementApi.StartSharedAsync();
        var (database, key) = await DatabaseAsync(api, "sql:read");
        var other = await api.CreateManagedDatabaseAsync();
        var otherKey = await api.Server.IssueNarrowedAsync(other.Organization, other.Project, null, "sql:read");

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

    private static async Task<(ManagedDatabaseRef Database, string Key)> DatabaseAsync(ManagementApi api, params string[] scopes)
    {
        var database = await api.CreateManagedDatabaseAsync();
        return (database, await api.Server.IssueNarrowedAsync(database.Organization, database.Project, null, scopes));
    }
}
