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
}
