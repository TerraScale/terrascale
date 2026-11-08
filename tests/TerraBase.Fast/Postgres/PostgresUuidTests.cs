using Npgsql;
using TerraBase.Testing;
using static TerraBase.Testing.PostgresSql;

namespace TerraBase.Fast.Postgres;

public sealed class PostgresUuidTests
{
    [Test]
    public async Task Prepared_statement_binds_and_returns_binary_uuid()
    {
        await using var api = await ManagementApi.StartSharedAsync();
        var (database, key) = await DatabaseAsync(api, "sql:read", "sql:write");
        await using var source = Source(api, key, database.Name);
        await using var connection = await source.OpenConnectionAsync();
        await ExecuteAsync(connection, "CREATE TABLE things (id UUID PRIMARY KEY, body TEXT)");
        var (one, two) = (Guid.NewGuid(), Guid.NewGuid());

        await using (var insert = new NpgsqlCommand("INSERT INTO things (id, body) VALUES ($1, $2)", connection))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = one });
            insert.Parameters.Add(new NpgsqlParameter { Value = "first" });
            await insert.PrepareAsync();
            await insert.ExecuteNonQueryAsync();
        }

        await using (var select = new NpgsqlCommand("SELECT id FROM things WHERE id = $1", connection))
        {
            select.Parameters.Add(new NpgsqlParameter { Value = one });
            await select.PrepareAsync();
            await using var reader = await select.ExecuteReaderAsync();
            var found = new List<Guid>();
            while (await reader.ReadAsync())
            {
                found.Add(reader.GetGuid(0));
            }

            await Assert.That(found).IsEquivalentTo([one]);
        }

        await using (var insert = new NpgsqlCommand("INSERT INTO things (id, body) VALUES ($1, $2)", connection))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = two });
            insert.Parameters.Add(new NpgsqlParameter { Value = "second" });
            await insert.PrepareAsync();
            await insert.ExecuteNonQueryAsync();
        }

        await using (var select = new NpgsqlCommand("SELECT id, body FROM things ORDER BY body", connection))
        {
            await select.PrepareAsync();
            await using var reader = await select.ExecuteReaderAsync();
            var found = new List<(Guid, string)>();
            while (await reader.ReadAsync())
            {
                found.Add((reader.GetGuid(0), reader.GetString(1)));
            }

            await Assert.That(found).IsEquivalentTo([(one, "first"), (two, "second")]);
        }
    }

    private static async Task<(ManagedDatabaseRef Database, string Key)> DatabaseAsync(ManagementApi api, params string[] scopes)
    {
        var database = await api.CreateManagedDatabaseAsync();
        return (database, await api.Server.IssueNarrowedAsync(database.Organization, database.Project, null, scopes));
    }
}
