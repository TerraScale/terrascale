using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Bogus;
using Npgsql;
using TerraBase.Primitives;
using TerraBase.Testing;

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
    public async Task Dashboard_api_keys_are_passwords_within_their_scopes_and_database()
    {
        await using var api = await ManagementApi.StartAsync();
        var database = await api.CreateManagedDatabaseAsync();
        await api.VerifyProjectBindingAsync(database.Organization, database.Project);
        var writer = DashboardKey(api, database, "documents:read documents:write", database.Name);
        var reader = DashboardKey(api, database, "documents:read", database.Name);
        var elsewhere = DashboardKey(api, database, "documents:read documents:write", "other");
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
        await using var psql = await RawClient.ConnectAsync(api.Server.PostgresPort, key, database.Name);

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

    private static async Task<PostgresException?> OpenFailureAsync(ManagementApi api, string key, string database)
    {
        await using var source = Source(api, key, database);
        return await Assert.ThrowsAsync<PostgresException>(async () => await source.OpenConnectionAsync());
    }

    // Npgsql defaults except TLS: the platform edge terminates it, the listener speaks plaintext.
    private static NpgsqlDataSource Source(ManagementApi api, string key, string database) => NpgsqlDataSource.Create(
        new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = api.Server.PostgresPort,
            Username = "wire",
            Password = key,
            Database = database,
            SslMode = SslMode.Disable,
        }.ConnectionString);

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return await command.ExecuteNonQueryAsync();
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

    // The simple-query protocol as psql speaks it: one Query message, every response until ReadyForQuery.
    internal sealed class RawClient(TcpClient tcp, NetworkStream stream) : IAsyncDisposable
    {
        public static async Task<RawClient> ConnectAsync(int port, string key, string database)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port);
            var client = new RawClient(tcp, tcp.GetStream());
            await client.SendAsync(null, [0, 3, 0, 0, .. CString("user"), .. CString("wire"), .. CString("database"), .. CString(database), 0]);
            await client.ReadAsync(); // AuthenticationCleartextPassword
            await client.SendAsync('p', CString(key));
            while ((await client.ReadAsync()).Type != 'Z')
            {
            }

            return client;
        }

        public async Task<List<(char Type, string?[] Fields)>> QueryAsync(string sql)
        {
            await SendAsync('Q', CString(sql));
            var messages = new List<(char Type, string?[] Fields)>();
            for (var message = await ReadAsync(); ; message = await ReadAsync())
            {
                var body = message.Body;
                messages.Add((message.Type, message.Type switch
                {
                    'D' => Values(body),
                    'E' => [Encoding.UTF8.GetString(body).Split('\0').First(field => field.StartsWith('C'))[1..]],
                    'C' => [Encoding.UTF8.GetString(body).TrimEnd('\0')],
                    'Z' => [((char)body[0]).ToString()],
                    _ => [null],
                }));
                if (message.Type == 'Z')
                {
                    return [.. messages.Where(entry => entry.Type is 'D' or 'E' or 'C' or 'Z')];
                }
            }
        }

        // Parse, bind and describe a portal, then either close it or execute with the requested row limit.
        public async Task<List<char>> PortalAsync(string sql, int? maxRows)
        {
            await SendAsync('P', [0, .. CString(sql), 0, 0]);
            await SendAsync('B', [0, 0, 0, 0, 0, 0, 0, 0]);
            await SendAsync('D', [(byte)'P', 0]);
            if (maxRows is { } limit)
            {
                var count = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(count, limit);
                await SendAsync('E', [0, .. count]);
            }
            else
            {
                await SendAsync('C', [(byte)'P', 0]);
            }

            await SendAsync('S', []);
            var messages = new List<char>();
            do
            {
                messages.Add((await ReadAsync()).Type);
            }
            while (messages[^1] != 'Z');

            return messages;
        }

        public async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync();
            tcp.Dispose();
        }

        private static string?[] Values(byte[] body)
        {
            var values = new string?[BinaryPrimitives.ReadInt16BigEndian(body)];
            for (int i = 0, at = 2; i < values.Length; i++)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(at));
                values[i] = length < 0 ? null : Encoding.UTF8.GetString(body, at + 4, length);
                at += 4 + Math.Max(0, length);
            }

            return values;
        }

        private static byte[] CString(string value) => [.. Encoding.UTF8.GetBytes(value), 0];

        private async Task SendAsync(char? type, byte[] body)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, body.Length + 4);
            await stream.WriteAsync((byte[])[.. type is { } t ? new[] { (byte)t } : [], .. length, .. body]);
        }

        private async Task<(char Type, byte[] Body)> ReadAsync()
        {
            var header = new byte[5];
            await stream.ReadExactlyAsync(header);
            var body = new byte[BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1)) - 4];
            await stream.ReadExactlyAsync(body);
            return ((char)header[0], body);
        }
    }
}
