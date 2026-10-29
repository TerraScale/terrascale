using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TerraBase.Testing;

namespace TerraBase.Fast.Sql;

internal sealed class SqlNativeDatabase : IAsyncDisposable
{
    private SqlNativeDatabase(ManagementApi api, ManagedDatabaseRef database, string writer, string reader)
    {
        Api = api;
        Database = database;
        Writer = api.DataClient(writer);
        Reader = api.DataClient(reader);
        Postgres = PostgresSql.Source(api, writer, database.Name);
        ReadOnlyPostgres = PostgresSql.Source(api, reader, database.Name);
    }

    public ManagementApi Api { get; }

    public ManagedDatabaseRef Database { get; }

    public HttpClient Writer { get; }

    public HttpClient Reader { get; }

    public NpgsqlDataSource Postgres { get; }

    public NpgsqlDataSource ReadOnlyPostgres { get; }

    public static async Task<SqlNativeDatabase> StartAsync(Action<IServiceCollection>? services = null)
    {
        var api = await ManagementApi.StartAsync(services: services);
        try
        {
            var database = await api.CreateManagedDatabaseAsync();
            var writer = await api.Server.IssueNarrowedAsync(database.Organization, database.Project, null, "documents:read", "documents:write");
            var reader = await api.Server.IssueNarrowedAsync(database.Organization, database.Project, null, "documents:read");
            return new SqlNativeDatabase(api, database, writer, reader);
        }
        catch
        {
            await api.DisposeAsync();
            throw;
        }
    }

    public static object Sql(string sql, params object?[] args) => new { sql, args };

    public static object Named(string sql, Dictionary<string, object?> named) => new { sql, named };

    public static object Blob(byte[] bytes) => new { base64 = Convert.ToBase64String(bytes) };

    public async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(HttpClient client, params object[] statements)
    {
        using var response = await client.PostAsJsonAsync(ManagementApi.Uri(Database.Data + "/sql/execute"), new { statements });
        return (response.StatusCode, JsonElement.Parse(await response.Content.ReadAsStringAsync()));
    }

    public async Task<JsonElement[]> OkAsync(params object[] statements)
    {
        var (status, body) = await PostAsync(Writer, statements);
        await Assert.That(status).IsEqualTo(HttpStatusCode.OK).Because(body.GetRawText());
        return [.. body.GetProperty("results").EnumerateArray()];
    }

    public Task<JsonElement[]> OkAsync(params string[] statements) => OkAsync([.. statements.Select(sql => Sql(sql))]);

    public async Task<SqlRefusal> RefusedAsync(HttpClient client, params object[] statements)
    {
        var (status, body) = await PostAsync(client, statements);
        await Assert.That(status).IsNotEqualTo(HttpStatusCode.OK).Because(body.GetRawText());
        var error = body.GetProperty("error");
        var statement = error.TryGetProperty("statement", out var index) || error.TryGetProperty("op", out index) ? index.GetInt32() : -1;
        return new SqlRefusal(status, error.TryGetProperty("sqlite_code", out var code) ? code.GetString() : null, statement);
    }

    public Task<SqlRefusal> RefusedAsync(params string[] statements) =>
        RefusedAsync(Writer, [.. statements.Select(sql => Sql(sql))]);

    public async Task<long> CountAsync(string table) =>
        (await OkAsync($"SELECT count(*) FROM {table}"))[0].GetProperty("rows")[0][0].GetInt64();

    public static async Task<List<object?[]>> ReadAsync(NpgsqlConnection connection, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i))]);
        }

        return rows;
    }

    public static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public static async Task<string?> StateAsync(NpgsqlConnection connection, string sql, params object[] values)
    {
        try
        {
            _ = await PostgresSql.ExecuteAsync(connection, sql, values);
            return null;
        }
        catch (PostgresException error)
        {
            return error.SqlState;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Writer.Dispose();
        Reader.Dispose();
        await Postgres.DisposeAsync();
        await ReadOnlyPostgres.DisposeAsync();
        await Api.DisposeAsync();
    }
}

internal sealed record SqlRefusal(HttpStatusCode Status, string? Code, int Statement);
