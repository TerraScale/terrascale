using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlConcatTests
{
    private static readonly string[] Expressions =
    [
        "'a' || 1 || 2.5 || NULL", "'a' || 1 || 2.5", "1.0 || ''", "(0.1 + 0.2) || ''", "1e20 || ''", "-0.5 || ''", "100.0 || ''",
        "1.5e-7 || ''", "123456789012345678 || 'x'", "1e15 || ''", "123456.789 || ''", "'é' || 'ü'", "NULL || 'x'",
    ];

    // The bounded concatenation answers exactly what Turso's native || answers.
    [Test]
    public async Task Bounded_concatenation_matches_native_text_conversion()
    {
        await using var api = await DocumentApi.StartAsync();
        var select = "SELECT " + string.Join(", ", Expressions);
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        string native;
        await using (var transaction = (await engine.BeginTransactionAsync(await api.TenantOfAsync(), write: false, CancellationToken.None))!)
        {
            var result = await transaction.ExecuteSqlAsync(new SqlProgram([], null, select, [], 10, 1_000_000), CancellationToken.None);
            native = JsonSerializer.Serialize(result.Rows[0]);
        }

        var rewritten = (await SqlApi.OkAsync(api, select))[0].GetProperty("rows")[0].GetRawText();

        await Assert.That(rewritten).IsEqualTo(native).Because(native);
    }

    [Test]
    public async Task Doubling_a_string_past_1_mib_is_too_big_and_concat_functions_are_refused()
    {
        await using var api = await DocumentApi.StartAsync();
        var inner = $"SELECT '{new string('a', 1024)}' AS x";
        for (var i = 0; i < 24; i++)
        {
            inner = $"SELECT x || x AS x FROM ({inner})";
        }

        var started = DateTime.UtcNow;
        var doubled = await SqlApi.ExecuteAsync(api, $"SELECT length(x) FROM ({inner})");
        var concat = await SqlApi.ExecuteAsync(api, "SELECT concat('a', 'b')");
        var bounded = await SqlApi.ExecuteAsync(api, "SELECT length('ab' || 'cd')");

        await Assert.That(SqlApi.Error(doubled)).IsEqualTo((HttpStatusCode.RequestEntityTooLarge, "sql_too_large", "SQLITE_TOOBIG", 0)).Because(doubled.Message!);
        await Assert.That(doubled.Message).IsEqualTo("a string value is larger than 1 MiB");
        await Assert.That(DateTime.UtcNow - started).IsLessThan(TimeSpan.FromSeconds(5));
        await Assert.That(SqlApi.Error(concat).Sqlite).IsEqualTo("SQLITE_ERROR");
        await Assert.That(bounded.Json.GetProperty("results")[0].GetProperty("rows").GetRawText()).IsEqualTo("[[4]]");
    }
}
