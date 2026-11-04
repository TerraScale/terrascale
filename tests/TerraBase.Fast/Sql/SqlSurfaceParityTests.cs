using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bogus;
using Npgsql;
using TerraBase.Testing;
using static TerraBase.Testing.SqlNativeDatabase;

namespace TerraBase.Fast.Sql;

public sealed partial class SqlSurfaceParityTests
{
    private static readonly Faker Faker = new();

    [Test]
    public async Task Rest_websocket_and_postgres_answer_the_same_results_and_errors()
    {
        await using var db = await StartAsync();
        var socketDatabase = await db.Api.CreateManagedDatabaseAsync(db.Database.Organization, db.Database.Project, "websocket");
        var wireDatabase = await db.Api.CreateManagedDatabaseAsync(db.Database.Organization, db.Database.Project, "wire");
        await using var socket = await SqlSocketClient.ConnectAsync(db.Api, socketDatabase.Data, apiKey: null);
        var admitted = await socket.AuthAsync(ImportClient.Token(db.Api, db.Database.Project, "documents:read documents:write"));
        await using var wire = PostgresSql.Source(db.Api, db.WriterKey, wireDatabase.Name);
        await using var connection = await wire.OpenConnectionAsync();
        var (name, other, email, second, city) = (Faker.Name.FullName(), Faker.Name.FullName(), Faker.Internet.Email(), Faker.Internet.Email(), Faker.Address.City());
        var script = new (string Sql, object?[] Args)[]
        {
            ("CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, email TEXT UNIQUE, score REAL, profile TEXT, age INTEGER CHECK (age >= 0))", []),
            ("INSERT INTO people VALUES ($1, $2, $3, $4, $5, $6)", [1L, name, email, 1.5, $$"""{"city":"{{city}}"}""", 30L]),
            ("INSERT INTO people (id, name, email, age) VALUES ($1, $2, $3, $4)", [2L, other, second, 40L]),
            ("UPDATE people SET age = age + 1 WHERE id = $1", [1L]),
            ("SELECT id, name, email, score, profile ->> '$.city' AS city, age FROM people ORDER BY id", []),
            ("SELECT count(*) AS people, sum(age) AS years, avg(score) AS mean FROM people", []),
            ("INSERT INTO people (id, name) VALUES ($1, $2)", [1L, other]),
            ("INSERT INTO people (id, name, email) VALUES ($1, $2, $3)", [3L, other, email]),
            ("INSERT INTO people (id, name) VALUES ($1, NULL)", [4L]),
            ("INSERT INTO people (id, name, age) VALUES ($1, $2, $3)", [5L, other, -1L]),
            ("SELECT * FROM missing", []),
            ("SELECT nope FROM people", []),
            ("SELEC 1", []),
            ("SELECT * FROM turso_cdc", []),
            ("VACUUM INTO '/tmp/escape.db'", []),
            ("DELETE FROM people WHERE id = $1", [2L]),
            ("SELECT count(*) AS left_over FROM people", []),
        };

        var leaks = new StringBuilder();
        var answers = new List<(Answer Http, Answer Socket, Answer Wire)>();
        foreach (var (sql, args) in script)
        {
            answers.Add((
                await RestAsync(db.Writer, db.Database.Data, sql, args, leaks),
                await SocketAsync(socket, sql, args, leaks),
                await WireAsync(connection, sql, args, leaks)));
        }

        await Assert.That(Type(admitted)).IsEqualTo("result");
        for (var i = 0; i < script.Length; i++)
        {
            var (rest, overSocket, overWire) = answers[i];
            await Assert.That(overSocket).IsEqualTo(rest).Because(script[i].Sql);
            await Assert.That(overWire with { Code = rest.Code }).IsEqualTo(rest).Because(script[i].Sql);
        }

        await Assert.That(string.Join(',', answers.Select(answer => answer.Http.Code)))
            .IsEqualTo(",,,,,,primary_key_violation,unique_violation,not_null_violation,check_violation,undefined_table,undefined_column,syntax_error,undefined_table,sql_sandbox,,");
        await Assert.That(string.Join(',', answers.Select(answer => answer.Wire.Sqlstate))).IsEqualTo(",,,,,,23505,23505,23502,23514,42P01,42703,42601,42P01,42501,,");
        await Assert.That(answers[1].Http.RowsAffected).IsEqualTo(1);
        await Assert.That(answers[3].Http.RowsAffected).IsEqualTo(1);
        await Assert.That(answers[4].Http.Rows).IsEqualTo($"1|{name}|{email}|1.5|{city}|31\n2|{other}|{second}|∅|∅|40");
        await Assert.That(answers[5].Http.Rows).IsEqualTo("2|71|1.5");
        await Assert.That(answers[16].Http.Rows).IsEqualTo("1");
        await Assert.That(EngineName().IsMatch(leaks.ToString())).IsFalse().Because(leaks.ToString());
    }

    private sealed record Answer(string Columns, string Rows, long RowsAffected, string? Code, string? Sqlstate, string? Message);

    private static async Task<Answer> RestAsync(HttpClient client, string database, string sql, object?[] args, StringBuilder leaks)
    {
        using var response = await client.PostAsJsonAsync(ManagementApi.Uri(database + "/sql/execute"),
            new { statements = new[] { new { sql = Positional(sql), args } } });
        var text = await response.Content.ReadAsStringAsync();
        leaks.AppendLine(text);
        var body = JsonElement.Parse(text);
        return body.TryGetProperty("error", out var error) ? Refused(error) : Result(body.GetProperty("results")[0]);
    }

    private static async Task<Answer> SocketAsync(SqlSocketClient socket, string sql, object?[] args, StringBuilder leaks)
    {
        var frame = await socket.ExecuteAsync(Stmt(Positional(sql), args));
        leaks.AppendLine(frame.GetRawText());
        return Type(frame) == "error" ? Refused(Error(frame)) : Result(frame.GetProperty("results")[0]);
    }

    private static async Task<Answer> WireAsync(NpgsqlConnection connection, string sql, object?[] args, StringBuilder leaks)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
        }

        try
        {
            if (!sql.StartsWith("SELECT", StringComparison.Ordinal) && !sql.StartsWith("SELEC ", StringComparison.Ordinal))
            {
                return Shaped([], [], Math.Max(0, await command.ExecuteNonQueryAsync()));
            }

            await using var reader = await command.ExecuteReaderAsync();
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<IReadOnlyList<string?>>();
            while (await reader.ReadAsync())
            {
                rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : Text(reader.GetValue(i)))]);
            }

            return Shaped(columns, rows, 0);
        }
        catch (PostgresException error)
        {
            leaks.AppendLine(error.MessageText);
            return new Answer("", "", 0, null, error.SqlState, error.MessageText);
        }
    }

    private static Answer Result(JsonElement result)
    {
        var columns = result.GetProperty("columns").EnumerateArray().Select(column => column.GetProperty("name").GetString()!).ToList();
        var rows = result.GetProperty("rows").EnumerateArray()
            .Select(row => (IReadOnlyList<string?>)[.. row.EnumerateArray().Select(value => value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => value.GetString(),
                _ => value.GetRawText(),
            })]).ToList();
        var write = result.GetProperty("columns").GetArrayLength() == 0;
        return Shaped(columns, rows, write ? result.GetProperty("rows_affected").GetInt64() : 0);
    }

    private static Answer Shaped(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string?>> rows, long affected) =>
        new(string.Join('|', columns), string.Join('\n', rows.Select(row => string.Join('|', row.Select(value => value ?? "∅")))), affected, null, null, null);

    private static Answer Refused(JsonElement error) =>
        new("", "", 0, error.GetProperty("code").GetString(), error.GetProperty("sqlstate").GetString(), error.GetProperty("message").GetString());

    private static string Text(object value) => value switch
    {
        double real => real.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()!,
    };

    private static string Positional(string sql) => ParameterPattern().Replace(sql, "?$1");

    private static string Type(JsonElement frame) => frame.GetProperty("type").GetString()!;

    private static JsonElement Error(JsonElement frame) => frame.GetProperty("error");

    [GeneratedRegex(@"\$(\d+)")]
    private static partial Regex ParameterPattern();

    [GeneratedRegex("turso|libsql|sqlite", RegexOptions.IgnoreCase)]
    private static partial Regex EngineName();
}
