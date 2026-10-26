using System.Net;
using TerraBase.Fast.Documents;

namespace TerraBase.Fast.Sql;

public sealed class SqlDropReuseTests
{
    [Test]
    public async Task Atomic_create_insert_drop_releases_the_name_for_the_next_request()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api,
            "CREATE TABLE transient (id INTEGER PRIMARY KEY, value TEXT)",
            "INSERT INTO transient VALUES (1, 'old')",
            "DROP TABLE transient");
        var absent = await SqlApi.OkAsync(api, "SELECT name FROM sqlite_master WHERE name = 'transient'");
        await Assert.That(absent[0].GetProperty("rows").GetArrayLength()).IsEqualTo(0);
        var recreated = await SqlApi.ExecuteAsync(api, "CREATE TABLE transient (id INTEGER PRIMARY KEY, fresh TEXT)");
        await Assert.That(recreated.Status).IsEqualTo(HttpStatusCode.OK).Because(recreated.Json.GetRawText());
        var empty = await SqlApi.OkAsync(api, "SELECT * FROM transient");
        await Assert.That(empty[0].GetProperty("rows").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Recreating_in_the_same_batch_replaces_the_definition_and_rows()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api,
            "CREATE TABLE transient (id INTEGER PRIMARY KEY, value TEXT)",
            "INSERT INTO transient VALUES (1, 'old')",
            "DROP TABLE transient",
            "CREATE TABLE transient (id INTEGER PRIMARY KEY, fresh TEXT)",
            "INSERT INTO transient VALUES (2, 'new')");
        var rows = await SqlApi.OkAsync(api, "SELECT id, fresh FROM transient");
        await Assert.That(rows[0].GetProperty("rows").GetRawText()).IsEqualTo("""[[2,"new"]]""");
    }

    [Test]
    public async Task A_failed_batch_rolls_back_the_drop_and_recreation()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api,
            "CREATE TABLE transient (id INTEGER PRIMARY KEY, value TEXT)",
            "INSERT INTO transient VALUES (1, 'old')");
        var failed = await SqlApi.ExecuteAsync(api,
            "DROP TABLE transient",
            "CREATE TABLE transient (id INTEGER PRIMARY KEY, fresh TEXT)",
            "INSERT INTO transient VALUES (2, 'new')",
            "SELECT * FROM absent");
        await Assert.That(failed.Status).IsEqualTo(HttpStatusCode.BadRequest);
        var rows = await SqlApi.OkAsync(api, "SELECT id, value FROM transient");
        await Assert.That(rows[0].GetProperty("rows").GetRawText()).IsEqualTo("""[[1,"old"]]""");
    }
}
