using System.Net;
using System.Text.Json;
using TerraBase.Testing;
using static TerraBase.Testing.SqlNativeDatabase;

namespace TerraBase.Fast.Functions;

public sealed class FunctionTests
{
    private const string Hello = """
        async function handler(event) {
            console.log("hello", event.body.name, event.query.lang);
            await sql.execute("CREATE TABLE IF NOT EXISTS audit (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
            const inserted = await sql.execute("INSERT INTO audit (name) VALUES (?)", [event.body.name]);
            const rows = await sql.query("SELECT id, name FROM audit WHERE id = :id", { id: inserted.lastInsertRowid });
            return { status: 201, body: { greeting: "hello " + rows[0].name, id: rows[0].id } };
        }
        """;

    private const string Escape = """
        async function handler(event) {
            try {
                await sql.execute("ATTACH DATABASE 'other.db' AS other");
                return { escaped: true };
            } catch (error) {
                return { status: 403, body: { code: error.code, sqlstate: error.sqlstate, statement: error.statement } };
            }
        }
        """;

    [Test]
    public async Task A_deployed_function_inserts_and_selects_through_sql_over_https_and_logs_the_run()
    {
        await using var db = await StartAsync();
        using var client = Project(db);
        var path = db.Database.Data + "/functions/hello";
        await DeployAsync(client, path, Hello);

        using var invoked = await client.PostAsJsonAsync(ManagementApi.Uri(path + "/invoke?lang=en"), new { name = "Ada" });
        await Assert.That(invoked.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await invoked.Content.ReadAsStringAsync());
        var answer = await invoked.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(answer.GetProperty("greeting").GetString()).IsEqualTo("hello Ada");
        var rows = (await db.OkAsync(Stmt("SELECT name FROM audit WHERE id = ?", answer.GetProperty("id").GetInt64())))[0].GetProperty("rows");
        await Assert.That(rows[0][0].GetString()).IsEqualTo("Ada");

        using var logs = await client.GetAsync(ManagementApi.Uri(path + "/logs"));
        var log = (await logs.Content.ReadFromJsonAsync<JsonElement>())[0];
        await Assert.That(log.GetProperty("status").GetString()).IsEqualTo("ok");
        await Assert.That(log.GetProperty("lines")[0].GetString()).IsEqualTo("hello Ada en");
    }

    [Test]
    public async Task A_function_is_refused_by_the_sql_sandbox_with_the_rest_error_model()
    {
        await using var db = await StartAsync();
        using var client = Project(db);
        var path = db.Database.Data + "/functions/escape";
        await DeployAsync(client, path, Escape);

        using var invoked = await client.PostAsJsonAsync(ManagementApi.Uri(path + "/invoke"), new { });
        await Assert.That(invoked.StatusCode).IsEqualTo(HttpStatusCode.Forbidden).Because(await invoked.Content.ReadAsStringAsync());
        var answer = await invoked.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(answer.GetProperty("code").GetString()).IsEqualTo("sql_sandbox");
        await Assert.That(answer.GetProperty("sqlstate").GetString()).IsEqualTo("42501");
        await Assert.That(answer.GetProperty("statement").GetInt32()).IsEqualTo(0);
    }

    private static HttpClient Project(SqlNativeDatabase db) =>
        db.Api.WithToken(db.Api.Issuer.Mint(db.Api.User, token =>
        {
            token.Claims["project_id"] = db.Database.Project;
            token.Claims["scope"] = "sql:read sql:write";
        }));

    private static async Task DeployAsync(HttpClient client, string path, string source)
    {
        using var deployed = await client.PutAsJsonAsync(ManagementApi.Uri(path), new { source });
        await Assert.That(deployed.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await deployed.Content.ReadAsStringAsync());
    }
}
