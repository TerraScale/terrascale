using System.Globalization;
using Npgsql;

namespace TerraBase.Testing;

internal static class PostgresSql
{
    // Npgsql defaults except TLS: the platform edge terminates it, the listener speaks plaintext.
    internal static NpgsqlDataSource Source(ManagementApi api, string key, string database) => NpgsqlDataSource.Create(
        new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = api.Server.PostgresPort,
            Username = "wire",
            Password = key,
            Database = database,
            SslMode = SslMode.Disable,
        }.ConnectionString);

    internal static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return await command.ExecuteNonQueryAsync();
    }

    internal static async Task<NpgsqlDataSource> SourceAsync(ManagementApi api)
    {
        var database = await api.CreateManagedDatabaseAsync();
        var key = await api.Server.IssueNarrowedAsync(database.Organization, database.Project, null, "documents:read", "documents:write");
        return Source(api, key, database.Name);
    }

    internal static async Task<string?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    internal static async Task StateAsync(NpgsqlConnection connection, string sql, string expected)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, sql));
        await Assert.That(error!.SqlState).IsEqualTo(expected);
    }
}
