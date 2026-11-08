using Npgsql;
using NpgsqlTypes;
using TerraBase.Testing;
using static TerraBase.Testing.PostgresSql;

namespace TerraBase.Fast.Postgres;

public sealed class PostgresNumericTests
{
    // Npgsql binds numeric parameters in binary and asks for binary results by default (ku TS-1306).
    [Test]
    public async Task Prepared_statement_binds_and_returns_binary_numeric()
    {
        await using var api = await ManagementApi.StartSharedAsync();
        var (database, key) = await DatabaseAsync(api, "sql:read", "sql:write");
        await using var source = Source(api, key, database.Name);
        await using var connection = await source.OpenConnectionAsync();
        await ExecuteAsync(connection, "CREATE TABLE t (id INTEGER PRIMARY KEY, amount NUMERIC, big TEXT)");
        var values = new[] { 42m, -0.5m };

        for (var i = 0; i < values.Length; i++)
        {
            await using (var insert = new NpgsqlCommand("INSERT INTO t (id, amount) VALUES ($1, $2)", connection))
            {
                insert.Parameters.Add(new NpgsqlParameter { Value = i + 1 });
                insert.Parameters.Add(new NpgsqlParameter { Value = values[i] });
                await insert.PrepareAsync();
                await insert.ExecuteNonQueryAsync();
            }
        }

        await using (var insert = new NpgsqlCommand("INSERT INTO t (id, big) VALUES ($1, $2)", connection))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = values.Length + 1 });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Numeric, Value = 12345678901234567890.123456789m });
            await insert.PrepareAsync();
            await insert.ExecuteNonQueryAsync();
        }

        await using (var select = new NpgsqlCommand("SELECT amount FROM t WHERE amount IS NOT NULL ORDER BY id", connection))
        {
            await select.PrepareAsync();
            await using var reader = await select.ExecuteReaderAsync();
            var found = new List<decimal>();
            while (await reader.ReadAsync())
            {
                found.Add(reader.GetDecimal(0));
            }

            await Assert.That(found).IsEquivalentTo(values);
        }

        await using (var select = new NpgsqlCommand("SELECT big FROM t WHERE big IS NOT NULL ORDER BY id", connection))
        {
            await select.PrepareAsync();
            await using var reader = await select.ExecuteReaderAsync();
            var found = new List<decimal>();
            while (await reader.ReadAsync())
            {
                found.Add(reader.GetDecimal(0));
            }

            await Assert.That(found).IsEquivalentTo([12345678901234567890.123456789m]);
        }
    }

    private static async Task<(ManagedDatabaseRef Database, string Key)> DatabaseAsync(ManagementApi api, params string[] scopes)
    {
        var database = await api.CreateManagedDatabaseAsync();
        return (database, await api.Server.IssueNarrowedAsync(database.Organization, database.Project, null, scopes));
    }
}
