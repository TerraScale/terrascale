using System.Net;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlExecuteApiTests
{
    [Test]
    public async Task Execute_answers_one_result_per_statement_and_names_the_failing_statement()
    {
        await using var api = await DocumentApi.StartAsync();

        var created = await SqlApi.ExecuteAsync(api, "CREATE TABLE t (id INTEGER PRIMARY KEY)", "CREATE TABLE u (id TEXT PRIMARY KEY)");
        var failed = await SqlApi.ExecuteAsync(api, "DROP TABLE IF EXISTS nothing", "CREATE TABLE secret_name (");

        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.OK);
        var first = created.Json.GetProperty("results")[0];
        await Assert.That(created.Json.GetProperty("results").GetArrayLength()).IsEqualTo(2);
        await Assert.That(first.GetRawText()).IsEqualTo("""{"columns":[],"rows":[],"rows_affected":0,"last_insert_rowid":null}""");
        await Assert.That(SqlApi.Error(failed)).IsEqualTo((HttpStatusCode.BadRequest, "sql_error", "SQLITE_ERROR", 1));
        await Assert.That(failed.Message!).DoesNotContain("secret_name");
        await Assert.That(await SqlHarness.DefinitionAsync(api, "u")).IsNotNull();
    }

    [Test]
    public async Task Writes_need_the_write_scope_and_every_statement_needs_the_read_scope()
    {
        await using var api = await DocumentApi.StartAsync();
        using var reader = await api.ClientWithScopesAsync("documents:read");
        using var writer = await api.ClientWithScopesAsync("documents:write");

        var denied = await SqlApi.ExecuteAsync(api, reader, "CREATE TABLE t (a TEXT)");
        var writeOnly = await SqlApi.ExecuteAsync(api, writer, "CREATE TABLE t (a TEXT)");

        await Assert.That(SqlApi.Error(denied)).IsEqualTo((HttpStatusCode.Forbidden, "forbidden_scope", "SQLITE_AUTH", 0));
        await Assert.That((writeOnly.Status, writeOnly.Code)).IsEqualTo((HttpStatusCode.Forbidden, "forbidden_scope"));
        await Assert.That(await SqlHarness.DefinitionAsync(api, "t")).IsNull();
    }

    [Test]
    public async Task Malformed_requests_are_invalid_requests()
    {
        await using var api = await DocumentApi.StartAsync();

        var empty = await SqlApi.SendAsync(api, api.Client);
        var badArgs = await SqlApi.SendAsync(api, api.Client, new { sql = "CREATE TABLE t (a TEXT)", args = new[] { new { nested = 1 } } });
        var unknownMember = await SqlApi.SendAsync(api, api.Client, new { sql = "CREATE TABLE t (a TEXT)", extra = 1 });
        var noDatabase = await DocumentApi.SendAsync(api.Client, HttpMethod.Post, api.DatabasePath("nope") + "/sql/execute",
            JsonContent.Create(new { statements = new[] { new { sql = "CREATE TABLE t (a TEXT)" } } }));

        await Assert.That((empty.Status, empty.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_request"));
        await Assert.That((badArgs.Status, badArgs.Code, badArgs.Op)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_request", 0));
        await Assert.That((unknownMember.Status, unknownMember.Code)).IsEqualTo((HttpStatusCode.BadRequest, "invalid_request"));
        await Assert.That((noDatabase.Status, noDatabase.Code)).IsEqualTo((HttpStatusCode.NotFound, "unknown_database"));
        await Assert.That(await SqlHarness.DefinitionAsync(api, "t")).IsNull();
    }
}
