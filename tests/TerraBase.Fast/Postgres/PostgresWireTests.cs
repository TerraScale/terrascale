using System.Text.Json;
using Bogus;
using Npgsql;
using TerraBase.Testing;

namespace TerraBase.Fast.Postgres;

public sealed class PostgresWireTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Npgsql_with_default_settings_authenticates_with_an_api_key_and_reads_documents()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key, names) = await SeedAsync(api);
        await using var source = Source(api, key, database.Name);
        await using var connection = await source.OpenConnectionAsync();

        var rows = await ReadAsync(connection, "SELECT name, age FROM people ORDER BY age");
        var version = await ReadAsync(connection, "SELECT version()");
        await using (var set = new NpgsqlCommand("SET application_name = 'wire test'", connection))
        {
            await set.ExecuteNonQueryAsync();
        }

        var shown = await ReadAsync(connection, "SHOW application_name");

        await Assert.That(connection.PostgreSqlVersion).IsEqualTo(new Version(16, 0));
        await Assert.That(rows.Select(row => ManagementApi.DocumentName(row[0]))).IsEquivalentTo(["p1", "p2", "p3"]);
        await Assert.That(rows.Select(row => JsonElement.Parse(row[1]).GetProperty("name").GetString()!)).IsEquivalentTo(names);
        await Assert.That(version[0][0]).IsEqualTo("PostgreSQL 16.0 (TerraBase)");
        await Assert.That(shown[0][0]).IsEqualTo("wire test");
    }

    [Test]
    public async Task Parameters_bind_as_literals_and_unsupported_statements_leave_the_session_usable()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key, _) = await SeedAsync(api, tricky: @"O'Brien \' OR 1=1 --");
        await using var source = Source(api, key, database.Name);
        await using var connection = await source.OpenConnectionAsync();

        await using var query = new NpgsqlCommand("SELECT _id FROM people WHERE name = $1 AND age >= $2 AND active = $3", connection)
        {
            Parameters = { new() { Value = @"O'Brien \' OR 1=1 --" }, new() { Value = 20 }, new() { Value = true } },
        };
        var matched = new List<string>();
        await using (var reader = await query.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                matched.Add(reader.GetString(0));
            }
        }

        var insert = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, "INSERT INTO people VALUES (1)"));
        var begin = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, "BEGIN"));
        var syntax = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, "SELECT * FROM"));
        var after = await ReadAsync(connection, "SELECT _id FROM people WHERE age = 40");

        await Assert.That(matched.Select(ManagementApi.DocumentName)).IsEquivalentTo(["p2"]);
        await Assert.That(insert!.SqlState).IsEqualTo("0A000");
        await Assert.That(insert.MessageText).Contains("not supported yet");
        await Assert.That(begin!.SqlState).IsEqualTo("0A000");
        await Assert.That(syntax!.SqlState).IsEqualTo("42601");
        await Assert.That(after.Select(row => ManagementApi.DocumentName(row[0]))).IsEquivalentTo(["p3"]);
    }

    [Test]
    public async Task Wrong_keys_unknown_databases_and_unreached_databases_fail_alike()
    {
        await using var api = await ManagementApi.StartAsync();
        var (database, key, _) = await SeedAsync(api);
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

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string[]>> ReadAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string[]>();
        while (await reader.ReadAsync())
        {
            rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(reader.GetString)]);
        }

        return rows;
    }

    // Three people aged 20, 30 and 40; p2 has the tricky name and p3 a lookalike, both active.
    private static async Task<(ManagedDatabaseRef Database, string Key, string[] Names)> SeedAsync(ManagementApi api, string? tricky = null)
    {
        var database = await api.CreateManagedDatabaseAsync();
        var key = await api.Server.IssueNarrowedAsync(database.Organization, database.Project, null, "documents:read", "documents:write");
        using var data = api.DataClient(key);
        await ManagementApi.CreateCollectionAsync(data, database, "people");
        string[] names = [Faker.Name.FullName(), tricky ?? Faker.Name.FullName(), tricky is null ? Faker.Name.FullName() : tricky.Replace("'", "", StringComparison.Ordinal)];
        for (var i = 0; i < names.Length; i++)
        {
            (await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", $"p{i + 1}")), new { name = names[i], age = 20 + i * 10, active = i > 0 }))
                .EnsureSuccessStatusCode();
        }

        return (database, key, names);
    }
}
