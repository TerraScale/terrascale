using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlInjectionTests
{
    private static async Task<long> StatesAsync(DocumentApi api)
    {
        await using var connection = new Turso.Data.Sqlite.SqliteConnection(DocumentApi.ConnectionString(await api.DatabaseFileAsync(), readOnly: true));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM _tb_state";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    // Literals and names SqlParserCS renders without escaping would make SQLite read other text than the validator saw.
    [Test]
    public async Task Literals_and_names_that_do_not_render_back_are_refused_for_read_and_write_keys()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, "CREATE TABLE t (v TEXT)", "INSERT INTO t VALUES ('a')");
        using var reader = await api.ClientWithScopesAsync("documents:read");
        var states = await StatesAsync(api);
        string[] attacks =
        [
            "SELECT N''', (SELECT v FROM _tb_state) AS ''' FROM t",
            "SELECT N''' FROM t; DELETE FROM _tb_state; --' FROM t",
            "SELECT [a]]b] FROM t",
            "SELECT `v``` FROM t",
            "SELECT `v\"` FROM t",
            "SELECT E'\\x27' FROM t",
            "UPDATE t SET v = N''' WHERE 1; DELETE FROM _tb_state; --'",
            "SELECT replace('x', 'x', 'y')",
            "SELECT printf('%.*c', 1000, 'x')",
            "SELECT format('%s', 'x')",
        ];

        foreach (var sql in attacks)
        {
            foreach (var client in new[] { api.Client, reader })
            {
                var sqlite = SqlApi.Error(await SqlApi.ExecuteAsync(api, client, sql)).Sqlite;
                await Assert.That(sqlite is "SQLITE_ERROR" or "SQLITE_AUTH").IsTrue().Because(sql + " " + sqlite);
            }
        }

        await Assert.That(await StatesAsync(api)).IsEqualTo(states);
        await Assert.That((await SqlApi.OkAsync(api, "SELECT v FROM t"))[0].GetProperty("rows").GetRawText()).IsEqualTo("""[["a"]]""");
    }

    [Test]
    public async Task Backtick_names_without_quote_characters_are_double_quoted_names()
    {
        await using var api = await DocumentApi.StartAsync();

        var results = await SqlApi.OkAsync(api,
            "CREATE TABLE `orm_t` (`id` integer PRIMARY KEY NOT NULL, `v` text DEFAULT false NOT NULL)",
            "CREATE UNIQUE INDEX `orm_t_v_unique` ON `orm_t` (`v`);",
            "INSERT INTO `orm_t` (`id`, `v`) VALUES (1, 'x')",
            "SELECT `orm_t`.`v`, \"v\" FROM `orm_t` WHERE `id` = 1");

        await Assert.That(results[3].GetProperty("rows").GetRawText()).IsEqualTo("""[["x","x"]]""");
    }

    [Test]
    public async Task The_engine_runs_exactly_one_statement()
    {
        await using var api = await DocumentApi.StartAsync();
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        await using var transaction = (await engine.BeginTransactionAsync(await api.TenantOfAsync(), write: true, CancellationToken.None))!;

        var error = await Assert.ThrowsAsync<SqlProgramException>(async () => await transaction.ExecuteSqlAsync(
            new SqlProgram([], null, "SELECT 'a;b', \"c;d\" /* ; */ -- ;\n; DELETE FROM _tb_state", [], 10, 1000), CancellationToken.None));
        var quoted = await transaction.ExecuteSqlAsync(new SqlProgram([], null, "SELECT 'a;''b', \"c;\"\"d\" /* ; */ -- ;\n", [], 10, 1000), CancellationToken.None);

        await Assert.That(error!.Message).IsEqualTo("a program runs exactly one statement");
        await Assert.That(quoted.Rows[0][0]).IsEqualTo("a;'b");
        await Assert.That((await api.SendAsync(HttpMethod.Get, api.Collections)).Status).IsEqualTo(HttpStatusCode.OK);
    }
}
