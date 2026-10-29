using System.Net;
using System.Net.Sockets;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using TerraBase.Testing;

namespace TerraBase.Fast.Postgres;

public sealed class PostgresTemporalTests
{
    [Test]
    public async Task Native_temporal_parameters_and_binary_results_preserve_microseconds_and_nulls()
    {
        await using var api = await ManagementApi.StartAsync();
        await using var connection = await ConnectAsync(api);
        await using var create = new NpgsqlCommand("CREATE TABLE moments (id BIGINT, d DATE, t TIMESTAMP, z TIMESTAMPTZ)", connection);
        await create.ExecuteNonQueryAsync();
        await using var insert = new NpgsqlCommand("INSERT INTO moments VALUES ($1, $2, $3, $4)", connection);
        foreach (var type in new[] { NpgsqlDbType.Integer, NpgsqlDbType.Date, NpgsqlDbType.Timestamp, NpgsqlDbType.TimestampTz })
        {
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = type });
        }

        await insert.PrepareAsync();
        DateTime[] clocks = [new(1, 1, 2), new DateTime(1999, 12, 31, 23, 59, 59).AddTicks(9999990),
            new(2000, 1, 1), new DateTime(2024, 2, 29, 12, 34, 56).AddTicks(1234560), new(9999, 12, 30)];
        for (var i = 0; i <= clocks.Length; i++)
        {
            insert.Parameters[0].Value = i;
            insert.Parameters[1].Value = i < clocks.Length ? DateOnly.FromDateTime(clocks[i]) : DBNull.Value;
            insert.Parameters[2].Value = i < clocks.Length ? clocks[i] : DBNull.Value;
            insert.Parameters[3].Value = i < clocks.Length ? DateTime.SpecifyKind(clocks[i], DateTimeKind.Utc) : DBNull.Value;
            await insert.ExecuteNonQueryAsync();
        }

        await using var query = new NpgsqlCommand("SELECT id, d, t, z FROM moments ORDER BY id", connection);
        await using var reader = await query.ExecuteReaderAsync();
        var row = 0;
        foreach (var clock in clocks)
        {
            await Assert.That(await reader.ReadAsync()).IsTrue();
            await Assert.That(reader.GetInt64(0)).IsEqualTo((long)row++);
            await Assert.That(reader.GetFieldValue<DateOnly>(1)).IsEqualTo(DateOnly.FromDateTime(clock));
            await Assert.That(reader.GetDateTime(2).Ticks).IsEqualTo(clock.Ticks);
            await Assert.That(reader.GetDateTime(2).Kind).IsEqualTo(DateTimeKind.Unspecified);
            await Assert.That(reader.GetDateTime(3).Ticks).IsEqualTo(clock.Ticks);
            await Assert.That(reader.GetDateTime(3).Kind).IsEqualTo(DateTimeKind.Utc);
        }

        await Assert.That(await reader.ReadAsync()).IsTrue();
        await Assert.That(reader.IsDBNull(1) && reader.IsDBNull(2) && reader.IsDBNull(3)).IsTrue();
        await Assert.That(await reader.ReadAsync()).IsFalse();
    }

    private static async Task<NpgsqlConnection> ConnectAsync(ManagementApi api)
    {
        var database = await api.CreateManagedDatabaseAsync();
        var key = await api.Server.IssueNarrowedAsync(database.Organization, database.Project, null, "documents:read", "documents:write");
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = api.Server.PostgresPort,
            Username = "wire",
            Password = key,
            Database = database.Name,
            SslMode = SslMode.Disable,
            Pooling = false,
        }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
